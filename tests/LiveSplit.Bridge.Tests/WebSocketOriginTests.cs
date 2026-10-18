using System.Text.Json;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class WebSocketOriginTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("http://localhost:54000")]
    [InlineData("https://localhost:54000")]
    [InlineData("http://127.0.0.1:54000")]
    [InlineData("https://127.0.0.1:54000")]
    [InlineData("http://[::1]:54000")]
    [InlineData("https://[::1]:54000")]
    public void ValidatorAllowsLoopbackAndMissingOrigins(string? origin)
    {
        Assert.True(WebSocketOriginValidator.IsAllowed(origin!));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://evil.example.com:54000")]
    [InlineData("null")]
    [InlineData("http://localhost.evil.com")]
    [InlineData("http://127.0.0.1.evil.com")]
    [InlineData("ftp://127.0.0.1")]
    public void ValidatorRejectsOtherOrigins(string origin)
    {
        Assert.False(WebSocketOriginValidator.IsAllowed(origin));
    }

    [Fact]
    public async Task JsonV4RpcAndEventsPreserveOriginPermissions()
    {
        foreach (var (origin, allowed) in new (string? Origin, bool Allowed)[]
        {
            (null, true),
            ("http://localhost:54000", true),
            ("http://127.0.0.1:54000", true),
            ("http://[::1]:54000", true),
            ("http://example.com", false),
            ("null", false),
        })
        {
            var port = BridgeTestEndpoints.GetFreePort();
            var run = new Run(new StandardComparisonGeneratorsFactory());
            run.Add(new Segment("One"));
            using var component = JsonWebSocketTestClient.StartComponent(TestLiveSplitState.Create(run), port);

            using var rpc = await JsonWebSocketTestClient.ConnectAsync(BridgeTestEndpoints.JsonRpc(port), origin).ConfigureAwait(false);
            await rpc.SendTextAsync("{\"request_id\":11,\"method\":\"timer_operation\",\"params\":{\"operation\":\"start\"}}");
            using var response = JsonDocument.Parse((await rpc.ReceiveAsync(TimeSpan.FromSeconds(5))).Data);
            Assert.Equal(11U, response.RootElement.GetProperty("request_id").GetUInt32());
            if (allowed)
            {
                Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("result").ValueKind);
                Assert.False(response.RootElement.TryGetProperty("error", out _));
            }
            else
            {
                Assert.Equal("permission_denied", response.RootElement.GetProperty("error").GetProperty("code").GetString());
                Assert.False(response.RootElement.TryGetProperty("result", out _));
            }

            using var events = await JsonWebSocketTestClient.ConnectAsync(BridgeTestEndpoints.JsonEvents(port), origin).ConfigureAwait(false);
            Assert.Equal(System.Net.WebSockets.WebSocketState.Open, events.State);
        }
    }
}
