using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveSplit.Bridge.Json;

internal static class ContractSamples
{
    internal static void Write(string outputDirectory)
    {
        var jsonOptions = BridgeJsonOptions.Create();
        var cases = new List<SerializedCase>();

        var requestSamples = Enum.GetValues(typeof(QueryMethod)).Cast<QueryMethod>()
            .Select(method => (RpcRequest)new QueryRequest { Method = method, RequestId = (uint)method + 1 }).ToList();
        foreach (TimerOperation operation in Enum.GetValues(typeof(TimerOperation)))
            requestSamples.Add(new TimerRequest { RequestId = (uint)requestSamples.Count + 1, Params = new() { Operation = operation } });
        requestSamples.Add(new GameTimeRequest<GameControlParams> { RequestId = 13, Params = new() { Operation = GameControlOperation.Initialize } });
        requestSamples.Add(new GameTimeRequest<GameSetParams> { RequestId = 14, Params = new() { Ticks = -123456 } });
        requestSamples.Add(new GameTimeRequest<GameControlParams> { RequestId = 15, Params = new() { Operation = GameControlOperation.Pause } });
        requestSamples.Add(new GameTimeRequest<GameControlParams> { RequestId = 16, Params = new() { Operation = GameControlOperation.Resume } });
        foreach (var request in requestSamples)
            AddSerializedCase("request", $"request {request.RequestId}", JsonSerializer.Serialize((object)request, jsonOptions));

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
            new Success<TimerState> { RequestId = 1, SessionId = 123, Result = timerState },
            new Success<RunState> { RequestId = 2, SessionId = 123, Result = emptyRun },
            new Success<RunState> { RequestId = 8, SessionId = 123, Result = populatedRun },
            new Success<AttemptState> { RequestId = 3, SessionId = 123, Result = new AttemptState { AttemptCount = 4, Segments = new List<AttemptSegment> { new() { Index = 0, SplitTime = new TimeValue { RealTimeTicks = null, GameTimeTicks = null }, CustomVariables = new Dictionary<string, string>() } } } },
            new Success<ContextState> { RequestId = 4, SessionId = 123, Result = new ContextState { CurrentTimingMethod = TimingMethod.GameTime, CurrentComparison = "Personal Best", CustomVariables = new Dictionary<string, string>() } },
            new Success<CompletedCount> { RequestId = 5, SessionId = 123, Result = new CompletedCount { Value = 2 } },
            new ControlSuccessResponse { RequestId = 6, SessionId = 123 },
            new ErrorResponse { RequestId = 7, SessionId = 123, Error = new BridgeError { Code = BridgeErrorCode.OperationFailed, Message = "Operation failed." } },
        };
        foreach (var response in responses) AddSerializedCase("response", response.GetType().Name, JsonSerializer.Serialize((object)response, jsonOptions));

        var eventSamples = new BridgeEvent[]
        {
            new TimerEvent { Type = TimerEventKind.Started, SessionId = 123, EventSequence = 1, TimerState = timerState },
            new TimerEvent { Type = TimerEventKind.Split, SessionId = 123, EventSequence = 2, TimerState = timerState },
            new TimerEvent { Type = TimerEventKind.Skipped, SessionId = 123, EventSequence = 3, TimerState = timerState },
            new TimerEvent { Type = TimerEventKind.Undo, SessionId = 123, EventSequence = 4, TimerState = timerState },
            new ResetEvent { SessionId = 123, EventSequence = 5, TimerState = timerState },
            new TimerEvent { Type = TimerEventKind.PhaseChanged, SessionId = 123, EventSequence = 6, TimerState = timerState },
            new ChangedEvent { Type = ChangedEventKind.RunChanged, SessionId = 123, EventSequence = 7 },
            new ChangedEvent { Type = ChangedEventKind.ContextChanged, SessionId = 123, EventSequence = 8 },
        };
        foreach (var bridgeEvent in eventSamples) AddSerializedCase("event", bridgeEvent.GetType().Name, JsonSerializer.Serialize((object)bridgeEvent, jsonOptions));
        AddSerializedCase("event", "ResetEvent with null timer state", JsonSerializer.Serialize((object)new ResetEvent { SessionId = 123, EventSequence = 9, TimerState = null }, jsonOptions));

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
