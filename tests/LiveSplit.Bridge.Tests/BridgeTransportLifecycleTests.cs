using System.Net;
using System.Net.Sockets;
using LiveSplit.Bridge.Protocol.V1;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class BridgeTransportLifecycleTests
{
    [Fact]
    public async Task StartBindsWebSocketEndpoints()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var transport = CreateTransport(port);

        transport.Start();

        BridgeTestEndpoints.WaitForListener(port, expected: true);
        Assert.True(transport.IsListening);

        using var rpc = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(port));
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
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
            using var transport = CreateTransport(port);

            var exception = Assert.Throws<BridgeTransportStartException>(() => transport.Start());

            Assert.Equal(BridgeTestEndpoints.WebSocket(port), exception.Endpoint);
            Assert.NotNull(exception.InnerException);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void DisposeReleasesEndpointForRestart()
    {
        var port = BridgeTestEndpoints.GetFreePort();

        var transport = CreateTransport(port);
        transport.Start();
        BridgeTestEndpoints.WaitForListener(port, expected: true);
        transport.Dispose();
        BridgeTestEndpoints.WaitForListener(port, expected: false);

        using var restarted = CreateTransport(port);
        restarted.Start();
        BridgeTestEndpoints.WaitForListener(port, expected: true);
    }

    [Fact]
    public async Task PublishedEventIsDeliveredToClient()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var transport = CreateTransport(port);
        transport.Start();

        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        await ReceiveEventUntilAsync(events, BridgeEventType.EventHeartbeat);

        transport.Publish(new BridgeEvent
        {
            SessionId = 7,
            EventSequence = 1,
            Type = BridgeEventType.EventTimerStarted,
            Snapshot = new TimerSnapshot(),
        });

        var received = await ReceiveEventUntilAsync(events, BridgeEventType.EventTimerStarted);

        Assert.Equal(7UL, received.SessionId);
        Assert.Equal(1UL, received.EventSequence);
        Assert.NotNull(received.Snapshot);
    }

    [Fact]
    public async Task PublishedEventIsBroadcastToMultipleClients()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var transport = CreateTransport(port);
        transport.Start();

        using var first = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        using var second = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        await ReceiveEventUntilAsync(first, BridgeEventType.EventHeartbeat);
        await ReceiveEventUntilAsync(second, BridgeEventType.EventHeartbeat);

        transport.Publish(new BridgeEvent
        {
            SessionId = 11,
            EventSequence = 4,
            Type = BridgeEventType.EventTimerSplit,
            Snapshot = new TimerSnapshot(),
        });

        var firstEvent = await ReceiveEventUntilAsync(first, BridgeEventType.EventTimerSplit);
        var secondEvent = await ReceiveEventUntilAsync(second, BridgeEventType.EventTimerSplit);

        Assert.Equal(4UL, firstEvent.EventSequence);
        Assert.Equal(4UL, secondEvent.EventSequence);
    }

    [Fact]
    public async Task BroadcastContinuesAfterClientDisconnects()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        using var transport = CreateTransport(port);
        transport.Start();

        var disconnected = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        using var remaining = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        await ReceiveEventUntilAsync(disconnected, BridgeEventType.EventHeartbeat);
        await ReceiveEventUntilAsync(remaining, BridgeEventType.EventHeartbeat);

        disconnected.Dispose();
        Thread.Sleep(200);

        transport.Publish(new BridgeEvent
        {
            SessionId = 13,
            EventSequence = 9,
            Type = BridgeEventType.EventTimerSplit,
            Snapshot = new TimerSnapshot(),
        });

        var received = await ReceiveEventUntilAsync(remaining, BridgeEventType.EventTimerSplit);
        Assert.Equal(9UL, received.EventSequence);
    }

    [Fact]
    public void PublishedEventSettlesSequenceThroughCallback()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        var settled = new List<ulong>();
        using var transport = new WebSocketTransport(
            port,
            BridgeRuntime.HeartbeatInterval,
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

    [Fact]
    public async Task RpcRequestsAreSerializedAcrossClients()
    {
        var port = BridgeTestEndpoints.GetFreePort();
        var current = 0;
        var maxConcurrent = 0;
        using var transport = new WebSocketTransport(
            port,
            BridgeRuntime.HeartbeatInterval,
            _ =>
            {
                var active = Interlocked.Increment(ref current);
                int observed;
                do
                {
                    observed = Volatile.Read(ref maxConcurrent);
                    if (active <= observed)
                    {
                        break;
                    }

                    Interlocked.CompareExchange(ref maxConcurrent, active, observed);
                }
                while (true);

                Thread.Sleep(100);
                Interlocked.Decrement(ref current);
                return new Response { ProtocolVersion = 1 };
            },
            () => new BridgeEvent { Type = BridgeEventType.EventHeartbeat },
            _ => { });
        transport.Start();

        var clients = new List<WebSocketTestClient>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                clients.Add(await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Rpc(port)));
            }

            var sends = clients.Select(client => client.SendRequestAsync(
                new Request
                {
                    ProtocolVersion = 1,
                    RequestId = 1,
                    GetSnapshot = new GetSnapshotRequest(),
                },
                TimeSpan.FromSeconds(10))).ToArray();

            await Task.WhenAll(sends);
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }

        Assert.Equal(1, maxConcurrent);
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

    private static async Task<BridgeEvent> ReceiveEventUntilAsync(
        WebSocketTestClient client,
        BridgeEventType type)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var data = await client.ReceiveBinaryAsync(TimeSpan.FromSeconds(3));
            var bridgeEvent = BridgeEvent.Parser.ParseFrom(data);
            if (bridgeEvent.Type == type)
            {
                return bridgeEvent;
            }
        }

        throw new TimeoutException($"Did not receive {type}.");
    }
}
