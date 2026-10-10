using LiveSplit.Bridge.Protocol.V3;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class BridgeRuntimeRpcTests
{
    [Fact]
    public async Task WebSocketRequestsReachV3RuntimeAndReturnV3Envelope()
    {
        using var fixture = await RpcFixture.CreateAsync();
        var query = await fixture.SendAsync(new Request { RequestId = 42, GetTimerState = new GetTimerStateRequest() });
        Assert.NotNull(query.GetTimerState);
        AssertEnvelope(query, fixture.Runtime, 42);

        var control = await fixture.SendAsync(new Request
        {
            RequestId = 43,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerStart },
        });
        Assert.NotNull(control.Operation);
        Assert.Empty(OperationResponse.Descriptor.Fields.InDeclarationOrder());
        AssertEnvelope(control, fixture.Runtime, 43);
    }

    [Fact]
    public async Task UnsupportedVersionReturnsV3ErrorAndRunQueryWorks()
    {
        using var fixture = await RpcFixture.CreateAsync();
        var unsupported = await fixture.SendAsync(new Request { ProtocolVersion = 2, RequestId = 7, GetTimerState = new GetTimerStateRequest() });
        var run = await fixture.SendAsync(new Request { RequestId = 8, GetRun = new GetRunRequest() });
        Assert.Equal(BridgeErrorCode.UnsupportedProtocolVersion, unsupported.Error.Code);
        Assert.NotNull(run.GetRun);
        Assert.Equal("RPC Game", run.GetRun.Run.GameName);
        AssertEnvelope(unsupported, fixture.Runtime, 7);
        AssertEnvelope(run, fixture.Runtime, 8);
    }

    [Fact]
    public async Task TimerEventsUseLiveSplitCallbacksAndBroadcastInSequence()
    {
        using var fixture = await RpcFixture.CreateAsync();
        using var first = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(fixture.Port));
        using var second = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(fixture.Port));

        await fixture.SendAsync(new Request
        {
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerStart },
        });
        await fixture.SendAsync(new Request
        {
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerSplit },
        });

        var firstStart = BridgeEvent.Parser.ParseFrom(await first.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        var secondStart = BridgeEvent.Parser.ParseFrom(await second.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        var firstSplit = BridgeEvent.Parser.ParseFrom(await first.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        var secondSplit = BridgeEvent.Parser.ParseFrom(await second.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(BridgeEventType.EventTimerStarted, firstStart.Type);
        Assert.Equal(BridgeEventType.EventTimerSplit, firstSplit.Type);
        Assert.Equal(firstStart.EventSequence, secondStart.EventSequence);
        Assert.Equal(firstSplit.EventSequence, secondSplit.EventSequence);
        Assert.Equal(firstStart.SessionId, firstSplit.SessionId);
        Assert.Equal(1UL, firstStart.EventSequence);
        Assert.Equal(2UL, firstSplit.EventSequence);
        Assert.NotNull(firstStart.TimerState);
        Assert.NotNull(firstSplit.TimerState);
    }

    [Fact]
    public async Task RunChangedIsPublishedAfterTheNewRunSnapshotIsAvailable()
    {
        using var fixture = await RpcFixture.CreateAsync();
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(fixture.Port));
        fixture.State.Run.GameName = "Edited Game";
        fixture.State.CallRunManuallyModified();

        var changed = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        var response = await fixture.SendAsync(new Request { GetRun = new GetRunRequest() });
        Assert.Equal(BridgeEventType.EventRunChanged, changed.Type);
        Assert.Null(changed.TimerState);
        Assert.Equal("Edited Game", response.GetRun.Run.GameName);
    }

    [Fact]
    public async Task ContextObservationPublishesOnlyTheChangedSnapshot()
    {
        using var fixture = await RpcFixture.CreateAsync();
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(fixture.Port));
        fixture.State.CurrentTimingMethod = LiveSplit.Model.TimingMethod.GameTime;
        fixture.State.CurrentComparison = "Best Segments";
        fixture.State.Run.Metadata.CustomVariables["route"] = new LiveSplit.Model.CustomVariable("right", false);

        await Task.Delay(110);
        fixture.Runtime.ObserveContextState();
        var changed = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        var response = await fixture.SendAsync(new Request { GetContextState = new GetContextStateRequest() });

        Assert.Equal(BridgeEventType.EventContextChanged, changed.Type);
        Assert.Null(changed.TimerState);
        Assert.Equal(LiveSplit.Bridge.Protocol.V3.TimingMethod.GameTime, response.GetContextState.ContextState.CurrentTimingMethod);
        Assert.Equal("Best Segments", response.GetContextState.ContextState.CurrentComparison);
        Assert.Equal("right", response.GetContextState.ContextState.CustomVariables["route"]);

        await Task.Delay(110);
        fixture.Runtime.ObserveContextState();
        Assert.Equal(Timeout.InfiniteTimeSpan, await TryReceiveTimeoutAsync(events));
    }

    [Fact]
    public async Task ResetPublishesUpdatedRunIdBeforeRunChangedAndKeepsEventOrder()
    {
        using var fixture = await RpcFixture.CreateAsync(run => run.Metadata.RunID = "before-reset");
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(fixture.Port));
        fixture.State.Run.Metadata.RunID = null;
        fixture.State.Run[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(6), null);

        await fixture.SendAsync(new Request
        {
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerStart },
        });
        var started = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(BridgeEventType.EventTimerStarted, started.Type);
        await fixture.SendAsync(new Request
        {
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerReset },
        });
        var reset = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        var changed = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        var response = await fixture.SendAsync(new Request { GetRun = new GetRunRequest() });

        Assert.Equal(BridgeEventType.EventTimerReset, reset.Type);
        Assert.Equal(BridgeEventType.EventRunChanged, changed.Type);
        Assert.Equal(reset.EventSequence + 1, changed.EventSequence);
        Assert.False(response.GetRun.Run.Metadata.HasRunId);
    }

    [Fact]
    public async Task ResetWithoutRunChangesDoesNotPublishRunChanged()
    {
        using var fixture = await RpcFixture.CreateAsync();
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(fixture.Port));
        await fixture.SendAsync(new Request
        {
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerStart },
        });
        Assert.Equal(BridgeEventType.EventTimerStarted,
            BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5))).Type);

        await fixture.SendAsync(new Request
        {
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerReset },
        });
        var reset = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(BridgeEventType.EventTimerReset, reset.Type);

        await fixture.SendAsync(new Request
        {
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerStart },
        });
        var started = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(BridgeEventType.EventTimerStarted, started.Type);
        Assert.Equal(reset.EventSequence + 1, started.EventSequence);
    }

    [Fact]
    public async Task BestSegmentOnlyResetChangePublishesRunChanged()
    {
        using var fixture = await RpcFixture.CreateAsync();
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(fixture.Port));
        await fixture.SendAsync(new Request
        {
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerStart },
        });
        Assert.Equal(BridgeEventType.EventTimerStarted,
            BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5))).Type);

        fixture.State.Run[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(9), null);
        await fixture.SendAsync(new Request
        {
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerReset },
        });
        var reset = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        var changed = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        var response = await fixture.SendAsync(new Request { GetRun = new GetRunRequest() });

        Assert.Equal(BridgeEventType.EventTimerReset, reset.Type);
        Assert.Equal(BridgeEventType.EventRunChanged, changed.Type);
        Assert.Equal(reset.EventSequence + 1, changed.EventSequence);
        Assert.Equal(TimeSpan.FromSeconds(9).Ticks, response.GetRun.Run.Segments[0].BestSegmentTime.RealTimeTicks);
    }

    [Fact]
    public async Task TimerOnlyRunReplacementDuringResetIsPublishedImmediately()
    {
        using var fixture = await RpcFixture.CreateAsync();
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(fixture.Port));
        var timer = new TimerModel { CurrentState = fixture.State };
        timer.Start();
        var started = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));

        var timerOnlyRun = new Run(new StandardComparisonGeneratorsFactory());
        timerOnlyRun.Add(new Segment(string.Empty));
        fixture.State.Run = timerOnlyRun;
        timer.Reset();

        var reset = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        var changed = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        var response = await fixture.SendAsync(new Request { GetRun = new GetRunRequest() });
        Assert.Equal(BridgeEventType.EventTimerStarted, started.Type);
        Assert.Equal(BridgeEventType.EventTimerReset, reset.Type);
        Assert.Equal(BridgeEventType.EventRunChanged, changed.Type);
        Assert.Single(response.GetRun.Run.Segments);
        Assert.Equal(string.Empty, response.GetRun.Run.Segments[0].Name);
    }

    private static async Task<TimeSpan> TryReceiveTimeoutAsync(WebSocketTestClient client)
    {
        try
        {
            await client.ReceiveBinaryAsync(TimeSpan.FromMilliseconds(150));
            return TimeSpan.Zero;
        }
        catch (OperationCanceledException)
        {
            return Timeout.InfiniteTimeSpan;
        }
    }

    private static void AssertEnvelope(Response response, BridgeRuntime runtime, ulong requestId)
    {
        Assert.Equal(3U, response.ProtocolVersion);
        Assert.Equal(requestId, response.RequestId);
        Assert.Equal(runtime.SessionId, response.SessionId);
    }

    private sealed class RpcFixture : IDisposable
    {
        private readonly WebSocketTestClient client;
        private RpcFixture(BridgeRuntime runtime, WebSocketTestClient client, int port, LiveSplitState state) { Runtime = runtime; this.client = client; Port = port; State = state; }
        public BridgeRuntime Runtime { get; }
        public int Port { get; }
        public LiveSplitState State { get; }

        public static async Task<RpcFixture> CreateAsync(Action<Run>? configureRun = null)
        {
            var port = BridgeTestEndpoints.GetFreePort();
            var run = new Run(new StandardComparisonGeneratorsFactory()) { GameName = "RPC Game", CategoryName = "Any%" };
            run.Add(new Segment("One"));
            configureRun?.Invoke(run);
            var state = TestLiveSplitState.Create(run);
            var runtime = new BridgeRuntime(state, port);
            var client = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(port));
            return new RpcFixture(runtime, client, port, state);
        }

        public Task<Response> SendAsync(Request request)
        {
            if (request.ProtocolVersion == 0) request.ProtocolVersion = 3;
            return client.SendRequestAsync(request, TimeSpan.FromSeconds(5));
        }

        public void Dispose() { client.Dispose(); Runtime.Dispose(); }
    }
}
