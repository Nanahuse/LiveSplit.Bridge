using System.Drawing;
using System.Windows.Forms;
using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class EventAuthorityTests
{
    [Fact]
    public void TimerOperationsDoNotBuildRunOrRuntimeProjections()
    {
        var run = new GuardedRun();
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        var icon = new Bitmap(2, 2);
        run.GameIcon = icon;
        run[0].Icon = icon;
        var state = TestLiveSplitState.Create(run);
        using var form = state.Form;
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        // From here on any attempt to rebuild the run or read its comparisons must
        // fail, so timer operations can be proven not to touch the run.
        icon.Dispose();
        run.RejectFullReads = true;

        foreach (var operation in new[] { TimerOperationType.TimerStart,
            TimerOperationType.TimerSkip, TimerOperationType.TimerUndo,
            TimerOperationType.TimerSplit, TimerOperationType.TimerPause,
            TimerOperationType.TimerResume })
        {
            var before = runtime.Adapter.UiThreadDispatchCount;
            var response = runtime.HandleRequest(new Request
            {
                ProtocolVersion = 2,
                TimerOperation = new TimerOperationRequest { Operation = operation },
            });
            Assert.Null(response.Error);
            Assert.True(response.Operation.Success, response.Operation.Message);
            Assert.NotNull(response.Operation.TimerState);
            // Timer control no longer dispatches to the UI thread.
            Assert.Equal(before, runtime.Adapter.UiThreadDispatchCount);
            Assert.Equal(1UL, runtime.RunRevision);
            Assert.Equal(1UL, runtime.RuntimeRevision);
        }

        // Timer transitions only marked the attempt dirty; they never committed a
        // projection and never synchronized the unrelated run/runtime state.
        Assert.Equal(1UL, runtime.AttemptRevision);
        Assert.Equal(1UL, runtime.RunRevision);
        Assert.Equal(1UL, runtime.RuntimeRevision);
        Assert.Equal(7UL, runtime.StateRevision);
    }

    [Fact]
    public void SkipAndUndoAdvanceAttemptOnlyAfterCommit()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        var state = TestLiveSplitState.Create(run);
        using var form = state.Form;
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());
        var model = new TimerModel { CurrentState = state };
        model.Start();
        runtime.Update();
        model.SkipSplit();
        runtime.Update();
        Assert.Equal(3UL, runtime.AttemptRevision);
        model.UndoSplit();
        runtime.Update();
        Assert.Equal(4UL, runtime.AttemptRevision);
        Assert.All(run, segment => Assert.Null(segment.SplitTime.RealTime));
    }

    [Fact]
    public void ResetTimerEventKeepsGenerationsUntilCommit()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        using var form = state.Form;
        _ = state.Form.Handle;
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());
        runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerStart);
        runtime.Update();
        var before = runtime.Adapter.UiThreadDispatchCount;
        var response = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 2,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerReset },
        });
        Assert.True(response.Operation.Success, response.Operation.Message);
        // Reset no longer dispatches to the UI thread.
        Assert.Equal(before, runtime.Adapter.UiThreadDispatchCount);
        // EVENT_TIMER_RESET is immediate but does not advance the run/attempt
        // generations; the projections are not complete until FixSplits has run.
        Assert.Equal(1UL, response.Operation.TimerState.RunRevision);
        Assert.Equal(2UL, response.Operation.TimerState.AttemptRevision);
        Assert.Equal(1UL, runtime.RunRevision);
        Assert.Equal(2UL, runtime.AttemptRevision);
        // No deferred callback advances the run revision on the next turn.
        Application.DoEvents();
        Assert.Equal(1UL, runtime.RunRevision);
        // The later commit publishes the new generations.
        runtime.Update();
        Assert.Equal(2UL, runtime.RunRevision);
        Assert.Equal(3UL, runtime.AttemptRevision);
    }

    [Fact]
    public void ComparisonRenameInvalidatesRunOnceAndOnlyChangedSelectionInvalidatesRuntime()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        using var form = state.Form;
        _ = state.Form.Handle;
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());
        state.CallComparisonRenamed(EventArgs.Empty);
        state.CallRunManuallyModified();
        runtime.Update();
        Assert.Equal(2UL, runtime.RunRevision);
        Assert.Equal(1UL, runtime.RuntimeRevision);

        state.CurrentComparison = "Renamed";
        state.CallComparisonRenamed(EventArgs.Empty);
        state.CallRunManuallyModified();
        runtime.Update();
        Assert.Equal(3UL, runtime.RunRevision);
        Assert.Equal(2UL, runtime.RuntimeRevision);
        runtime.Update();
        Assert.Equal(3UL, runtime.RunRevision);
        Assert.Equal(2UL, runtime.RuntimeRevision);
    }

    private sealed class GuardedRun : Run, IRun
    {
        public GuardedRun() : base(new StandardComparisonGeneratorsFactory()) { }
        public bool RejectFullReads { get; set; }

        IEnumerable<string> IRun.Comparisons => RejectFullReads
            ? throw new InvalidOperationException("Comparison list was scanned.") : Comparisons;

        IList<Attempt> IRun.AttemptHistory
        {
            get => RejectFullReads
                ? throw new InvalidOperationException("Attempt history was scanned.") : AttemptHistory;
            set => AttemptHistory = value;
        }

        ISegment IList<ISegment>.this[int index]
        {
            get => RejectFullReads && index != 0
                ? throw new InvalidOperationException("Unrelated segment was read.") : this[index];
            set => this[index] = value;
        }
    }
}
