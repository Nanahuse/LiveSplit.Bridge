from livesplit.bridge.v1 import common_pb2 as _common_pb2
from google.protobuf import descriptor as _descriptor
from google.protobuf import message as _message
from collections.abc import Mapping as _Mapping
from typing import ClassVar as _ClassVar, Optional as _Optional, Union as _Union

DESCRIPTOR: _descriptor.FileDescriptor

class Request(_message.Message):
    __slots__ = ("protocol_version", "request_id", "attach", "get_snapshot", "timer_operation", "game_time_operation")
    PROTOCOL_VERSION_FIELD_NUMBER: _ClassVar[int]
    REQUEST_ID_FIELD_NUMBER: _ClassVar[int]
    ATTACH_FIELD_NUMBER: _ClassVar[int]
    GET_SNAPSHOT_FIELD_NUMBER: _ClassVar[int]
    TIMER_OPERATION_FIELD_NUMBER: _ClassVar[int]
    GAME_TIME_OPERATION_FIELD_NUMBER: _ClassVar[int]
    protocol_version: int
    request_id: int
    attach: AttachRequest
    get_snapshot: GetSnapshotRequest
    timer_operation: TimerOperationRequest
    game_time_operation: GameTimeOperationRequest
    def __init__(self, protocol_version: _Optional[int] = ..., request_id: _Optional[int] = ..., attach: _Optional[_Union[AttachRequest, _Mapping]] = ..., get_snapshot: _Optional[_Union[GetSnapshotRequest, _Mapping]] = ..., timer_operation: _Optional[_Union[TimerOperationRequest, _Mapping]] = ..., game_time_operation: _Optional[_Union[GameTimeOperationRequest, _Mapping]] = ...) -> None: ...

class Response(_message.Message):
    __slots__ = ("protocol_version", "request_id", "error", "attach", "get_snapshot", "operation")
    PROTOCOL_VERSION_FIELD_NUMBER: _ClassVar[int]
    REQUEST_ID_FIELD_NUMBER: _ClassVar[int]
    ERROR_FIELD_NUMBER: _ClassVar[int]
    ATTACH_FIELD_NUMBER: _ClassVar[int]
    GET_SNAPSHOT_FIELD_NUMBER: _ClassVar[int]
    OPERATION_FIELD_NUMBER: _ClassVar[int]
    protocol_version: int
    request_id: int
    error: _common_pb2.BridgeError
    attach: AttachResponse
    get_snapshot: GetSnapshotResponse
    operation: _common_pb2.OperationResponse
    def __init__(self, protocol_version: _Optional[int] = ..., request_id: _Optional[int] = ..., error: _Optional[_Union[_common_pb2.BridgeError, _Mapping]] = ..., attach: _Optional[_Union[AttachResponse, _Mapping]] = ..., get_snapshot: _Optional[_Union[GetSnapshotResponse, _Mapping]] = ..., operation: _Optional[_Union[_common_pb2.OperationResponse, _Mapping]] = ...) -> None: ...

class AttachRequest(_message.Message):
    __slots__ = ()
    def __init__(self) -> None: ...

class AttachResponse(_message.Message):
    __slots__ = ("session_id", "snapshot")
    SESSION_ID_FIELD_NUMBER: _ClassVar[int]
    SNAPSHOT_FIELD_NUMBER: _ClassVar[int]
    session_id: int
    snapshot: _common_pb2.TimerSnapshot
    def __init__(self, session_id: _Optional[int] = ..., snapshot: _Optional[_Union[_common_pb2.TimerSnapshot, _Mapping]] = ...) -> None: ...

class GetSnapshotRequest(_message.Message):
    __slots__ = ()
    def __init__(self) -> None: ...

class GetSnapshotResponse(_message.Message):
    __slots__ = ("snapshot",)
    SNAPSHOT_FIELD_NUMBER: _ClassVar[int]
    snapshot: _common_pb2.TimerSnapshot
    def __init__(self, snapshot: _Optional[_Union[_common_pb2.TimerSnapshot, _Mapping]] = ...) -> None: ...

class TimerOperationRequest(_message.Message):
    __slots__ = ("operation",)
    OPERATION_FIELD_NUMBER: _ClassVar[int]
    operation: _common_pb2.TimerOperationType
    def __init__(self, operation: _Optional[_Union[_common_pb2.TimerOperationType, str]] = ...) -> None: ...

class GameTimeOperationRequest(_message.Message):
    __slots__ = ("operation", "ticks")
    OPERATION_FIELD_NUMBER: _ClassVar[int]
    TICKS_FIELD_NUMBER: _ClassVar[int]
    operation: _common_pb2.GameTimeOperationType
    ticks: int
    def __init__(self, operation: _Optional[_Union[_common_pb2.GameTimeOperationType, str]] = ..., ticks: _Optional[int] = ...) -> None: ...
