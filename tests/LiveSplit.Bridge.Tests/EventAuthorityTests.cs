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
    public void TimerOperationsDoNotReadIconsOrSynchronizeUnrelatedState()
    {
        var run = new GuardedRun();
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        // A disposed icon throws if a fingerprint or PNG encoder touches it.
        var icon = new Bitmap(2, 2);
        icon.Dispose();
        run.GameIcon = icon;
        run[0].Icon = icon;
        var state = TestLiveSplitState.Create(run);
        using var form = state.Form;
        run.RejectFullReads = true;
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());
        run.GameName = "Unannounced change";
        run.Metadata.GetOrAddCustomVariable("variable").Value = "changed";
        state.CurrentComparison = "Best Segments";

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
            Assert.Equal(before + 1, runtime.Adapter.UiThreadDispatchCount);
            Assert.Equal(1UL, runtime.RunRevision);
            Assert.Equal(1UL, runtime.RuntimeRevision);
        }

        Assert.Equal(5UL, runtime.AttemptRevision);
        Assert.Equal(7UL, runtime.StateRevision);
        state.CallRunManuallyModified();
        Assert.Equal(2UL, runtime.RunRevision);
        runtime.ObserveExternalState();
        Assert.Equal(2UL, runtime.RuntimeRevision);
    }

    [Fact]
    public void SkipAndUndoAdvanceAttemptEvenWhenContentIsUnchanged()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        var state = TestLiveSplitState.Create(run);
        using var form = state.Form;
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());
        var model = new TimerModel { CurrentState = state };
        model.Start();
        model.SkipSplit();
        Assert.Equal(3UL, runtime.AttemptRevision);
        model.UndoSplit();
        Assert.Equal(4UL, runtime.AttemptRevision);
        Assert.All(run, segment => Assert.Null(segment.SplitTime.RealTime));
    }

    [Fact]
    public void ResetResponsePrecedesDeferredRunGenerationAndDisposedRuntimeIgnoresCallback()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        using var form = state.Form;
        _ = state.Form.Handle;
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());
        runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerStart);
        var before = runtime.Adapter.UiThreadDispatchCount;
        var response = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 2,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerReset },
        });
        Assert.True(response.Operation.Success, response.Operation.Message);
        Assert.Equal(before + 1, runtime.Adapter.UiThreadDispatchCount);
        Assert.Equal(1UL, response.Operation.TimerState.RunRevision);
        Assert.Equal(3UL, response.Operation.TimerState.AttemptRevision);
        Assert.Equal(1UL, runtime.RunRevision);
        Application.DoEvents();
        Assert.Equal(2UL, runtime.RunRevision);

        runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerStart);
        runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerReset);
        runtime.Dispose();
        Application.DoEvents();
        Assert.Equal(2UL, runtime.RunRevision);
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
        Application.DoEvents();
        Assert.Equal(2UL, runtime.RunRevision);
        Assert.Equal(1UL, runtime.RuntimeRevision);

        state.CurrentComparison = "Renamed";
        state.CallComparisonRenamed(EventArgs.Empty);
        state.CallRunManuallyModified();
        Assert.Equal(3UL, runtime.RunRevision);
        Assert.Equal(1UL, runtime.RuntimeRevision);
        Application.DoEvents();
        Assert.Equal(3UL, runtime.RunRevision);
        Assert.Equal(2UL, runtime.RuntimeRevision);
        runtime.ObserveExternalState();
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
