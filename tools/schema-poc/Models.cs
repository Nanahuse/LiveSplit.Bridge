using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

[AttributeUsage(AttributeTargets.Property)]
public sealed class SchemaConstNullAttribute : Attribute { }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "method")]
[JsonDerivedType(typeof(GetTimerStateRequest), "get_timer_state")]
[JsonDerivedType(typeof(TimerOperationRequest), "timer_operation")]
public abstract class RpcRequest
{
    [JsonRequired]
    public uint RequestId { get; set; }
}

public sealed class GetTimerStateRequest : RpcRequest { }

public sealed class TimerOperationRequest : RpcRequest
{
    [JsonRequired]
    public TimerOperationParams Params { get; set; } = new();
}

public sealed class TimerOperationParams
{
    [JsonRequired]
    public TimerOperation Operation { get; set; }
}

public enum TimerOperation { Start, Split, Skip, Undo, Reset, Pause, Resume }

[JsonPolymorphic]
[JsonDerivedType(typeof(TimerStateResponse))]
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
    [JsonRequired]
    public TimerState Result { get; set; } = new();
}

public sealed class ControlSuccessResponse : RpcResponse
{
    [JsonRequired]
    [SchemaConstNull]
    public NullResult Result { get; set; } = NullResult.Value;
}

[JsonConverter(typeof(NullResultConverter))]
public sealed class NullResult
{
    public static NullResult Value { get; } = new();
    private NullResult() { }
}

public sealed class NullResultConverter : JsonConverter<NullResult>
{
    public override bool HandleNull => true;

    public override NullResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Null)
        {
            throw new JsonException("Control result must be null.");
        }

        return NullResult.Value;
    }

    public override void Write(Utf8JsonWriter writer, NullResult value, JsonSerializerOptions options)
        => writer.WriteNullValue();
}

public sealed class ErrorResponse : RpcResponse
{
    [JsonRequired]
    public BridgeError Error { get; set; } = new();
}

public sealed class TimerState
{
    [JsonRequired] public string Phase { get; set; } = "not_running";
    [JsonRequired] public int SplitIndex { get; set; }
    [JsonRequired] public long RealTimeTicks { get; set; }
}

public sealed class BridgeError
{
    [JsonRequired] public string Code { get; set; } = "";
    [JsonRequired] public string Message { get; set; } = "";
}

public sealed class NullabilityProbe
{
    [JsonRequired] public string RequiredText { get; set; } = "";
    [JsonRequired] public string? NullableText { get; set; }
    [JsonRequired] public List<string> Labels { get; set; } = new();
    [JsonRequired] public Dictionary<string, string> Tags { get; set; } = new();
    public byte[]? Image { get; set; }
}

public sealed class SessionIdProbe
{
    [JsonRequired] public uint SessionId { get; set; }
}
