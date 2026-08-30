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
import zmq

from livesplit.bridge.v1 import common_pb2

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


@pytest.fixture(scope="session")
def build_test_host() -> None:
    subprocess.run(
        ["dotnet", "build", str(TEST_HOST_PROJECT), "--nologo"],
        check=True,
        cwd=REPOSITORY_ROOT,
        timeout=120,
    )


@pytest.fixture
def bridge_endpoints(build_test_host: None) -> Iterator[tuple[str, str]]:
    rpc_port = unused_tcp_port()
    event_port = unused_tcp_port()
    while event_port == rpc_port:
        event_port = unused_tcp_port()
    rpc_endpoint = f"tcp://127.0.0.1:{rpc_port}"
    event_endpoint = f"tcp://127.0.0.1:{event_port}"
    environment = os.environ.copy()
    environment["LIVESPLIT_BRIDGE_RPC_ENDPOINT"] = rpc_endpoint
    environment["LIVESPLIT_BRIDGE_EVENT_ENDPOINT"] = event_endpoint
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
        yield rpc_endpoint, event_endpoint
    finally:
        process.communicate("\n", timeout=10)


def run_cli(rpc_endpoint: str, *arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(CLI), "--rpc-endpoint", rpc_endpoint, "--timeout", "3", *arguments],
        capture_output=True,
        check=False,
        text=True,
        timeout=10,
    )


def receive_heartbeat(subscriber: zmq.Socket[bytes]) -> common_pb2.BridgeEvent:
    while True:
        event = common_pb2.BridgeEvent.FromString(subscriber.recv())
        if event.type == common_pb2.EVENT_HEARTBEAT:
            return event


def test_cli_controls_bridge_timer(
    bridge_endpoints: tuple[str, str],
) -> None:
    rpc_endpoint, _ = bridge_endpoints

    initial = run_cli(rpc_endpoint, "--json", "snapshot")
    no_op = run_cli(rpc_endpoint, "--json", "timer", "pause")
    started = run_cli(rpc_endpoint, "--json", "timer", "start")
    snapshot = run_cli(rpc_endpoint, "--json", "snapshot")

    assert initial.returncode == 0, initial.stderr
    initial_snapshot = json.loads(initial.stdout)["get_snapshot"]["snapshot"]
    assert initial_snapshot["phase"] == "NOT_RUNNING"
    assert no_op.returncode == 0, no_op.stderr
    no_op_snapshot = json.loads(no_op.stdout)["operation"]["snapshot"]
    assert no_op_snapshot["state_revision"] == initial_snapshot["state_revision"]
    assert started.returncode == 0, started.stderr
    started_snapshot = json.loads(started.stdout)["operation"]["snapshot"]
    assert int(started_snapshot["state_revision"]) == (
        int(initial_snapshot["state_revision"]) + 1
    )
    assert snapshot.returncode == 0, snapshot.stderr
    running = json.loads(snapshot.stdout)["get_snapshot"]["snapshot"]
    assert running["phase"] == "RUNNING"
    assert "split_index" not in running  # proto3 omits the default value (zero).
    assert running["split_count"] == 2


def test_cli_sets_bridge_game_time(
    bridge_endpoints: tuple[str, str],
) -> None:
    rpc_endpoint, _ = bridge_endpoints

    result = run_cli(rpc_endpoint, "--json", "game-time", "set", "12.345")
    no_op = run_cli(rpc_endpoint, "--json", "game-time", "set", "12.345")

    assert result.returncode == 0, result.stderr
    operation = json.loads(result.stdout)["operation"]
    assert no_op.returncode == 0, no_op.stderr
    no_op_operation = json.loads(no_op.stdout)["operation"]
    assert operation["success"] is True
    assert operation["snapshot"]["game_time_ticks"] == "123450000"
    assert operation["snapshot"]["is_game_time_initialized"] is True
    assert (
        no_op_operation["snapshot"]["state_revision"]
        == operation["snapshot"]["state_revision"]
    )


def test_bridge_publishes_heartbeat_without_advancing_sequence(
    bridge_endpoints: tuple[str, str],
) -> None:
    rpc_endpoint, event_endpoint = bridge_endpoints
    context = zmq.Context()
    subscriber = context.socket(zmq.SUB)
    subscriber.setsockopt(zmq.LINGER, 0)
    subscriber.setsockopt(zmq.RCVTIMEO, 4_000)
    subscriber.setsockopt(zmq.SUBSCRIBE, b"")
    subscriber.connect(event_endpoint)

    try:
        initial_heartbeat = receive_heartbeat(subscriber)
        repeated_heartbeat = receive_heartbeat(subscriber)
        started = run_cli(rpc_endpoint, "timer", "start")
        assert started.returncode == 0, started.stderr

        while True:
            timer_event = common_pb2.BridgeEvent.FromString(subscriber.recv())
            if timer_event.type == common_pb2.EVENT_TIMER_STARTED:
                break

        next_heartbeat = receive_heartbeat(subscriber)

        assert initial_heartbeat.session_id != 0
        assert initial_heartbeat.event_sequence == 0
        assert not initial_heartbeat.HasField("snapshot")
        assert repeated_heartbeat.session_id == initial_heartbeat.session_id
        assert repeated_heartbeat.event_sequence == initial_heartbeat.event_sequence
        assert not repeated_heartbeat.HasField("snapshot")
        assert timer_event.event_sequence == 1
        assert timer_event.HasField("snapshot")
        assert next_heartbeat.session_id == initial_heartbeat.session_id
        assert next_heartbeat.event_sequence == timer_event.event_sequence
        assert not next_heartbeat.HasField("snapshot")
    finally:
        subscriber.close()
        context.term()
