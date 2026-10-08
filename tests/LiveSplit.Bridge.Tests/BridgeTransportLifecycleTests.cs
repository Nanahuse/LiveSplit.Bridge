using System.Net;
using System.Net.Sockets;
using LiveSplit.Bridge.Protocol.V3;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class BridgeTransportLifecycleTests
{
    [Fact]
    public async Task RuntimeBindsV3RpcEndpointAndDisposesIt()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        var run = new Run(new StandardComparisonGeneratorsFactory());
        var runtime = new BridgeRuntime(TestLiveSplitState.Create(run), port);
        Assert.True(runtime.IsListening);
        using (var client = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(port)))
        {
            var response = await client.SendRequestAsync(new Request
            {
                ProtocolVersion = 3,
                RequestId = 5,
                GetCompletedCount = new GetCompletedCountRequest(),
            }, TimeSpan.FromSeconds(5));
            Assert.NotNull(response.GetCompletedCount);
            Assert.Equal(runtime.SessionId, response.SessionId);
        }
        runtime.Dispose();
        BridgeTestEndpoints.WaitForListener(port, expected: false);
    }

    [Fact]
    public void StartDisablesInactiveSessionCleanup()
    {
        using var transport = new WebSocketTransport(BridgeTestEndpoints.GetFreePort(), _ => new Response { ProtocolVersion = 3 });
        transport.Start();
        Assert.True(transport.IsListening);
        Assert.False(transport.IsInactiveSessionCleanupEnabled);
    }

    [Fact]
    public void StartOnUsedPortReportsWebSocketEndpoint()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Server.ExclusiveAddressUse = true;
        blocker.Start();
        try
        {
            using var transport = new WebSocketTransport(port, _ => new Response { ProtocolVersion = 3 });
            var exception = Assert.Throws<BridgeTransportStartException>(() => transport.Start());
            Assert.Equal($"ws://127.0.0.1:{port}", exception.Endpoint);
        }
        finally { blocker.Stop(); }
    }
}
