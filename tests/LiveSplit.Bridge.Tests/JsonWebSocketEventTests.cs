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
}
