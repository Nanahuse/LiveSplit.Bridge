using System.Text.Json;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public sealed class JsonWebSocketLifecycleTests
{
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
}
