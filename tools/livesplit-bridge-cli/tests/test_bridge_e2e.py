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

ROOT = Path(__file__).resolve().parents[3]
HOST_EXE = (
    ROOT
    / "tests"
    / "LiveSplit.Bridge.TestHost"
    / "bin"
    / "Release"
    / "net4.8.1"
    / "LiveSplit.Bridge.TestHost.exe"
)


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
            cwd=ROOT / "tools" / "livesplit-bridge-cli",
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

    def wait_for_events(self, expected: int, timeout_seconds: int = 5) -> None:
        assert self.process.stdin is not None
        self.process.stdin.write(f"WAIT_EVENTS {expected} {timeout_seconds * 1000}\n")
        self.process.stdin.flush()
        try:
            response = self.output_lines.get(timeout=timeout_seconds + 1)
        except queue.Empty:
            pytest.fail("TestHost did not respond to the Events subscription check")
        assert response == f"EVENTS_READY {expected}", response


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


def test_queries_return_v3_timer_run_attempt_context_and_completed_count(
    test_host: BridgeTestHost,
) -> None:
    timer = test_host.cli("timer-state")
    run = test_host.cli("run")
    attempt = test_host.cli("attempt")
    context = test_host.cli("context")
    completed = test_host.cli("completed-count")

    assert timer.returncode == 0, timer.stderr
    assert "phase=NOT_RUNNING" in timer.stdout
    assert "revision=" not in timer.stdout
    assert run.returncode == 0, run.stderr
    assert "game=Bridge Test Game category=Any%" in run.stdout
    assert "Personal Best" in run.stdout
    assert "[0] First" in run.stdout and "[1] Second" in run.stdout
    assert "Personal Best:" in run.stdout
    assert attempt.returncode == 0, attempt.stderr
    assert "attempt_count=0" in attempt.stdout
    assert "[0] real_time=- game_time=-" in attempt.stdout
    assert context.returncode == 0, context.stderr
    assert "timing_method=REAL_TIME" in context.stdout
    assert "current_comparison=Personal Best" in context.stdout
    assert "custom_variable host_var=host-value" in context.stdout
    assert completed.returncode == 0, completed.stderr
    assert "completed_count=0" in completed.stdout

    json_response = test_host.cli("timer-state", json_output=True)
    parsed = json.loads(json_response.stdout)
    assert parsed["protocol_version"] == 3
    assert int(parsed["session_id"]) > 0
    assert "get_timer_state" in parsed


def test_timer_and_game_time_controls_change_observed_state(
    test_host: BridgeTestHost,
) -> None:
    started = test_host.cli("timer", "start")
    assert started.returncode == 0, started.stderr
    assert "phase=RUNNING" in test_host.cli("timer-state").stdout

    assert test_host.cli("timer", "pause").returncode == 0
    assert "phase=PAUSED" in test_host.cli("timer-state").stdout
    assert test_host.cli("timer", "resume").returncode == 0
    assert "phase=RUNNING" in test_host.cli("timer-state").stdout

    split = test_host.cli("timer", "split")
    assert split.returncode == 0, split.stderr
    assert "split_index=1" in test_host.cli("timer-state").stdout
    assert test_host.cli("timer", "skip").returncode == 0
    assert test_host.cli("timer", "undo").returncode == 0

    initialized = test_host.cli("game-time", "initialize")
    assert initialized.returncode == 0, initialized.stderr
    assert test_host.cli("game-time", "pause").returncode == 0
    set_time = test_host.cli("game-time", "set", "12.345")
    assert set_time.returncode == 0, set_time.stderr
    timer = test_host.cli("timer-state")
    assert "game_time=0:00:12.345" in timer.stdout
    negative_time = test_host.cli("game-time", "set", "-2.5")
    assert negative_time.returncode == 0, negative_time.stderr
    assert "game_time=-0:00:02.500" in test_host.cli("timer-state").stdout
    assert "game_time_initialized=True" in timer.stdout
    assert "game_time_paused=True" in test_host.cli("timer-state").stdout
    assert test_host.cli("game-time", "resume").returncode == 0
    assert "game_time_paused=False" in test_host.cli("timer-state").stdout

    reset = test_host.cli("timer", "reset")
    assert reset.returncode == 0, reset.stderr
    assert "phase=NOT_RUNNING" in test_host.cli("timer-state").stdout


def test_events_cli_receives_types_and_contiguous_sequences(
    test_host: BridgeTestHost,
) -> None:
    command = [
        sys.executable,
        "-m",
        "livesplit_bridge_cli.cli",
        "--port",
        str(test_host.port),
        "events",
        "--count",
        "3",
    ]
    events = subprocess.Popen(
        command,
        cwd=ROOT / "tools" / "livesplit-bridge-cli",
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    try:
        test_host.wait_for_events(1)
        for operation in ("start", "split", "reset"):
            result = test_host.cli("timer", operation)
            assert result.returncode == 0, result.stderr

        output, error = events.communicate(timeout=8)
        assert events.returncode == 0, error
        rows = [line for line in output.splitlines() if line.startswith("[")]
        assert [row.split("] ", 1)[1] for row in rows] == [
            "EVENT_TIMER_STARTED",
            "EVENT_TIMER_SPLIT",
            "EVENT_TIMER_RESET",
        ]
        sequences = [int(row[1:].split("]", 1)[0]) for row in rows]
        assert sequences == list(range(sequences[0], sequences[0] + len(sequences)))
    finally:
        if events.poll() is None:
            events.terminate()
            try:
                events.wait(timeout=5)
            except subprocess.TimeoutExpired:
                events.kill()
                events.wait(timeout=5)


@pytest.mark.parametrize("seconds", ["nan", "inf", "-inf", "1e300", "-1e300"])
def test_game_time_set_rejects_invalid_seconds_without_traceback(
    test_host: BridgeTestHost, seconds: str
) -> None:
    result = test_host.cli("game-time", "set", seconds)
    assert result.returncode != 0
    assert "error:" in result.stderr
    assert "Traceback" not in result.stderr


@pytest.mark.parametrize("timeout", ["nan", "inf", "-inf", "0", "-1"])
def test_timeout_rejects_non_finite_or_non_positive_values(
    test_host: BridgeTestHost, timeout: str
) -> None:
    result = subprocess.run(
        [
            sys.executable,
            "-m",
            "livesplit_bridge_cli.cli",
            "--port",
            str(test_host.port),
            "--timeout",
            timeout,
            "timer-state",
        ],
        cwd=ROOT / "tools" / "livesplit-bridge-cli",
        text=True,
        capture_output=True,
        timeout=8,
        check=False,
    )
    assert result.returncode != 0
    assert "error:" in result.stderr
    assert "Traceback" not in result.stderr


def test_connection_failure_returns_nonzero_and_error(
    test_host: BridgeTestHost,
) -> None:
    test_host.close()
    result = test_host.cli("timer-state")
    assert result.returncode != 0
    assert "error:" in result.stderr
    assert "Failed to connect" in result.stderr


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
