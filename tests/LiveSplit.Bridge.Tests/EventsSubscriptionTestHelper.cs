using System;
using System.Threading;

namespace LiveSplit.Bridge.Tests;

internal static class EventsSubscriptionTestHelper
{
    internal static void WaitForCount(BridgeRuntime runtime, int expectedCount)
    {
        const int timeoutMilliseconds = 5000;
        Assert.True(
            SpinWait.SpinUntil(() => runtime.EventsSessionCount == expectedCount, timeoutMilliseconds),
            $"Expected {expectedCount} Events subscriptions, but observed {runtime.EventsSessionCount} after {timeoutMilliseconds} ms."
        );
    }
}
