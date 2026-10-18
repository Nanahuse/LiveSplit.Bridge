import json
import subprocess
import sys
from pathlib import Path

import pytest

from livesplit_bridge_cli import client as client_module
from livesplit_bridge_cli.cli import TICKS_PER_SECOND, format_ticks, main

CLI_ROOT = Path(__file__).resolve().parents[1]


class JsonSocket:
    def __init__(self, error: dict[str, str] | None = None) -> None:
        self.error = error
        self.request: dict[str, object] | None = None
        self.response: dict[str, object] = {}

    def send(self, data: str) -> None:
        self.request = json.loads(data)
        self.response = {
            "request_id": self.request["request_id"],
            "session_id": 17,
            "error": self.error
            or {"code": "operation_failed", "message": "timer unavailable"},
        }

    def recv(self) -> str:
        return json.dumps(self.response)

    def close(self) -> None:
        pass


def test_format_ticks() -> None:
    assert format_ticks(12 * TICKS_PER_SECOND + 3_450_000) == "0:00:12.345"
    assert format_ticks(-TICKS_PER_SECOND) == "-0:00:01.000"


def test_rpc_error_is_reported_and_returns_nonzero(monkeypatch, capsys) -> None:
    socket = JsonSocket()
    monkeypatch.setattr(
        client_module.websocket, "create_connection", lambda *a, **k: socket
    )

    result = main(["timer", "start"])
    captured = capsys.readouterr()
    assert result == 1
    assert "Bridge error operation_failed" in captured.err
    assert "timer unavailable" in captured.err
    assert socket.request == {
        "request_id": 1,
        "method": "timer_operation",
        "params": {"operation": "start"},
    }


@pytest.mark.parametrize(
    ("arguments", "message"),
    [
        (["game-time", "set", "nan"], "seconds must be finite"),
        (["game-time", "set", "inf"], "seconds must be finite"),
        (["game-time", "set", "1e300"], "signed 64-bit tick range"),
        (
            ["game-time", "set", "922337203685.4775808"],
            "signed 64-bit tick range",
        ),
        (
            ["game-time", "set", "-922337203685.4775809"],
            "signed 64-bit tick range",
        ),
        (["game-time", "set"], "requires seconds"),
        (["game-time", "pause", "5"], "does not accept seconds"),
        (["--timeout", "nan", "timer-state"], "finite number"),
        (["--timeout", "inf", "timer-state"], "finite number"),
        (["--timeout", "1e306", "timer-state"], "not supported by the socket"),
        (
            ["--timeout", "1e999999999999999999", "timer-state"],
            "not supported by the socket",
        ),
        (["--timeout", "0", "timer-state"], "finite number"),
        (["--timeout", "-1", "timer-state"], "finite number"),
        (["--timeout", "0.0001", "timer-state"], "at least 1 millisecond"),
    ],
)
def test_invalid_arguments_fail_before_connecting(
    arguments: list[str], message: str
) -> None:
    result = subprocess.run(
        [sys.executable, "-m", "livesplit_bridge_cli.cli", "--port", "1", *arguments],
        cwd=CLI_ROOT,
        text=True,
        capture_output=True,
        timeout=5,
        check=False,
    )
    assert result.returncode != 0
    assert message in result.stderr
    assert "Failed to connect" not in result.stderr
    assert "Traceback" not in result.stderr


@pytest.mark.parametrize(
    ("environment", "arguments", "expected_port", "message"),
    [
        (None, [], 54000, None),
        ("54001", [], 54001, None),
        ("invalid", [], None, "invalid int value"),
        ("invalid", ["--port", "54002"], 54002, None),
        ("70000", [], None, "between 1 and 65535"),
    ],
)
def test_port_environment_parsing_and_cli_precedence(
    monkeypatch, capsys, environment, arguments, expected_port, message
) -> None:
    if environment is None:
        monkeypatch.delenv("LIVESPLIT_BRIDGE_WEBSOCKET_PORT", raising=False)
    else:
        monkeypatch.setenv("LIVESPLIT_BRIDGE_WEBSOCKET_PORT", environment)

    endpoints: list[str] = []

    def create_connection(endpoint: str, **kwargs: object) -> JsonSocket:
        endpoints.append(endpoint)
        return JsonSocket()

    monkeypatch.setattr(client_module.websocket, "create_connection", create_connection)

    if message is not None:
        with pytest.raises(SystemExit) as error:
            main([*arguments, "timer-state"])
        captured = capsys.readouterr()
        assert error.value.code == 2
        assert message in captured.err
        assert endpoints == []
    else:
        assert main([*arguments, "timer-state"]) == 1
        assert endpoints == [f"ws://127.0.0.1:{expected_port}/bridge/v4/rpc"]


def test_socket_supported_timeout_is_passed_to_rpc(monkeypatch) -> None:
    socket = JsonSocket()
    timeouts: list[float] = []

    def create_connection(*args: object, **kwargs: object) -> JsonSocket:
        timeouts.append(kwargs["timeout"])
        return socket

    monkeypatch.setattr(client_module.websocket, "create_connection", create_connection)

    assert main(["--timeout", "86400", "timer-state"]) == 1
    assert timeouts == [86400.0]


@pytest.mark.parametrize(
    ("seconds", "expected_ticks"),
    [
        ("12.345", 123_450_000),
        ("-2.5", -25_000_000),
        ("0.0000001", 1),
        ("922337203685.4775807", 9_223_372_036_854_775_807),
        ("-922337203685.4775808", -9_223_372_036_854_775_808),
        ("0.00000005", 0),
        ("0.00000015", 2),
        ("-0.00000015", -2),
    ],
)
def test_decimal_ticks_are_sent_exactly_in_json_rpc(
    monkeypatch, seconds: str, expected_ticks: int
) -> None:
    socket = JsonSocket()
    monkeypatch.setattr(
        client_module.websocket,
        "create_connection",
        lambda *args, **kwargs: socket,
    )

    assert main(["game-time", "set", seconds]) == 1
    assert socket.request == {
        "request_id": 1,
        "method": "game_time_operation",
        "params": {"operation": "set", "ticks": expected_ticks},
    }


@pytest.mark.parametrize(
    ("session_id", "valid"), [(1, True), (2**32 - 1, True), (0, False), (True, False)]
)
def test_rpc_response_rejects_zero_or_non_integer_session_id(
    monkeypatch, session_id: object, valid: bool
) -> None:
    socket = JsonSocket()
    monkeypatch.setattr(
        client_module.websocket, "create_connection", lambda *a, **k: socket
    )

    with client_module.BridgeClient("ws://localhost/bridge/v4/rpc") as client:
        response = {
            "request_id": 1,
            "session_id": session_id,
            "result": {
                "phase": "not_running",
                "split_index": -1,
                "real_time_ticks": 0,
                "game_time_ticks": None,
                "is_game_time_initialized": False,
                "is_game_time_paused": False,
            },
        }
        socket.recv = lambda: json.dumps(response)
        if valid:
            assert client.timer_state()["session_id"] == session_id
        else:
            with pytest.raises(client_module.BridgeClientError, match="session_id"):
                client.timer_state()


def test_json_output_preserves_complete_response(monkeypatch, capsys) -> None:
    socket = JsonSocket()
    monkeypatch.setattr(
        client_module.websocket, "create_connection", lambda *a, **k: socket
    )
    socket.send = lambda data: setattr(
        socket,
        "response",
        {
            "request_id": 1,
            "session_id": 17,
            "result": {
                "phase": "not_running",
                "split_index": -1,
                "real_time_ticks": 0,
                "game_time_ticks": None,
                "is_game_time_initialized": False,
                "is_game_time_paused": False,
            },
        },
    )

    assert main(["--json", "timer-state"]) == 0
    output = json.loads(capsys.readouterr().out)
    assert output["request_id"] == 1
    assert output["session_id"] == 17
    assert output["result"]["game_time_ticks"] is None
