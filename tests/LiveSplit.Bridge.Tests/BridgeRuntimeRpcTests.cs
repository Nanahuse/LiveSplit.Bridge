using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V1;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using NetMQ;
using NetMQ.Sockets;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class BridgeRuntimeRpcTests
{
    [Fact]
    public void AttachReturnsSessionIdSnapshotAndPreservesRequestId()
    {
        using var fixture = RpcFixture.Create();

        var response = fixture.Send(new Request
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
    public void UnsupportedProtocolVersionReturnsErrorAndPreservesRequestId()
    {
        using var fixture = RpcFixture.Create();

        var response = fixture.Send(new Request
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
    public void GetSnapshotReturnsCurrentSnapshot()
    {
        using var fixture = RpcFixture.Create();

        var response = fixture.Send(new Request
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
    public void GetRunReturnsCurrentlyLoadedRun()
    {
        using var fixture = RpcFixture.Create();

        var response = fixture.Send(new Request
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
    public void TimerOperationStartSucceedsAndReturnsSnapshot()
    {
        using var fixture = RpcFixture.Create();

        var response = fixture.Send(new Request
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
    public void GameTimeOperationInitializeSucceedsAndReturnsSnapshot()
    {
        using var fixture = RpcFixture.Create();

        var response = fixture.Send(new Request
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
        private readonly RequestSocket requestSocket;

        private RpcFixture(BridgeRuntime runtime, int rpcPort)
        {
            Runtime = runtime;
            requestSocket = new RequestSocket();
            requestSocket.Connect(BridgeTestEndpoints.Rpc(rpcPort));
        }

        public BridgeRuntime Runtime { get; }

        public static RpcFixture Create()
        {
            var (rpcPort, eventPort) = BridgeTestEndpoints.GetFreePorts();
            var run = new Run(new StandardComparisonGeneratorsFactory())
            {
                GameName = "RPC Game",
                CategoryName = "Any%",
            };
            run.Add(new Segment("One"));
            var state = TestLiveSplitState.Create(run);
            var runtime = new BridgeRuntime(state, rpcPort, eventPort);
            return new RpcFixture(runtime, rpcPort);
        }

        public Response Send(Request request)
        {
            if (request.ProtocolVersion == 0)
            {
                request.ProtocolVersion = 1;
            }

            requestSocket.SendFrame(request.ToByteArray());
            Assert.True(
                requestSocket.TryReceiveFrameBytes(TimeSpan.FromSeconds(5), out var data),
                "Timed out waiting for RPC response.");
            return Response.Parser.ParseFrom(data);
        }

        public void Dispose()
        {
            requestSocket.Dispose();
            Runtime.Dispose();
        }
    }
}
