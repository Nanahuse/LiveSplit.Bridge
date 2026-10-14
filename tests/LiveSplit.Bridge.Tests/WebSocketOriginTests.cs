using LiveSplit.Bridge.Protocol.V3;

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

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://localhost", true)]
    [InlineData("null", false)]
    [InlineData("http://absolute", false)]
    [InlineData("http://example.com", false)]
    public async Task RpcAcceptsEveryOriginAndCapturesControlPermission(string? origin, bool expectedPermission)
    {
        var port = BridgeTestEndpoints.GetFreePort();
        var permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = CreateTransport(port, allowControl => permission.TrySetResult(allowControl));
        transport.Start();

        using var client = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(port), origin);
        var response = await client.SendRequestAsync(new Request
        {
            ProtocolVersion = 3,
            RequestId = 11,
            GetTimerState = new GetTimerStateRequest(),
        }, TimeSpan.FromSeconds(5));

        Assert.Equal(11UL, response.RequestId);
        Assert.Equal(expectedPermission, await permission.Task);
        Assert.True(transport.IsListening);
    }

    private static WebSocketTransport CreateTransport(int port, Action<bool> onRequest)
    {
        return new WebSocketTransport(port, (request, allowControl) =>
        {
            onRequest(allowControl);
            return new Response { ProtocolVersion = 3, RequestId = request.RequestId };
        });
    }
}
