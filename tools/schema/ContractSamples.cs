using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveSplit.Bridge.Json;

internal static class ContractSamples
{
    internal static void Write(string outputDirectory)
    {
        var jsonOptions = BridgeJsonOptions.Create();
        var cases = new List<SerializedCase>();

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

        var serializedCasesPath = Path.Combine(outputDirectory, "csharp-serialized-cases.json");
        File.WriteAllText(serializedCasesPath, JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) + Environment.NewLine);
        Console.WriteLine($"Serialized messages for contract validation: {cases.Count}");

        void AddSerializedCase(string schema, string name, string json)
            => cases.Add(new SerializedCase(schema, name, true, JsonNode.Parse(json)!));
    }

    private sealed class SerializedCase
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
}
