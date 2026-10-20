using System.Net.WebSockets;
using System.Text.Json;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public sealed class JsonWebSocketEventTests
{
    [Fact]
    public async Task TimerControlProducesOrderedTextEventWithMatchingSession()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        using var host = JsonWebSocketTestClient.StartComponent(state, port);
        using var events = await JsonWebSocketTestClient.ConnectAsync(BridgeTestEndpoints.JsonEvents(port), "http://example.com").ConfigureAwait(false);
        using var secondEvents = await JsonWebSocketTestClient.ConnectAsync(BridgeTestEndpoints.JsonEvents(port)).ConfigureAwait(false);
        using var rpc = await JsonWebSocketTestClient.ConnectAsync(BridgeTestEndpoints.JsonRpc(port)).ConfigureAwait(false);

        await rpc.SendTextAsync("{\"request_id\":100,\"method\":\"timer_operation\",\"params\":{\"operation\":\"start\"}}");
        using var response = JsonDocument.Parse((await rpc.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
        Assert.Equal(100U, response.RootElement.GetProperty("request_id").GetUInt32());
        Assert.True(response.RootElement.GetProperty("result").ValueKind == JsonValueKind.Null);

        using var eventJson = JsonDocument.Parse((await events.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
        Assert.Equal("timer_started", eventJson.RootElement.GetProperty("type").GetString());
        Assert.Equal(response.RootElement.GetProperty("session_id").GetUInt32(), eventJson.RootElement.GetProperty("session_id").GetUInt32());
        Assert.Equal(1U, eventJson.RootElement.GetProperty("event_sequence").GetUInt32());
        Assert.Equal(JsonValueKind.Object, eventJson.RootElement.GetProperty("timer_state").ValueKind);

        using var secondEventJson = JsonDocument.Parse((await secondEvents.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
        Assert.Equal("timer_started", secondEventJson.RootElement.GetProperty("type").GetString());
        Assert.Equal(eventJson.RootElement.GetProperty("session_id").GetUInt32(), secondEventJson.RootElement.GetProperty("session_id").GetUInt32());
        Assert.Equal(1U, secondEventJson.RootElement.GetProperty("event_sequence").GetUInt32());

        await rpc.SendTextAsync("{\"request_id\":101,\"method\":\"timer_operation\",\"params\":{\"operation\":\"reset\"}}");
        using var resetResponse = JsonDocument.Parse((await rpc.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
        using var resetEvent = JsonDocument.Parse((await events.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
        Assert.Equal(101U, resetResponse.RootElement.GetProperty("request_id").GetUInt32());
        Assert.Equal("timer_reset", resetEvent.RootElement.GetProperty("type").GetString());
        Assert.Equal(2U, resetEvent.RootElement.GetProperty("event_sequence").GetUInt32());
        Assert.Equal(JsonValueKind.Object, resetEvent.RootElement.GetProperty("timer_state").ValueKind);
        using var secondResetEvent = JsonDocument.Parse((await secondEvents.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
        Assert.Equal("timer_reset", secondResetEvent.RootElement.GetProperty("type").GetString());
        Assert.Equal(2U, secondResetEvent.RootElement.GetProperty("event_sequence").GetUInt32());

        var replacementRun = new Run(new StandardComparisonGeneratorsFactory()) { GameName = "Replacement" };
        replacementRun.Add(new Segment("New segment"));
        state.Run = replacementRun;
        state.CallRunManuallyModified();
        host.Update(null!, state, 0, 0, LiveSplit.UI.LayoutMode.Vertical);

        using var changedEvent = JsonDocument.Parse((await events.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
        Assert.Equal("run_changed", changedEvent.RootElement.GetProperty("type").GetString());
        Assert.Equal(3U, changedEvent.RootElement.GetProperty("event_sequence").GetUInt32());
        Assert.Equal(JsonValueKind.Null, changedEvent.RootElement.GetProperty("timer_state").ValueKind);

        using var changedEvent2 = JsonDocument.Parse((await secondEvents.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
        Assert.Equal("run_changed", changedEvent2.RootElement.GetProperty("type").GetString());
        Assert.Equal(3U, changedEvent2.RootElement.GetProperty("event_sequence").GetUInt32());
        await rpc.SendTextAsync("{\"request_id\":102,\"method\":\"get_run\"}");
        using var latestRun = JsonDocument.Parse((await rpc.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
        Assert.Equal("Replacement", latestRun.RootElement.GetProperty("result").GetProperty("game_name").GetString());
    }

    [Fact]
    public async Task RemainingTimerEventsAndQueriesShareTheWebSocketContract()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        run.Add(new Segment("Three"));
        var state = TestLiveSplitState.Create(run);
        using var host = JsonWebSocketTestClient.StartComponent(state, port);
        using var events = await JsonWebSocketTestClient.ConnectAsync(BridgeTestEndpoints.JsonEvents(port)).ConfigureAwait(false);
        using var rpc = await JsonWebSocketTestClient.ConnectAsync(BridgeTestEndpoints.JsonRpc(port)).ConfigureAwait(false);
        uint lastSequence = 0;
        uint lastRequestId = 300;

        async Task AssertControlEvent(string operation, string expectedType)
        {
            var requestId = ++lastRequestId;
            await rpc.SendTextAsync($"{{\"request_id\":{requestId},\"method\":\"timer_operation\",\"params\":{{\"operation\":\"{operation}\"}}}}");
            var responseMessage = await rpc.ReceiveAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WebSocketMessageType.Text, responseMessage.Type);
            using var response = JsonDocument.Parse(responseMessage.Data);
            Assert.Equal(requestId, response.RootElement.GetProperty("request_id").GetUInt32());
            Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("result").ValueKind);
            var sessionId = response.RootElement.GetProperty("session_id").GetUInt32();

            JsonElement timerState = default;
            while (true)
            {
                var eventMessage = await events.ReceiveAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(WebSocketMessageType.Text, eventMessage.Type);
                using var bridgeEvent = JsonDocument.Parse(eventMessage.Data);
                var root = bridgeEvent.RootElement;
                Assert.Equal(sessionId, root.GetProperty("session_id").GetUInt32());
                Assert.Equal(lastSequence + 1, root.GetProperty("event_sequence").GetUInt32());
                lastSequence++;
                var eventType = root.GetProperty("type").GetString();
                if (eventType == expectedType)
                {
                    Assert.Equal(JsonValueKind.Object, root.GetProperty("timer_state").ValueKind);
                    timerState = root.GetProperty("timer_state").Clone();
                    break;
                }

                // TimerModel may also publish a RunChanged notification while a control updates
                // attempt/run metadata. It remains ordered on the same event stream.
                Assert.Equal("run_changed", eventType);
                Assert.Equal(JsonValueKind.Null, root.GetProperty("timer_state").ValueKind);
            }

            await rpc.SendTextAsync($"{{\"request_id\":{++lastRequestId},\"method\":\"get_timer_state\"}}");
            var queryMessage = await rpc.ReceiveAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WebSocketMessageType.Text, queryMessage.Type);
            using var query = JsonDocument.Parse(queryMessage.Data);
            var current = query.RootElement.GetProperty("result");
            Assert.Equal(timerState.GetProperty("phase").GetString(), current.GetProperty("phase").GetString());
            Assert.Equal(timerState.GetProperty("split_index").GetInt32(), current.GetProperty("split_index").GetInt32());
        }

        await AssertControlEvent("start", "timer_started");
        await AssertControlEvent("split", "timer_split");
        await AssertControlEvent("skip", "timer_skipped");
        await AssertControlEvent("undo", "timer_undo");
        await AssertControlEvent("pause", "timer_phase_changed");
        await AssertControlEvent("resume", "timer_phase_changed");
    }

    [Fact]
    public async Task ContextChangesArePublishedAsNullStateTextEvents()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        using var host = JsonWebSocketTestClient.StartComponent(state, port);
        using var events = await JsonWebSocketTestClient.ConnectAsync(BridgeTestEndpoints.JsonEvents(port)).ConfigureAwait(false);
        using var rpc = await JsonWebSocketTestClient.ConnectAsync(BridgeTestEndpoints.JsonRpc(port)).ConfigureAwait(false);
        state.CurrentComparison = "Best Segments";
        using var stopUpdates = new CancellationTokenSource();
        var updates = Task.Run(() =>
        {
            while (!stopUpdates.IsCancellationRequested)
            {
                host.Update(null!, state, 0, 0, LiveSplit.UI.LayoutMode.Vertical);
                Thread.Yield();
            }
        });

        try
        {
            var eventMessage = await events.ReceiveAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WebSocketMessageType.Text, eventMessage.Type);
            using var bridgeEvent = JsonDocument.Parse(eventMessage.Data);
            Assert.Equal("context_changed", bridgeEvent.RootElement.GetProperty("type").GetString());
            Assert.Equal(1U, bridgeEvent.RootElement.GetProperty("event_sequence").GetUInt32());
            Assert.Equal(JsonValueKind.Null, bridgeEvent.RootElement.GetProperty("timer_state").ValueKind);

            await rpc.SendTextAsync("{\"request_id\":401,\"method\":\"get_context_state\"}");
            var responseMessage = await rpc.ReceiveAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WebSocketMessageType.Text, responseMessage.Type);
            using var response = JsonDocument.Parse(responseMessage.Data);
            Assert.Equal(bridgeEvent.RootElement.GetProperty("session_id").GetUInt32(), response.RootElement.GetProperty("session_id").GetUInt32());
            Assert.Equal("Best Segments", response.RootElement.GetProperty("result").GetProperty("current_comparison").GetString());
        }
        finally
        {
            stopUpdates.Cancel();
            await updates.ConfigureAwait(false);
        }
    }
}
