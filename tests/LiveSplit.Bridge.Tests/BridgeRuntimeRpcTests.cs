using LiveSplit.Bridge.Protocol.V3;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class BridgeRuntimeRpcTests
{
    [Fact]
    public async Task WebSocketRequestsReachV3RuntimeAndReturnV3Envelope()
    {
        using var fixture = await RpcFixture.CreateAsync();
        var query = await fixture.SendAsync(new Request { RequestId = 42, GetTimerState = new GetTimerStateRequest() });
        Assert.NotNull(query.GetTimerState);
        AssertEnvelope(query, fixture.Runtime, 42);

        var control = await fixture.SendAsync(new Request
        {
            RequestId = 43,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerStart },
        });
        Assert.NotNull(control.Operation);
        Assert.Empty(OperationResponse.Descriptor.Fields.InDeclarationOrder());
        AssertEnvelope(control, fixture.Runtime, 43);
    }

    [Fact]
    public async Task UnsupportedVersionAndDeferredQueryReturnV3Errors()
    {
        using var fixture = await RpcFixture.CreateAsync();
        var unsupported = await fixture.SendAsync(new Request { ProtocolVersion = 2, RequestId = 7, GetTimerState = new GetTimerStateRequest() });
        var deferred = await fixture.SendAsync(new Request { RequestId = 8, GetRun = new GetRunRequest() });
        Assert.Equal(BridgeErrorCode.UnsupportedProtocolVersion, unsupported.Error.Code);
        Assert.Equal(BridgeErrorCode.OperationFailed, deferred.Error.Code);
        AssertEnvelope(unsupported, fixture.Runtime, 7);
        AssertEnvelope(deferred, fixture.Runtime, 8);
    }

    private static void AssertEnvelope(Response response, BridgeRuntime runtime, ulong requestId)
    {
        Assert.Equal(3U, response.ProtocolVersion);
        Assert.Equal(requestId, response.RequestId);
        Assert.Equal(runtime.SessionId, response.SessionId);
    }

    private sealed class RpcFixture : IDisposable
    {
        private readonly WebSocketTestClient client;
        private RpcFixture(BridgeRuntime runtime, WebSocketTestClient client) { Runtime = runtime; this.client = client; }
        public BridgeRuntime Runtime { get; }

        public static async Task<RpcFixture> CreateAsync()
        {
            var port = BridgeTestEndpoints.GetFreePort();
            var run = new Run(new StandardComparisonGeneratorsFactory()) { GameName = "RPC Game", CategoryName = "Any%" };
            run.Add(new Segment("One"));
            var runtime = new BridgeRuntime(TestLiveSplitState.Create(run), port);
            var client = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(port));
            return new RpcFixture(runtime, client);
        }

        public Task<Response> SendAsync(Request request)
        {
            if (request.ProtocolVersion == 0) request.ProtocolVersion = 3;
            return client.SendRequestAsync(request, TimeSpan.FromSeconds(5));
        }

        public void Dispose() { client.Dispose(); Runtime.Dispose(); }
    }
}
