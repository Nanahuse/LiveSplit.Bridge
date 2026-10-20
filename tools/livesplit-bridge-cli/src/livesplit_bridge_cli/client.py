from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Self

import websocket

UINT32_MAX = (1 << 32) - 1
RPC_PATH = "/bridge/v4/rpc"


class BridgeClientError(RuntimeError):
    pass


QUERY_RESULT_TYPES = {
    "get_timer_state": "TimerState",
    "get_run": "RunState",
    "get_attempt": "AttemptState",
    "get_context_state": "ContextState",
    "get_completed_count": "CompletedCount",
}
CONTROL_METHODS = {"timer_operation", "game_time_operation"}
TIMER_PHASES = {"unspecified", "not_running", "running", "paused", "ended"}
TIMING_METHODS = {"unspecified", "real_time", "game_time"}
EVENT_TYPES = {
    "timer_started",
    "timer_split",
    "timer_skipped",
    "timer_undo",
    "timer_reset",
    "timer_phase_changed",
    "run_changed",
    "context_changed",
}
TIMER_STATE_EVENTS = {
    "timer_started",
    "timer_split",
    "timer_skipped",
    "timer_undo",
    "timer_phase_changed",
}
ERROR_CODES = {
    "invalid_request",
    "invalid_argument",
    "operation_failed",
    "internal_error",
    "permission_denied",
}


def is_uint32(value: object, *, nonzero: bool = False) -> bool:
    return (
        type(value) is int and (not nonzero or value != 0) and 0 <= value <= UINT32_MAX
    )


def _invalid(path: str, expected: str) -> BridgeClientError:
    return BridgeClientError(f"Bridge returned invalid {path}: expected {expected}")


def _object(value: object, path: str) -> dict[str, object]:
    if not isinstance(value, dict):
        raise _invalid(path, "an object")
    return value


def _field(obj: dict[str, object], name: str, path: str) -> object:
    if name not in obj:
        raise _invalid(f"{path}.{name}", "a required field")
    return obj[name]


def _integer(value: object, path: str, minimum: int, maximum: int) -> None:
    if type(value) is not int or not minimum <= value <= maximum:
        raise _invalid(path, f"an integer from {minimum} through {maximum}")


def _uint32_field(obj: dict[str, object], name: str, path: str) -> None:
    _integer(_field(obj, name, path), f"{path}.{name}", 0, UINT32_MAX)


def _int32_field(obj: dict[str, object], name: str, path: str) -> None:
    _integer(_field(obj, name, path), f"{path}.{name}", -(1 << 31), (1 << 31) - 1)


def _int64_field(
    obj: dict[str, object], name: str, path: str, *, nullable: bool = False
) -> None:
    value = _field(obj, name, path)
    if nullable and value is None:
        return
    _integer(value, f"{path}.{name}", -(1 << 63), (1 << 63) - 1)


def _string_field(
    obj: dict[str, object], name: str, path: str, *, nullable: bool = False
) -> None:
    value = _field(obj, name, path)
    if nullable and value is None:
        return
    if not isinstance(value, str):
        suffix = " or null" if nullable else ""
        raise _invalid(f"{path}.{name}", f"a string{suffix}")


def _bool_field(obj: dict[str, object], name: str, path: str) -> None:
    if type(_field(obj, name, path)) is not bool:
        raise _invalid(f"{path}.{name}", "a boolean")


def _string_map(value: object, path: str) -> None:
    obj = _object(value, path)
    for key, item in obj.items():
        if not isinstance(key, str) or not isinstance(item, str):
            raise _invalid(path, "an object with string values")


def _array(value: object, path: str) -> list[object]:
    if not isinstance(value, list):
        raise _invalid(path, "an array")
    return value


def validate_timer_state(value: object, path: str = "TimerState") -> dict[str, object]:
    state = _object(value, path)
    phase = _field(state, "phase", path)
    if not isinstance(phase, str) or phase not in TIMER_PHASES:
        raise _invalid(f"{path}.phase", "a TimerPhase string")
    _int32_field(state, "split_index", path)
    _int64_field(state, "real_time_ticks", path)
    _int64_field(state, "game_time_ticks", path, nullable=True)
    _bool_field(state, "is_game_time_initialized", path)
    _bool_field(state, "is_game_time_paused", path)
    return state


def validate_time_value(value: object, path: str = "TimeValue") -> dict[str, object]:
    time_value = _object(value, path)
    _int64_field(time_value, "real_time_ticks", path, nullable=True)
    _int64_field(time_value, "game_time_ticks", path, nullable=True)
    return time_value


def _validate_run_state(value: object) -> dict[str, object]:
    run = _object(value, "RunState")
    _string_field(run, "game_name", "RunState")
    _string_field(run, "category_name", "RunState")
    _int64_field(run, "offset_ticks", "RunState")
    _string_field(run, "file_path", "RunState", nullable=True)
    _string_field(run, "layout_path", "RunState", nullable=True)

    metadata_value = _field(run, "metadata", "RunState")
    if metadata_value is not None:
        metadata = _object(metadata_value, "RunState.metadata")
        for name in ("run_id", "platform_name", "region_name"):
            _string_field(metadata, name, "RunState.metadata", nullable=True)
        _bool_field(metadata, "uses_emulator", "RunState.metadata")
        _string_map(
            _field(metadata, "variables", "RunState.metadata"),
            "RunState.metadata.variables",
        )

    comparisons = _array(_field(run, "comparisons", "RunState"), "RunState.comparisons")
    if any(not isinstance(name, str) for name in comparisons):
        raise _invalid("RunState.comparisons", "an array of strings")

    segments = _array(_field(run, "segments", "RunState"), "RunState.segments")
    for index, item in enumerate(segments):
        path = f"RunState.segments[{index}]"
        segment = _object(item, path)
        _uint32_field(segment, "index", path)
        _string_field(segment, "name", path)
        segment_comparisons = _array(
            _field(segment, "comparisons", path), f"{path}.comparisons"
        )
        for comparison_index, comparison_value in enumerate(segment_comparisons):
            comparison_path = f"{path}.comparisons[{comparison_index}]"
            comparison = _object(comparison_value, comparison_path)
            _string_field(comparison, "name", comparison_path)
            validate_time_value(
                _field(comparison, "time", comparison_path),
                f"{comparison_path}.time",
            )
        validate_time_value(
            _field(segment, "best_segment_time", path), f"{path}.best_segment_time"
        )
        _string_field(segment, "icon", path, nullable=True)
    _string_field(run, "game_icon", "RunState", nullable=True)
    return run


def _validate_attempt_state(value: object) -> dict[str, object]:
    attempt = _object(value, "AttemptState")
    _uint32_field(attempt, "attempt_count", "AttemptState")
    segments = _array(
        _field(attempt, "segments", "AttemptState"), "AttemptState.segments"
    )
    for index, item in enumerate(segments):
        path = f"AttemptState.segments[{index}]"
        segment = _object(item, path)
        _uint32_field(segment, "index", path)
        validate_time_value(_field(segment, "split_time", path), f"{path}.split_time")
        _string_map(
            _field(segment, "custom_variables", path), f"{path}.custom_variables"
        )
    return attempt


def _validate_context_state(value: object) -> dict[str, object]:
    context = _object(value, "ContextState")
    method = _field(context, "current_timing_method", "ContextState")
    if not isinstance(method, str) or method not in TIMING_METHODS:
        raise _invalid("ContextState.current_timing_method", "a TimingMethod string")
    _string_field(context, "current_comparison", "ContextState")
    _string_map(
        _field(context, "custom_variables", "ContextState"),
        "ContextState.custom_variables",
    )
    return context


def validate_query_result(method: str, value: object) -> dict[str, object]:
    if method == "get_timer_state":
        return validate_timer_state(value)
    if method == "get_run":
        return _validate_run_state(value)
    if method == "get_attempt":
        return _validate_attempt_state(value)
    if method == "get_context_state":
        return _validate_context_state(value)
    if method == "get_completed_count":
        completed = _object(value, "CompletedCount")
        _uint32_field(completed, "completed_count", "CompletedCount")
        return completed
    raise BridgeClientError(f"Unsupported RPC method: {method}")


def validate_event(value: object) -> dict[str, object]:
    event = _object(value, "BridgeEvent")
    session_id = _field(event, "session_id", "BridgeEvent")
    sequence = _field(event, "event_sequence", "BridgeEvent")
    if not is_uint32(session_id, nonzero=True):
        raise _invalid("BridgeEvent.session_id", "a nonzero uint32")
    if not is_uint32(sequence, nonzero=True):
        raise _invalid("BridgeEvent.event_sequence", "a nonzero uint32")
    event_type = _field(event, "type", "BridgeEvent")
    if not isinstance(event_type, str) or event_type not in EVENT_TYPES:
        raise _invalid("BridgeEvent.type", "a known event type")
    timer_state = _field(event, "timer_state", "BridgeEvent")
    if event_type in TIMER_STATE_EVENTS:
        validate_timer_state(timer_state, "BridgeEvent.timer_state")
    elif event_type == "timer_reset":
        if timer_state is not None:
            validate_timer_state(timer_state, "BridgeEvent.timer_state")
    elif timer_state is not None:
        raise _invalid("BridgeEvent.timer_state", "null for this event type")
    return event


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

        if data is None:
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
            if code not in ERROR_CODES:
                raise BridgeClientError("Bridge returned an unknown error code")
            raise BridgeClientError(f"Bridge error {code}: {message}")

        result = response["result"]
        expected_type = QUERY_RESULT_TYPES.get(method)
        if expected_type is not None:
            validate_query_result(method, result)
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
