#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
#if SCHEMA_GENERATION
using Json.Schema.Generation;
using Range = System.ComponentModel.DataAnnotations.RangeAttribute;
using PngImage = string;
#else
using PngImage = byte[];
#endif

namespace LiveSplit.Bridge.Json;

public enum QueryMethod
{
    [JsonStringEnumMemberName("get_timer_state")] GetTimerState,
    [JsonStringEnumMemberName("get_run")] GetRun,
    [JsonStringEnumMemberName("get_run_icons")] GetRunIcons,
    [JsonStringEnumMemberName("get_attempt")] GetAttempt,
    [JsonStringEnumMemberName("get_context_state")] GetContextState,
    [JsonStringEnumMemberName("get_completed_count")] GetCompletedCount,
}

public enum TimerOperation
{
    [JsonStringEnumMemberName("start")] Start,
    [JsonStringEnumMemberName("split")] Split,
    [JsonStringEnumMemberName("skip")] Skip,
    [JsonStringEnumMemberName("undo")] Undo,
    [JsonStringEnumMemberName("reset")] Reset,
    [JsonStringEnumMemberName("pause")] Pause,
    [JsonStringEnumMemberName("resume")] Resume,
}

public enum GameControlOperation
{
    [JsonStringEnumMemberName("initialize")] Initialize,
    [JsonStringEnumMemberName("pause")] Pause,
    [JsonStringEnumMemberName("resume")] Resume,
}

public enum TimerEventKind
{
    [JsonStringEnumMemberName("timer_started")] Started,
    [JsonStringEnumMemberName("timer_split")] Split,
    [JsonStringEnumMemberName("timer_skipped")] Skipped,
    [JsonStringEnumMemberName("timer_undo")] Undo,
    [JsonStringEnumMemberName("timer_phase_changed")] PhaseChanged,
}

public enum ChangedEventKind
{
    [JsonStringEnumMemberName("run_changed")] RunChanged,
    [JsonStringEnumMemberName("context_changed")] ContextChanged,
}

public enum TimerPhase
{
    [JsonStringEnumMemberName("unspecified")] Unspecified,
    [JsonStringEnumMemberName("not_running")] NotRunning,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("paused")] Paused,
    [JsonStringEnumMemberName("ended")] Ended,
}

public enum TimingMethod
{
    [JsonStringEnumMemberName("unspecified")] Unspecified,
    [JsonStringEnumMemberName("real_time")] RealTime,
    [JsonStringEnumMemberName("game_time")] GameTime,
}

public enum BridgeErrorCode
{
    [JsonStringEnumMemberName("invalid_request")] InvalidRequest,
    [JsonStringEnumMemberName("invalid_argument")] InvalidArgument,
    [JsonStringEnumMemberName("operation_failed")] OperationFailed,
    [JsonStringEnumMemberName("internal_error")] InternalError,
    [JsonStringEnumMemberName("permission_denied")] PermissionDenied,
}

#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public abstract class CorrelatedMessage
{
#if SCHEMA_GENERATION
    [Range(typeof(uint), "0", "4294967295")]
#endif
    [JsonRequired] public uint RequestId { get; set; }
}

public abstract class RpcRequest : CorrelatedMessage { }

public sealed class QueryRequest : RpcRequest
{
    [JsonRequired] public QueryMethod Method { get; set; }
}

public sealed class TimerRequest : RpcRequest
{
    public const string MethodName = "timer_operation";
#if SCHEMA_GENERATION
    [Const(MethodName)]
#endif
    [JsonRequired] public string Method
    {
        get => MethodName;
        set { if (value != MethodName) throw new JsonException("Invalid timer method."); }
    }
    [JsonRequired] public TimerParams Params { get; set; } = new();
}

#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public sealed class TimerParams
{
    [JsonRequired] public TimerOperation Operation { get; set; }
}

public sealed class GameTimeRequest<T> : RpcRequest where T : GameTimeParams
{
    public const string MethodName = "game_time_operation";
    private T arguments = default!;
#if SCHEMA_GENERATION
    [Const(MethodName)]
#endif
    [JsonRequired] public string Method
    {
        get => MethodName;
        set { if (value != MethodName) throw new JsonException("Invalid game-time method."); }
    }
#if SCHEMA_GENERATION
    [Nullable(false)]
#endif
    [JsonRequired] public T Params
    {
        get => arguments ?? throw new JsonException("Game-time params are required.");
        set => arguments = value ?? throw new JsonException("Game-time params cannot be null.");
    }
}

#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public abstract class GameTimeParams { }

public sealed class GameControlParams : GameTimeParams
{
    [JsonRequired] public GameControlOperation Operation { get; set; }
}

public sealed class GameSetParams : GameTimeParams
{
    public const string OperationName = "set";
#if SCHEMA_GENERATION
    [Const(OperationName)]
#endif
    [JsonRequired] public string Operation
    {
        get => OperationName;
        set { if (value != OperationName) throw new JsonException("Invalid game-time set operation."); }
    }
#if SCHEMA_GENERATION
    [Range(typeof(long), "-9223372036854775808", "9223372036854775807")]
#endif
    [JsonRequired] public long Ticks { get; set; }
}

public abstract class RpcResponse : CorrelatedMessage
{
#if SCHEMA_GENERATION
    [Range(typeof(uint), "1", "4294967295")]
#endif
    [JsonRequired] public uint SessionId { get; set; }
}

public sealed class Success<T> : RpcResponse where T : class
{
    private T result = default!;
#if SCHEMA_GENERATION
    [Nullable(false)]
#endif
    [JsonRequired] public T Result
    {
        get => result ?? throw new JsonException("A success result is required.");
        set => result = value ?? throw new JsonException("A success result cannot be null.");
    }
}

public sealed class ControlSuccessResponse : RpcResponse
{
#if SCHEMA_GENERATION
    [Const((string)null!)]
#endif
    [JsonRequired] public object? Result
    {
        get => null;
        set { if (value != null) throw new JsonException("Control result must be null."); }
    }
}

public sealed class ErrorResponse : RpcResponse
{
    [JsonRequired] public BridgeError Error { get; set; } = new();
}

#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public abstract class BridgeEvent
{
#if SCHEMA_GENERATION
    [Range(typeof(uint), "1", "4294967295")]
#endif
    [JsonRequired] public uint SessionId { get; set; }
#if SCHEMA_GENERATION
    [Range(typeof(uint), "1", "4294967295")]
#endif
    [JsonRequired] public uint EventSequence { get; set; }
}

public sealed class TimerEvent : BridgeEvent
{
    [JsonRequired] public TimerEventKind Type { get; set; }
    [JsonRequired] public TimerState TimerState { get; set; } = new();
}

public sealed class ResetEvent : BridgeEvent
{
    public const string TypeName = "timer_reset";
#if SCHEMA_GENERATION
    [Const(TypeName)]
#endif
    [JsonRequired] public string Type
    {
        get => TypeName;
        set { if (value != TypeName) throw new JsonException("Invalid reset event type."); }
    }
    [JsonRequired] public TimerState? TimerState { get; set; }
}

public sealed class ChangedEvent : BridgeEvent
{
    [JsonRequired] public ChangedEventKind Type { get; set; }
#if SCHEMA_GENERATION
    [Const((string)null!)]
#endif
    [JsonRequired] public object? TimerState
    {
        get => null;
        set { if (value != null) throw new JsonException("Changed event state must be null."); }
    }
}

#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public sealed class TimerState
{
    [JsonRequired] public TimerPhase Phase { get; set; }
#if SCHEMA_GENERATION
    [Range(int.MinValue, int.MaxValue)]
#endif
    [JsonRequired] public int SplitIndex { get; set; }
#if SCHEMA_GENERATION
    [Range(typeof(long), "-9223372036854775808", "9223372036854775807")]
#endif
    [JsonRequired] public long RealTimeTicks { get; set; }
#if SCHEMA_GENERATION
    [Range(typeof(long), "-9223372036854775808", "9223372036854775807")]
#endif
    [JsonRequired] public long? GameTimeTicks { get; set; }
    [JsonRequired] public bool IsGameTimeInitialized { get; set; }
    [JsonRequired] public bool IsGameTimePaused { get; set; }
}

#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public sealed class TimeValue
{
#if SCHEMA_GENERATION
    [Range(typeof(long), "-9223372036854775808", "9223372036854775807")]
#endif
    [JsonRequired] public long? RealTimeTicks { get; set; }
#if SCHEMA_GENERATION
    [Range(typeof(long), "-9223372036854775808", "9223372036854775807")]
#endif
    [JsonRequired] public long? GameTimeTicks { get; set; }
}

#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public sealed class AttemptState
{
#if SCHEMA_GENERATION
    [Range(typeof(uint), "0", "4294967295")]
#endif
    [JsonRequired] public uint AttemptCount { get; set; }
    [JsonRequired] public List<AttemptSegment> Segments { get; set; } = new();
}
#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public sealed class AttemptSegment
{
#if SCHEMA_GENERATION
    [Range(typeof(uint), "0", "4294967295")]
#endif
    [JsonRequired] public uint Index { get; set; }
    [JsonRequired] public TimeValue SplitTime { get; set; } = new();
    [JsonRequired] public Dictionary<string, string> CustomVariables { get; set; } = new();
}
#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public sealed class CompletedCount
{
#if SCHEMA_GENERATION
    [Range(typeof(uint), "0", "4294967295")]
#endif
    [JsonRequired, JsonPropertyName("completed_count")] public uint Value { get; set; }
}
#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public sealed class ContextState
{
    [JsonRequired] public TimingMethod CurrentTimingMethod { get; set; }
    [JsonRequired] public string CurrentComparison { get; set; } = "";
    [JsonRequired] public Dictionary<string, string> CustomVariables { get; set; } = new();
}
#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public sealed class RunState
{
    [JsonRequired] public string GameName { get; set; } = "";
    [JsonRequired] public string CategoryName { get; set; } = "";
#if SCHEMA_GENERATION
    [Range(typeof(long), "-9223372036854775808", "9223372036854775807")]
#endif
    [JsonRequired] public long OffsetTicks { get; set; }
    [JsonRequired] public string? FilePath { get; set; }
    [JsonRequired] public string? LayoutPath { get; set; }
    [JsonRequired] public RunMetadata? Metadata { get; set; }
    [JsonRequired] public List<string> Comparisons { get; set; } = new();
    [JsonRequired] public List<SegmentInfo> Segments { get; set; } = new();
}
#if SCHEMA_GENERATION
[AdditionalProperties(false)]
// Generation 7.3 applies the nested Nullable attribute to the collection too.
// This standard conditional excludes null collections while retaining null items.
[If(nameof(RunIcons.SegmentIcons), (string)null!, "icons_array")]
#endif
public sealed class RunIcons
{
#if SCHEMA_GENERATION
    [Description("Base64-encoded PNG image; null when absent.")]
#endif
    [JsonRequired] public PngImage? GameIcon { get; set; }
#if SCHEMA_GENERATION
    [Nullable(true, GenericParameter = 0)]
    [Nullable(false, ConditionGroup = "icons_array")]
    [Description("Segment icons in Run order, as Base64-encoded PNG images; null entries when absent.")]
#endif
    [JsonRequired] public List<PngImage?> SegmentIcons { get; set; } = new();
}
#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public sealed class SegmentInfo
{
#if SCHEMA_GENERATION
    [Range(typeof(uint), "0", "4294967295")]
#endif
    [JsonRequired] public uint Index { get; set; }
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public List<ComparisonTime> Comparisons { get; set; } = new();
    [JsonRequired] public TimeValue BestSegmentTime { get; set; } = new();
}
#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public sealed class ComparisonTime
{
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public TimeValue Time { get; set; } = new();
}
#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public sealed class RunMetadata
{
    [JsonRequired] public string? RunId { get; set; }
    [JsonRequired] public string? PlatformName { get; set; }
    [JsonRequired] public string? RegionName { get; set; }
    [JsonRequired] public bool UsesEmulator { get; set; }
    [JsonRequired] public Dictionary<string, string> Variables { get; set; } = new();
}
#if SCHEMA_GENERATION
[AdditionalProperties(false)]
#endif
public sealed class BridgeError
{
    [JsonRequired] public BridgeErrorCode Code { get; set; }
    [JsonRequired] public string Message { get; set; } = "";
}
