from __future__ import annotations

from dataclasses import dataclass
from typing import Self

import websocket

from livesplit.bridge.v3 import bridge_pb2, common_pb2

PROTOCOL_VERSION = 3


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

    def request(
        self, request: bridge_pb2.Request, expected_body: str
    ) -> bridge_pb2.Response:
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

        try:
            response = bridge_pb2.Response.FromString(data)
        except Exception as error:
            raise BridgeClientError(
                f"Bridge returned invalid protobuf data: {error}"
            ) from error
        if response.protocol_version != PROTOCOL_VERSION:
            raise BridgeClientError(
                f"Protocol version mismatch: expected {PROTOCOL_VERSION}, "
                f"got {response.protocol_version}"
            )
        if response.request_id != request_id:
            raise BridgeClientError(
                f"Request ID mismatch: expected {request_id}, got {response.request_id}"
            )
        if response.session_id == 0:
            raise BridgeClientError("Bridge returned an invalid session_id")
        body = response.WhichOneof("body")
        if body == "error":
            raise BridgeClientError(
                f"Bridge error {response.error.code}: {response.error.message}"
            )
        if body != expected_body:
            raise BridgeClientError(
                f"Unexpected response body: expected {expected_body}, "
                f"got {body or 'none'}"
            )
        return response

    def timer_state(self) -> bridge_pb2.Response:
        return self.request(
            bridge_pb2.Request(get_timer_state=bridge_pb2.GetTimerStateRequest()),
            "get_timer_state",
        )

    def run(self) -> bridge_pb2.Response:
        return self.request(
            bridge_pb2.Request(get_run=bridge_pb2.GetRunRequest()), "get_run"
        )

    def attempt(self) -> bridge_pb2.Response:
        return self.request(
            bridge_pb2.Request(get_attempt=bridge_pb2.GetAttemptRequest()),
            "get_attempt",
        )

    def context(self) -> bridge_pb2.Response:
        return self.request(
            bridge_pb2.Request(get_context_state=bridge_pb2.GetContextStateRequest()),
            "get_context_state",
        )

    def completed_count(self) -> bridge_pb2.Response:
        return self.request(
            bridge_pb2.Request(
                get_completed_count=bridge_pb2.GetCompletedCountRequest()
            ),
            "get_completed_count",
        )

    def timer(self, operation: int) -> bridge_pb2.Response:
        return self.request(
            bridge_pb2.Request(
                timer_operation=bridge_pb2.TimerOperationRequest(operation=operation)
            ),
            "operation",
        )

    def game_time(
        self, operation: int, ticks: int | None = None
    ) -> bridge_pb2.Response:
        request = bridge_pb2.GameTimeOperationRequest(operation=operation)
        if ticks is not None:
            request.ticks = ticks
        return self.request(
            bridge_pb2.Request(game_time_operation=request), "operation"
        )


TIMER_OPERATIONS = {
    "start": common_pb2.TIMER_START,
    "split": common_pb2.TIMER_SPLIT,
    "skip": common_pb2.TIMER_SKIP,
    "undo": common_pb2.TIMER_UNDO,
    "reset": common_pb2.TIMER_RESET,
    "pause": common_pb2.TIMER_PAUSE,
    "resume": common_pb2.TIMER_RESUME,
}

GAME_TIME_OPERATIONS = {
    "initialize": common_pb2.INITIALIZE,
    "pause": common_pb2.GAME_TIME_PAUSE,
    "resume": common_pb2.GAME_TIME_RESUME,
}
