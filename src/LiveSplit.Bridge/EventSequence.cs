using System.Threading;

namespace LiveSplit.Bridge;

internal sealed class EventSequence
{
    private long lastAssigned;
    private long lastSettled;

    public ulong LastSettled => unchecked((ulong)Interlocked.Read(ref lastSettled));

    public ulong Begin()
    {
        return unchecked((ulong)Interlocked.Increment(ref lastAssigned));
    }

    public void Settle(ulong sequence)
    {
        Interlocked.Exchange(ref lastSettled, unchecked((long)sequence));
    }
}
