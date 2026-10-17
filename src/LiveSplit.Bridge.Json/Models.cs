#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveSplit.Bridge.Json;

[AttributeUsage(AttributeTargets.Property)]
public sealed class SchemaConstNullAttribute : Attribute { }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "method")]
[JsonDerivedType(typeof(GetTimerStateRequest), "get_timer_state")]
[JsonDerivedType(typeof(GetRunRequest), "get_run")]
[JsonDerivedType(typeof(GetAttemptRequest), "get_attempt")]
[JsonDerivedType(typeof(GetContextStateRequest), "get_context_state")]
[JsonDerivedType(typeof(GetCompletedCountRequest), "get_completed_count")]
[JsonDerivedType(typeof(TimerOperationRequest), "timer_operation")]
[JsonDerivedType(typeof(GameTimeOperationRequest), "game_time_operation")]
public abstract class RpcRequest
{
    [JsonRequired]
    public uint RequestId { get; set; }
}

public sealed class GetTimerStateRequest : RpcRequest { }
public sealed class GetRunRequest : RpcRequest { }
public sealed class GetAttemptRequest : RpcRequest { }
public sealed class GetContextStateRequest : RpcRequest { }
public sealed class GetCompletedCountRequest : RpcRequest { }

public sealed class TimerOperationRequest : RpcRequest
{
    [JsonRequired]
    public TimerOperationParams Params { get; set; } = new TimerStartParams();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(TimerStartParams), "start")]
[JsonDerivedType(typeof(TimerSplitParams), "split")]
[JsonDerivedType(typeof(TimerSkipParams), "skip")]
[JsonDerivedType(typeof(TimerUndoParams), "undo")]
[JsonDerivedType(typeof(TimerResetParams), "reset")]
[JsonDerivedType(typeof(TimerPauseParams), "pause")]
[JsonDerivedType(typeof(TimerResumeParams), "resume")]
public abstract class TimerOperationParams { }
public sealed class TimerStartParams : TimerOperationParams { }
public sealed class TimerSplitParams : TimerOperationParams { }
public sealed class TimerSkipParams : TimerOperationParams { }
public sealed class TimerUndoParams : TimerOperationParams { }
public sealed class TimerResetParams : TimerOperationParams { }
public sealed class TimerPauseParams : TimerOperationParams { }
public sealed class TimerResumeParams : TimerOperationParams { }

public sealed class GameTimeOperationRequest : RpcRequest
{
    [JsonRequired]
    public GameTimeOperationParams Params { get; set; } = new GameTimeInitializeParams();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(GameTimeInitializeParams), "initialize")]
[JsonDerivedType(typeof(GameTimeSetParams), "set")]
[JsonDerivedType(typeof(GameTimePauseParams), "pause")]
[JsonDerivedType(typeof(GameTimeResumeParams), "resume")]
public abstract class GameTimeOperationParams { }
public sealed class GameTimeInitializeParams : GameTimeOperationParams { }
public sealed class GameTimeSetParams : GameTimeOperationParams
{
    [JsonRequired]
    public long Ticks { get; set; }
}
public sealed class GameTimePauseParams : GameTimeOperationParams { }
public sealed class GameTimeResumeParams : GameTimeOperationParams { }

[JsonPolymorphic]
[JsonDerivedType(typeof(TimerStateResponse))]
[JsonDerivedType(typeof(RunStateResponse))]
[JsonDerivedType(typeof(AttemptStateResponse))]
[JsonDerivedType(typeof(ContextStateResponse))]
[JsonDerivedType(typeof(CompletedCountResponse))]
[JsonDerivedType(typeof(ControlSuccessResponse))]
[JsonDerivedType(typeof(ErrorResponse))]
public abstract class RpcResponse
{
    [JsonRequired]
    public uint RequestId { get; set; }

    [JsonRequired]
    public uint SessionId { get; set; }
}

public sealed class TimerStateResponse : RpcResponse
{
    [JsonRequired] public TimerState Result { get; set; } = new();
}
public sealed class RunStateResponse : RpcResponse
{
    [JsonRequired] public RunState Result { get; set; } = new();
}
public sealed class AttemptStateResponse : RpcResponse
{
    [JsonRequired] public AttemptState Result { get; set; } = new();
}
public sealed class ContextStateResponse : RpcResponse
{
    [JsonRequired] public ContextState Result { get; set; } = new();
}
public sealed class CompletedCountResponse : RpcResponse
{
    [JsonRequired] public CompletedCount Result { get; set; } = new();
}
public sealed class ControlSuccessResponse : RpcResponse
{
    [JsonRequired]
    [SchemaConstNull]
    public JsonNullValue Result { get; set; } = JsonNullValue.Value;
}
public sealed class ErrorResponse : RpcResponse
{
    [JsonRequired] public BridgeError Error { get; set; } = new();
}

[JsonConverter(typeof(JsonNullValueConverter))]
public sealed class JsonNullValue
{
    public static JsonNullValue Value { get; } = new();
    private JsonNullValue() { }
}

public sealed class JsonNullValueConverter : JsonConverter<JsonNullValue>
{
    public override bool HandleNull => true;
    public override JsonNullValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Null) throw new JsonException("Value must be null.");
        return JsonNullValue.Value;
    }
    public override void Write(Utf8JsonWriter writer, JsonNullValue value, JsonSerializerOptions options) => writer.WriteNullValue();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TimerStartedEvent), "timer_started")]
[JsonDerivedType(typeof(TimerSplitEvent), "timer_split")]
[JsonDerivedType(typeof(TimerSkippedEvent), "timer_skipped")]
[JsonDerivedType(typeof(TimerUndoEvent), "timer_undo")]
[JsonDerivedType(typeof(TimerResetEvent), "timer_reset")]
[JsonDerivedType(typeof(TimerPhaseChangedEvent), "timer_phase_changed")]
[JsonDerivedType(typeof(RunChangedEvent), "run_changed")]
[JsonDerivedType(typeof(ContextChangedEvent), "context_changed")]
public abstract class BridgeEvent
{
    [JsonRequired] public uint SessionId { get; set; }
    [JsonRequired] public uint EventSequence { get; set; }
}

public abstract class TimerBridgeEvent : BridgeEvent
{
    [JsonRequired] public TimerState TimerState { get; set; } = new();
}
public sealed class TimerStartedEvent : TimerBridgeEvent { }
public sealed class TimerSplitEvent : TimerBridgeEvent { }
public sealed class TimerSkippedEvent : TimerBridgeEvent { }
public sealed class TimerUndoEvent : TimerBridgeEvent { }
public sealed class TimerResetEvent : BridgeEvent
{
    [JsonRequired] public TimerState? TimerState { get; set; }
}
public sealed class TimerPhaseChangedEvent : TimerBridgeEvent { }

public abstract class StateChangedBridgeEvent : BridgeEvent
{
    [JsonRequired, SchemaConstNull] public JsonNullValue TimerState { get; set; } = JsonNullValue.Value;
}
public sealed class RunChangedEvent : StateChangedBridgeEvent { }
public sealed class ContextChangedEvent : StateChangedBridgeEvent { }

public enum TimerPhase { Unspecified, NotRunning, Running, Paused, Ended }
public enum TimingMethod { Unspecified, RealTime, GameTime }
public enum BridgeErrorCode { InvalidRequest, InvalidArgument, OperationFailed, InternalError, PermissionDenied }

public sealed class TimerState
{
    [JsonRequired] public TimerPhase Phase { get; set; }
    [JsonRequired] public int SplitIndex { get; set; }
    [JsonRequired] public long RealTimeTicks { get; set; }
    [JsonRequired] public long? GameTimeTicks { get; set; }
    [JsonRequired] public bool IsGameTimeInitialized { get; set; }
    [JsonRequired] public bool IsGameTimePaused { get; set; }
}

public sealed class TimeValue
{
    [JsonRequired] public long? RealTimeTicks { get; set; }
    [JsonRequired] public long? GameTimeTicks { get; set; }
}

public sealed class AttemptState
{
    [JsonRequired] public uint AttemptCount { get; set; }
    [JsonRequired] public List<AttemptSegment> Segments { get; set; } = new();
}
public sealed class AttemptSegment
{
    [JsonRequired] public uint Index { get; set; }
    [JsonRequired] public TimeValue SplitTime { get; set; } = new();
    [JsonRequired] public Dictionary<string, string> CustomVariables { get; set; } = new();
}
public sealed class CompletedCount
{
    [JsonRequired, JsonPropertyName("completed_count")] public uint Value { get; set; }
}
public sealed class ContextState
{
    [JsonRequired] public TimingMethod CurrentTimingMethod { get; set; }
    [JsonRequired] public string CurrentComparison { get; set; } = "";
    [JsonRequired] public Dictionary<string, string> CustomVariables { get; set; } = new();
}
public sealed class RunState
{
    [JsonRequired] public string GameName { get; set; } = "";
    [JsonRequired] public string CategoryName { get; set; } = "";
    [JsonRequired] public long OffsetTicks { get; set; }
    [JsonRequired] public string? FilePath { get; set; }
    [JsonRequired] public string? LayoutPath { get; set; }
    [JsonRequired] public RunMetadata? Metadata { get; set; }
    [JsonRequired] public List<string> Comparisons { get; set; } = new();
    [JsonRequired] public List<SegmentInfo> Segments { get; set; } = new();
    [JsonRequired] public byte[]? GameIcon { get; set; }
}
public sealed class SegmentInfo
{
    [JsonRequired] public uint Index { get; set; }
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public List<ComparisonTime> Comparisons { get; set; } = new();
    [JsonRequired] public TimeValue BestSegmentTime { get; set; } = new();
    [JsonRequired] public byte[]? Icon { get; set; }
}
public sealed class ComparisonTime
{
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public TimeValue Time { get; set; } = new();
}
public sealed class RunMetadata
{
    [JsonRequired] public string? RunId { get; set; }
    [JsonRequired] public string? PlatformName { get; set; }
    [JsonRequired] public string? RegionName { get; set; }
    [JsonRequired] public bool UsesEmulator { get; set; }
    [JsonRequired] public Dictionary<string, string> Variables { get; set; } = new();
}
public sealed class BridgeError
{
    [JsonRequired] public BridgeErrorCode Code { get; set; }
    [JsonRequired] public string Message { get; set; } = "";
}
