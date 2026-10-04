from __future__ import annotations

import json
import os
import queue
import socket
import subprocess
import sys
import threading
from collections.abc import Iterator
from pathlib import Path

import pytest
import websocket

from livesplit.bridge.v2 import common_pb2

REPOSITORY_ROOT = Path(__file__).parents[3]
TEST_HOST_PROJECT = (
    REPOSITORY_ROOT
    / "tests"
    / "LiveSplit.Bridge.TestHost"
    / "LiveSplit.Bridge.TestHost.csproj"
)
TEST_HOST = (
    TEST_HOST_PROJECT.parent
    / "bin"
    / "Debug"
    / "net4.8.1"
    / "LiveSplit.Bridge.TestHost.exe"
)
CLI = Path(sys.executable).with_name(
    "livesplit-bridge.exe" if os.name == "nt" else "livesplit-bridge"
)


def unused_tcp_port() -> int:
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def events_endpoint(port: int) -> str:
    return f"ws://127.0.0.1:{port}/bridge/v2/events"


@pytest.fixture(scope="session")
def build_test_host() -> None:
    subprocess.run(
        ["dotnet", "build", str(TEST_HOST_PROJECT), "--nologo"],
        check=True,
        cwd=REPOSITORY_ROOT,
        timeout=120,
    )


@pytest.fixture
def bridge_port(build_test_host: None) -> Iterator[int]:
    port = unused_tcp_port()
    environment = os.environ.copy()
    environment["LIVESPLIT_BRIDGE_WEBSOCKET_PORT"] = str(port)
    process = subprocess.Popen(
        [str(TEST_HOST)],
        env=environment,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    stdout = process.stdout
    assert stdout is not None
    ready: queue.Queue[str] = queue.Queue()
    threading.Thread(target=lambda: ready.put(stdout.readline()), daemon=True).start()
    assert ready.get(timeout=10).strip() == "READY"
    try:
        yield port
    finally:
        process.communicate("\n", timeout=10)


def run_cli(port: int, *arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(CLI), "--port", str(port), "--timeout", "3", *arguments],
        capture_output=True,
        check=False,
        text=True,
        timeout=10,
    )


def connect_events(port: int) -> websocket.WebSocket:
    return websocket.create_connection(events_endpoint(port), timeout=4)


def receive_heartbeat(subscriber: websocket.WebSocket) -> common_pb2.BridgeEvent:
    while True:
        data = subscriber.recv()
        assert isinstance(data, bytes)
        event = common_pb2.BridgeEvent.FromString(data)
        if event.type == common_pb2.EVENT_HEARTBEAT:
            return event


def test_cli_controls_bridge_timer(bridge_port: int) -> None:
    initial = run_cli(bridge_port, "--json", "timer-state")
    no_op = run_cli(bridge_port, "--json", "timer", "pause")
    started = run_cli(bridge_port, "--json", "timer", "start")
    state = run_cli(bridge_port, "--json", "timer-state")

    assert initial.returncode == 0, initial.stderr
    initial_state = json.loads(initial.stdout)["get_timer_state"]["timer_state"]
    assert initial_state["phase"] == "NOT_RUNNING"
    assert no_op.returncode == 0, no_op.stderr
    no_op_state = json.loads(no_op.stdout)["operation"]["timer_state"]
    assert no_op_state["state_revision"] == initial_state["state_revision"]
    assert started.returncode == 0, started.stderr
    started_state = json.loads(started.stdout)["operation"]["timer_state"]
    assert int(started_state["state_revision"]) == (
        int(initial_state["state_revision"]) + 1
    )
    assert state.returncode == 0, state.stderr
    running = json.loads(state.stdout)["get_timer_state"]["timer_state"]
    assert running["phase"] == "RUNNING"
    assert "split_index" not in running  # proto3 omits the default value (zero).


def test_cli_gets_current_run(bridge_port: int) -> None:
    result = run_cli(bridge_port, "--json", "run")

    assert result.returncode == 0, result.stderr
    run = json.loads(result.stdout)["get_run"]["run"]
    assert run["run_revision"] == "1"
    assert [segment["name"] for segment in run["segments"]] == ["First", "Second"]
    assert [segment.get("index", 0) for segment in run["segments"]] == [0, 1]
    assert run["comparisons"] == [
        "Personal Best",
        "Best Segments",
        "Average Segments",
    ]


def test_cli_gets_attempt_and_runtime_state(bridge_port: int) -> None:
    attempt = run_cli(bridge_port, "--json", "attempt")
    runtime = run_cli(bridge_port, "--json", "runtime")

    assert attempt.returncode == 0, attempt.stderr
    attempt_state = json.loads(attempt.stdout)["get_attempt"]["attempt"]
    assert attempt_state["attempt_revision"] == "1"
    assert [segment.get("index", 0) for segment in attempt_state["segments"]] == [0, 1]

    assert runtime.returncode == 0, runtime.stderr
    runtime_state = json.loads(runtime.stdout)["get_runtime_state"]["runtime_state"]
    assert runtime_state["runtime_revision"] == "1"


def test_cli_gets_run_revision_from_timer_state(bridge_port: int) -> None:
    result = run_cli(bridge_port, "--json", "timer-state")

    assert result.returncode == 0, result.stderr
    state = json.loads(result.stdout)["get_timer_state"]["timer_state"]
    assert state["run_revision"] == "1"
    assert state["attempt_revision"] == "1"
    assert state["runtime_revision"] == "1"


def test_cli_sets_bridge_game_time(bridge_port: int) -> None:
    result = run_cli(bridge_port, "--json", "game-time", "set", "12.345")
    no_op = run_cli(bridge_port, "--json", "game-time", "set", "12.345")

    assert result.returncode == 0, result.stderr
    operation = json.loads(result.stdout)["operation"]
    assert no_op.returncode == 0, no_op.stderr
    no_op_operation = json.loads(no_op.stdout)["operation"]
    assert operation["success"] is True
    assert operation["timer_state"]["game_time_ticks"] == "123450000"
    assert operation["timer_state"]["is_game_time_initialized"] is True
    assert (
        no_op_operation["timer_state"]["state_revision"]
        == operation["timer_state"]["state_revision"]
    )


def test_bridge_publishes_heartbeat_without_advancing_sequence(
    bridge_port: int,
) -> None:
    subscriber = connect_events(bridge_port)

    try:
        initial_heartbeat = receive_heartbeat(subscriber)
        repeated_heartbeat = receive_heartbeat(subscriber)
        started = run_cli(bridge_port, "timer", "start")
        assert started.returncode == 0, started.stderr

        while True:
            data = subscriber.recv()
            assert isinstance(data, bytes)
            timer_event = common_pb2.BridgeEvent.FromString(data)
            if timer_event.type == common_pb2.EVENT_TIMER_STARTED:
                break

        next_heartbeat = receive_heartbeat(subscriber)

        assert initial_heartbeat.session_id != 0
        assert initial_heartbeat.event_sequence == 0
        assert not initial_heartbeat.HasField("timer_state")
        assert repeated_heartbeat.session_id == initial_heartbeat.session_id
        assert repeated_heartbeat.event_sequence == initial_heartbeat.event_sequence
        assert not repeated_heartbeat.HasField("timer_state")
        assert timer_event.event_sequence == 1
        assert timer_event.HasField("timer_state")
        assert next_heartbeat.session_id == initial_heartbeat.session_id
        assert next_heartbeat.event_sequence == timer_event.event_sequence
        assert not next_heartbeat.HasField("timer_state")
    finally:
        subscriber.close()
