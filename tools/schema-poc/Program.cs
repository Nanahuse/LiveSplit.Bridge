using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";

var jsonOptions = BridgeJsonOptions.Create();

var exporterOptions = new JsonSchemaExporterOptions
{
    TreatNullObliviousAsNonNullable = true,
    TransformSchemaNode = (context, schema) =>
    {
        var property = context.PropertyInfo;
        if (property != null && schema is JsonObject objectSchema)
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type == typeof(uint))
            {
                objectSchema["minimum"] = 0;
                objectSchema["maximum"] = uint.MaxValue;
            }
            else if (type == typeof(int))
            {
                objectSchema["minimum"] = int.MinValue;
                objectSchema["maximum"] = int.MaxValue;
            }
            else if (type == typeof(long))
            {
                objectSchema["minimum"] = long.MinValue;
                objectSchema["maximum"] = long.MaxValue;
            }

            if (property.Name is "session_id" or "event_sequence")
            {
                objectSchema["minimum"] = 1;
            }

            if (property.PropertyType == typeof(byte[]))
            {
                objectSchema["contentEncoding"] = "base64";
                objectSchema["contentMediaType"] = "image/png";
            }
        }

        var attributeProvider = property?.AttributeProvider as ICustomAttributeProvider;
        if (attributeProvider?.GetCustomAttributes(inherit: true).OfType<SchemaConstNullAttribute>().Any() == true)
        {
            if (schema is JsonObject resultSchema) resultSchema["const"] = null;
            else schema = new JsonObject { ["const"] = null };
        }
        return schema;
    },
};

var outputDirectory = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outputDirectory);
foreach (var name in new[] { "rpc-request.schema.json", "rpc-response.schema.json", "bridge-event.schema.json" })
    WriteSchema(name);

var cases = new List<SerializedCase>();
var runtimeChecks = new List<string>();

var requestSamples = new List<RpcRequest>
{
    new GetTimerStateRequest { RequestId = 1 },
    new GetRunRequest { RequestId = 2 },
    new GetAttemptRequest { RequestId = 3 },
    new GetContextStateRequest { RequestId = 4 },
    new GetCompletedCountRequest { RequestId = 5 },
};
var timerOperations = new TimerOperationParams[]
{
    new TimerStartParams(), new TimerSplitParams(), new TimerSkipParams(), new TimerUndoParams(),
    new TimerResetParams(), new TimerPauseParams(), new TimerResumeParams(),
};
foreach (var operation in timerOperations)
    requestSamples.Add(new TimerOperationRequest { RequestId = (uint)requestSamples.Count + 1, Params = operation });
var gameTimeOperations = new GameTimeOperationParams[]
{
    new GameTimeInitializeParams(), new GameTimeSetParams { Ticks = -123456 }, new GameTimePauseParams(), new GameTimeResumeParams(),
};
foreach (var operation in gameTimeOperations)
    requestSamples.Add(new GameTimeOperationRequest { RequestId = (uint)requestSamples.Count + 1, Params = operation });
foreach (var request in requestSamples) AddSerializedCase("request", request.GetType().Name, JsonSerializer.Serialize<RpcRequest>(request, jsonOptions));

var emptyRun = new RunState
{
    GameName = "",
    CategoryName = "",
    OffsetTicks = -100,
    FilePath = null,
    LayoutPath = null,
    Metadata = null,
    Comparisons = new List<string>(),
    Segments = new List<SegmentInfo>(),
    GameIcon = null,
};
var populatedRun = new RunState
{
    GameName = "Example Game",
    CategoryName = "Any%",
    OffsetTicks = -98765,
    FilePath = "C:/runs/example.lss",
    LayoutPath = null,
    Metadata = new RunMetadata
    {
        RunId = "run-123",
        PlatformName = "PC",
        RegionName = "US",
        UsesEmulator = false,
        Variables = new Dictionary<string, string> { ["character"] = "Hero" },
    },
    Comparisons = new List<string> { "Personal Best" },
    Segments = new List<SegmentInfo>
    {
        new()
        {
            Index = 0,
            Name = "Opening",
            Comparisons = new List<ComparisonTime> { new() { Name = "Personal Best", Time = new TimeValue { RealTimeTicks = 1234, GameTimeTicks = null } } },
            BestSegmentTime = new TimeValue { RealTimeTicks = -100, GameTimeTicks = 500 },
            Icon = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/b2sAAAAASUVORK5CYII="),
        },
    },
    GameIcon = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/b2sAAAAASUVORK5CYII="),
};

var timerState = new TimerState
{
    Phase = TimerPhase.Running,
    SplitIndex = 2,
    RealTimeTicks = 123456,
    GameTimeTicks = null,
    IsGameTimeInitialized = false,
    IsGameTimePaused = false,
};
var responses = new RpcResponse[]
{
    new TimerStateResponse { RequestId = 1, SessionId = 123, Result = timerState },
    new RunStateResponse { RequestId = 2, SessionId = 123, Result = emptyRun },
    new RunStateResponse { RequestId = 8, SessionId = 123, Result = populatedRun },
    new AttemptStateResponse { RequestId = 3, SessionId = 123, Result = new AttemptState { AttemptCount = 4, Segments = new List<AttemptSegment> { new() { Index = 0, SplitTime = new TimeValue { RealTimeTicks = null, GameTimeTicks = null }, CustomVariables = new Dictionary<string, string>() } } } },
    new ContextStateResponse { RequestId = 4, SessionId = 123, Result = new ContextState { CurrentTimingMethod = TimingMethod.GameTime, CurrentComparison = "Personal Best", CustomVariables = new Dictionary<string, string>() } },
    new CompletedCountResponse { RequestId = 5, SessionId = 123, Result = new CompletedCount { Value = 2 } },
    new ControlSuccessResponse { RequestId = 6, SessionId = 123 },
    new ErrorResponse { RequestId = 7, SessionId = 123, Error = new BridgeError { Code = BridgeErrorCode.OperationFailed, Message = "Operation failed." } },
};
foreach (var response in responses) AddSerializedCase("response", response.GetType().Name, JsonSerializer.Serialize<RpcResponse>(response, jsonOptions));

var eventSamples = new BridgeEvent[]
{
    new TimerStartedEvent { SessionId = 123, EventSequence = 1, TimerState = timerState },
    new TimerSplitEvent { SessionId = 123, EventSequence = 2, TimerState = timerState },
    new TimerSkippedEvent { SessionId = 123, EventSequence = 3, TimerState = timerState },
    new TimerUndoEvent { SessionId = 123, EventSequence = 4, TimerState = timerState },
    new TimerResetEvent { SessionId = 123, EventSequence = 5, TimerState = timerState },
    new TimerPhaseChangedEvent { SessionId = 123, EventSequence = 6, TimerState = timerState },
    new RunChangedEvent { SessionId = 123, EventSequence = 7 },
    new ContextChangedEvent { SessionId = 123, EventSequence = 8 },
};
foreach (var bridgeEvent in eventSamples) AddSerializedCase("event", bridgeEvent.GetType().Name, JsonSerializer.Serialize<BridgeEvent>(bridgeEvent, jsonOptions));
AddSerializedCase("event", "TimerResetEvent with null timer state", JsonSerializer.Serialize<BridgeEvent>(new TimerResetEvent { SessionId = 123, EventSequence = 9, TimerState = null }, jsonOptions));

var outOfOrderOptions = new JsonSerializerOptions(jsonOptions) { AllowOutOfOrderMetadataProperties = false };
Check("five query methods deserialize to their declared derived types", () =>
{
    var json = new[] { "get_timer_state", "get_run", "get_attempt", "get_context_state", "get_completed_count" };
    var expected = new[] { typeof(GetTimerStateRequest), typeof(GetRunRequest), typeof(GetAttemptRequest), typeof(GetContextStateRequest), typeof(GetCompletedCountRequest) };
    return json.Select((method, index) => JsonSerializer.Deserialize<RpcRequest>($"{{\"method\":\"{method}\",\"request_id\":{index + 1}}}", jsonOptions)?.GetType() == expected[index]).All(x => x);
});
Check("all seven timer operation parameter types survive round-trip", () => requestSamples.OfType<TimerOperationRequest>().All(request =>
{
    var roundTrip = JsonSerializer.Deserialize<RpcRequest>(JsonSerializer.Serialize<RpcRequest>(request, jsonOptions), jsonOptions) as TimerOperationRequest;
    return roundTrip?.Params.GetType() == request.Params.GetType();
}));
Check("all four game-time operation parameter types and set ticks survive round-trip", () => requestSamples.OfType<GameTimeOperationRequest>().All(request =>
{
    var roundTrip = JsonSerializer.Deserialize<RpcRequest>(JsonSerializer.Serialize<RpcRequest>(request, jsonOptions), jsonOptions) as GameTimeOperationRequest;
    if (roundTrip?.Params.GetType() != request.Params.GetType()) return false;
    return request.Params is not GameTimeSetParams originalSet || roundTrip.Params is GameTimeSetParams roundTripSet && roundTripSet.Ticks == originalSet.Ticks;
}));
Check("missing operation discriminator is rejected", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"timer_operation\",\"request_id\":1,\"params\":{}}", jsonOptions)));
Check("game_time_operation set requires ticks", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"game_time_operation\",\"request_id\":1,\"params\":{\"operation\":\"set\"}}", jsonOptions)));
Check("game_time_operation set accepts signed ticks before its discriminator", () =>
{
    var request = JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"game_time_operation\",\"request_id\":1,\"params\":{\"ticks\":-123456,\"operation\":\"set\"}}", jsonOptions) as GameTimeOperationRequest;
    return request?.Params is GameTimeSetParams { Ticks: -123456 };
});
Check("unknown timer or game-time operation discriminator is rejected", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"timer_operation\",\"request_id\":1,\"params\":{\"operation\":\"teleport\"}}", jsonOptions)) && Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"game_time_operation\",\"request_id\":1,\"params\":{\"operation\":\"teleport\",\"ticks\":1}}", jsonOptions)));
Check("null or wrong-typed required game-time ticks are rejected", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"game_time_operation\",\"request_id\":1,\"params\":{\"operation\":\"set\",\"ticks\":null}}", jsonOptions)) && Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"game_time_operation\",\"request_id\":1,\"params\":{\"operation\":\"set\",\"ticks\":\"soon\"}}", jsonOptions)));
Check("non-set game-time operation rejects ticks as unmapped", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"game_time_operation\",\"request_id\":1,\"params\":{\"operation\":\"pause\",\"ticks\":1}}", jsonOptions)));
Check("unknown request method is rejected", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"unknown\",\"request_id\":1}", jsonOptions)));
Check("query method rejects operation params", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"get_run\",\"request_id\":1,\"params\":{}}", jsonOptions)));
Check("missing operation params is rejected", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"timer_operation\",\"request_id\":1}", jsonOptions)));
Check("request_id before method is accepted when out-of-order metadata is enabled", () => JsonSerializer.Deserialize<RpcRequest>("{\"request_id\":1,\"method\":\"get_run\"}", jsonOptions) is GetRunRequest);
Check("request_id before method is rejected when out-of-order metadata is disabled", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"request_id\":1,\"method\":\"get_run\"}", outOfOrderOptions)));
Check("invalid enum text and numeric enum values are rejected", () => Rejects(() => JsonSerializer.Deserialize<TimerState>("{\"phase\":\"flying\",\"split_index\":0,\"real_time_ticks\":0,\"game_time_ticks\":null,\"is_game_time_initialized\":false,\"is_game_time_paused\":false}", jsonOptions)) && Rejects(() => JsonSerializer.Deserialize<TimerState>("{\"phase\":1,\"split_index\":0,\"real_time_ticks\":0,\"game_time_ticks\":null,\"is_game_time_initialized\":false,\"is_game_time_paused\":false}", jsonOptions)));
Check("shared explicit-null converter emits and accepts null only", () => JsonSerializer.Serialize<RpcResponse>(new ControlSuccessResponse { RequestId = 1, SessionId = 123 }, jsonOptions).Contains("\"result\":null") && Rejects(() => JsonSerializer.Deserialize<JsonNullValue>("true", jsonOptions)));
Check("success and error response DTOs serialize mutually exclusive members", () => responses.All(response =>
{
    using var document = JsonDocument.Parse(JsonSerializer.Serialize<RpcResponse>(response, jsonOptions));
    var root = document.RootElement;
    return root.TryGetProperty("result", out _) != root.TryGetProperty("error", out _);
}));
Check("required nullable TimeValue fields reject omission but allow null", () => Rejects(() => JsonSerializer.Deserialize<TimeValue>("{}", jsonOptions)) && JsonSerializer.Deserialize<TimeValue>("{\"real_time_ticks\":null,\"game_time_ticks\":null}", jsonOptions) != null);
Check("RunState byte arrays serialize as Base64 strings and nullable fields as null", () =>
{
    var json = JsonSerializer.Serialize(emptyRun, jsonOptions);
    using var document = JsonDocument.Parse(json);
    return document.RootElement.GetProperty("game_icon").ValueKind == JsonValueKind.Null
        && document.RootElement.GetProperty("metadata").ValueKind == JsonValueKind.Null
        && JsonSerializer.Serialize(new byte[] { 1, 2, 3 }, jsonOptions) == "\"AQID\"";
});
Check("System.Text.Json rejects invalid Base64 while Ajv annotation validation is optional", () => Rejects(() => JsonSerializer.Deserialize<RunState>("{\"game_name\":\"\",\"category_name\":\"\",\"offset_ticks\":0,\"file_path\":null,\"layout_path\":null,\"metadata\":null,\"comparisons\":[],\"segments\":[],\"game_icon\":\"%%%\"}", jsonOptions)));
Check("all eight event types serialize and deserialize with type discriminator", () => eventSamples.All(sample =>
{
    var json = JsonSerializer.Serialize<BridgeEvent>(sample, jsonOptions);
    return JsonSerializer.Deserialize<BridgeEvent>(json, jsonOptions)?.GetType() == sample.GetType();
}));
Check("timer_reset requires timer_state and accepts object or null", () =>
{
    var resetWithState = JsonSerializer.Serialize<BridgeEvent>(new TimerResetEvent { SessionId = 123, EventSequence = 5, TimerState = timerState }, jsonOptions);
    var resetWithoutState = JsonSerializer.Serialize<BridgeEvent>(new TimerResetEvent { SessionId = 123, EventSequence = 5, TimerState = null }, jsonOptions);
    return JsonSerializer.Deserialize<BridgeEvent>(resetWithState, jsonOptions) is TimerResetEvent { TimerState: not null }
        && JsonSerializer.Deserialize<BridgeEvent>(resetWithoutState, jsonOptions) is TimerResetEvent { TimerState: null }
        && Rejects(() => JsonSerializer.Deserialize<BridgeEvent>("{\"type\":\"timer_reset\",\"session_id\":123,\"event_sequence\":5}", jsonOptions));
});
Check("non-reset timer events reject null timer_state", () => Rejects(() => JsonSerializer.Deserialize<BridgeEvent>("{\"type\":\"timer_started\",\"session_id\":123,\"event_sequence\":1,\"timer_state\":null}", jsonOptions)));
Check("run/context changed events emit and accept only explicit null timer_state", () => eventSamples.Skip(6).All(sample =>
{
    var json = JsonSerializer.Serialize<BridgeEvent>(sample, jsonOptions);
    return json.Contains("\"timer_state\":null")
        && JsonSerializer.Deserialize<BridgeEvent>(json, jsonOptions)?.GetType() == sample.GetType()
        && Rejects(() => JsonSerializer.Deserialize<BridgeEvent>(json.Replace("\"timer_state\":null", "\"timer_state\":{}"), jsonOptions));
}));
Check("unknown properties are rejected on requests and state DTOs", () => Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"get_run\",\"request_id\":1,\"extra\":1}", jsonOptions)) && Rejects(() => JsonSerializer.Deserialize<TimerState>("{\"phase\":\"running\",\"split_index\":0,\"real_time_ticks\":0,\"game_time_ticks\":null,\"is_game_time_initialized\":false,\"is_game_time_paused\":false,\"extra\":1}", jsonOptions)));
Check("request and response request_id round-trip zero, one, and uint32 maximum", () => new[] { 0u, 1u, uint.MaxValue }.All(id =>
{
    var request = new GetRunRequest { RequestId = id };
    var requestJson = JsonSerializer.Serialize<RpcRequest>(request, jsonOptions);
    var requestRoundTrip = JsonSerializer.Deserialize<RpcRequest>(requestJson, jsonOptions);
    var response = new ControlSuccessResponse { RequestId = id, SessionId = uint.MaxValue };
    var responseJson = JsonSerializer.Serialize<RpcResponse>(response, jsonOptions);
    var responseRoundTrip = JsonSerializer.Deserialize<ControlSuccessResponse>(responseJson, jsonOptions);
    return requestRoundTrip?.RequestId == id && responseRoundTrip is not null && responseRoundTrip.RequestId == id && responseRoundTrip.SessionId == uint.MaxValue;
}));
Check("request_id rejects overflow, negative, null, and omission in Request and Response", () =>
{
    var invalidValues = new[] { "4294967296", "-1", "null" };
    return invalidValues.All(value => Rejects(() => JsonSerializer.Deserialize<RpcRequest>($"{{\"method\":\"get_run\",\"request_id\":{value}}}", jsonOptions))
        && Rejects(() => JsonSerializer.Deserialize<ControlSuccessResponse>($"{{\"request_id\":{value},\"session_id\":1,\"result\":null}}", jsonOptions)))
        && Rejects(() => JsonSerializer.Deserialize<RpcRequest>("{\"method\":\"get_run\"}", jsonOptions))
        && Rejects(() => JsonSerializer.Deserialize<ControlSuccessResponse>("{\"session_id\":1,\"result\":null}", jsonOptions));
});
Check("event_sequence uint32 accepts zero and maximum in Serializer", () => new[] { 0u, 1u, uint.MaxValue }.All(sequence =>
{
    var json = JsonSerializer.Serialize(new RunChangedEvent { SessionId = 1, EventSequence = sequence }, jsonOptions);
    return JsonSerializer.Deserialize<RunChangedEvent>(json, jsonOptions)?.EventSequence == sequence;
}));
Check("event_sequence rejects overflow, negative, null, and omission in Serializer", () => new[] { "4294967296", "-1", "null" }.All(value => Rejects(() => JsonSerializer.Deserialize<RunChangedEvent>($"{{\"session_id\":1,\"event_sequence\":{value},\"timer_state\":null}}", jsonOptions)))
    && Rejects(() => JsonSerializer.Deserialize<RunChangedEvent>("{\"session_id\":1,\"timer_state\":null}", jsonOptions)));
Check("session_id uint32 accepts zero and maximum in Serializer but rejects overflow", () => JsonSerializer.Deserialize<SessionIdProbe>("{\"session_id\":0}", jsonOptions)?.SessionId == 0
    && JsonSerializer.Deserialize<SessionIdProbe>($"{{\"session_id\":{uint.MaxValue}}}", jsonOptions)?.SessionId == uint.MaxValue
    && Rejects(() => JsonSerializer.Deserialize<SessionIdProbe>("{\"session_id\":4294967296}", jsonOptions)));
Check("nullable collection elements and dictionary values accept null in System.Text.Json", () => JsonSerializer.Deserialize<RunState>("{\"game_name\":\"\",\"category_name\":\"\",\"offset_ticks\":0,\"file_path\":null,\"layout_path\":null,\"metadata\":{\"run_id\":null,\"platform_name\":null,\"region_name\":null,\"uses_emulator\":false,\"variables\":{\"x\":null}},\"comparisons\":[null],\"segments\":[],\"game_icon\":null}", jsonOptions) is { Comparisons.Count: 1, Metadata.Variables.Count: 1 });
Check("root null is accepted by System.Text.Json for reference model", () => JsonSerializer.Deserialize<RunState>("null", jsonOptions) == null);

var serializedCasesPath = Path.Combine(outputDirectory, "csharp-serialized-cases.json");
File.WriteAllText(serializedCasesPath, JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) + Environment.NewLine);
File.WriteAllLines(Path.Combine(outputDirectory, "runtime-validation.txt"), runtimeChecks);
Console.WriteLine($"C# serializer/deserializer checks: {runtimeChecks.Count(x => x.StartsWith("PASS "))}/{runtimeChecks.Count}");
Console.WriteLine(string.Join(Environment.NewLine, runtimeChecks));
Console.WriteLine($"Serialized valid messages for independent validation: {cases.Count}");
if (runtimeChecks.Any(result => result.StartsWith("FAIL ", StringComparison.Ordinal))) Environment.ExitCode = 1;

void WriteSchema(string fileName)
{
    Type type = fileName switch
    {
        "rpc-request.schema.json" => typeof(RpcRequest),
        "rpc-response.schema.json" => typeof(RpcResponse),
        _ => typeof(BridgeEvent),
    };
    var node = jsonOptions.GetJsonSchemaAsNode(type, exporterOptions);
    if (node is JsonObject root) root["$schema"] = Draft202012;
    File.WriteAllText(Path.Combine(outputDirectory, fileName), node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
}

void AddSerializedCase(string schema, string name, string json)
    => cases.Add(new SerializedCase(schema, name, true, JsonNode.Parse(json)!));

void Check(string name, Func<bool> check)
{
    try { runtimeChecks.Add($"{(check() ? "PASS" : "FAIL")} {name}"); }
    catch (Exception exception) { runtimeChecks.Add($"FAIL {name} ({exception.GetType().Name}: {exception.Message})"); }
}

static bool Rejects(Action action)
{
    try { action(); return false; }
    catch (JsonException) { return true; }
    catch (NotSupportedException) { return true; }
}

public sealed class SerializedCase
{
    public SerializedCase(string schema, string name, bool valid, JsonNode data)
    {
        Schema = schema;
        Name = name;
        Valid = valid;
        Data = data;
    }
    public string Schema { get; }
    public string Name { get; }
    public bool Valid { get; }
    public JsonNode Data { get; }
}

public sealed class SessionIdProbe
{
    [JsonRequired] public uint SessionId { get; set; }
}
