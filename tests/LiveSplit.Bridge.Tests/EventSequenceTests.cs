namespace LiveSplit.Bridge.Tests;

public class EventSequenceTests
{
    [Fact]
    public void SettledSequenceAdvancesOnlyAfterSettlement()
    {
        var sequence = new EventSequence();

        var first = sequence.Begin();

        Assert.Equal(1UL, first);
        Assert.Equal(0UL, sequence.LastSettled);

        sequence.Settle(first);

        Assert.Equal(first, sequence.LastSettled);
    }

    [Fact]
    public void AssignedSequenceIsNotReusedAfterFailedDelivery()
    {
        var sequence = new EventSequence();

        var failed = sequence.Begin();
        sequence.Settle(failed);
        var next = sequence.Begin();

        Assert.Equal(1UL, failed);
        Assert.Equal(2UL, next);
        Assert.Equal(failed, sequence.LastSettled);
    }

    [Fact]
    public void RuntimeUsesSpecifiedHeartbeatAndSnapshotIntervals()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), BridgeRuntime.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), BridgeRuntime.PeriodicSnapshotInterval);
    }
}
