using LiveSplit.Bridge.Protocol.V1;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class BridgeRuntimeRpcTests
{
    [Fact]
    public async Task AttachReturnsSessionIdSnapshotAndPreservesRequestId()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 1,
            RequestId = 42,
            Attach = new AttachRequest(),
        });

        Assert.NotNull(response.Attach);
        Assert.Equal(42UL, response.RequestId);
        Assert.NotEqual(0UL, response.Attach.SessionId);
        Assert.NotNull(response.Attach.Snapshot);
        Assert.Equal(1UL, response.Attach.Snapshot.StateRevision);
        Assert.Equal(1UL, response.Attach.Snapshot.RunRevision);
        Assert.Equal(response.Attach.SessionId, response.Attach.Snapshot.SessionId);
    }

    [Fact]
    public async Task UnsupportedProtocolVersionReturnsErrorAndPreservesRequestId()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 99,
            RequestId = 7,
            GetSnapshot = new GetSnapshotRequest(),
        });

        Assert.NotNull(response.Error);
        Assert.Equal(100, response.Error.Code);
        Assert.Equal(7UL, response.RequestId);
    }

    [Fact]
    public async Task GetSnapshotReturnsCurrentSnapshot()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 1,
            RequestId = 8,
            GetSnapshot = new GetSnapshotRequest(),
        });

        Assert.Equal(8UL, response.RequestId);
        Assert.NotNull(response.GetSnapshot);
        Assert.NotNull(response.GetSnapshot.Snapshot);
        Assert.Equal(1UL, response.GetSnapshot.Snapshot.StateRevision);
    }

    [Fact]
    public async Task GetRunReturnsCurrentlyLoadedRun()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 1,
            RequestId = 9,
            GetRun = new GetRunRequest(),
        });

        Assert.NotNull(response.GetRun);
        Assert.Equal("RPC Game", response.GetRun.Run.GameName);
        Assert.Equal("Any%", response.GetRun.Run.CategoryName);
        Assert.Equal("One", Assert.Single(response.GetRun.Run.Segments).Name);
    }

    [Fact]
    public async Task TimerOperationStartSucceedsAndReturnsSnapshot()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 1,
            RequestId = 10,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerStart },
        });

        Assert.NotNull(response.Operation);
        Assert.True(response.Operation.Success);
        Assert.NotNull(response.Operation.Snapshot);
    }

    [Fact]
    public async Task GameTimeOperationInitializeSucceedsAndReturnsSnapshot()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 1,
            RequestId = 11,
            GameTimeOperation = new GameTimeOperationRequest { Operation = GameTimeOperationType.Initialize },
        });

        Assert.NotNull(response.Operation);
        Assert.True(response.Operation.Success);
        Assert.NotNull(response.Operation.Snapshot);
        Assert.True(response.Operation.Snapshot.IsGameTimeInitialized);
    }

    private sealed class RpcFixture : IDisposable
    {
        private readonly WebSocketTestClient client;

        private RpcFixture(BridgeRuntime runtime, WebSocketTestClient client)
        {
            Runtime = runtime;
            this.client = client;
        }

        public BridgeRuntime Runtime { get; }

        public static async Task<RpcFixture> CreateAsync()
        {
            var port = BridgeTestEndpoints.GetFreePort();
            var run = new Run(new StandardComparisonGeneratorsFactory())
            {
                GameName = "RPC Game",
                CategoryName = "Any%",
            };
            run.Add(new Segment("One"));
            var state = TestLiveSplitState.Create(run);
            var runtime = new BridgeRuntime(state, port);
            var client = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(port));
            return new RpcFixture(runtime, client);
        }

        public Task<Response> SendAsync(Request request)
        {
            if (request.ProtocolVersion == 0)
            {
                request.ProtocolVersion = 1;
            }

            return client.SendRequestAsync(request, TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            client.Dispose();
            Runtime.Dispose();
        }
    }
}
