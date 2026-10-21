from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Self

import websocket

UINT32_MAX = (1 << 32) - 1


class BridgeClientError(RuntimeError):
    pass


QUERY_METHODS = {
    "get_timer_state",
    "get_run",
    "get_attempt",
    "get_context_state",
    "get_completed_count",
}
CONTROL_METHODS = {"timer_operation", "game_time_operation"}


def is_uint32(value: object, *, nonzero: bool = False) -> bool:
    return (
        type(value) is int and (not nonzero or value != 0) and 0 <= value <= UINT32_MAX
    )


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
        except (
            OSError,
            OverflowError,
            ValueError,
            websocket.WebSocketException,
        ) as error:
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
        self, method: str, params: dict[str, object] | None = None
    ) -> dict[str, object]:
        request_id = self._next_request_id
        self._next_request_id = (request_id + 1) & UINT32_MAX
        request: dict[str, object] = {"request_id": request_id, "method": method}
        if params is not None:
            request["params"] = params

        try:
            self._socket.send(json.dumps(request, separators=(",", ":")))
            data = self._socket.recv()
        except websocket.WebSocketTimeoutException as error:
            raise BridgeClientError(
                f"RPC timed out after {self.timeout_ms} ms ({self.rpc_endpoint})"
            ) from error
        except (OSError, websocket.WebSocketException) as error:
            raise BridgeClientError(f"RPC failed: {error}") from error

        if data is None or data == "":
            raise BridgeClientError("RPC connection closed before a response arrived")
        if not isinstance(data, str):
            raise BridgeClientError("Bridge returned a binary frame; text expected")
        try:
            response = json.loads(data)
        except (json.JSONDecodeError, UnicodeError, RecursionError) as error:
            raise BridgeClientError(f"Bridge returned invalid JSON: {error}") from error
        if not isinstance(response, dict):
            raise BridgeClientError("Bridge returned a non-object RPC response")

        response_id = response.get("request_id")
        if not is_uint32(response_id) or response_id != request_id:
            raise BridgeClientError(
                f"Request ID mismatch: expected {request_id}, got {response_id!r}"
            )
        session_id = response.get("session_id")
        if not is_uint32(session_id, nonzero=True):
            raise BridgeClientError("Bridge returned an invalid session_id")

        has_result = "result" in response
        has_error = "error" in response
        if has_result == has_error:
            raise BridgeClientError(
                "Bridge response must contain exactly one of result or error"
            )

        if has_error:
            error_body = response["error"]
            if not isinstance(error_body, dict):
                raise BridgeClientError("Bridge returned an invalid error object")
            code = error_body.get("code")
            message = error_body.get("message")
            if not isinstance(code, str) or not isinstance(message, str):
                raise BridgeClientError(
                    "Bridge returned an error without code and message"
                )
            raise BridgeClientError(f"Bridge error {code}: {message}")

        result = response["result"]
        if method in QUERY_METHODS:
            if not isinstance(result, dict):
                raise BridgeClientError("Bridge returned a non-object query result")
        elif method in CONTROL_METHODS:
            if result is not None:
                raise BridgeClientError("Control success result must be null")
        else:
            raise BridgeClientError(f"Unsupported RPC method: {method}")
        return response

    def timer_state(self) -> dict[str, object]:
        return self.request("get_timer_state")

    def run(self) -> dict[str, object]:
        return self.request("get_run")

    def attempt(self) -> dict[str, object]:
        return self.request("get_attempt")

    def context(self) -> dict[str, object]:
        return self.request("get_context_state")

    def completed_count(self) -> dict[str, object]:
        return self.request("get_completed_count")

    def timer(self, operation: str) -> dict[str, object]:
        return self.request("timer_operation", {"operation": operation})

    def game_time(self, operation: str, ticks: int | None = None) -> dict[str, object]:
        params: dict[str, object] = {"operation": operation}
        if ticks is not None:
            params["ticks"] = ticks
        return self.request("game_time_operation", params)
