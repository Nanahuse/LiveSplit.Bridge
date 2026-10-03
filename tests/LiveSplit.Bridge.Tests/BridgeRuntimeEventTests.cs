using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V1;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using NetMQ;
using NetMQ.Sockets;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class BridgeRuntimeEventTests
{
    [Fact]
    public void TimerStartedOnLiveSplitSidePublishesTimerEvent()
    {
        using var harness = EventHarness.Create();
        harness.WaitForHeartbeat();

        var timerModel = new TimerModel { CurrentState = harness.State };
        harness.State.RegisterTimerModel(timerModel);
        timerModel.Start();

        var bridgeEvent = harness.ReceiveUntil(BridgeEventType.EventTimerStarted);

        Assert.NotNull(bridgeEvent.Snapshot);
        Assert.Equal("Timer started", bridgeEvent.Description);
        Assert.Equal(1UL, bridgeEvent.EventSequence);
        Assert.NotEqual(0UL, bridgeEvent.SessionId);
    }

    [Fact]
    public void GameTimeInitializePublishesGameTimeEvent()
    {
        using var harness = EventHarness.Create();
        harness.WaitForHeartbeat();

        var response = harness.Send(new Request
        {
            ProtocolVersion = 1,
            RequestId = 1,
            GameTimeOperation = new GameTimeOperationRequest { Operation = GameTimeOperationType.Initialize },
        });

        Assert.True(response.Operation.Success);

        var bridgeEvent = harness.ReceiveUntil(BridgeEventType.EventGameTimeInitialized);

        Assert.NotNull(bridgeEvent.Snapshot);
        Assert.Equal(1UL, bridgeEvent.EventSequence);
    }

    [Fact]
    public void RunChangePublishesRunChangedEventWithIncrementingSequence()
    {
        using var harness = EventHarness.Create();
        harness.WaitForHeartbeat();

        harness.State.CallRunManuallyModified();
        var first = harness.ReceiveUntil(BridgeEventType.EventRunChanged);
        harness.State.CallRunManuallyModified();
        var second = harness.ReceiveUntil(BridgeEventType.EventRunChanged);

        Assert.Equal(1UL, first.EventSequence);
        Assert.Equal(2UL, second.EventSequence);
        Assert.NotNull(first.Snapshot);
        Assert.Equal(2UL, first.Snapshot.RunRevision);
        Assert.Equal(3UL, second.Snapshot.RunRevision);
    }

    [Fact]
    public void PeriodicSnapshotPublishesStateSnapshotEvent()
    {
        using var harness = EventHarness.Create();
        harness.WaitForHeartbeat();

        harness.Runtime.PublishPeriodicSnapshot();

        var bridgeEvent = harness.ReceiveUntil(BridgeEventType.EventStateSnapshot);

        Assert.NotNull(bridgeEvent.Snapshot);
        Assert.Equal("Periodic snapshot", bridgeEvent.Description);
        Assert.Equal(1UL, bridgeEvent.EventSequence);
    }

    [Fact]
    public void HeartbeatHasNoSnapshotAndDoesNotAdvanceSequence()
    {
        using var harness = EventHarness.Create();

        var first = harness.ReceiveUntil(BridgeEventType.EventHeartbeat);
        var second = harness.ReceiveUntil(BridgeEventType.EventHeartbeat);

        Assert.Null(first.Snapshot);
        Assert.Null(second.Snapshot);
        Assert.Equal(0UL, first.EventSequence);
        Assert.Equal(0UL, second.EventSequence);
    }

    [Fact]
    public void EventsCarryTheSameSessionIdAsAttach()
    {
        using var harness = EventHarness.Create();
        harness.WaitForHeartbeat();

        var attach = harness.Send(new Request
        {
            ProtocolVersion = 1,
            RequestId = 1,
            Attach = new AttachRequest(),
        });

        harness.State.CallRunManuallyModified();
        var bridgeEvent = harness.ReceiveUntil(BridgeEventType.EventRunChanged);

        Assert.Equal(attach.Attach.SessionId, bridgeEvent.SessionId);
    }

    private sealed class EventHarness : IDisposable
    {
        private readonly RequestSocket requestSocket;
        private readonly SubscriberSocket subscriber;

        private EventHarness(BridgeRuntime runtime, LiveSplitState state, int rpcPort, int eventPort)
        {
            Runtime = runtime;
            State = state;
            subscriber = new SubscriberSocket();
            subscriber.Subscribe(string.Empty);
            subscriber.Connect(BridgeTestEndpoints.Event(eventPort));
            requestSocket = new RequestSocket();
            requestSocket.Connect(BridgeTestEndpoints.Rpc(rpcPort));
        }

        public BridgeRuntime Runtime { get; }
        public LiveSplitState State { get; }

        public static EventHarness Create()
        {
            var (rpcPort, eventPort) = BridgeTestEndpoints.GetFreePorts();
            var run = new Run(new StandardComparisonGeneratorsFactory());
            run.Add(new Segment("One"));
            run.Add(new Segment("Two"));
            var state = TestLiveSplitState.Create(run);
            var runtime = new BridgeRuntime(state, rpcPort, eventPort);
            return new EventHarness(runtime, state, rpcPort, eventPort);
        }

        public void WaitForHeartbeat()
        {
            ReceiveUntil(BridgeEventType.EventHeartbeat);
        }

        public Response Send(Request request)
        {
            if (request.ProtocolVersion == 0)
            {
                request.ProtocolVersion = 1;
            }

            requestSocket.SendFrame(request.ToByteArray());
            Assert.True(
                requestSocket.TryReceiveFrameBytes(TimeSpan.FromSeconds(5), out var data),
                "Timed out waiting for RPC response.");
            return Response.Parser.ParseFrom(data);
        }

        public BridgeEvent ReceiveUntil(BridgeEventType type)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                Assert.True(
                    subscriber.TryReceiveFrameBytes(TimeSpan.FromSeconds(2), out var data),
                    $"Timed out waiting for {type}.");
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
            subscriber.Dispose();
            requestSocket.Dispose();
            Runtime.Dispose();
        }
    }
}
