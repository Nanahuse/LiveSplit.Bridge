using System.Drawing;
using System.Net.WebSockets;
using System.Text.Json;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using Json = LiveSplit.Bridge.Json;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public sealed class JsonWebSocketLifecycleTests
{
    [Fact]
    public async Task StopDuringInvokedIconCaptureCompletesRequestAndAllowsRestart()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var icon = new Bitmap(2, 2);
        var segment = new StopOnIconReadSegment { Icon = icon };
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(segment);
        using var ui = new LiveSplitTestUiThread(run);
        var adapter = new CompletingIconAdapter(new JsonLiveSplitAdapter(ui.State));
        using var transport = new JsonWebSocketTransport(port);
        using var runtime = new JsonBridgeRuntime(adapter, transport, ui.State);
        ui.State.Form.Invoke((Action)(() => transport.Start(runtime)));
        using var stopped = new ManualResetEventSlim();
        segment.OnIconRead = () =>
        {
            // This getter runs inside the real Form.Invoke callback: the RPC thread
            // is waiting for capture to finish while the Form owner stops Bridge.
            Assert.Equal(ui.ManagedThreadId, Thread.CurrentThread.ManagedThreadId);
            runtime.Dispose();
            transport.Dispose();
            segment.OnIconRead = null;
            stopped.Set();
        };
        using var client = await JsonWebSocketTestClient.ConnectAsync(BridgeTestEndpoints.JsonRpc(port));
        await client.SendTextAsync("{\"request_id\":1,\"method\":\"get_run_icons\"}");
        var closed = await client.ReceiveAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketMessageType.Close, closed.Type);
        Assert.True(stopped.Wait(TimeSpan.FromSeconds(5)), "Bridge stop did not return on the UI thread.");
        Assert.Same(adapter.Finished.Task, await Task.WhenAny(adapter.Finished.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        await adapter.Finished.Task;
        BridgeTestEndpoints.WaitForListener(port, expected: false);

        // A callback after shutdown confirms that the UI still pumps messages.
        var responsive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.State.Form.BeginInvoke((Action)(() => responsive.SetResult(true)));
        Assert.Same(responsive.Task, await Task.WhenAny(responsive.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        await responsive.Task;
        using var replacement = new JsonWebSocketTransport(port);
        using var replacementRuntime = new JsonBridgeRuntime(ui.State, replacement);
        ui.State.Form.Invoke((Action)(() => replacement.Start(replacementRuntime)));
        using var reconnected = await JsonWebSocketTestClient.ConnectAsync(BridgeTestEndpoints.JsonRpc(port));
        await reconnected.SendTextAsync("{\"request_id\":2,\"method\":\"get_timer_state\"}");
        using var response = JsonDocument.Parse((await reconnected.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
        Assert.Equal(2U, response.RootElement.GetProperty("request_id").GetUInt32());
        Assert.True(response.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task DisposeWithConnectedClientReleasesPortForReconnect()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        using var first = JsonWebSocketTestClient.StartComponent(state, port);
        using var staleClient = await JsonWebSocketTestClient.ConnectAsync(BridgeTestEndpoints.JsonRpc(port)).ConfigureAwait(false);

        first.Dispose();
        BridgeTestEndpoints.WaitForListener(port, expected: false);

        using var replacement = JsonWebSocketTestClient.StartComponent(state, port);
        using var client = await JsonWebSocketTestClient.ConnectAsync(BridgeTestEndpoints.JsonRpc(port)).ConfigureAwait(false);
        await client.SendTextAsync("{\"request_id\":1,\"method\":\"get_completed_count\"}");
        using var response = JsonDocument.Parse((await client.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
        Assert.Equal(1U, response.RootElement.GetProperty("request_id").GetUInt32());
        Assert.True(response.RootElement.GetProperty("session_id").GetUInt32() > 0);
        Assert.Equal(0U, response.RootElement.GetProperty("result").GetProperty("completed_count").GetUInt32());
    }

    private sealed class StopOnIconReadSegment : Segment, ISegment
    {
        internal Action? OnIconRead { get; set; }
        internal StopOnIconReadSegment() : base("One") { }
        Image ISegment.Icon
        {
            get { OnIconRead?.Invoke(); return Icon; }
            set => Icon = value;
        }
    }

    private sealed class CompletingIconAdapter : IJsonLiveSplitAdapter
    {
        private readonly IJsonLiveSplitAdapter inner;
        internal TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CompletingIconAdapter(IJsonLiveSplitAdapter inner) => this.inner = inner;
        public Json.RunIcons GetRunIcons()
        {
            try { return inner.GetRunIcons(); }
            finally { Finished.TrySetResult(true); }
        }
        public Json.TimerState GetTimerState() => inner.GetTimerState();
        public Json.AttemptState GetAttempt() => inner.GetAttempt();
        public Json.CompletedCount GetCompletedCount() => inner.GetCompletedCount();
        public Json.ContextState GetContextState() => inner.GetContextState();
        public Json.RunState GetRunState() => inner.GetRunState();
        public bool TryUpdateRunTimings(Json.RunState published, out Json.RunState updated) => inner.TryUpdateRunTimings(published, out updated);
        public void ExecuteTimerOperation(Json.TimerOperation operation) => inner.ExecuteTimerOperation(operation);
        public void ExecuteGameTimeOperation(Json.GameTimeParams operation) => inner.ExecuteGameTimeOperation(operation);
    }
}
