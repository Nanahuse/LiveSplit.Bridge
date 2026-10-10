import subprocess
import sys
from pathlib import Path

import pytest

from livesplit.bridge.v3 import bridge_pb2, common_pb2
from livesplit_bridge_cli import client as client_module
from livesplit_bridge_cli.cli import TICKS_PER_SECOND, format_ticks, main

CLI_ROOT = Path(__file__).resolve().parents[1]


class ErrorSocket:
    request: bridge_pb2.Request
    response: bridge_pb2.Response

    def send_binary(self, data: bytes) -> None:
        self.request = bridge_pb2.Request.FromString(data)
        self.response = bridge_pb2.Response(
            protocol_version=3,
            request_id=self.request.request_id,
            session_id=17,
            error=common_pb2.BridgeError(
                code=common_pb2.OPERATION_FAILED, message="timer unavailable"
            ),
        )

    def recv(self) -> bytes:
        return self.response.SerializeToString()

    def close(self) -> None:
        pass


def test_format_ticks() -> None:
    assert format_ticks(12 * TICKS_PER_SECOND + 3_450_000) == "0:00:12.345"
    assert format_ticks(-TICKS_PER_SECOND) == "-0:00:01.000"


def test_rpc_error_is_reported_and_returns_nonzero(monkeypatch, capsys) -> None:
    socket = ErrorSocket()
    monkeypatch.setattr(
        client_module.websocket, "create_connection", lambda *a, **k: socket
    )

    result = main(["timer", "start"])
    captured = capsys.readouterr()
    assert result == 1
    assert "Bridge error" in captured.err
    assert "timer unavailable" in captured.err


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
        [
            sys.executable,
            "-m",
            "livesplit_bridge_cli.cli",
            "--port",
            "1",
            *arguments,
        ],
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

    def create_connection(endpoint: str, **kwargs: object) -> ErrorSocket:
        endpoints.append(endpoint)
        return ErrorSocket()

    monkeypatch.setattr(client_module.websocket, "create_connection", create_connection)

    if message is not None:
        with pytest.raises(SystemExit) as error:
            main([*arguments, "timer-state"])
        captured = capsys.readouterr()
        assert error.value.code == 2
        assert message in captured.err
        assert endpoints == []
    else:
        # The fake RPC returns an operation error after connection, proving the
        # parsed port reached the normal connection path.
        assert main([*arguments, "timer-state"]) == 1
        assert endpoints == [f"ws://127.0.0.1:{expected_port}/bridge/v3/rpc"]


def test_socket_supported_timeout_is_passed_to_rpc(monkeypatch) -> None:
    socket = ErrorSocket()
    timeouts: list[float] = []

    def create_connection(*args: object, **kwargs: object) -> ErrorSocket:
        timeouts.append(kwargs["timeout"])
        return socket

    monkeypatch.setattr(client_module.websocket, "create_connection", create_connection)

    # 24 hours is accepted by socket.settimeout on supported platforms.
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
def test_decimal_ticks_are_sent_exactly_in_the_rpc(
    monkeypatch, seconds: str, expected_ticks: int
) -> None:
    socket = ErrorSocket()
    monkeypatch.setattr(
        client_module.websocket,
        "create_connection",
        lambda *args, **kwargs: socket,
    )

    assert main(["game-time", "set", seconds]) == 1
    assert socket.request.game_time_operation.ticks == expected_ticks
