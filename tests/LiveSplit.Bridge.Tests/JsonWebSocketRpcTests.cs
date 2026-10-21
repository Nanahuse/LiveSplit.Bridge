using System.Drawing;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public sealed class JsonWebSocketRpcTests
{
    private const int MaximumRpcMessageBytes = 64 * 1024;

    [Fact]
    public async Task V4QueryAndIdentifiableInvalidRequestsUseTextResponses()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var host = CreateHost(port);
        using var client = await ConnectRpc(port).ConfigureAwait(false);

        await client.SendTextAsync("{\"request_id\":0,\"method\":\"get_completed_count\"}");
        using (var response = JsonDocument.Parse((await client.ReceiveAsync(TimeSpan.FromSeconds(5))).Data))
        {
            Assert.Equal(0U, response.RootElement.GetProperty("request_id").GetUInt32());
            Assert.True(response.RootElement.GetProperty("session_id").GetUInt32() > 0);
            Assert.Equal(0U, response.RootElement.GetProperty("result").GetProperty("completed_count").GetUInt32());
        }

        uint nextId = 1;
        foreach (var method in new[] { "get_timer_state", "get_run", "get_run_icons", "get_attempt", "get_context_state", "get_completed_count" })
        {
            await client.SendTextAsync($"{{\"request_id\":{nextId},\"method\":\"{method}\"}}");
            using var query = JsonDocument.Parse((await client.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
            Assert.Equal(nextId, query.RootElement.GetProperty("request_id").GetUInt32());
            Assert.True(query.RootElement.GetProperty("session_id").GetUInt32() > 0);
            Assert.True(query.RootElement.TryGetProperty("result", out _));
            nextId++;
        }

        foreach (var operation in new[] { "start", "split", "skip", "undo", "reset", "pause", "resume" })
        {
            await client.SendTextAsync($"{{\"request_id\":{nextId},\"method\":\"timer_operation\",\"params\":{{\"operation\":\"{operation}\"}}}}");
            using var control = JsonDocument.Parse((await client.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
            Assert.Equal(nextId, control.RootElement.GetProperty("request_id").GetUInt32());
            Assert.True(control.RootElement.GetProperty("session_id").GetUInt32() > 0);
            Assert.True(control.RootElement.TryGetProperty("result", out _) || control.RootElement.TryGetProperty("error", out _));
            nextId++;
        }

        foreach (var operation in new[] { "initialize", "set", "pause", "resume" })
        {
            var parameters = operation == "set"
                ? "\"params\":{\"operation\":\"set\",\"ticks\":-9007199254740991}"
                : $"\"params\":{{\"operation\":\"{operation}\"}}";
            await client.SendTextAsync($"{{\"request_id\":{nextId},\"method\":\"game_time_operation\",{parameters}}}");
            using var control = JsonDocument.Parse((await client.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
            Assert.Equal(nextId, control.RootElement.GetProperty("request_id").GetUInt32());
            Assert.True(control.RootElement.GetProperty("session_id").GetUInt32() > 0);
            Assert.True(control.RootElement.TryGetProperty("result", out _) || control.RootElement.TryGetProperty("error", out _));
            nextId++;
        }

        await client.SendTextAsync("{\"request_id\":23,\"method\":\"not_a_method\"}");
        AssertError(await client.ReceiveAsync(TimeSpan.FromSeconds(5)), 23, "invalid_request");

        await client.SendTextAsync("{\"request_id\":24,\"method\":\"game_time_operation\",\"params\":{\"operation\":\"set\"}}");
        AssertError(await client.ReceiveAsync(TimeSpan.FromSeconds(5)), 24, "invalid_request");

        await client.SendTextAsync("{\"request_id\":25,\"method\":\"get_run\",\"unexpected\":true}");
        AssertError(await client.ReceiveAsync(TimeSpan.FromSeconds(5)), 25, "invalid_request");

        await client.SendTextAsync("{\"request_id\":26,\"method\":\"timer_operation\",\"params\":{\"operation\":\"start, reset\"}}");
        AssertError(await client.ReceiveAsync(TimeSpan.FromSeconds(5)), 26, "invalid_request");

        await client.SendTextAsync("{\"request_id\":27,\"method\":\"get_timer_state, get_run\"}");
        AssertError(await client.ReceiveAsync(TimeSpan.FromSeconds(5)), 27, "invalid_request");
    }

    [Fact]
    public async Task UnidentifiableOrMalformedEnvelopeIsDropped()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var host = CreateHost(port);

        using var client = await ConnectRpc(port).ConfigureAwait(false);
        foreach (var invalid in new[]
        {
            "not json",
            "[]",
            "{\"method\":\"get_run\"}",
            "{\"request_id\":null,\"method\":\"get_run\"}",
            "{\"request_id\":-1,\"method\":\"get_run\"}",
            "{\"request_id\":4294967296,\"method\":\"get_run\"}",
        })
            await client.SendTextAsync(invalid);

        await client.SendTextAsync("{\"request_id\":27,\"method\":\"get_run\"}");
        using var response = JsonDocument.Parse((await client.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
        Assert.Equal(27U, response.RootElement.GetProperty("request_id").GetUInt32());
        Assert.True(response.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task DuplicatePropertiesAreRejectedWithKnownRequestId()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var host = CreateHost(port);
        using var client = await ConnectRpc(port).ConfigureAwait(false);

        await client.SendTextAsync("{\"request_id\":26,\"method\":\"timer_operation\",\"params\":{\"operation\":\"start\",\"operation\":\"pause\"}}");
        AssertError(await client.ReceiveAsync(TimeSpan.FromSeconds(5)), 26, "invalid_request");
    }

    [Fact]
    public async Task BinaryMessageIsDroppedAndConnectionRemainsUsable()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var host = CreateHost(port);
        using var client = await ConnectRpc(port).ConfigureAwait(false);

        await client.SendBinaryAsync(Encoding.UTF8.GetBytes("{\"request_id\":30,\"method\":\"get_run\"}"));
        await client.SendTextAsync("{\"request_id\":31,\"method\":\"get_run\"}");
        using var response = JsonDocument.Parse((await client.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
        Assert.Equal(31U, response.RootElement.GetProperty("request_id").GetUInt32());
    }

    [Fact]
    public async Task RpcEnforces64KibibyteLimitAcrossFragments()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var host = CreateHost(port);
        using (var client = await ConnectRpc(port).ConfigureAwait(false))
        {
            var json = "{\"request_id\":32,\"method\":\"get_run\"}";
            json += new string(' ', MaximumRpcMessageBytes - Encoding.UTF8.GetByteCount(json));
            Assert.Equal(MaximumRpcMessageBytes, Encoding.UTF8.GetByteCount(json));
            await client.SendFragmentedTextAsync(json, 4093);
            Assert.Equal(WebSocketMessageType.Text, (await client.ReceiveAsync(TimeSpan.FromSeconds(5))).Type);
        }

        await AssertOversizedMessageClosesAsync(port, fragmented: false);
        await AssertOversizedMessageClosesAsync(port, fragmented: true);
    }

    [Fact]
    public async Task RunExcludesImagesAndIconsQueryReturnsPngWithoutResponseSizeLimit()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory()) { GameName = "Large icon response" };
        run.Add(new Segment("One"));
        using var icon = new Bitmap(256, 256);
        var random = new Random(17);
        for (var y = 0; y < icon.Height; y++)
            for (var x = 0; x < icon.Width; x++)
                icon.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
        run.GameIcon = icon;

        var port = BridgeTestEndpoints.GetFreePort();
        using var component = JsonWebSocketTestClient.StartComponent(TestLiveSplitState.Create(run), port);
        using var client = await ConnectRpc(port).ConfigureAwait(false);
        await client.SendTextAsync("{\"request_id\":38,\"method\":\"get_run\"}");
        using (var runJson = JsonDocument.Parse((await client.ReceiveAsync(TimeSpan.FromSeconds(5))).Data))
        {
            var payload = runJson.RootElement.GetProperty("result");
            Assert.Equal("Large icon response", payload.GetProperty("game_name").GetString());
            Assert.False(payload.TryGetProperty("game_icon", out _));
            Assert.False(payload.GetProperty("segments")[0].TryGetProperty("icon", out _));
        }
        await client.SendTextAsync("{\"request_id\":39,\"method\":\"get_run_icons\"}");
        var response = await client.ReceiveAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WebSocketMessageType.Text, response.Type);
        Assert.True(response.Data.Length > MaximumRpcMessageBytes);
        using var json = JsonDocument.Parse(response.Data);
        Assert.Equal(39U, json.RootElement.GetProperty("request_id").GetUInt32());
        var result = json.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("game_icon").GetString()!.Length > MaximumRpcMessageBytes);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("segment_icons")[0].ValueKind);
    }

    [Fact]
    public async Task ExternalOriginRetainsQueryButDeniesControl()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var host = CreateHost(port);
        using var client = await ConnectRpc(port, "http://example.com").ConfigureAwait(false);
        await client.SendTextAsync("{\"request_id\":40,\"method\":\"get_run\"}");
        Assert.Equal(WebSocketMessageType.Text, (await client.ReceiveAsync(TimeSpan.FromSeconds(5))).Type);
        await client.SendTextAsync("{\"request_id\":41,\"method\":\"timer_operation\",\"params\":{\"operation\":\"start\"}}");
        AssertError(await client.ReceiveAsync(TimeSpan.FromSeconds(5)), 41, "permission_denied");
    }

    private static Component CreateHost(int port)
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        return JsonWebSocketTestClient.StartComponent(TestLiveSplitState.Create(run), port);
    }

    private static Task<JsonWebSocketTestClient> ConnectRpc(int port, string? origin = null) =>
        JsonWebSocketTestClient.ConnectAsync(BridgeTestEndpoints.JsonRpc(port), origin);

    private static void AssertError((WebSocketMessageType Type, byte[] Data) message, uint id, string code)
    {
        Assert.Equal(WebSocketMessageType.Text, message.Type);
        using var json = JsonDocument.Parse(message.Data);
        Assert.Equal(id, json.RootElement.GetProperty("request_id").GetUInt32());
        Assert.True(json.RootElement.GetProperty("session_id").GetUInt32() > 0);
        Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(json.RootElement.TryGetProperty("result", out _));
    }

    private static async Task AssertOversizedMessageClosesAsync(int port, bool fragmented)
    {
        using var client = await ConnectRpc(port).ConfigureAwait(false);
        var json = "{\"request_id\":33,\"method\":\"get_run\"}" +
            new string(' ', MaximumRpcMessageBytes);
        Assert.True(Encoding.UTF8.GetByteCount(json) > MaximumRpcMessageBytes);
        if (fragmented) await client.SendFragmentedTextAsync(json, 4093);
        else await client.SendTextAsync(json);
        Assert.Equal(WebSocketMessageType.Close, (await client.ReceiveAsync(TimeSpan.FromSeconds(5))).Type);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, client.CloseStatus);
    }
}
