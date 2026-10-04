using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class AttemptRevisionTests
{
    [Fact]
    public void StartAdvancesAttemptRevisionAndAttemptCount()
    {
        var (runtime, state, timerModel, _) = Create();
        using var _r = runtime;

        var before = GetAttempt(runtime);
        timerModel.Start();

        var after = GetAttempt(runtime);
        Assert.Equal(before.AttemptRevision + 1, after.AttemptRevision);
        Assert.Equal(before.AttemptCount + 1, after.AttemptCount);
    }

    [Fact]
    public void SplitAdvancesAttemptRevisionAndChangesSplitTime()
    {
        var (runtime, state, timerModel, _) = Create();
        using var _r = runtime;

        timerModel.Start();
        var before = GetAttempt(runtime);

        timerModel.Split();

        var after = GetAttempt(runtime);
        Assert.Equal(before.AttemptRevision + 1, after.AttemptRevision);
        Assert.True(after.Segments[0].SplitTime.HasRealTimeTicks);
    }

    [Fact]
    public void SkipAdvancesAttemptRevisionAndChangesSegmentState()
    {
        var (runtime, state, timerModel, run) = Create();
        using var _r = runtime;

        // Simulate an in-progress attempt that carries a split time for the current
        // segment, so that Skip observably changes AttemptState content.
        run[0].SplitTime = new Time(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4));
        timerModel.Start();
        var before = GetAttempt(runtime);

        timerModel.SkipSplit();

        var after = GetAttempt(runtime);
        Assert.Equal(before.AttemptRevision + 1, after.AttemptRevision);
        Assert.False(after.Segments[0].SplitTime.HasRealTimeTicks);
    }

    [Fact]
    public void UndoAdvancesAttemptRevision()
    {
        var (runtime, state, timerModel, _) = Create();
        using var _r = runtime;

        timerModel.Start();
        timerModel.Split();
        var before = GetAttempt(runtime);

        timerModel.UndoSplit();

        var after = GetAttempt(runtime);
        Assert.Equal(before.AttemptRevision + 1, after.AttemptRevision);
    }

    [Fact]
    public void ResetAdvancesAttemptRevisionAndClearsSplitTime()
    {
        var (runtime, state, timerModel, _) = Create();
        using var _r = runtime;

        timerModel.Start();
        timerModel.Split();
        var before = GetAttempt(runtime);

        _ = state.Form.Handle;
        timerModel.Reset();
        System.Windows.Forms.Application.DoEvents();

        var after = GetAttempt(runtime);
        Assert.Equal(before.AttemptRevision + 1, after.AttemptRevision);
        Assert.False(after.Segments[0].SplitTime.HasRealTimeTicks);
    }

    [Fact]
    public void NoOpTimerOperationDoesNotAdvanceAttemptRevision()
    {
        var (runtime, state, timerModel, _) = Create();
        using var _r = runtime;

        // Timer is not running, so Split is a no-op.
        var before = GetAttempt(runtime);
        timerModel.Split();

        var after = GetAttempt(runtime);
        Assert.Equal(before.AttemptRevision, after.AttemptRevision);
    }

    private static (BridgeRuntime Runtime, LiveSplitState State, TimerModel TimerModel, Run Run) Create()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        var state = TestLiveSplitState.Create(run);
        var timerModel = new TimerModel { CurrentState = state };
        var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());
        return (runtime, state, timerModel, run);
    }

    private static AttemptState GetAttempt(BridgeRuntime runtime)
    {
        var response = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            GetAttempt = new GetAttemptRequest(),
        });

        return response.GetAttempt.Attempt;
    }
}
