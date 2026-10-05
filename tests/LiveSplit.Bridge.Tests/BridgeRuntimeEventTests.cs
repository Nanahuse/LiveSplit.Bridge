using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class BridgeRuntimeEventTests
{
    [Fact]
    public async Task TimerStartedOnLiveSplitSidePublishesTimerEvent()
    {
        using var harness = await EventHarness.CreateAsync();
        await harness.WaitForHeartbeatAsync();

        var timerModel = new TimerModel { CurrentState = harness.State };
        timerModel.Start();

        var bridgeEvent = await harness.ReceiveUntilAsync(BridgeEventType.EventTimerStarted);

        Assert.NotNull(bridgeEvent.TimerState);
        Assert.Equal(1UL, bridgeEvent.EventSequence);
        Assert.NotEqual(0UL, bridgeEvent.SessionId);
    }

    [Fact]
    public async Task GameTimeInitializePublishesGameTimeEvent()
    {
        using var harness = await EventHarness.CreateAsync();
        await harness.WaitForHeartbeatAsync();

        var response = await harness.SendAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            GameTimeOperation = new GameTimeOperationRequest { Operation = GameTimeOperationType.Initialize },
        });

        Assert.True(response.Operation.Success);

        var bridgeEvent = await harness.ReceiveUntilAsync(BridgeEventType.EventGameTimeInitialized);

        Assert.NotNull(bridgeEvent.TimerState);
        Assert.Equal(1UL, bridgeEvent.EventSequence);
    }

    [Fact]
    public async Task RunChangePublishesRunChangedEventWithIncrementingSequence()
    {
        using var harness = await EventHarness.CreateAsync();
        await harness.WaitForHeartbeatAsync();

        harness.State.Run.GameName = "First Change";
        harness.State.CallRunManuallyModified();
        harness.Runtime.Update();
        var first = await harness.ReceiveUntilAsync(BridgeEventType.EventRunChanged);
        harness.State.Run.GameName = "Second Change";
        harness.State.CallRunManuallyModified();
        harness.Runtime.Update();
        var second = await harness.ReceiveUntilAsync(BridgeEventType.EventRunChanged);

        Assert.Equal(1UL, first.EventSequence);
        Assert.Equal(2UL, second.EventSequence);
        Assert.NotNull(first.TimerState);
        Assert.Equal(2UL, first.TimerState.RunRevision);
        Assert.Equal(3UL, second.TimerState.RunRevision);
    }

    [Fact]
    public async Task HeartbeatHasNoTimerStateAndDoesNotAdvanceSequence()
    {
        using var harness = await EventHarness.CreateAsync();

        var first = await harness.ReceiveUntilAsync(BridgeEventType.EventHeartbeat);
        var second = await harness.ReceiveUntilAsync(BridgeEventType.EventHeartbeat);

        Assert.Null(first.TimerState);
        Assert.Null(second.TimerState);
        Assert.Equal(0UL, first.EventSequence);
        Assert.Equal(0UL, second.EventSequence);
    }

    [Fact]
    public async Task EventsCarryTheSameSessionIdAsAttach()
    {
        using var harness = await EventHarness.CreateAsync();
        await harness.WaitForHeartbeatAsync();

        var attach = await harness.SendAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            Attach = new AttachRequest(),
        });

        harness.State.Run.GameName = "Session Change";
        harness.State.CallRunManuallyModified();
        harness.Runtime.Update();
        var bridgeEvent = await harness.ReceiveUntilAsync(BridgeEventType.EventRunChanged);

        Assert.Equal(attach.Attach.SessionId, bridgeEvent.SessionId);
    }

    private sealed class EventHarness : IDisposable
    {
        private readonly WebSocketTestClient events;
        private readonly WebSocketTestClient rpc;

        private EventHarness(BridgeRuntime runtime, LiveSplitState state, WebSocketTestClient events, WebSocketTestClient rpc)
        {
            Runtime = runtime;
            State = state;
            this.events = events;
            this.rpc = rpc;
        }

        public BridgeRuntime Runtime { get; }
        public LiveSplitState State { get; }

        public static async Task<EventHarness> CreateAsync()
        {
            var port = BridgeTestEndpoints.GetFreePort();
            var run = new Run(new StandardComparisonGeneratorsFactory());
            run.Add(new Segment("One"));
            run.Add(new Segment("Two"));
            var state = TestLiveSplitState.Create(run);
            var runtime = new BridgeRuntime(state, port);
            var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
            var rpc = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(port));
            return new EventHarness(runtime, state, events, rpc);
        }

        public Task WaitForHeartbeatAsync()
        {
            return ReceiveUntilAsync(BridgeEventType.EventHeartbeat);
        }

        public Task<Response> SendAsync(Request request)
        {
            if (request.ProtocolVersion == 0)
            {
                request.ProtocolVersion = 2;
            }

            return rpc.SendRequestAsync(request, TimeSpan.FromSeconds(5));
        }

        public async Task<BridgeEvent> ReceiveUntilAsync(BridgeEventType type)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var data = await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(3));
                var bridgeEvent = BridgeEvent.Parser.ParseFrom(data);
                if (bridgeEvent.Type == type)
                {
                    return bridgeEvent;
                }
            }

            throw new TimeoutException($"Did not receive {type}.");
        }

        public void Dispose()
        {
            events.Dispose();
            rpc.Dispose();
            Runtime.Dispose();
        }
    }
}
