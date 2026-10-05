using System.Windows.Forms;
using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class UiOperationTests
{
    [Fact]
    public async Task SplitResponseIsCapturedBeforeTheNextUiTurn()
    {
        using var ui = await UiHost.CreateAsync();
        await ui.InvokeAsync(() =>
        {
            ui.Runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerStart);
            ui.State.OnSplit += (_, _) => ui.State.Form.BeginInvoke((Action)(() =>
                ui.Runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerSkip)));
        });
        using var rpc = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
        var response = await rpc.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerSplit },
        }, TimeSpan.FromSeconds(5));
        Assert.True(response.Operation.Success, response.Operation.Message);
        Assert.Equal(1, response.Operation.TimerState.SplitIndex);
        Assert.Equal(3UL, response.Operation.TimerState.AttemptRevision);
        await ui.InvokeAsync(() => Assert.Equal(2, ui.State.CurrentSplitIndex));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetPublishesSingleTimerResetCarryingRunAndAttemptRevisions(bool rpcReset)
    {
        using var ui = await UiHost.CreateAsync();
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(ui.Port));
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);
        await ui.InvokeAsync(() => ui.Runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerStart));
        var started = await ReadEventAsync(events, BridgeEventType.EventTimerStarted);
        var stateBefore = started.TimerState.StateRevision;

        if (rpcReset)
        {
            using var rpc = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
            var response = await rpc.SendRequestAsync(new Request
            {
                ProtocolVersion = 2,
                TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerReset },
            }, TimeSpan.FromSeconds(5));
            Assert.True(response.Operation.Success);
            Assert.Equal(2UL, response.Operation.TimerState.RunRevision);
            Assert.Equal(3UL, response.Operation.TimerState.AttemptRevision);
        }
        else
        {
            await ui.InvokeAsync(() => new TimerModel { CurrentState = ui.State }.Reset());
        }

        // Reset is expressed by a single EVENT_TIMER_RESET; no separate
        // EVENT_RUN_CHANGED follows. The event carries all updated generations.
        var reset = await ReadEventAsync(events, BridgeEventType.EventTimerReset);
        Assert.Equal(2UL, reset.TimerState.RunRevision);
        Assert.Equal(3UL, reset.TimerState.AttemptRevision);
        Assert.True(reset.TimerState.StateRevision > stateBefore);
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);
    }

    [Fact]
    public async Task SlowGetRunDoesNotBlockSplitFromAnotherConnection()
    {
        using var ui = await UiHost.CreateAsync();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        ui.Runtime.Adapter.BeforeBuildRunState = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        using var rpcRun = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
        using var rpcSplit = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
        await ui.InvokeAsync(() => ui.Runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerStart));

        var getRunTask = rpcRun.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            GetRun = new GetRunRequest(),
        }, TimeSpan.FromSeconds(15));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "get_run did not start.");

        // get_run is holding the UI thread, but Split from another connection must
        // still complete because it never takes the query path or a global lock.
        var splitResponse = await rpcSplit.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 2,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerSplit },
        }, TimeSpan.FromSeconds(5));
        Assert.True(splitResponse.Operation.Success, splitResponse.Operation.Message);
        Assert.Equal(1, splitResponse.Operation.TimerState.SplitIndex);

        release.Set();
        var runResponse = await getRunTask;
        Assert.NotNull(runResponse.GetRun);
    }

    [Fact]
    public async Task GetRunDuringResetFixSplitsDoesNotReturnIntermediateState()
    {
        using var ui = await UiHost.CreateAsync();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        ui.Runtime.ResetBarrier = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        using var rpcReset = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
        using var rpcRun = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
        await ui.InvokeAsync(() => ui.Runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerStart));

        var resetTask = rpcReset.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerReset },
        }, TimeSpan.FromSeconds(15));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "Reset did not reach the FixSplits window.");

        // run_revision is already advanced but FixSplits has not run. A stable
        // snapshot is not possible, so the query must fail instead of returning
        // the intermediate run content with the new revision.
        var duringReset = await rpcRun.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 2,
            GetRun = new GetRunRequest(),
        }, TimeSpan.FromSeconds(10));
        Assert.NotNull(duringReset.Error);
        Assert.Null(duringReset.GetRun);
        Assert.Equal(103, duringReset.Error.Code);

        release.Set();
        var resetResponse = await resetTask;
        Assert.True(resetResponse.Operation.Success, resetResponse.Operation.Message);

        var afterReset = await rpcRun.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 3,
            GetRun = new GetRunRequest(),
        }, TimeSpan.FromSeconds(10));
        Assert.NotNull(afterReset.GetRun);
        Assert.True(afterReset.GetRun.Run.RunRevision >= 2UL);
    }

    [Fact]
    public async Task GetAttemptDuringSplitDoesNotReturnIntermediateState()
    {
        using var ui = await UiHost.CreateAsync();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        ui.Runtime.SplitBarrier = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        using var rpcSplit = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
        using var rpcAttempt = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
        await ui.InvokeAsync(() => ui.Runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerStart));

        var splitTask = rpcSplit.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerSplit },
        }, TimeSpan.FromSeconds(15));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "Split did not reach the mutation window.");

        var duringSplit = await rpcAttempt.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 2,
            GetAttempt = new GetAttemptRequest(),
        }, TimeSpan.FromSeconds(10));
        Assert.NotNull(duringSplit.Error);
        Assert.Null(duringSplit.GetAttempt);
        Assert.Equal(103, duringSplit.Error.Code);

        release.Set();
        var splitResponse = await splitTask;
        Assert.True(splitResponse.Operation.Success, splitResponse.Operation.Message);

        var afterSplit = await rpcAttempt.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 3,
            GetAttempt = new GetAttemptRequest(),
        }, TimeSpan.FromSeconds(10));
        Assert.NotNull(afterSplit.GetAttempt);
        Assert.True(afterSplit.GetAttempt.Attempt.AttemptRevision >= 3UL);
        Assert.True(afterSplit.GetAttempt.Attempt.Segments[0].SplitTime.HasRealTimeTicks);
    }

    [Fact]
    public async Task RenamePublishesRunThenRuntimeWithoutDuplicateRunEvent()
    {
        using var ui = await UiHost.CreateAsync();
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(ui.Port));
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);
        await ui.InvokeAsync(() =>
        {
            ui.State.CurrentComparison = "Renamed";
            ui.State.CallComparisonRenamed(EventArgs.Empty);
            ui.State.CallRunManuallyModified();
        });
        var run = await ReadEventAsync(events, BridgeEventType.EventRunChanged);
        var runtime = await ReadEventAsync(events, BridgeEventType.EventRuntimeChanged);
        // RUN_CHANGED and RUNTIME_CHANGED are consecutive, with no second run event.
        Assert.Equal(run.EventSequence + 1, runtime.EventSequence);
        Assert.Equal(2UL, run.TimerState.RunRevision);
        Assert.Equal(2UL, runtime.TimerState.RunRevision);
        Assert.Equal(2UL, runtime.TimerState.RuntimeRevision);
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);
    }

    [Fact]
    public async Task UnrelatedComparisonRenamePublishesOnlyRunChanged()
    {
        using var ui = await UiHost.CreateAsync();
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(ui.Port));
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);
        await ui.InvokeAsync(() =>
        {
            // The current comparison is unchanged by this rename.
            ui.State.CallComparisonRenamed(EventArgs.Empty);
            ui.State.CallRunManuallyModified();
        });
        var run = await ReadEventAsync(events, BridgeEventType.EventRunChanged);
        Assert.Equal(2UL, run.TimerState.RunRevision);
        Assert.Equal(1UL, run.TimerState.RuntimeRevision);
        // A RUNTIME_CHANGED here would fail this heartbeat read.
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);
    }

    [Fact]
    public async Task NormalRunChangePublishesOnlyRunChangedAndDoesNotLeakPendingRename()
    {
        using var ui = await UiHost.CreateAsync();
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(ui.Port));
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);

        await ui.InvokeAsync(() =>
        {
            ui.State.CallComparisonRenamed(EventArgs.Empty);
            ui.State.CallRunManuallyModified();
        });
        await ReadEventAsync(events, BridgeEventType.EventRunChanged);
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);

        await ui.InvokeAsync(() =>
        {
            ui.State.Run.GameName = "Changed";
            ui.State.CallRunManuallyModified();
        });
        var run = await ReadEventAsync(events, BridgeEventType.EventRunChanged);
        Assert.Equal(3UL, run.TimerState.RunRevision);
        Assert.Equal(1UL, run.TimerState.RuntimeRevision);
        // The earlier rename pending state must not survive into this change.
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);
    }

    private static async Task<BridgeEvent> ReadEventAsync(WebSocketTestClient client, BridgeEventType expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var result = BridgeEvent.Parser.ParseFrom(await client.ReceiveBinaryAsync(deadline - DateTime.UtcNow));
            if (result.Type == BridgeEventType.EventHeartbeat && expected != result.Type)
                continue;
            Assert.Equal(expected, result.Type);
            return result;
        }
        throw new TimeoutException($"Did not receive {expected}.");
    }

    private sealed class UiHost : IDisposable
    {
        private Thread thread = null!;
        public LiveSplitState State { get; private set; } = null!;
        public BridgeRuntime Runtime { get; private set; } = null!;
        public int Port { get; } = BridgeTestEndpoints.GetFreePort();

        public static async Task<UiHost> CreateAsync()
        {
            var host = new UiHost();
            var ready = new TaskCompletionSource<UiHost>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.thread = new Thread(() =>
            {
                try
                {
                    var run = new Run(new StandardComparisonGeneratorsFactory());
                    for (var i = 0; i < 3; i++) run.Add(new Segment($"Segment {i}"));
                    host.State = TestLiveSplitState.Create(run);
                    using var form = host.State.Form;
                    _ = form.Handle;
                    using var runtime = new BridgeRuntime(host.State, host.Port);
                    host.Runtime = runtime;
                    form.BeginInvoke((Action)(() => ready.SetResult(host)));
                    Application.Run();
                }
                catch (Exception ex)
                {
                    ready.TrySetException(ex);
                }
            }) { IsBackground = true };
            host.thread.SetApartmentState(ApartmentState.STA);
            host.thread.Start();
            return await ready.Task;
        }

        public Task InvokeAsync(Action action)
        {
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            State.Form.BeginInvoke((Action)(() =>
            {
                try { action(); completed.SetResult(true); }
                catch (Exception ex) { completed.SetException(ex); }
            }));
            return completed.Task;
        }

        public void Dispose()
        {
            State.Form.BeginInvoke((Action)Application.ExitThread);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "UI thread did not stop.");
        }
    }
}
