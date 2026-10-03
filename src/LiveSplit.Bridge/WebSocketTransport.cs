using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading;
using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V1;
using WebSocketSharp.Server;

namespace LiveSplit.Bridge;

internal sealed class WebSocketTransport : IBridgeTransport
{
    internal const string RpcPath = "/bridge/v1/rpc";
    internal const string EventPath = "/bridge/v1/events";

    private readonly int port;
    private readonly TimeSpan heartbeatInterval;
    private readonly Func<Request, Response> requestHandler;
    private readonly Func<BridgeEvent> heartbeatFactory;
    private readonly Action<ulong> eventSettled;
    private readonly CancellationTokenSource cancellation = new();
    private readonly BlockingCollection<BridgeEvent> publishQueue = new();
    private readonly object rpcLock = new();
    private readonly Thread publisherThread;
    private WebSocketServer server;
    private WebSocketSessionManager eventSessions;
    private int disposed;

    public WebSocketTransport(
        int port,
        TimeSpan heartbeatInterval,
        Func<Request, Response> requestHandler,
        Func<BridgeEvent> heartbeatFactory,
        Action<ulong> eventSettled)
    {
        if (port < 1 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        this.port = port;
        this.heartbeatInterval = heartbeatInterval;
        this.requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
        this.heartbeatFactory = heartbeatFactory ?? throw new ArgumentNullException(nameof(heartbeatFactory));
        this.eventSettled = eventSettled ?? throw new ArgumentNullException(nameof(eventSettled));

        publisherThread = new Thread(PublisherLoop)
        {
            IsBackground = true,
            Name = "LiveSplit.Bridge.PublisherLoop"
        };
    }

    internal int Port => port;

    internal string Endpoint => $"ws://127.0.0.1:{port}";

    internal bool IsListening => server?.IsListening ?? false;

    public void Start()
    {
        try
        {
            server = new WebSocketServer(IPAddress.Loopback, port);
            server.AddWebSocketService<WebSocketRpcBehavior>(
                RpcPath,
                () => new WebSocketRpcBehavior(requestHandler, rpcLock));
            server.AddWebSocketService<WebSocketEventBehavior>(EventPath, () => new WebSocketEventBehavior());
            server.Start();
        }
        catch (Exception exception)
        {
            StopServer();
            throw new BridgeTransportStartException(BridgeEndpointKind.WebSocket, Endpoint, exception);
        }

        if (!server.IsListening)
        {
            StopServer();
            throw new BridgeTransportStartException(
                BridgeEndpointKind.WebSocket,
                Endpoint,
                new InvalidOperationException("The WebSocket server did not start listening."));
        }

        eventSessions = server.WebSocketServices[EventPath].Sessions;
        publisherThread.Start();
        Debug.WriteLine($"[LiveSplit.Bridge] WebSocket server listening on {Endpoint}");
    }

    public void Publish(BridgeEvent bridgeEvent)
    {
        if (bridgeEvent == null)
        {
            throw new ArgumentNullException(nameof(bridgeEvent));
        }

        if (cancellation.IsCancellationRequested || publishQueue.IsAddingCompleted)
        {
            return;
        }

        try
        {
            publishQueue.Add(bridgeEvent, cancellation.Token);
        }
        catch (InvalidOperationException)
        {
            // The queue was completed during shutdown.
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        cancellation.Cancel();
        publishQueue.CompleteAdding();

        JoinThread(publisherThread);

        StopServer();

        eventSessions = null;
        publishQueue.Dispose();
        cancellation.Dispose();
    }

    private void StopServer()
    {
        try
        {
            server?.Stop();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] WebSocket server stop failed: {exception.Message}");
        }
        finally
        {
            server = null;
        }
    }

    private static void JoinThread(Thread thread)
    {
        try
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
        catch (ThreadStateException)
        {
            // The thread was never started because startup failed.
        }
    }

    private void PublisherLoop()
    {
        try
        {
            var clock = Stopwatch.StartNew();
            var nextHeartbeat = heartbeatInterval;

            while (!cancellation.IsCancellationRequested)
            {
                var remaining = nextHeartbeat - clock.Elapsed;
                var waitMilliseconds = remaining <= TimeSpan.Zero
                    ? 0
                    : (int)Math.Min(Math.Ceiling(remaining.TotalMilliseconds), int.MaxValue);

                if (publishQueue.TryTake(out var bridgeEvent, waitMilliseconds, cancellation.Token))
                {
                    BroadcastSequencedEvent(bridgeEvent);
                }

                if (clock.Elapsed >= nextHeartbeat)
                {
                    BroadcastHeartbeat();
                    do
                    {
                        nextHeartbeat += heartbeatInterval;
                    }
                    while (nextHeartbeat <= clock.Elapsed);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] Publisher loop error: {exception.Message}");
        }
    }

    private void BroadcastSequencedEvent(BridgeEvent bridgeEvent)
    {
        var sequence = bridgeEvent.EventSequence;

        try
        {
            Broadcast(bridgeEvent.ToByteArray());
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] Event broadcast failed: {exception.Message}");
        }
        finally
        {
            eventSettled(sequence);
        }
    }

    private void BroadcastHeartbeat()
    {
        try
        {
            Broadcast(heartbeatFactory().ToByteArray());
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] Heartbeat broadcast failed: {exception.Message}");
        }
    }

    private void Broadcast(byte[] data)
    {
        var sessions = eventSessions;
        if (sessions == null)
        {
            return;
        }

        foreach (var session in sessions.Sessions)
        {
            try
            {
                sessions.SendTo(data, session.ID);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[LiveSplit.Bridge] Failed to send to event session {session.ID}: {exception.Message}");
            }
        }
    }
}
