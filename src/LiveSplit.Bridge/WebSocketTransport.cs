#nullable enable
using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using LiveSplit.Bridge.Protocol.V3;
using WebSocketSharp.Server;

namespace LiveSplit.Bridge;

internal sealed class WebSocketTransport : IDisposable
{
    internal const string RpcPath = "/bridge/v3/rpc";
    private readonly int port;
    private readonly Func<Request, Response> requestHandler;
    private WebSocketServer? server;
    private int disposed;

    public WebSocketTransport(int port, Func<Request, Response> requestHandler)
    {
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        this.port = port;
        this.requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
    }

    internal int Port => port;
    internal string Endpoint => $"ws://127.0.0.1:{port}";
    internal bool IsListening => server?.IsListening ?? false;
    internal bool IsInactiveSessionCleanupEnabled => server?.KeepClean ?? false;

    public void Start()
    {
        try
        {
            server = new WebSocketServer(IPAddress.Loopback, port) { KeepClean = false };
            server.AddWebSocketService<WebSocketRpcBehavior>(RpcPath, () => new WebSocketRpcBehavior(requestHandler));
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
        Debug.WriteLine($"[LiveSplit.Bridge] WebSocket server listening on {Endpoint}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        StopServer();
    }

    private void StopServer()
    {
        try { server?.Stop(); }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] WebSocket server stop failed: {exception.Message}"); }
        finally { server = null; }
    }
}
