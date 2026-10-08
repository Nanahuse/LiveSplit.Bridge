using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class BridgeRuntimeRpcTests
{
    [Fact]
    public async Task AttachReturnsSessionIdTimerStateAndPreservesRequestId()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 42,
            Attach = new AttachRequest(),
        });

        Assert.NotNull(response.Attach);
        Assert.Equal(42UL, response.RequestId);
        Assert.NotEqual(0UL, response.Attach.SessionId);
        Assert.NotNull(response.Attach.TimerState);
        Assert.Equal(1UL, response.Attach.TimerState.StateRevision);
        Assert.Equal(1UL, response.Attach.TimerState.RunRevision);
        Assert.Equal(response.Attach.SessionId, response.Attach.TimerState.SessionId);
    }

    [Fact]
    public async Task UnsupportedProtocolVersionReturnsErrorAndPreservesRequestId()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 99,
            RequestId = 7,
            GetTimerState = new GetTimerStateRequest(),
        });

        Assert.NotNull(response.Error);
        Assert.Equal(100, response.Error.Code);
        Assert.Equal(7UL, response.RequestId);
    }

    [Fact]
    public async Task ProtocolVersionOneIsRejected()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 1,
            RequestId = 6,
            GetTimerState = new GetTimerStateRequest(),
        });

        Assert.NotNull(response.Error);
        Assert.Equal(100, response.Error.Code);
        Assert.Equal(6UL, response.RequestId);
    }

    [Fact]
    public async Task GetTimerStateReturnsCurrentTimerState()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 8,
            GetTimerState = new GetTimerStateRequest(),
        });

        Assert.Equal(8UL, response.RequestId);
        Assert.NotNull(response.GetTimerState);
        Assert.NotNull(response.GetTimerState.TimerState);
        Assert.Equal(1UL, response.GetTimerState.TimerState.StateRevision);
        Assert.Equal(1UL, response.GetTimerState.TimerState.AttemptRevision);
        Assert.Equal(1UL, response.GetTimerState.TimerState.RuntimeRevision);
    }

    [Fact]
    public async Task GetRunReturnsCurrentlyLoadedRun()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 9,
            GetRun = new GetRunRequest(),
        });

        Assert.NotNull(response.GetRun);
        Assert.Equal("RPC Game", response.GetRun.Run.GameName);
        Assert.Equal("Any%", response.GetRun.Run.CategoryName);
        Assert.Equal("One", Assert.Single(response.GetRun.Run.Segments).Name);
    }

    [Fact]
    public async Task GetAttemptReturnsCurrentAttempt()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 12,
            GetAttempt = new GetAttemptRequest(),
        });

        Assert.NotNull(response.GetAttempt);
        Assert.Equal(1UL, response.GetAttempt.Attempt.AttemptRevision);
        var segment = Assert.Single(response.GetAttempt.Attempt.Segments);
        Assert.Equal(0U, segment.Index);
        Assert.NotNull(segment.SplitTime);
    }

    [Fact]
    public async Task GetRuntimeStateReturnsCurrentRuntimeState()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 13,
            GetRuntimeState = new GetRuntimeStateRequest(),
        });

        Assert.NotNull(response.GetRuntimeState);
        Assert.Equal(1UL, response.GetRuntimeState.RuntimeState.RuntimeRevision);
    }

    [Fact]
    public async Task TimerOperationStartSucceedsAndReturnsTimerState()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 10,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerStart },
        });

        Assert.NotNull(response.Operation);
        Assert.True(response.Operation.Success);
        Assert.NotNull(response.Operation.TimerState);
        Assert.Equal(2UL, response.Operation.TimerState.AttemptRevision);
    }

    [Fact]
    public async Task GameTimeOperationInitializeSucceedsAndReturnsTimerState()
    {
        using var fixture = await RpcFixture.CreateAsync();

        var response = await fixture.SendAsync(new Request
        {
            ProtocolVersion = 2,
            RequestId = 11,
            GameTimeOperation = new GameTimeOperationRequest { Operation = GameTimeOperationType.Initialize },
        });

        Assert.NotNull(response.Operation);
        Assert.True(response.Operation.Success);
        Assert.NotNull(response.Operation.TimerState);
        Assert.True(response.Operation.TimerState.IsGameTimeInitialized);
    }

    private sealed class RpcFixture : IDisposable
    {
        private readonly WebSocketTestClient client;

        private RpcFixture(V2BridgeRuntime runtime, WebSocketTestClient client)
        {
            Runtime = runtime;
            this.client = client;
        }

        public V2BridgeRuntime Runtime { get; }

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
            var runtime = new V2BridgeRuntime(state, port);
            var client = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(port));
            return new RpcFixture(runtime, client);
        }

        public Task<Response> SendAsync(Request request)
        {
            if (request.ProtocolVersion == 0)
            {
                request.ProtocolVersion = 2;
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
