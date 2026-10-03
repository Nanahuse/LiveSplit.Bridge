from __future__ import annotations

from dataclasses import dataclass
from typing import Self

import websocket

from livesplit.bridge.v1 import bridge_pb2

PROTOCOL_VERSION = 1


class BridgeClientError(RuntimeError):
    pass


@dataclass
class BridgeClient:
    rpc_endpoint: str
    timeout_ms: int = 3000

    def __post_init__(self) -> None:
        try:
            self._socket = websocket.create_connection(
                self.rpc_endpoint,
                timeout=self.timeout_ms / 1000,
            )
        except (OSError, websocket.WebSocketException) as error:
            raise BridgeClientError(
                f"Failed to connect to {self.rpc_endpoint}: {error}"
            ) from error
        self._next_request_id = 1

    def close(self) -> None:
        self._socket.close()

    def __enter__(self) -> Self:
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def request(self, request: bridge_pb2.Request) -> bridge_pb2.Response:
        request_id = self._next_request_id
        self._next_request_id += 1
        request.protocol_version = PROTOCOL_VERSION
        request.request_id = request_id
        try:
            self._socket.send_binary(request.SerializeToString())
            data = self._socket.recv()
        except websocket.WebSocketTimeoutException as error:
            raise BridgeClientError(
                f"RPC timed out after {self.timeout_ms} ms ({self.rpc_endpoint})"
            ) from error
        except websocket.WebSocketException as error:
            raise BridgeClientError(f"RPC failed: {error}") from error
        if isinstance(data, str):
            raise BridgeClientError("Bridge returned a text frame; binary expected")
        response = bridge_pb2.Response.FromString(data)
        if response.request_id != request_id:
            raise BridgeClientError(
                f"Request ID mismatch: expected {request_id}, got {response.request_id}"
            )
        if response.HasField("error"):
            raise BridgeClientError(
                f"Bridge error {response.error.code}: {response.error.message}"
            )
        return response

    def attach(self) -> bridge_pb2.Response:
        return self.request(bridge_pb2.Request(attach=bridge_pb2.AttachRequest()))

    def snapshot(self) -> bridge_pb2.Response:
        return self.request(
            bridge_pb2.Request(get_snapshot=bridge_pb2.GetSnapshotRequest())
        )

    def run(self) -> bridge_pb2.Response:
        return self.request(bridge_pb2.Request(get_run=bridge_pb2.GetRunRequest()))

    def timer(self, operation: str) -> bridge_pb2.Response:
        return self.request(
            bridge_pb2.Request(
                timer_operation=bridge_pb2.TimerOperationRequest(operation=operation)
            )
        )

    def game_time(
        self, operation: str, ticks: int | None = None
    ) -> bridge_pb2.Response:
        request = bridge_pb2.GameTimeOperationRequest(operation=operation)
        if ticks is not None:
            request.ticks = ticks
        return self.request(bridge_pb2.Request(game_time_operation=request))


TIMER_OPERATIONS = {
    "start": "TIMER_START",
    "split": "TIMER_SPLIT",
    "skip": "TIMER_SKIP",
    "undo": "TIMER_UNDO",
    "reset": "TIMER_RESET",
    "pause": "TIMER_PAUSE",
    "resume": "TIMER_RESUME",
}

GAME_TIME_OPERATIONS = {
    "initialize": "INITIALIZE",
    "pause": "GAME_TIME_PAUSE",
    "resume": "GAME_TIME_RESUME",
}
