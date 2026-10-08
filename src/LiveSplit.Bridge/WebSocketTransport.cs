#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading;
using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V3;
using WebSocketSharp.Server;

namespace LiveSplit.Bridge;

internal sealed class WebSocketTransport : IDisposable
{
    internal const string RpcPath = "/bridge/v3/rpc";
    internal const string EventsPath = "/bridge/v3/events";
    private readonly int port;
    private readonly Func<Request, Response> requestHandler;
    private readonly Action<byte[]>? eventSender;
    private readonly BlockingCollection<BridgeEvent> publishQueue = new();
    private readonly Thread publisherThread;
    private WebSocketServer? server;
    private WebSocketSessionManager? eventSessions;
    private int disposed;

    public WebSocketTransport(int port, Func<Request, Response> requestHandler, Action<byte[]>? eventSender = null)
    {
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        this.port = port;
        this.requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
        this.eventSender = eventSender;
        publisherThread = new Thread(PublisherLoop)
        {
            IsBackground = true,
            Name = "LiveSplit.Bridge.EventPublisher",
        };
    }

    internal int Port => port;
    internal string Endpoint => $"ws://127.0.0.1:{port}";
    internal string EventsEndpoint => $"{Endpoint}{EventsPath}";
    internal bool IsListening => server?.IsListening ?? false;
    internal bool IsInactiveSessionCleanupEnabled => server?.KeepClean ?? false;

    public void Start()
    {
        try
        {
            server = new WebSocketServer(IPAddress.Loopback, port) { KeepClean = false };
            server.AddWebSocketService<WebSocketRpcBehavior>(RpcPath, () => new WebSocketRpcBehavior(requestHandler));
            server.AddWebSocketService<WebSocketEventBehavior>(EventsPath, () => new WebSocketEventBehavior());
            server.Start();
        }
        catch (Exception exception)
        {
            StopServer();
            throw new BridgeTransportStartException(Endpoint, exception);
        }
        if (!server.IsListening)
        {
            StopServer();
            throw new BridgeTransportStartException(Endpoint, new InvalidOperationException("The WebSocket server did not start listening."));
        }
        eventSessions = server.WebSocketServices[EventsPath].Sessions;
        publisherThread.Start();
        Debug.WriteLine($"[LiveSplit.Bridge] WebSocket server listening on {Endpoint}");
    }

    internal void Publish(BridgeEvent bridgeEvent)
    {
        if (bridgeEvent == null || Volatile.Read(ref disposed) != 0) return;
        try
        {
            publishQueue.TryAdd(bridgeEvent);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown disposed the queue while a LiveSplit callback was publishing.
        }
        catch (InvalidOperationException)
        {
            // Shutdown completed the queue while a LiveSplit callback was publishing.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        publishQueue.CompleteAdding();
        try { publisherThread.Join(TimeSpan.FromSeconds(2)); }
        catch (ThreadStateException) { }
        StopServer();
        eventSessions = null;
        publishQueue.Dispose();
    }

    private void StopServer()
    {
        try { server?.Stop(); }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] WebSocket server stop failed: {exception.Message}"); }
        finally { server = null; }
    }

    private void PublisherLoop()
    {
        try
        {
            foreach (var bridgeEvent in publishQueue.GetConsumingEnumerable()) Broadcast(bridgeEvent.ToByteArray());
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] Event publisher failed: {exception.Message}");
        }
    }

    private void Broadcast(byte[] data)
    {
        if (eventSender != null)
        {
            eventSender(data);
            return;
        }

        var sessions = eventSessions;
        if (sessions == null) return;
        foreach (var session in sessions.Sessions)
        {
            try { sessions.SendTo(data, session.ID); }
            catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] Failed to send to event session {session.ID}: {exception.Message}"); }
        }
    }
}
