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
        using var runtime = new V2BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

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
        using var runtime = new V2BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        run.GameName = "Changed";
        state.CallRunManuallyModified();

        Assert.Equal(2UL, runtime.RunRevision);
    }

    [Fact]
    public void UnannouncedRunAndAttemptChangesAreNotPolled()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        using var runtime = new V2BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());
        run.CategoryName = "Any%";
        run[0].SplitTime = new Time(TimeSpan.FromSeconds(1), null);
        for (var iteration = 0; iteration < 50; iteration++)
        {
            runtime.ObserveExternalState();
        }
        Assert.Equal(1UL, runtime.RunRevision);
        Assert.Equal(1UL, runtime.AttemptRevision);
    }
}
