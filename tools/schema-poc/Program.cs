using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true,
};
jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));

var exporterOptions = new JsonSchemaExporterOptions
{
    TreatNullObliviousAsNonNullable = true,
    TransformSchemaNode = (context, schema) =>
    {
        if (context.PropertyInfo?.Name == "session_id" && schema is JsonObject sessionIdSchema)
        {
            sessionIdSchema["minimum"] = 1;
            sessionIdSchema["maximum"] = uint.MaxValue;
        }

        var attributeProvider = context.PropertyInfo?.AttributeProvider as ICustomAttributeProvider;
        if (attributeProvider?.GetCustomAttributes(inherit: true).OfType<SchemaConstNullAttribute>().Any() == true)
        {
            if (schema is JsonObject resultSchema)
            {
                resultSchema["const"] = null;
            }
            else
            {
                schema = new JsonObject { ["const"] = null };
            }
        }

        return schema;
    },
};

var outputDirectory = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outputDirectory);

Console.WriteLine("Export Request");
WriteSchema<RpcRequest>("rpc-request.schema.json");
Console.WriteLine("Export Response");
WriteSchema<RpcResponse>("rpc-response.schema.json");
Console.WriteLine("Export nullability probe");
WriteSchema<NullabilityProbe>("nullability-probe.schema.json");
Console.WriteLine("Export session id probe");
WriteSchema<SessionIdProbe>("session-id-probe.schema.json");

var getTimer = new GetTimerStateRequest { RequestId = 1 };
var timerOperation = new TimerOperationRequest
{
    RequestId = 2,
    Params = new TimerOperationParams { Operation = TimerOperation.Split },
};
var queryResponse = new TimerStateResponse
{
    RequestId = 1,
    SessionId = 123,
    Result = new TimerState { Phase = "running", SplitIndex = 4, RealTimeTicks = 9000 },
};
var controlResponse = new ControlSuccessResponse { RequestId = 2, SessionId = 123 };
var errorResponse = new ErrorResponse
{
    RequestId = 3,
    SessionId = 123,
    Error = new BridgeError { Code = "operation_failed", Message = "The operation failed." },
};
var serializedGetTimer = JsonSerializer.Serialize<RpcRequest>(getTimer, jsonOptions);
var serializedRequest = JsonSerializer.Serialize<RpcRequest>(timerOperation, jsonOptions);
var serializedQuery = JsonSerializer.Serialize<RpcResponse>(queryResponse, jsonOptions);
var serializedControl = JsonSerializer.Serialize<RpcResponse>(controlResponse, jsonOptions);
var serializedError = JsonSerializer.Serialize<RpcResponse>(errorResponse, jsonOptions);

var outOfOrderOptions = new JsonSerializerOptions(jsonOptions) { AllowOutOfOrderMetadataProperties = false };
var probeJson = "{\"required_text\":\"ok\",\"nullable_text\":null,\"labels\":[null],\"tags\":{\"x\":null},\"image\":null}";
var validations = new[]
{
    Check("request: get_timer_state deserializes to its derived type", () => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"get_timer_state\",\"request_id\":1}", jsonOptions) is GetTimerStateRequest),
    Check("request: timer_operation deserializes to its derived type", () => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"timer_operation\",\"request_id\":2,\"params\":{\"operation\":\"split\"}}", jsonOptions) is TimerOperationRequest),
    Check("request: request_id before method accepted with AllowOutOfOrderMetadataProperties", () => JsonSerializer.Deserialize<RpcRequest>("{\"request_id\":1,\"method\":\"get_timer_state\"}", jsonOptions) is GetTimerStateRequest),
    Check("request: request_id before method rejected when AllowOutOfOrderMetadataProperties is false", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"request_id\":1,\"method\":\"get_timer_state\"}", outOfOrderOptions))),
    Check("request: unknown method rejected", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"request_id\":1,\"method\":\"unknown\"}", jsonOptions))),
    Check("request: params on get_timer_state rejected as unmapped", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"get_timer_state\",\"request_id\":1,\"params\":{}}", jsonOptions))),
    Check("request: get_timer_state serialization emits method without params", () => JsonDocument.Parse(serializedGetTimer).RootElement.GetProperty("method").GetString() == "get_timer_state" && !JsonDocument.Parse(serializedGetTimer).RootElement.TryGetProperty("params", out _)),
    Check("request: missing timer_operation params rejected", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"timer_operation\",\"request_id\":1}", jsonOptions))),
    Check("request: null timer_operation params rejected", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"timer_operation\",\"request_id\":1,\"params\":null}", jsonOptions))),
    Check("request: missing operation rejected", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"timer_operation\",\"request_id\":1,\"params\":{}}", jsonOptions))),
    Check("request: invalid operation string rejected", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"timer_operation\",\"request_id\":1,\"params\":{\"operation\":\"teleport\"}}", jsonOptions))),
    Check("request: numeric operation rejected", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"timer_operation\",\"request_id\":1,\"params\":{\"operation\":1}}", jsonOptions))),
    Check("request: unknown property rejected by UnmappedMemberHandling.Disallow", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"get_timer_state\",\"request_id\":1,\"extra\":true}", jsonOptions))),
    Check("request: timer_operation params with unknown property rejected", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"timer_operation\",\"request_id\":1,\"params\":{\"operation\":\"split\",\"extra\":true}}", jsonOptions))),
    Check("request: serialization writes method discriminator and params", () => JsonDocument.Parse(serializedRequest).RootElement.GetProperty("method").GetString() == "timer_operation" && JsonDocument.Parse(serializedRequest).RootElement.GetProperty("params").GetProperty("operation").GetString() == "split"),
    Check("response: Query serializes with result object and no discriminator", () => HasNoDiscriminator(serializedQuery) && JsonDocument.Parse(serializedQuery).RootElement.GetProperty("result").ValueKind == JsonValueKind.Object),
    Check("response: Control serializes result:null and no discriminator", () => HasNullResult(serializedControl) && HasNoDiscriminator(serializedControl)),
    Check("response: Control result converter accepts only null", () => JsonSerializer.Deserialize<NullResult>("null", jsonOptions) is not null && Rejects(() => JsonSerializer.Deserialize<NullResult>("true", jsonOptions))),
    Check("response: Error serializes error only and no discriminator", () => HasErrorOnly(serializedError) && HasNoDiscriminator(serializedError)),
    Check("required non-null property: omission rejected", () => Rejects(() => JsonSerializer.Deserialize<NullabilityProbe>("{\"nullable_text\":null,\"labels\":[],\"tags\":{}}", jsonOptions))),
    Check("required non-null property: null rejected", () => Rejects(() => JsonSerializer.Deserialize<NullabilityProbe>("{\"required_text\":null,\"nullable_text\":null,\"labels\":[],\"tags\":{}}", jsonOptions))),
    Check("required nullable property: null accepted; list elements and dictionary values null accepted", () => JsonSerializer.Deserialize<NullabilityProbe>(probeJson, jsonOptions) is { NullableText: null, Labels.Count: 1, Tags.Count: 1 }),
    Check("root null accepted by System.Text.Json", () => JsonSerializer.Deserialize<NullabilityProbe>("null", jsonOptions) is null),
    Check("unknown property rejected for ordinary DTO", () => Rejects(() => JsonSerializer.Deserialize<NullabilityProbe>("{\"required_text\":\"ok\",\"nullable_text\":null,\"labels\":[],\"tags\":{},\"unknown\":1}", jsonOptions))),
    Check("uint SessionId rejects values outside uint range", () => Rejects(() => JsonSerializer.Deserialize<SessionIdProbe>("{\"session_id\":4294967296}", jsonOptions))),
    Check("uint SessionId accepts zero (nonzero is schema-only output constraint)", () => JsonSerializer.Deserialize<SessionIdProbe>("{\"session_id\":0}", jsonOptions)?.SessionId == 0),
};
File.WriteAllLines(Path.Combine(outputDirectory, "rpc-validation.txt"), validations);
Console.WriteLine(string.Join(Environment.NewLine, validations));
if (validations.Any(result => result.StartsWith("FAIL ", StringComparison.Ordinal)))
{
    Environment.ExitCode = 1;
}

void WriteSchema<T>(string fileName)
{
    JsonNode? node;
    try
    {
        node = jsonOptions.GetJsonSchemaAsNode(typeof(T), exporterOptions);
    }
    catch (Exception exception)
    {
        Console.WriteLine($"Schema export failed for {typeof(T).FullName}: {exception.GetType().FullName}: {exception.Message}");
        Console.WriteLine(exception.StackTrace);
        throw;
    }
    if (node is JsonObject root)
    {
        root["$schema"] = "https://json-schema.org/draft/2020-12/schema";
    }
    File.WriteAllText(Path.Combine(outputDirectory, fileName), node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
}

static string Check(string name, Func<bool> check)
{
    try { return $"{(check() ? "PASS" : "FAIL")} {name}"; }
    catch (Exception exception) { return $"FAIL {name} ({exception.GetType().Name}: {exception.Message})"; }
}

static bool Rejects(Action action)
{
    try { action(); return false; }
    catch (JsonException) { return true; }
    catch (NotSupportedException) { return true; }
}

static bool HasNoDiscriminator(string json)
{
    using var document = JsonDocument.Parse(json);
    return !document.RootElement.TryGetProperty("$type", out _) && !document.RootElement.TryGetProperty("method", out _);
}

static bool HasNullResult(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Null;
}

static bool HasErrorOnly(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.TryGetProperty("error", out _) && !document.RootElement.TryGetProperty("result", out _);
}
