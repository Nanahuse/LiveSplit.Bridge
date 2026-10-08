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
    public async Task ResetPublishesTimerThenOneRunEventAfterFixSplits(bool rpcReset)
    {
        using var ui = await UiHost.CreateAsync();
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(ui.Port));
        await ReadEventAsync(events, BridgeEventType.EventHeartbeat);
        await ui.InvokeAsync(() => ui.Runtime.Adapter.ExecuteTimerOperation(TimerOperationType.TimerStart));
        await ReadEventAsync(events, BridgeEventType.EventTimerStarted);
        if (rpcReset)
        {
            using var rpc = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(ui.Port));
            var response = await rpc.SendRequestAsync(new Request
            {
                ProtocolVersion = 2,
                TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerReset },
            }, TimeSpan.FromSeconds(5));
            Assert.True(response.Operation.Success);
            Assert.Equal(1UL, response.Operation.TimerState.RunRevision);
        }
        else
        {
            await ui.InvokeAsync(() => new TimerModel { CurrentState = ui.State }.Reset());
        }

        var reset = await ReadEventAsync(events, BridgeEventType.EventTimerReset);
        var run = await ReadEventAsync(events, BridgeEventType.EventRunChanged);
        Assert.Equal(reset.EventSequence + 1, run.EventSequence);
        Assert.Equal(1UL, reset.TimerState.RunRevision);
        Assert.Equal(2UL, run.TimerState.RunRevision);
        Assert.Equal(3UL, run.TimerState.AttemptRevision);
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
        public V2BridgeRuntime Runtime { get; private set; } = null!;
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
                    using var runtime = new V2BridgeRuntime(host.State, host.Port);
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
