using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.Options;
using ProtocolTimingMethod = LiveSplit.Bridge.Protocol.V2.TimingMethod;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class DetailedRpcSyncTests
{
    [Fact]
    public void GetRunReturnsChangedContentWithUpdatedRevision()
    {
        var state = CreateState(out var run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        // Change Run content without raising RunManuallyModified or running any monitor.
        run.GameName = "Directly Changed";

        var runState = GetRun(runtime);

        Assert.Equal("Directly Changed", runState.GameName);
        Assert.Equal(2UL, runState.RunRevision);
        Assert.Equal(2UL, runtime.RunRevision);
    }

    [Fact]
    public async Task GetRunPublishesRunChangedEventOnce()
    {
        var state = CreateState(out var run);
        var port = BridgeTestEndpoints.GetFreePort();
        using var runtime = new BridgeRuntime(state, port);

        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        await ReceiveUntilAsync(events, BridgeEventType.EventHeartbeat);

        run.GameName = "Directly Changed";
        GetRun(runtime);

        var runChanged = await ReceiveUntilAsync(events, BridgeEventType.EventRunChanged);
        Assert.Equal(2UL, runChanged.TimerState.RunRevision);

        // Re-querying must not republish the same change.
        GetRun(runtime);
        var duplicate = await TryReceiveNonHeartbeatAsync(events, TimeSpan.FromSeconds(1));
        Assert.Null(duplicate);
    }

    [Fact]
    public void GetAttemptReturnsChangedContentWithUpdatedRevision()
    {
        var state = CreateState(out var run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        // Change AttemptState content directly (no timer event / monitoring).
        run[0].SplitTime = new Time(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));

        var response = GetAttempt(runtime);

        var segment = Assert.Single(response.Segments);
        Assert.True(segment.SplitTime.HasRealTimeTicks);
        Assert.Equal(TimeSpan.FromSeconds(3).Ticks, segment.SplitTime.RealTimeTicks);
        Assert.Equal(2UL, response.AttemptRevision);
        Assert.Equal(2UL, runtime.AttemptRevision);
    }

    [Fact]
    public void GetRuntimeStateReturnsChangedContentWithUpdatedRevision()
    {
        var state = CreateState(out _);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        state.CurrentComparison = "Best Segments";
        state.CurrentTimingMethod = LiveSplit.Model.TimingMethod.GameTime;

        var response = GetRuntimeState(runtime);

        Assert.Equal("Best Segments", response.CurrentComparison);
        Assert.Equal(ProtocolTimingMethod.GameTime, response.CurrentTimingMethod);
        Assert.Equal(2UL, response.RuntimeRevision);
        Assert.Equal(2UL, runtime.RuntimeRevision);
    }

    [Fact]
    public void GetRuntimeStateSyncsMetadataCustomVariables()
    {
        var state = CreateState(out var run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        run.Metadata.GetOrAddCustomVariable("custom").Value = "changed";

        var response = GetRuntimeState(runtime);

        Assert.Equal("changed", response.CustomVariables["custom"]);
        Assert.Equal(2UL, response.RuntimeRevision);
    }

    [Fact]
    public void AttachSynchronizesAllRevisionsBeforeDetailedQueries()
    {
        var state = CreateState(out var run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        // Changes made before any monitoring runs.
        run.GameName = "Changed Game";
        run[0].SplitTime = new Time(TimeSpan.FromSeconds(1), null);
        state.CurrentComparison = "Best Segments";
        run.Metadata.GetOrAddCustomVariable("custom").Value = "changed";

        var attach = Handle(runtime, new Request { RequestId = 1, Attach = new AttachRequest() });
        var timerState = attach.Attach.TimerState;

        var runState = GetRun(runtime);
        var attemptState = GetAttempt(runtime);
        var runtimeState = GetRuntimeState(runtime);

        Assert.Equal(timerState.RunRevision, runState.RunRevision);
        Assert.Equal(timerState.AttemptRevision, attemptState.AttemptRevision);
        Assert.Equal(timerState.RuntimeRevision, runtimeState.RuntimeRevision);
    }

    [Fact]
    public void DetailedRpcSyncDoesNotRepublishAfterObservation()
    {
        var state = CreateState(out var run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        run.GameName = "Changed Game";
        state.CurrentComparison = "Best Segments";

        var runRevision = GetRun(runtime).RunRevision;
        var runtimeRevision = GetRuntimeState(runtime).RuntimeRevision;

        // The detailed sync already refreshed the observed snapshots.
        runtime.ObserveExternalState();
        runtime.ObserveContentState();

        Assert.Equal(runRevision, runtime.RunRevision);
        Assert.Equal(runtimeRevision, runtime.RuntimeRevision);
    }

    [Fact]
    public void GetTimerStateKeepsRevisionsInSyncWithoutFullContentSync()
    {
        var state = CreateState(out var run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        // get_timer_state stays lightweight and must not advance revisions by itself.
        run.GameName = "Changed Game";
        var timerState = GetTimerState(runtime);

        Assert.Equal(1UL, timerState.RunRevision);
        Assert.Equal(1UL, timerState.AttemptRevision);
        Assert.Equal(1UL, timerState.RuntimeRevision);
    }

    private static LiveSplitState CreateState(out Run run)
    {
        run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));

        var settings = new Settings
        {
            HotkeyProfiles = new Dictionary<string, HotkeyProfile>
            {
                ["Default"] = new HotkeyProfile { GlobalHotkeysEnabled = true },
            },
        };

        var state = TestLiveSplitState.Create(run, settings);
        state.CurrentHotkeyProfile = "Default";
        state.CurrentComparison = "Personal Best";
        return state;
    }

    private static Response Handle(BridgeRuntime runtime, Request request)
    {
        request.ProtocolVersion = 2;
        return runtime.HandleRequest(request);
    }

    private static RunState GetRun(BridgeRuntime runtime)
    {
        return Handle(runtime, new Request { RequestId = 1, GetRun = new GetRunRequest() }).GetRun.Run;
    }

    private static AttemptState GetAttempt(BridgeRuntime runtime)
    {
        return Handle(runtime, new Request { RequestId = 1, GetAttempt = new GetAttemptRequest() })
            .GetAttempt.Attempt;
    }

    private static RuntimeState GetRuntimeState(BridgeRuntime runtime)
    {
        return Handle(runtime, new Request { RequestId = 1, GetRuntimeState = new GetRuntimeStateRequest() })
            .GetRuntimeState.RuntimeState;
    }

    private static TimerState GetTimerState(BridgeRuntime runtime)
    {
        return Handle(runtime, new Request { RequestId = 1, GetTimerState = new GetTimerStateRequest() })
            .GetTimerState.TimerState;
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

    private static async Task<BridgeEvent?> TryReceiveNonHeartbeatAsync(
        WebSocketTestClient client,
        TimeSpan timeout)
    {
        try
        {
            while (true)
            {
                var data = await client.ReceiveBinaryAsync(timeout);
                var bridgeEvent = BridgeEvent.Parser.ParseFrom(data);
                if (bridgeEvent.Type != BridgeEventType.EventHeartbeat)
                {
                    return bridgeEvent;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
