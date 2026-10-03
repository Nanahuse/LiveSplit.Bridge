using System.Net.WebSockets;
using LiveSplit.Bridge.Protocol.V1;

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
    public async Task ServerAcceptsConnectionWithoutOrigin()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var transport = CreateTransport(port);
        transport.Start();

        using var client = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(port));

        Assert.True(transport.IsListening);
    }

    [Fact]
    public async Task ServerAcceptsLoopbackOrigin()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var transport = CreateTransport(port);
        transport.Start();

        using var client = await WebSocketTestClient.ConnectAsync(
            BridgeTestEndpoints.Rpc(port),
            $"http://localhost:{port}");
    }

    [Fact]
    public async Task ServerRejectsForeignOrigin()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var transport = CreateTransport(port);
        transport.Start();

        await Assert.ThrowsAsync<WebSocketException>(
            () => WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(port), "http://example.com"));
    }

    [Fact]
    public async Task ServerRejectsNullOrigin()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var transport = CreateTransport(port);
        transport.Start();

        await Assert.ThrowsAsync<WebSocketException>(
            () => WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(port), "null"));
    }

    private static WebSocketTransport CreateTransport(int port)
    {
        return new WebSocketTransport(
            port,
            BridgeRuntime.HeartbeatInterval,
            _ => new Response { ProtocolVersion = 1 },
            () => new BridgeEvent { Type = BridgeEventType.EventHeartbeat },
            _ => { });
    }
}
