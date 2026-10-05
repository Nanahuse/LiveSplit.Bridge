using System.Windows.Forms;
using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class UiOperationTests
{
    [Fact]
    public async Task SplitResponseReportsTimerStateBeforeProjectionCommit()
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
        // The detailed projections are committed later; the operation response
        // still reports the previously published attempt generation.
        Assert.Equal(1UL, response.Operation.TimerState.AttemptRevision);
        await ui.InvokeAsync(() => Assert.Equal(2, ui.State.CurrentSplitIndex));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetPublishesTimerResetThenProjectionChangedEvents(bool rpcReset)
    {
        using var ui = await UiHost.CreateAsync();
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(ui.Port));
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);
        await ui.InvokeAsync(() => ui.Runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerStart));
        // Commit the start before resetting so the reset generation is well defined.
        await ui.InvokeAsync(() => ui.Runtime.Update());
        await DrainAsync(events);

        var runBefore = ui.Runtime.RunRevision;
        var attemptBefore = ui.Runtime.AttemptRevision;

        if (rpcReset)
        {
            using var rpc = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
            var response = await rpc.SendRequestAsync(new Request
            {
                ProtocolVersion = 2,
                TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerReset },
            }, TimeSpan.FromSeconds(5));
            Assert.True(response.Operation.Success);
            Assert.Equal(runBefore, response.Operation.TimerState.RunRevision);
            Assert.Equal(attemptBefore, response.Operation.TimerState.AttemptRevision);
        }
        else
        {
            await ui.InvokeAsync(() => new TimerModel { CurrentState = ui.State }.Reset());
        }

        // Reset is expressed by a single EVENT_TIMER_RESET carrying the previously
        // published generations. No separate EVENT_RUN_CHANGED is emitted yet.
        var reset = await ReadUntilAsync(events, BridgeEventType.EventTimerReset);
        Assert.Equal(runBefore, reset.TimerState.RunRevision);
        Assert.Equal(attemptBefore, reset.TimerState.AttemptRevision);
        Assert.Equal(runBefore, ui.Runtime.RunRevision);

        // The projections are committed on the next UI turn, after FixSplits.
        await ui.InvokeAsync(() => ui.Runtime.Update());

        var attemptChanged = await ReadUntilAsync(events, BridgeEventType.EventAttemptChanged);
        var runChanged = await ReadUntilAsync(events, BridgeEventType.EventRunChanged);
        Assert.True(attemptChanged.EventSequence > reset.EventSequence);
        Assert.True(runChanged.EventSequence > attemptChanged.EventSequence);
        Assert.Equal(attemptBefore + 1, attemptChanged.TimerState.AttemptRevision);
        Assert.Equal(runBefore + 1, runChanged.TimerState.RunRevision);

        await ReadUntilAsync(events, BridgeEventType.EventHeartbeat);
    }

    [Fact]
    public async Task SlowProjectionCommitDoesNotBlockSplitFromAnotherConnection()
    {
        using var ui = await UiHost.CreateAsync();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        ui.Runtime.Adapter.BeforeBuildRunState = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        using var rpcSplit = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
        await ui.InvokeAsync(() => ui.Runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerStart));

        var committed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.State.Form.BeginInvoke((Action)(() =>
        {
            try
            {
                ui.State.CallRunManuallyModified();
                ui.Runtime.Update();
                committed.TrySetResult(true);
            }
            catch (Exception exception)
            {
                committed.TrySetException(exception);
            }
        }));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "projection build did not start.");

        // The UI thread is busy building the run projection, but Split from another
        // connection runs on its own thread and must still complete.
        var splitResponse = await rpcSplit.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 2,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerSplit },
        }, TimeSpan.FromSeconds(5));
        Assert.True(splitResponse.Operation.Success, splitResponse.Operation.Message);
        Assert.Equal(1, splitResponse.Operation.TimerState.SplitIndex);

        release.Set();
        await committed.Task;

        // The Split overlapped the build, so the build was discarded instead of
        // being committed with a stale generation.
        Assert.Equal(1UL, ui.Runtime.RunRevision);

        ui.Runtime.Adapter.BeforeBuildRunState = null;
        await ui.InvokeAsync(() => ui.Runtime.Update());
        Assert.Equal(2UL, ui.Runtime.RunRevision);
    }

    [Fact]
    public async Task GetRunDuringResetReturnsLastCompletedProjection()
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
        await ui.InvokeAsync(() => ui.Runtime.Update());
        var runRevisionBefore = ui.Runtime.RunRevision;

        var resetTask = rpcReset.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerReset },
        }, TimeSpan.FromSeconds(15));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "Reset did not reach the FixSplits window.");

        // FixSplits has not run, but the query still returns the last completed
        // projection with a matching revision instead of an intermediate state.
        var duringReset = await rpcRun.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 2,
            GetRun = new GetRunRequest(),
        }, TimeSpan.FromSeconds(10));
        Assert.Null(duringReset.Error);
        Assert.NotNull(duringReset.GetRun);
        Assert.Equal(runRevisionBefore, duringReset.GetRun.Run.RunRevision);

        release.Set();
        var resetResponse = await resetTask;
        Assert.True(resetResponse.Operation.Success, resetResponse.Operation.Message);

        // Still the old projection until the commit happens on the UI thread.
        var beforeCommit = await rpcRun.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 3,
            GetRun = new GetRunRequest(),
        }, TimeSpan.FromSeconds(10));
        Assert.Null(beforeCommit.Error);
        Assert.Equal(runRevisionBefore, beforeCommit.GetRun.Run.RunRevision);

        await ui.InvokeAsync(() => ui.Runtime.Update());

        var afterCommit = await rpcRun.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 4,
            GetRun = new GetRunRequest(),
        }, TimeSpan.FromSeconds(10));
        Assert.NotNull(afterCommit.GetRun);
        Assert.Equal(runRevisionBefore + 1, afterCommit.GetRun.Run.RunRevision);
    }

    [Fact]
    public async Task GetAttemptDuringSplitReturnsLastCompletedProjection()
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
        await ui.InvokeAsync(() => ui.Runtime.Update());
        var attemptRevisionBefore = ui.Runtime.AttemptRevision;

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
        Assert.Null(duringSplit.Error);
        Assert.NotNull(duringSplit.GetAttempt);
        Assert.Equal(attemptRevisionBefore, duringSplit.GetAttempt.Attempt.AttemptRevision);
        Assert.False(duringSplit.GetAttempt.Attempt.Segments[0].SplitTime.HasRealTimeTicks);

        release.Set();
        var splitResponse = await splitTask;
        Assert.True(splitResponse.Operation.Success, splitResponse.Operation.Message);

        await ui.InvokeAsync(() => ui.Runtime.Update());

        var afterSplit = await rpcAttempt.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 3,
            GetAttempt = new GetAttemptRequest(),
        }, TimeSpan.FromSeconds(10));
        Assert.NotNull(afterSplit.GetAttempt);
        Assert.Equal(attemptRevisionBefore + 1, afterSplit.GetAttempt.Attempt.AttemptRevision);
        Assert.True(afterSplit.GetAttempt.Attempt.Segments[0].SplitTime.HasRealTimeTicks);
    }

    [Fact]
    public async Task ExternalResetDuringFixSplitsReturnsLastCompletedProjection()
    {
        using var ui = await UiHost.CreateAsync();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        ui.Runtime.ResetBarrier = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        using var rpcRun = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
        await ui.InvokeAsync(() => ui.Runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerStart));
        await ui.InvokeAsync(() => ui.Runtime.Update());
        var runRevisionBefore = ui.Runtime.RunRevision;

        // Bridge外: a TimerModel created directly on a background thread, the same
        // shape LiveSplit's own command server uses.
        var resetTask = Task.Run(() => new TimerModel { CurrentState = ui.State }.Reset());

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "External reset did not reach the FixSplits window.");

        var duringReset = await rpcRun.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            GetRun = new GetRunRequest(),
        }, TimeSpan.FromSeconds(10));
        Assert.Null(duringReset.Error);
        Assert.Equal(runRevisionBefore, duringReset.GetRun.Run.RunRevision);

        release.Set();
        await resetTask;

        await ui.InvokeAsync(() => ui.Runtime.Update());

        var afterReset = await rpcRun.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 2,
            GetRun = new GetRunRequest(),
        }, TimeSpan.FromSeconds(10));
        Assert.NotNull(afterReset.GetRun);
        Assert.Equal(runRevisionBefore + 1, afterReset.GetRun.Run.RunRevision);
    }

    [Fact]
    public async Task ExternalSplitDuringMutationReturnsLastCompletedProjection()
    {
        using var ui = await UiHost.CreateAsync();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        ui.Runtime.SplitBarrier = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        using var rpcAttempt = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
        await ui.InvokeAsync(() => ui.Runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerStart));
        await ui.InvokeAsync(() => ui.Runtime.Update());
        var attemptRevisionBefore = ui.Runtime.AttemptRevision;

        // Bridge外: a Split issued directly from a background thread.
        var splitTask = Task.Run(() => new TimerModel { CurrentState = ui.State }.Split());

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "External split did not reach the mutation window.");

        var duringSplit = await rpcAttempt.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            GetAttempt = new GetAttemptRequest(),
        }, TimeSpan.FromSeconds(10));
        Assert.Null(duringSplit.Error);
        Assert.Equal(attemptRevisionBefore, duringSplit.GetAttempt.Attempt.AttemptRevision);

        release.Set();
        await splitTask;

        await ui.InvokeAsync(() => ui.Runtime.Update());

        var afterSplit = await rpcAttempt.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 2,
            GetAttempt = new GetAttemptRequest(),
        }, TimeSpan.FromSeconds(10));
        Assert.NotNull(afterSplit.GetAttempt);
        Assert.Equal(attemptRevisionBefore + 1, afterSplit.GetAttempt.Attempt.AttemptRevision);
        Assert.True(afterSplit.GetAttempt.Attempt.Segments[0].SplitTime.HasRealTimeTicks);
    }

    [Fact]
    public async Task UpdateDuringResetDoesNotCommitIntermediateProjection()
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
        await ui.InvokeAsync(() => ui.Runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerStart));
        await ui.InvokeAsync(() => ui.Runtime.Update());
        var runBefore = ui.Runtime.RunRevision;
        var attemptBefore = ui.Runtime.AttemptRevision;

        var resetTask = rpcReset.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerReset },
        }, TimeSpan.FromSeconds(15));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "Reset did not reach the FixSplits window.");

        // The control mutation is still running. An Update must not commit the
        // half-reset run as a completed projection.
        await ui.InvokeAsync(() => ui.Runtime.Update());
        Assert.Equal(runBefore, ui.Runtime.RunRevision);
        Assert.Equal(attemptBefore, ui.Runtime.AttemptRevision);

        release.Set();
        var resetResponse = await resetTask;
        Assert.True(resetResponse.Operation.Success, resetResponse.Operation.Message);

        // Now that the control mutation has finished, the next stable Update commits.
        await ui.InvokeAsync(() => ui.Runtime.Update());
        Assert.Equal(runBefore + 1, ui.Runtime.RunRevision);
        Assert.Equal(attemptBefore + 1, ui.Runtime.AttemptRevision);
    }

    [Fact]
    public async Task ControlMutationDuringProjectionBuildDiscardsStaleBuild()
    {
        using var ui = await UiHost.CreateAsync();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        ui.Runtime.Adapter.BeforeBuildRunState = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        using var rpcSplit = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
        var runRevisionBefore = ui.Runtime.RunRevision;

        // Start a run projection build on the UI thread and hold it open.
        var built = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.State.Form.BeginInvoke((Action)(() =>
        {
            try
            {
                ui.State.CallRunManuallyModified();
                ui.Runtime.Update();
                built.TrySetResult(true);
            }
            catch (Exception exception)
            {
                built.TrySetException(exception);
            }
        }));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "projection build did not start.");

        // A Bridge control mutation runs while the build is in flight.
        var splitResponse = await rpcSplit.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerSplit },
        }, TimeSpan.FromSeconds(5));
        Assert.True(splitResponse.Operation.Success, splitResponse.Operation.Message);

        release.Set();
        await built.Task;

        // The overlapped build was discarded, so nothing was committed yet.
        Assert.Equal(runRevisionBefore, ui.Runtime.RunRevision);

        // The next stable Update rebuilds and commits.
        ui.Runtime.Adapter.BeforeBuildRunState = null;
        await ui.InvokeAsync(() => ui.Runtime.Update());
        Assert.Equal(runRevisionBefore + 1, ui.Runtime.RunRevision);
    }

    [Fact]
    public async Task GameTimeOperationOverlappingUpdateDoesNotPublishDuplicateEvent()
    {
        using var ui = await UiHost.CreateAsync();
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(ui.Port));
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        ui.Runtime.GameTimeBarrier = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        using var rpc = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
        var stateRevisionBefore = ui.Runtime.StateRevision;

        var operationTask = rpc.SendRequestAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            GameTimeOperation = new GameTimeOperationRequest { Operation = GameTimeOperationType.Initialize },
        }, TimeSpan.FromSeconds(15));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "GameTime mutation did not reach the barrier.");

        // The control mutation is in flight. A concurrent Update must treat the
        // in-progress mutation as Bridge-owned and not as an external GameTime change.
        await ui.InvokeAsync(() => ui.Runtime.Update());
        Assert.Equal(stateRevisionBefore, ui.Runtime.StateRevision);

        release.Set();
        var response = await operationTask;
        Assert.True(response.Operation.Success, response.Operation.Message);
        Assert.True(response.Operation.TimerState.IsGameTimeInitialized);

        // Exactly one GameTime event is published for the change.
        var initialized = await ReadEventAsync(events, BridgeEventType.EventGameTimeInitialized);
        Assert.True(initialized.EventSequence > 0);
        Assert.Equal(stateRevisionBefore + 1, ui.Runtime.StateRevision);

        // The next stable Update must not publish the same change again.
        await ui.InvokeAsync(() => ui.Runtime.Update());
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);
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
            ui.Runtime.Update();
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
            ui.Runtime.Update();
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
            ui.Runtime.Update();
        });
        await ReadEventAsync(events, BridgeEventType.EventRunChanged);
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);

        await ui.InvokeAsync(() =>
        {
            ui.State.Run.GameName = "Changed";
            ui.State.CallRunManuallyModified();
            ui.Runtime.Update();
        });
        var run = await ReadEventAsync(events, BridgeEventType.EventRunChanged);
        Assert.Equal(3UL, run.TimerState.RunRevision);
        Assert.Equal(1UL, run.TimerState.RuntimeRevision);
        // The earlier rename pending state must not survive into this change.
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);
    }

    // Reads until a heartbeat arrives with no event published since the previous
    // heartbeat, which drains events published before the reset.
    private static async Task DrainAsync(WebSocketTestClient client)
    {
        var sawEventSinceHeartbeat = false;
        while (true)
        {
            BridgeEvent result;
            try
            {
                result = BridgeEvent.Parser.ParseFrom(
                    await client.ReceiveBinaryAsync(TimeSpan.FromSeconds(3)));
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (result.Type == BridgeEventType.EventHeartbeat)
            {
                if (!sawEventSinceHeartbeat)
                {
                    return;
                }

                sawEventSinceHeartbeat = false;
            }
            else
            {
                sawEventSinceHeartbeat = true;
            }
        }
    }

    // Strict: the next non-heartbeat event must be the expected one. Used to prove
    // that no other projection changed event is published.
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

    // Lenient: scans past unrelated events. Used when events may legitimately
    // interleave but relative ordering among the found events is still asserted.
    private static async Task<BridgeEvent> ReadUntilAsync(WebSocketTestClient client, BridgeEventType expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            BridgeEvent result;
            try
            {
                result = BridgeEvent.Parser.ParseFrom(
                    await client.ReceiveBinaryAsync(deadline - DateTime.UtcNow));
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (result.Type == expected)
            {
                return result;
            }
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
