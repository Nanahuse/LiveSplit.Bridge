using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class RunRevisionTests
{
    [Fact]
    public void RunRevisionAdvancesForEveryRunModifiedEvent()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        var state = TestLiveSplitState.Create(run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        var initialStateRevision = runtime.StateRevision;
        Assert.Equal(1UL, runtime.RunRevision);

        var attached = Handle(runtime, new Request { RequestId = 1, Attach = new AttachRequest() });
        Assert.Equal(1UL, attached.Attach.TimerState.RunRevision);
        Assert.Equal(initialStateRevision, attached.Attach.TimerState.StateRevision);

        // RunManuallyModified advances the generation even with identical content.
        state.CallRunManuallyModified();
        runtime.ObserveExternalState();
        Assert.Equal(2UL, runtime.RunRevision);
        Assert.Equal(initialStateRevision, runtime.StateRevision);

        run.GameName = "Changed Game";
        state.CallRunManuallyModified();

        Assert.Equal(3UL, runtime.RunRevision);
        // Run-only changes do not advance state_revision.
        Assert.Equal(initialStateRevision, runtime.StateRevision);

        var afterChange = Handle(runtime, new Request { RequestId = 2, GetRun = new GetRunRequest() });
        Assert.Equal(3UL, afterChange.GetRun.Run.RunRevision);
        Assert.Equal("Changed Game", afterChange.GetRun.Run.GameName);
    }

    [Fact]
    public void GetRunReturnsTheCurrentlyLoadedRun()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory())
        {
            GameName = "First Game",
            CategoryName = "First Category",
        };
        run.Add(new Segment("First"));
        var state = TestLiveSplitState.Create(run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        var first = Handle(runtime, new Request { RequestId = 1, GetRun = new GetRunRequest() });
        Assert.Equal("First Game", first.GetRun.Run.GameName);

        var replacement = new Run(new StandardComparisonGeneratorsFactory())
        {
            GameName = "Second Game",
            CategoryName = "Second Category",
        };
        replacement.Add(new Segment("Second"));
        state.Run = replacement;
        state.CallRunManuallyModified();

        var second = Handle(runtime, new Request { RequestId = 2, GetRun = new GetRunRequest() });
        Assert.Equal("Second Game", second.GetRun.Run.GameName);
        Assert.Equal("Second Category", second.GetRun.Run.CategoryName);
        Assert.Equal("Second", Assert.Single(second.GetRun.Run.Segments).Name);
        Assert.Equal(runtime.RunRevision, second.GetRun.Run.RunRevision);
    }

    [Fact]
    public void SegmentNameChangeAdvancesRunRevision()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        run[0].Name = "Renamed";
        state.CallRunManuallyModified();

        Assert.Equal(2UL, runtime.RunRevision);
        var response = Handle(runtime, new Request { RequestId = 1, GetRun = new GetRunRequest() });
        Assert.Equal("Renamed", response.GetRun.Run.Segments[0].Name);
    }

    [Fact]
    public void ResetWithPersonalBestUpdateAdvancesRunRevision()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        var state = TestLiveSplitState.Create(run);
        var timerModel = new TimerModel { CurrentState = state };
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        var originalRunRevision = runtime.RunRevision;

        // Complete a run so that Reset's FixSplits records a Personal Best.
        timerModel.Start();
        timerModel.Split();
        timerModel.Split();
        runtime.ObserveExternalState();

        _ = state.Form.Handle;
        timerModel.Reset();
        System.Windows.Forms.Application.DoEvents();
        runtime.ObserveExternalState();

        Assert.True(runtime.RunRevision > originalRunRevision);
    }

    [Fact]
    public void ResetWithoutRunContentChangeStillAdvancesRunGeneration()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        var timerModel = new TimerModel { CurrentState = state };
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        // Start but never split: Reset has no split times to fold into the run.
        timerModel.Start();
        runtime.ObserveExternalState();
        var beforeReset = runtime.RunRevision;

        _ = state.Form.Handle;
        timerModel.Reset();
        System.Windows.Forms.Application.DoEvents();
        runtime.ObserveExternalState();

        Assert.Equal(beforeReset + 1, runtime.RunRevision);
    }

    [Fact]
    public void ComparisonRenameAdvancesRunExactlyOnceAndRuntimeOnlyWhenSelectionChanges()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        state.CurrentComparison = "Personal Best";
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        // Rename that leaves the current comparison unchanged: only run_revision moves.
        state.CallComparisonRenamed(EventArgs.Empty);
        state.CallRunManuallyModified();
        Assert.Equal(2UL, runtime.RunRevision);
        Assert.Equal(1UL, runtime.RuntimeRevision);

        // Rename of the current comparison: run_revision and runtime_revision each move once.
        state.CurrentComparison = "Renamed";
        state.CallComparisonRenamed(EventArgs.Empty);
        state.CallRunManuallyModified();
        Assert.Equal(3UL, runtime.RunRevision);
        Assert.Equal(2UL, runtime.RuntimeRevision);
    }

    [Fact]
    public void NormalRunChangeDoesNotSynchronizeRuntimeAndDoesNotLeakRenamePending()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        state.CurrentComparison = "Personal Best";
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        state.CallComparisonRenamed(EventArgs.Empty);
        state.CallRunManuallyModified();
        Assert.Equal(2UL, runtime.RunRevision);
        Assert.Equal(1UL, runtime.RuntimeRevision);

        // A plain run change must not carry the earlier rename's runtime check.
        run.GameName = "Changed";
        state.CallRunManuallyModified();
        Assert.Equal(3UL, runtime.RunRevision);
        Assert.Equal(1UL, runtime.RuntimeRevision);

        runtime.ObserveExternalState();
        Assert.Equal(1UL, runtime.RuntimeRevision);
    }

    [Fact]
    public void RunChangePublishesRunChangedEventWithUpdatedRevision()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        run.GameName = "Updated Game";
        state.CallRunManuallyModified();

        Assert.Equal(2UL, runtime.RunRevision);
    }

    [Fact]
    public async Task RunChangePublishesRunChangedEventCarryingUpdatedRevision()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        var port = BridgeTestEndpoints.GetFreePort();
        using var runtime = new BridgeRuntime(state, port);

        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        await ReceiveUntilAsync(events, BridgeEventType.EventHeartbeat);

        run.GameName = "Updated Game";
        state.CallRunManuallyModified();

        var runChanged = await ReceiveUntilAsync(events, BridgeEventType.EventRunChanged);
        Assert.NotNull(runChanged.TimerState);
        Assert.Equal(2UL, runChanged.TimerState.RunRevision);
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

    private static Response Handle(BridgeRuntime runtime, Request request)
    {
        request.ProtocolVersion = 2;
        return runtime.HandleRequest(request);
    }
}
