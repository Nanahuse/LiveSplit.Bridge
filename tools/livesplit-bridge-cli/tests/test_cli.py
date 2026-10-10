from livesplit.bridge.v3 import bridge_pb2, common_pb2
from livesplit_bridge_cli import client as client_module
from livesplit_bridge_cli.cli import TICKS_PER_SECOND, format_ticks, main


def test_format_ticks() -> None:
    assert format_ticks(12 * TICKS_PER_SECOND + 3_450_000) == "0:00:12.345"
    assert format_ticks(-TICKS_PER_SECOND) == "-0:00:01.000"


def test_rpc_error_is_reported_and_returns_nonzero(monkeypatch, capsys) -> None:
    class ErrorSocket:
        def send_binary(self, data: bytes) -> None:
            request = bridge_pb2.Request.FromString(data)
            self.response = bridge_pb2.Response(
                protocol_version=3,
                request_id=request.request_id,
                session_id=17,
                error=common_pb2.BridgeError(
                    code=common_pb2.OPERATION_FAILED, message="timer unavailable"
                ),
            )

        def recv(self) -> bytes:
            return self.response.SerializeToString()

        def close(self) -> None:
            pass

    monkeypatch.setattr(
        client_module.websocket, "create_connection", lambda *a, **k: ErrorSocket()
    )

    result = main(["timer", "start"])
    captured = capsys.readouterr()
    assert result == 1
    assert "Bridge error" in captured.err
    assert "timer unavailable" in captured.err
