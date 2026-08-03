from google.protobuf.internal import enum_type_wrapper as _enum_type_wrapper
from google.protobuf import descriptor as _descriptor
from google.protobuf import message as _message
from collections.abc import Mapping as _Mapping
from typing import ClassVar as _ClassVar, Optional as _Optional, Union as _Union

DESCRIPTOR: _descriptor.FileDescriptor

class TimerPhase(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    TIMER_PHASE_UNSPECIFIED: _ClassVar[TimerPhase]
    NOT_RUNNING: _ClassVar[TimerPhase]
    STARTING: _ClassVar[TimerPhase]
    RUNNING: _ClassVar[TimerPhase]
    PAUSED: _ClassVar[TimerPhase]
    ENDED: _ClassVar[TimerPhase]

class TimerOperationType(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    TIMER_OPERATION_UNSPECIFIED: _ClassVar[TimerOperationType]
    TIMER_START: _ClassVar[TimerOperationType]
    TIMER_SPLIT: _ClassVar[TimerOperationType]
    TIMER_SKIP: _ClassVar[TimerOperationType]
    TIMER_UNDO: _ClassVar[TimerOperationType]
    TIMER_RESET: _ClassVar[TimerOperationType]
    TIMER_PAUSE: _ClassVar[TimerOperationType]
    TIMER_RESUME: _ClassVar[TimerOperationType]

class GameTimeOperationType(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    GAME_TIME_OPERATION_UNSPECIFIED: _ClassVar[GameTimeOperationType]
    INITIALIZE: _ClassVar[GameTimeOperationType]
    SET: _ClassVar[GameTimeOperationType]
    GAME_TIME_PAUSE: _ClassVar[GameTimeOperationType]
    GAME_TIME_RESUME: _ClassVar[GameTimeOperationType]

class BridgeEventType(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    BRIDGE_EVENT_UNSPECIFIED: _ClassVar[BridgeEventType]
    EVENT_TIMER_STARTED: _ClassVar[BridgeEventType]
    EVENT_TIMER_SPLIT: _ClassVar[BridgeEventType]
    EVENT_TIMER_SKIPPED: _ClassVar[BridgeEventType]
    EVENT_TIMER_UNDO: _ClassVar[BridgeEventType]
    EVENT_TIMER_RESET: _ClassVar[BridgeEventType]
    EVENT_TIMER_PAUSED: _ClassVar[BridgeEventType]
    EVENT_TIMER_RESUMED: _ClassVar[BridgeEventType]
    EVENT_GAME_TIME_INITIALIZED: _ClassVar[BridgeEventType]
    EVENT_GAME_TIME_SET: _ClassVar[BridgeEventType]
    EVENT_GAME_TIME_PAUSED: _ClassVar[BridgeEventType]
    EVENT_GAME_TIME_RESUMED: _ClassVar[BridgeEventType]
    EVENT_RUN_CHANGED: _ClassVar[BridgeEventType]
    EVENT_STATE_SNAPSHOT: _ClassVar[BridgeEventType]
TIMER_PHASE_UNSPECIFIED: TimerPhase
NOT_RUNNING: TimerPhase
STARTING: TimerPhase
RUNNING: TimerPhase
PAUSED: TimerPhase
ENDED: TimerPhase
TIMER_OPERATION_UNSPECIFIED: TimerOperationType
TIMER_START: TimerOperationType
TIMER_SPLIT: TimerOperationType
TIMER_SKIP: TimerOperationType
TIMER_UNDO: TimerOperationType
TIMER_RESET: TimerOperationType
TIMER_PAUSE: TimerOperationType
TIMER_RESUME: TimerOperationType
GAME_TIME_OPERATION_UNSPECIFIED: GameTimeOperationType
INITIALIZE: GameTimeOperationType
SET: GameTimeOperationType
GAME_TIME_PAUSE: GameTimeOperationType
GAME_TIME_RESUME: GameTimeOperationType
BRIDGE_EVENT_UNSPECIFIED: BridgeEventType
EVENT_TIMER_STARTED: BridgeEventType
EVENT_TIMER_SPLIT: BridgeEventType
EVENT_TIMER_SKIPPED: BridgeEventType
EVENT_TIMER_UNDO: BridgeEventType
EVENT_TIMER_RESET: BridgeEventType
EVENT_TIMER_PAUSED: BridgeEventType
EVENT_TIMER_RESUMED: BridgeEventType
EVENT_GAME_TIME_INITIALIZED: BridgeEventType
EVENT_GAME_TIME_SET: BridgeEventType
EVENT_GAME_TIME_PAUSED: BridgeEventType
EVENT_GAME_TIME_RESUMED: BridgeEventType
EVENT_RUN_CHANGED: BridgeEventType
EVENT_STATE_SNAPSHOT: BridgeEventType

class TimerSnapshot(_message.Message):
    __slots__ = ("state_revision", "session_id", "event_sequence", "phase", "split_index", "split_count", "real_time_ticks", "game_time_ticks", "is_paused", "is_game_time_initialized")
    STATE_REVISION_FIELD_NUMBER: _ClassVar[int]
    SESSION_ID_FIELD_NUMBER: _ClassVar[int]
    EVENT_SEQUENCE_FIELD_NUMBER: _ClassVar[int]
    PHASE_FIELD_NUMBER: _ClassVar[int]
    SPLIT_INDEX_FIELD_NUMBER: _ClassVar[int]
    SPLIT_COUNT_FIELD_NUMBER: _ClassVar[int]
    REAL_TIME_TICKS_FIELD_NUMBER: _ClassVar[int]
    GAME_TIME_TICKS_FIELD_NUMBER: _ClassVar[int]
    IS_PAUSED_FIELD_NUMBER: _ClassVar[int]
    IS_GAME_TIME_INITIALIZED_FIELD_NUMBER: _ClassVar[int]
    state_revision: int
    session_id: int
    event_sequence: int
    phase: TimerPhase
    split_index: int
    split_count: int
    real_time_ticks: int
    game_time_ticks: int
    is_paused: bool
    is_game_time_initialized: bool
    def __init__(self, state_revision: _Optional[int] = ..., session_id: _Optional[int] = ..., event_sequence: _Optional[int] = ..., phase: _Optional[_Union[TimerPhase, str]] = ..., split_index: _Optional[int] = ..., split_count: _Optional[int] = ..., real_time_ticks: _Optional[int] = ..., game_time_ticks: _Optional[int] = ..., is_paused: _Optional[bool] = ..., is_game_time_initialized: _Optional[bool] = ...) -> None: ...

class OperationResponse(_message.Message):
    __slots__ = ("success", "message", "snapshot")
    SUCCESS_FIELD_NUMBER: _ClassVar[int]
    MESSAGE_FIELD_NUMBER: _ClassVar[int]
    SNAPSHOT_FIELD_NUMBER: _ClassVar[int]
    success: bool
    message: str
    snapshot: TimerSnapshot
    def __init__(self, success: _Optional[bool] = ..., message: _Optional[str] = ..., snapshot: _Optional[_Union[TimerSnapshot, _Mapping]] = ...) -> None: ...

class BridgeError(_message.Message):
    __slots__ = ("code", "message")
    CODE_FIELD_NUMBER: _ClassVar[int]
    MESSAGE_FIELD_NUMBER: _ClassVar[int]
    code: int
    message: str
    def __init__(self, code: _Optional[int] = ..., message: _Optional[str] = ...) -> None: ...

class BridgeEvent(_message.Message):
    __slots__ = ("session_id", "event_sequence", "type", "snapshot", "description")
    SESSION_ID_FIELD_NUMBER: _ClassVar[int]
    EVENT_SEQUENCE_FIELD_NUMBER: _ClassVar[int]
    TYPE_FIELD_NUMBER: _ClassVar[int]
    SNAPSHOT_FIELD_NUMBER: _ClassVar[int]
    DESCRIPTION_FIELD_NUMBER: _ClassVar[int]
    session_id: int
    event_sequence: int
    type: BridgeEventType
    snapshot: TimerSnapshot
    description: str
    def __init__(self, session_id: _Optional[int] = ..., event_sequence: _Optional[int] = ..., type: _Optional[_Union[BridgeEventType, str]] = ..., snapshot: _Optional[_Union[TimerSnapshot, _Mapping]] = ..., description: _Optional[str] = ...) -> None: ...
