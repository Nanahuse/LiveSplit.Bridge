using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class EventConsistencyTests
{
    [Fact]
    public async Task ConcurrentRunChangesProduceMonotonicSequenceAndConsistentRevisions()
    {
        const int threadCount = 8;
        const int perThread = 25;
        const int total = threadCount * perThread;

        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        var port = BridgeTestEndpoints.GetFreePort();
        using var runtime = new BridgeRuntime(state, port);

        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        await ReceiveUntilAsync(events, BridgeEventType.EventHeartbeat);

        var workers = new Thread[threadCount];
        for (var thread = 0; thread < threadCount; thread++)
        {
            workers[thread] = new Thread(() =>
            {
                for (var iteration = 0; iteration < perThread; iteration++)
                {
                    state.CallRunManuallyModified();
                }
            })
            {
                IsBackground = true
            };
            workers[thread].Start();
        }

        foreach (var worker in workers)
        {
            worker.Join();
        }

        var received = new List<BridgeEvent>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (received.Count < total && DateTime.UtcNow < deadline)
        {
            var data = await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(3));
            var bridgeEvent = BridgeEvent.Parser.ParseFrom(data);
            if (bridgeEvent.Type == BridgeEventType.EventRunChanged)
            {
                received.Add(bridgeEvent);
            }
        }

        Assert.Equal(total, received.Count);

        // event_sequence and the revision captured in its TimerState describe the
        // same logical point, so both increase in lockstep without gaps.
        for (var index = 0; index < received.Count; index++)
        {
            Assert.Equal((ulong)(index + 1), received[index].EventSequence);
            Assert.NotNull(received[index].TimerState);
            Assert.Equal((ulong)(index + 2), received[index].TimerState.RunRevision);
        }
    }

    private static async Task<BridgeEvent> ReceiveUntilAsync(WebSocketTestClient client, BridgeEventType type)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var data = await client.ReceiveBinaryAsync(TimeSpan.FromSeconds(3));
            var bridgeEvent = BridgeEvent.Parser.ParseFrom(data);
            if (bridgeEvent.Type == type)
            {
                return bridgeEvent;
            }
        }

        throw new TimeoutException($"Did not receive {type}.");
    }
}
