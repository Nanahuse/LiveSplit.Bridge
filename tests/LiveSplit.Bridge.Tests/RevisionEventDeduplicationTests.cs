using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class RevisionEventDeduplicationTests
{
    [Fact]
    public async Task RpcTimerOperationCommitsAttemptProjectionExactlyOnce()
    {
        using var harness = await Harness.CreateAsync();
        await harness.WaitForHeartbeatAsync();

        var response = await harness.SendAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerStart },
        });

        Assert.True(response.Operation.Success);

        var started = await harness.ReceiveUntilAsync(BridgeEventType.EventTimerStarted);
        // The immediate timer transition reports the previously published attempt
        // generation; the projection is committed later by Update.
        Assert.Equal(1UL, started.TimerState.AttemptRevision);

        harness.Runtime.Update();

        var attemptChanged = await harness.ReceiveUntilAsync(BridgeEventType.EventAttemptChanged);
        Assert.Equal(2UL, attemptChanged.TimerState.AttemptRevision);

        // Re-observing must not emit a duplicate event for the same change.
        harness.Runtime.Update();
        harness.Runtime.Update();

        var duplicate = await harness.TryReceiveNonHeartbeatAsync(TimeSpan.FromSeconds(1));
        Assert.Null(duplicate);
    }

    [Fact]
    public async Task RpcGameTimeOperationIsNotRepublishedByUpdate()
    {
        using var harness = await Harness.CreateAsync();
        await harness.WaitForHeartbeatAsync();

        var response = await harness.SendAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            GameTimeOperation = new GameTimeOperationRequest { Operation = GameTimeOperationType.Initialize },
        });

        Assert.True(response.Operation.Success);

        var initialized = await harness.ReceiveUntilAsync(BridgeEventType.EventGameTimeInitialized);
        Assert.Equal(1UL, initialized.EventSequence);

        harness.Runtime.Update();

        var duplicate = await harness.TryReceiveNonHeartbeatAsync(TimeSpan.FromSeconds(1));
        Assert.Null(duplicate);
    }

    private sealed class Harness : IDisposable
    {
        private readonly WebSocketTestClient events;
        private readonly WebSocketTestClient rpc;

        private Harness(BridgeRuntime runtime, WebSocketTestClient events, WebSocketTestClient rpc)
        {
            Runtime = runtime;
            this.events = events;
            this.rpc = rpc;
        }

        public BridgeRuntime Runtime { get; }

        public static async Task<Harness> CreateAsync()
        {
            var port = BridgeTestEndpoints.GetFreePort();
            var run = new Run(new StandardComparisonGeneratorsFactory());
            run.Add(new Segment("One"));
            run.Add(new Segment("Two"));
            var state = TestLiveSplitState.Create(run);
            var runtime = new BridgeRuntime(state, port);
            var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
            var rpc = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(port));
            return new Harness(runtime, events, rpc);
        }

        public Task WaitForHeartbeatAsync()
        {
            return ReceiveUntilAsync(BridgeEventType.EventHeartbeat);
        }

        public Task<Response> SendAsync(Request request)
        {
            request.ProtocolVersion = 2;
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

        public async Task<BridgeEvent?> TryReceiveNonHeartbeatAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    var data = await events.ReceiveBinaryAsync(deadline - DateTime.UtcNow);
                    var bridgeEvent = BridgeEvent.Parser.ParseFrom(data);
                    if (bridgeEvent.Type != BridgeEventType.EventHeartbeat)
                    {
                        return bridgeEvent;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            return null;
        }

        public void Dispose()
        {
            events.Dispose();
            rpc.Dispose();
            Runtime.Dispose();
        }
    }
}
