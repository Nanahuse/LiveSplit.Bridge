using System.Net;
using System.Net.Sockets;
using LiveSplit.Bridge.Protocol.V1;
using NetMQ;
using NetMQ.Sockets;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class BridgeTransportLifecycleTests
{
    [Fact]
    public void StartBindsRpcAndEventEndpoints()
    {
        var (rpcPort, eventPort) = BridgeTestEndpoints.GetFreePorts();
        using var transport = CreateTransport(rpcPort, eventPort);

        transport.Start();

        BridgeTestEndpoints.WaitForListener(rpcPort, expected: true);
        BridgeTestEndpoints.WaitForListener(eventPort, expected: true);
    }

    [Fact]
    public void RpcBindFailureReportsRpcEndpoint()
    {
        var (rpcPort, eventPort) = BridgeTestEndpoints.GetFreePorts();
        var blocker = new TcpListener(IPAddress.Loopback, rpcPort);
        blocker.Server.ExclusiveAddressUse = true;
        blocker.Start();

        try
        {
            using var transport = CreateTransport(rpcPort, eventPort);

            var exception = Assert.Throws<BridgeTransportStartException>(() => transport.Start());

            Assert.Equal(BridgeEndpointKind.Rpc, exception.EndpointKind);
            Assert.Equal(BridgeTestEndpoints.Rpc(rpcPort), exception.Endpoint);
            Assert.NotNull(exception.InnerException);
            BridgeTestEndpoints.WaitForListener(eventPort, expected: false);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void EventBindFailureReportsEventEndpoint()
    {
        var (rpcPort, eventPort) = BridgeTestEndpoints.GetFreePorts();
        var blocker = new TcpListener(IPAddress.Loopback, eventPort);
        blocker.Server.ExclusiveAddressUse = true;
        blocker.Start();

        try
        {
            using var transport = CreateTransport(rpcPort, eventPort);

            var exception = Assert.Throws<BridgeTransportStartException>(() => transport.Start());

            Assert.Equal(BridgeEndpointKind.Event, exception.EndpointKind);
            Assert.Equal(BridgeTestEndpoints.Event(eventPort), exception.Endpoint);
            Assert.NotNull(exception.InnerException);
            BridgeTestEndpoints.WaitForListener(rpcPort, expected: false);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void DisposeReleasesEndpointsForRestart()
    {
        var (rpcPort, eventPort) = BridgeTestEndpoints.GetFreePorts();

        var transport = CreateTransport(rpcPort, eventPort);
        transport.Start();
        BridgeTestEndpoints.WaitForListener(rpcPort, expected: true);
        BridgeTestEndpoints.WaitForListener(eventPort, expected: true);
        transport.Dispose();
        BridgeTestEndpoints.WaitForListener(rpcPort, expected: false);
        BridgeTestEndpoints.WaitForListener(eventPort, expected: false);

        using var restarted = CreateTransport(rpcPort, eventPort);
        restarted.Start();
        BridgeTestEndpoints.WaitForListener(rpcPort, expected: true);
        BridgeTestEndpoints.WaitForListener(eventPort, expected: true);
    }

    [Fact]
    public void PublishedEventIsDeliveredToSubscriber()
    {
        var (rpcPort, eventPort) = BridgeTestEndpoints.GetFreePorts();
        using var transport = CreateTransport(rpcPort, eventPort);
        transport.Start();

        using var subscriber = new SubscriberSocket();
        subscriber.Subscribe(string.Empty);
        subscriber.Connect(BridgeTestEndpoints.Event(eventPort));

        WaitForHeartbeat(subscriber);

        transport.Publish(new BridgeEvent
        {
            SessionId = 7,
            EventSequence = 1,
            Type = BridgeEventType.EventTimerStarted,
            Snapshot = new TimerSnapshot(),
        });

        var received = ReceiveUntil(subscriber, BridgeEventType.EventTimerStarted);

        Assert.Equal(7UL, received.SessionId);
        Assert.Equal(1UL, received.EventSequence);
        Assert.NotNull(received.Snapshot);
    }

    [Fact]
    public void PublishedEventSettlesSequenceThroughCallback()
    {
        var (rpcPort, eventPort) = BridgeTestEndpoints.GetFreePorts();
        var settled = new List<ulong>();
        using var transport = new ZeroMqTransport(
            BridgeTestEndpoints.Rpc(rpcPort),
            BridgeTestEndpoints.Event(eventPort),
            _ => new Response { ProtocolVersion = 1 },
            () => new BridgeEvent { Type = BridgeEventType.EventHeartbeat },
            sequence =>
            {
                lock (settled)
                {
                    settled.Add(sequence);
                }
            });
        transport.Start();

        transport.Publish(new BridgeEvent
        {
            SessionId = 1,
            EventSequence = 5,
            Type = BridgeEventType.EventTimerSplit,
            Snapshot = new TimerSnapshot(),
        });

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (settled)
            {
                if (settled.Contains(5UL))
                {
                    return;
                }
            }

            Thread.Sleep(20);
        }

        Assert.Contains(5UL, settled);
    }

    private static ZeroMqTransport CreateTransport(int rpcPort, int eventPort)
    {
        return new ZeroMqTransport(
            BridgeTestEndpoints.Rpc(rpcPort),
            BridgeTestEndpoints.Event(eventPort),
            _ => new Response { ProtocolVersion = 1 },
            () => new BridgeEvent { Type = BridgeEventType.EventHeartbeat },
            _ => { });
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
}
