using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class EventConsistencyTests
{
    [Fact]
    public async Task SequentialRunCommitsProduceMonotonicSequenceAndRevisions()
    {
        const int commits = 20;

        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        var port = BridgeTestEndpoints.GetFreePort();
        using var runtime = new BridgeRuntime(state, port);

        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        await ReceiveUntilAsync(events, BridgeEventType.EventHeartbeat);

        // Start collecting concurrently, before the burst, so every published event
        // is drained from the socket while it is produced. The receiver signals once
        // it is about to read, so the burst never outruns the receiver.
        using var receiverStarted = new ManualResetEventSlim(false);
        var receiver = Task.Run(() => ReceiveRunChangedAsync(events, commits, receiverStarted));
        Assert.True(receiverStarted.Wait(TimeSpan.FromSeconds(5)), "receiver did not start.");

        for (var iteration = 0; iteration < commits; iteration++)
        {
            run.GameName = $"Change {iteration}";
            state.CallRunManuallyModified();
            runtime.Update();
        }

        var received = await receiver;

        Assert.Equal(commits, received.Count);

        // event_sequence and the revision captured in its TimerState describe the
        // same logical point, so both increase in lockstep without gaps or duplicates.
        for (var index = 0; index < received.Count; index++)
        {
            Assert.Equal((ulong)(index + 1), received[index].EventSequence);
            Assert.NotNull(received[index].TimerState);
            Assert.Equal((ulong)(index + 2), received[index].TimerState.RunRevision);
        }
    }

    [Fact]
    public void CoalescedRunChangesCommitASingleProjection()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        for (var iteration = 0; iteration < 25; iteration++)
        {
            state.CallRunManuallyModified();
        }

        runtime.Update();

        Assert.Equal(2UL, runtime.RunRevision);
    }

    private static async Task<List<BridgeEvent>> ReceiveRunChangedAsync(
        WebSocketTestClient client,
        int count,
        ManualResetEventSlim started)
    {
        var received = new List<BridgeEvent>(count);
        started.Set();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (received.Count < count && DateTime.UtcNow < deadline)
        {
            var data = await client.ReceiveBinaryAsync(TimeSpan.FromSeconds(5));
            var bridgeEvent = BridgeEvent.Parser.ParseFrom(data);
            if (bridgeEvent.Type == BridgeEventType.EventRunChanged)
            {
                received.Add(bridgeEvent);
            }
        }

        return received;
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
