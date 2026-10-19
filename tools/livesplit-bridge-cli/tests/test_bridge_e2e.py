from __future__ import annotations

import json
import queue
import socket
import subprocess
import sys
import threading
from collections.abc import Iterator
from dataclasses import dataclass
from pathlib import Path

import pytest

from livesplit_bridge_cli.client import BridgeClient, BridgeClientError

ROOT = Path(__file__).resolve().parents[3]
CLI_ROOT = ROOT / "tools" / "livesplit-bridge-cli"
HOST_EXE = (
    ROOT
    / "tests"
    / "LiveSplit.Bridge.TestHost"
    / "bin"
    / "Release"
    / "net4.8.1"
    / "LiveSplit.Bridge.TestHost.exe"
)
TICKS_PER_SECOND = 10_000_000


def free_port() -> int:
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def enqueue_lines(stream: object, lines: queue.Queue[str]) -> None:
    for line in stream:  # type: ignore[attr-defined]
        lines.put(line.rstrip())


@dataclass
class BridgeTestHost:
    process: subprocess.Popen[str]
    port: int
    output_lines: queue.Queue[str]

    def cli(
        self, *args: str, json_output: bool = False
    ) -> subprocess.CompletedProcess[str]:
        command = [
            sys.executable,
            "-m",
            "livesplit_bridge_cli.cli",
            "--port",
            str(self.port),
            "--timeout",
            "5",
        ]
        if json_output:
            command.append("--json")
        return subprocess.run(
            [*command, *args],
            cwd=CLI_ROOT,
            text=True,
            capture_output=True,
            timeout=8,
            check=False,
        )

    def close(self) -> None:
        if self.process.poll() is None:
            try:
                self.process.stdin.write("\n")
                self.process.stdin.flush()
            except OSError:
                pass
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.terminate()
                self.process.wait(timeout=5)


def start_event_cli(
    host: BridgeTestHost, count: int, *, json_output: bool = False
) -> tuple[subprocess.Popen[str], queue.Queue[str], threading.Thread]:
    command = [
        sys.executable,
        "-m",
        "livesplit_bridge_cli.cli",
        "--port",
        str(host.port),
    ]
    if json_output:
        command.append("--json")
    command.extend(["events", "--count", str(count)])
    process = subprocess.Popen(
        command,
        cwd=CLI_ROOT,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        bufsize=1,
    )
    stderr_lines: queue.Queue[str] = queue.Queue()
    stderr_reader = threading.Thread(
        target=enqueue_lines, args=(process.stderr, stderr_lines), daemon=True
    )
    stderr_reader.start()
    try:
        status = stderr_lines.get(timeout=8)
    except queue.Empty:
        process.terminate()
        process.wait(timeout=5)
        pytest.fail("Events CLI did not report a completed WebSocket connection")
    assert status.startswith("Monitoring ws://127.0.0.1:") or "error:" in status, status
    if "error:" in status:
        pytest.fail(f"Events CLI failed to connect: {status}")
    return process, stderr_lines, stderr_reader


def finish_event_cli(
    process: subprocess.Popen[str],
    stderr_lines: queue.Queue[str],
    stderr_reader: threading.Thread,
) -> tuple[str, list[str]]:
    try:
        process.wait(timeout=8)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)
        pytest.fail("Events CLI did not exit after receiving its requested Event count")
    output = process.stdout.read()
    stderr_reader.join(timeout=1)
    errors: list[str] = []
    while True:
        try:
            errors.append(stderr_lines.get_nowait())
        except queue.Empty:
            break
    assert process.returncode == 0, "\n".join(errors)
    return output, errors


@pytest.fixture
def test_host() -> Iterator[BridgeTestHost]:
    assert HOST_EXE.exists(), f"TestHost was not built: {HOST_EXE}"
    port = free_port()
    process = subprocess.Popen(
        [str(HOST_EXE), "--port", str(port)],
        cwd=ROOT,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        bufsize=1,
    )
    lines: queue.Queue[str] = queue.Queue()
    threading.Thread(
        target=enqueue_lines, args=(process.stdout, lines), daemon=True
    ).start()
    try:
        line = lines.get(timeout=20)
    except queue.Empty:
        process.terminate()
        output = process.communicate(timeout=5)[0]
        pytest.fail(
            f"TestHost did not become ready (exit={process.returncode}): {output}"
        )
    if line != "READY":
        process.terminate()
        process.wait(timeout=5)
        pytest.fail(f"TestHost failed to start: {line}")
    host = BridgeTestHost(process, port, lines)
    try:
        yield host
    finally:
        host.close()


def rpc_json(host: BridgeTestHost, *args: str) -> dict[str, object]:
    response = host.cli(*args, json_output=True)
    assert response.returncode == 0, response.stderr
    parsed = json.loads(response.stdout)
    assert isinstance(parsed, dict)
    return parsed


def test_queries_return_v4_state_and_complete_json_envelopes(
    test_host: BridgeTestHost,
) -> None:
    timer = rpc_json(test_host, "timer-state")
    assert timer["request_id"] == 1
    assert isinstance(timer["session_id"], int) and timer["session_id"] > 0
    assert "protocol_version" not in timer
    assert timer["result"]["phase"] == "not_running"
    assert timer["result"]["real_time_ticks"] == 0
    assert timer["result"]["game_time_ticks"] is None

    run = rpc_json(test_host, "run")["result"]
    assert run["game_name"] == "Bridge Test Game"
    assert run["category_name"] == "Any%"
    assert "Personal Best" in run["comparisons"]
    assert [segment["name"] for segment in run["segments"]] == ["First", "Second"]
    assert run["segments"][0]["comparisons"][0]["name"] == "Personal Best"

    attempt = rpc_json(test_host, "attempt")["result"]
    assert attempt["attempt_count"] == 0
    assert len(attempt["segments"]) == 2
    assert attempt["segments"][0]["split_time"] == {
        "real_time_ticks": None,
        "game_time_ticks": None,
    }

    context = rpc_json(test_host, "context")["result"]
    assert context["current_timing_method"] == "real_time"
    assert context["current_comparison"] == "Personal Best"
    assert context["custom_variables"]["host_var"] == "host-value"

    completed = rpc_json(test_host, "completed-count")["result"]
    assert completed == {"completed_count": 0}

    human = test_host.cli("timer-state")
    assert human.returncode == 0, human.stderr
    assert "phase=not_running" in human.stdout
    assert "game_time=-" in human.stdout


def test_timer_and_game_time_controls_change_observed_state(
    test_host: BridgeTestHost,
) -> None:
    started = test_host.cli("timer", "start", json_output=True)
    assert started.returncode == 0, started.stderr
    control_response = json.loads(started.stdout)
    assert control_response["request_id"] == 1
    assert control_response["session_id"] > 0
    assert control_response["result"] is None
    assert "error" not in control_response
    assert rpc_json(test_host, "timer-state")["result"]["phase"] == "running"

    assert test_host.cli("timer", "pause").returncode == 0
    assert rpc_json(test_host, "timer-state")["result"]["phase"] == "paused"
    assert test_host.cli("timer", "resume").returncode == 0
    assert test_host.cli("timer", "split").returncode == 0
    assert rpc_json(test_host, "timer-state")["result"]["split_index"] == 1
    assert test_host.cli("timer", "skip").returncode == 0
    assert test_host.cli("timer", "undo").returncode == 0

    assert test_host.cli("game-time", "initialize").returncode == 0
    assert test_host.cli("game-time", "pause").returncode == 0
    for seconds, expected_ticks in [
        ("12.345", 123_450_000),
        ("-2.5", -25_000_000),
        ("0.0000001", 1),
        ("0.00000005", 0),
        ("0.00000015", 2),
        ("-0.00000015", -2),
    ]:
        result = test_host.cli("game-time", "set", seconds)
        assert result.returncode == 0, result.stderr
        state = rpc_json(test_host, "timer-state")["result"]
        assert state["game_time_ticks"] == expected_ticks
    state = rpc_json(test_host, "timer-state")["result"]
    assert state["is_game_time_initialized"] is True
    assert state["is_game_time_paused"] is True
    assert test_host.cli("game-time", "resume").returncode == 0
    assert rpc_json(test_host, "timer-state")["result"]["is_game_time_paused"] is False

    reset = test_host.cli("timer", "reset")
    assert reset.returncode == 0, reset.stderr
    assert rpc_json(test_host, "timer-state")["result"]["phase"] == "not_running"


def test_events_cli_receives_ordered_v4_events(test_host: BridgeTestHost) -> None:
    events, errors, stderr_reader = start_event_cli(test_host, 3)
    for operation in ("start", "split", "reset"):
        result = test_host.cli("timer", operation)
        assert result.returncode == 0, result.stderr

    output, remaining_errors = finish_event_cli(events, errors, stderr_reader)
    assert not any("warning:" in line for line in remaining_errors)
    rows = [line for line in output.splitlines() if line.startswith("[")]
    assert [row.split("] ", 1)[1] for row in rows] == [
        "timer_started",
        "timer_split",
        "timer_reset",
    ]
    sequences = [int(row[1:].split("]", 1)[0]) for row in rows]
    assert sequences == list(range(sequences[0], sequences[0] + len(sequences)))
    assert "phase=running" in output
    assert "phase=not_running" in output


def test_events_json_outputs_complete_event_and_preserves_null(
    test_host: BridgeTestHost,
) -> None:
    events, errors, stderr_reader = start_event_cli(test_host, 1, json_output=True)
    result = test_host.cli("timer", "start")
    assert result.returncode == 0, result.stderr

    output, _ = finish_event_cli(events, errors, stderr_reader)
    event = json.loads(output)
    assert event["type"] == "timer_started"
    assert event["session_id"] > 0
    assert event["event_sequence"] > 0
    assert event["timer_state"]["phase"] == "running"
    assert event["timer_state"]["game_time_ticks"] is None


def test_bridge_invalid_request_error_is_reported_by_client(
    test_host: BridgeTestHost,
) -> None:
    with BridgeClient(f"ws://127.0.0.1:{test_host.port}/bridge/v4/rpc") as client:
        with pytest.raises(BridgeClientError, match="Bridge error invalid_request"):
            client.request("unknown_method")


def test_connection_failure_returns_nonzero_and_error(
    test_host: BridgeTestHost,
) -> None:
    test_host.close()
    result = test_host.cli("timer-state")
    assert result.returncode != 0
    assert "error:" in result.stderr
    assert "Failed to connect" in result.stderr


def test_testhost_rejects_port_occupied_by_another_process() -> None:
    port = free_port()
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", port))
        listener.listen()
        result = subprocess.run(
            [str(HOST_EXE), "--port", str(port)],
            cwd=ROOT,
            capture_output=True,
            timeout=8,
            check=False,
        )

    assert result.returncode != 0
    assert b"READY" not in result.stdout
    assert b"already occupied" in result.stderr


def test_testhost_shutdown_releases_rpc_port(test_host: BridgeTestHost) -> None:
    port = test_host.port
    test_host.close()

    with socket.socket() as probe:
        assert probe.connect_ex(("127.0.0.1", port)) != 0


def test_event_cli_reports_component_shutdown_as_connection_error(
    test_host: BridgeTestHost,
) -> None:
    events, errors, stderr_reader = start_event_cli(test_host, 2)
    test_host.close()
    try:
        events.wait(timeout=8)
    except subprocess.TimeoutExpired:
        events.kill()
        events.wait(timeout=5)
        pytest.fail("Events CLI did not exit after the Component shut down")
    output = events.stdout.read()
    stderr_reader.join(timeout=1)
    messages = []
    while True:
        try:
            messages.append(errors.get_nowait())
        except queue.Empty:
            break
    assert events.returncode == 1, (output, messages)
    assert any("Events connection closed" in message for message in messages), messages


def test_explicit_port_overrides_invalid_environment(
    test_host: BridgeTestHost, monkeypatch
) -> None:
    monkeypatch.setenv("LIVESPLIT_BRIDGE_WEBSOCKET_PORT", "invalid")
    result = test_host.cli("timer-state")

    assert result.returncode == 0, result.stderr
    assert "phase=not_running" in result.stdout


@pytest.mark.parametrize("port", ["0", "65536"])
def test_testhost_rejects_invalid_port(port: str) -> None:
    result = subprocess.run(
        [str(HOST_EXE), "--port", port],
        cwd=ROOT,
        text=True,
        capture_output=True,
        timeout=5,
        check=False,
    )
    assert result.returncode == 2
    assert "Usage:" in result.stderr
