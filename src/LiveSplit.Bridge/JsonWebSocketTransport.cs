#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebSocketSharp;
using WebSocketSharp.Server;
using Json = LiveSplit.Bridge.Json;

namespace LiveSplit.Bridge;

/// <summary>Loopback-only JSON v4 WebSocket endpoints.</summary>
internal sealed class JsonWebSocketTransport : IJsonEventSink, IDisposable
{
    internal const int MaximumRpcMessageBytes = 64 * 1024;
    internal const string RpcPath = "/bridge/v4/rpc";
    internal const string EventsPath = "/bridge/v4/events";

    private readonly int port;
    private readonly JsonSerializerOptions serializerOptions = Json.BridgeJsonOptions.Create();
    // One shared, bounded buffer keeps socket writes off LiveSplit's update thread.
    // Notifications are best effort: a full buffer drops new events instead of waiting.
    private readonly BlockingCollection<Json.BridgeEvent> events = new(256);
    private WebSocketServer? server;
    private Task? publisher;
    private int disposed;

    internal JsonWebSocketTransport(int port)
    {
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        this.port = port;
    }

    private string Endpoint => $"ws://127.0.0.1:{port}";

    internal void Start(JsonBridgeRuntime runtime)
    {
        if (runtime == null) throw new ArgumentNullException(nameof(runtime));
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(JsonWebSocketTransport));
        try
        {
            server = new WebSocketServer(IPAddress.Loopback, port) { KeepClean = false };
            server.Log.Output = (message, _) => Debug.WriteLine($"[LiveSplit.Bridge] {message}");
            server.AddWebSocketService<RpcBehavior>(RpcPath, () => new RpcBehavior(runtime, serializerOptions));
            server.AddWebSocketService<EventBehavior>(EventsPath);
            server.Start();
            if (!server.IsListening) throw new InvalidOperationException("The WebSocket server did not start listening.");
            publisher = Task.Run(PublishEvents);
        }
        catch (Exception exception)
        {
            Dispose();
            throw new BridgeTransportStartException(Endpoint, exception);
        }
        Debug.WriteLine($"[LiveSplit.Bridge] JSON v4 WebSocket server listening on {Endpoint}");
    }

    public void Enqueue(Json.BridgeEvent bridgeEvent)
    {
        if (bridgeEvent == null || Volatile.Read(ref disposed) != 0) return;
        try { events.TryAdd(bridgeEvent); }
        catch (InvalidOperationException) { } // Disposal may complete the collection concurrently.
    }

    private void PublishEvents()
    {
        foreach (var bridgeEvent in events.GetConsumingEnumerable())
        {
            if (Volatile.Read(ref disposed) != 0) return;
            try
            {
                server?.WebSocketServices[EventsPath].Sessions.Broadcast(
                    JsonSerializer.Serialize((object)bridgeEvent, serializerOptions));
            }
            catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON event send failed: {exception}"); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        events.CompleteAdding();
        try { server?.Stop(); }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON server stop failed: {exception}"); }
        finally { server = null; }
        try { publisher?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON publisher stop failed: {exception.GetBaseException().Message}"); }
        if (publisher?.IsCompleted != false) events.Dispose();
    }

    private sealed class RpcBehavior : WebSocketBehavior
    {
        private readonly JsonBridgeRuntime runtime;
        private readonly JsonSerializerOptions options;
        private bool allowControl;

        public RpcBehavior(JsonBridgeRuntime runtime, JsonSerializerOptions options)
        {
            this.runtime = runtime;
            this.options = options;
        }

        protected override void OnOpen() => allowControl = WebSocketOriginValidator.IsAllowed(Context.Origin);

        protected override void OnMessage(MessageEventArgs message)
        {
            // WebSocketSharp assembles fragments before OnMessage; its public API has no
            // streaming receive limit. Check the complete UTF-8 payload before parsing RPC.
            if (message.RawData.Length > MaximumRpcMessageBytes)
            {
                Context.WebSocket.Close(CloseStatusCode.TooBig, "RPC message exceeds 64 KiB.");
                return;
            }
            if (!message.IsText) return;
            var response = JsonWebSocketRpcHandler.Handle(message.RawData, runtime.HandleRequest,
                runtime.InvalidRequest, allowControl, options);
            if (response != null) Send(JsonSerializer.Serialize((object)response, options));
        }
    }

    private sealed class EventBehavior : WebSocketBehavior
    {
        public EventBehavior() { }
    }
}
