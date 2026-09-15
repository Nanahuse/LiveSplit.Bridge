using System.Net;
using System.Net.Sockets;
using LiveSplit.Bridge.Protocol.V1;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using NetMQ;
using NetMQ.Sockets;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class RunRevisionTests
{
    [Fact]
    public void RunRevisionStartsAtOneAndAdvancesWithRunChanges()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        var state = TestLiveSplitState.Create(run);
        var (rpcPort, eventPort) = GetFreePorts();
        using var runtime = new BridgeRuntime(state, rpcPort, eventPort);

        var initialStateRevision = runtime.StateRevision;
        Assert.Equal(1UL, runtime.RunRevision);

        var attached = Handle(runtime, new Request { RequestId = 1, Attach = new AttachRequest() });
        Assert.Equal(1UL, attached.Attach.Snapshot.RunRevision);
        Assert.Equal(initialStateRevision, attached.Attach.Snapshot.StateRevision);

        state.CallRunManuallyModified();

        Assert.Equal(2UL, runtime.RunRevision);
        Assert.Equal(initialStateRevision + 1, runtime.StateRevision);

        var afterChange = Handle(runtime, new Request { RequestId = 2, GetRun = new GetRunRequest() });
        Assert.Equal(2UL, afterChange.GetRun.Run.RunRevision);
        Assert.Equal(runtime.StateRevision, afterChange.GetRun.Run.CapturedStateRevision);

        state.CallRunManuallyModified();
        state.CallRunManuallyModified();

        Assert.Equal(4UL, runtime.RunRevision);
        Assert.Equal(initialStateRevision + 3, runtime.StateRevision);

        var latest = Handle(runtime, new Request { RequestId = 3, GetRun = new GetRunRequest() });
        Assert.Equal(4UL, latest.GetRun.Run.RunRevision);
        Assert.Equal("One", latest.GetRun.Run.Segments[0].Name);
        Assert.Equal("Two", latest.GetRun.Run.Segments[1].Name);
    }

    [Fact]
    public void GetRunReturnsTheCurrentlyLoadedRun()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory())
        {
            GameName = "First Game",
            CategoryName = "First Category",
        };
        run.Add(new Segment("First"));
        var state = TestLiveSplitState.Create(run);
        var (rpcPort, eventPort) = GetFreePorts();
        using var runtime = new BridgeRuntime(state, rpcPort, eventPort);

        var first = Handle(runtime, new Request { RequestId = 1, GetRun = new GetRunRequest() });
        Assert.Equal("First Game", first.GetRun.Run.GameName);

        var replacement = new Run(new StandardComparisonGeneratorsFactory())
        {
            GameName = "Second Game",
            CategoryName = "Second Category",
        };
        replacement.Add(new Segment("Second"));
        state.Run = replacement;
        state.CallRunManuallyModified();

        var second = Handle(runtime, new Request { RequestId = 2, GetRun = new GetRunRequest() });
        Assert.Equal("Second Game", second.GetRun.Run.GameName);
        Assert.Equal("Second Category", second.GetRun.Run.CategoryName);
        Assert.Equal("Second", Assert.Single(second.GetRun.Run.Segments).Name);
        Assert.Equal(runtime.RunRevision, second.GetRun.Run.RunRevision);
    }

    [Fact]
    public void RunChangePublishesRunChangedEventWithUpdatedRevision()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        var state = TestLiveSplitState.Create(run);
        var (rpcPort, eventPort) = GetFreePorts();
        using var runtime = new BridgeRuntime(state, rpcPort, eventPort);

        using var subscriber = new SubscriberSocket();
        subscriber.Subscribe(string.Empty);
        subscriber.Connect($"tcp://127.0.0.1:{eventPort}");

        WaitForHeartbeat(subscriber);

        state.CallRunManuallyModified();

        var runChanged = ReceiveUntil(subscriber, BridgeEventType.EventRunChanged);
        Assert.NotNull(runChanged.Snapshot);
        Assert.Equal(2UL, runChanged.Snapshot.RunRevision);
    }

    private static void WaitForHeartbeat(SubscriberSocket subscriber)
    {
        ReceiveUntil(subscriber, BridgeEventType.EventHeartbeat);
    }

    private static BridgeEvent ReceiveUntil(SubscriberSocket subscriber, BridgeEventType type)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Assert.True(
                subscriber.TryReceiveFrameBytes(TimeSpan.FromSeconds(2), out var data),
                $"Timed out waiting for {type}.");
            var bridgeEvent = BridgeEvent.Parser.ParseFrom(data);
            if (bridgeEvent.Type == type)
            {
                return bridgeEvent;
            }
        }

        throw new TimeoutException($"Did not receive {type}.");
    }

    private static Response Handle(BridgeRuntime runtime, Request request)
    {
        request.ProtocolVersion = 1;
        return runtime.HandleRequest(request);
    }

    private static (int Rpc, int Event) GetFreePorts()
    {
        var rpc = GetFreePort();
        int @event;
        do
        {
            @event = GetFreePort();
        }
        while (@event == rpc);

        return (rpc, @event);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
