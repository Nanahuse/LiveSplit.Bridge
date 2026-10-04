using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class RunMonitoringTests
{
    [Fact]
    public void RepeatedHighFrequencyObservationDoesNotChangeRunRevision()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        var state = TestLiveSplitState.Create(run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        for (var iteration = 0; iteration < 50; iteration++)
        {
            runtime.ObserveExternalState();
        }

        Assert.Equal(1UL, runtime.RunRevision);
        Assert.Equal(1UL, runtime.AttemptRevision);
    }

    [Fact]
    public void RunManuallyModifiedIsDetectedWithoutHighFrequencyScan()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        run.GameName = "Changed";
        state.CallRunManuallyModified();

        Assert.Equal(2UL, runtime.RunRevision);
    }

    [Fact]
    public void RunChangeIsDetectedByContentFallback()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        // Simulate a change that never raises RunManuallyModified.
        run.CategoryName = "Any%";
        runtime.ObserveContentState();

        Assert.Equal(2UL, runtime.RunRevision);
    }

    [Fact]
    public void ContentFallbackIsThrottledBetweenWindows()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        run.GameName = "First";
        runtime.ObserveContentState();
        Assert.Equal(2UL, runtime.RunRevision);

        // A second fallback within the same window must be skipped.
        run.GameName = "Second";
        runtime.ObserveContentState();
        Assert.Equal(2UL, runtime.RunRevision);
    }

    [Fact]
    public void ResetWithPersonalBestUpdateIsDetectedByFallback()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        var state = TestLiveSplitState.Create(run);
        var timerModel = new TimerModel { CurrentState = state };
        state.RegisterTimerModel(timerModel);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        var before = runtime.RunRevision;

        timerModel.Start();
        timerModel.Split();
        timerModel.Split();
        timerModel.Reset();

        // Force the fallback window to elapse.
        Thread.Sleep(BridgeRuntime.ContentFallbackInterval + TimeSpan.FromMilliseconds(100));
        runtime.ObserveContentState();

        Assert.True(runtime.RunRevision > before);
    }

    [Fact]
    public void ResetWithoutRunContentChangeKeepsRunRevisionWithFallback()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        var timerModel = new TimerModel { CurrentState = state };
        state.RegisterTimerModel(timerModel);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        timerModel.Start();
        timerModel.Reset();

        Thread.Sleep(BridgeRuntime.ContentFallbackInterval + TimeSpan.FromMilliseconds(100));
        runtime.ObserveContentState();

        Assert.Equal(1UL, runtime.RunRevision);
    }
}
