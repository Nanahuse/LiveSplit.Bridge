#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Json = LiveSplit.Bridge.Json;

namespace LiveSplit.Bridge;

/// <summary>Loopback-only JSON v4 WebSocket endpoints.</summary>
internal sealed class JsonWebSocketTransport : IJsonEventSink, IDisposable
{
    internal const int MaximumRpcMessageBytes = 64 * 1024;
    // Queue entries are references to the same immutable event snapshots sent to every subscriber;
    // 256 bounds per-client reference storage to roughly 2 KiB plus queue overhead and tolerates a
    // short burst. A persistently slower client is disconnected rather than making this unbounded.
    internal const int EventQueueCapacity = 256;
    internal const string RpcPath = "/bridge/v4/rpc";
    internal const string EventsPath = "/bridge/v4/events";

    private readonly int port;
    private readonly JsonSerializerOptions serializerOptions = Json.BridgeJsonOptions.Create();
    private readonly object clientsGate = new();
    private readonly HashSet<WebSocket> clients = new();
    private readonly HashSet<EventSubscription> subscriptions = new();
    private readonly HashSet<Task> connectionTasks = new();
    private readonly CancellationTokenSource stop = new();
    private HttpListener? listener;
    private Func<Json.RpcRequest?, bool, Json.RpcResponse>? requestHandler;
    private Func<uint, string, Json.RpcResponse>? invalidRequest;
    private Task? acceptTask;
    private int disposed;

    internal JsonWebSocketTransport(int port)
    {
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        this.port = port;
    }

    internal string Endpoint => $"ws://127.0.0.1:{port}";
    internal bool IsListening => listener?.IsListening ?? false;
    internal int EventsSessionCount { get { lock (clientsGate) return subscriptions.Count; } }

    internal void Start(JsonBridgeRuntime runtime)
    {
        if (runtime == null) throw new ArgumentNullException(nameof(runtime));
        if (Interlocked.CompareExchange(ref disposed, 0, 0) != 0) throw new ObjectDisposedException(nameof(JsonWebSocketTransport));
        requestHandler = runtime.HandleRequest;
        invalidRequest = runtime.InvalidRequest;
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            acceptTask = Task.Run(AcceptLoopAsync);
        }
        catch (Exception exception)
        {
            StopListener();
            throw new BridgeTransportStartException(Endpoint, exception);
        }

        if (!IsListening)
        {
            StopListener();
            throw new BridgeTransportStartException(Endpoint, new InvalidOperationException("The WebSocket listener did not start."));
        }
        Debug.WriteLine($"[LiveSplit.Bridge] JSON v4 WebSocket server listening on {Endpoint}");
    }

    public void Enqueue(Json.BridgeEvent bridgeEvent)
    {
        if (bridgeEvent == null || Volatile.Read(ref disposed) != 0) return;
        EventSubscription[] current;
        lock (clientsGate) current = subscriptions.ToArray();
        foreach (var subscription in current)
        {
            if (subscription.TryEnqueue(bridgeEvent) == EventEnqueueResult.QueueFull)
            {
                // Disconnect asynchronously: this callback is invoked under JsonBridgeRuntime.eventGate.
                _ = Task.Run(subscription.DisconnectSlowClient);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        StopListener();
        WebSocket[] sockets;
        EventSubscription[] current;
        Task[] connections;
        lock (clientsGate)
        {
            sockets = clients.ToArray();
            current = subscriptions.ToArray();
            connections = connectionTasks.ToArray();
            subscriptions.Clear();
            clients.Clear();
        }
        foreach (var subscription in current) subscription.Dispose();
        foreach (var socket in sockets)
        {
            try { socket.Abort(); }
            catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON socket abort failed: {exception.Message}"); }
            socket.Dispose();
        }
        try { acceptTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON accept loop stop failed: {exception.GetBaseException().Message}"); }
        try { Task.WaitAll(connections, TimeSpan.FromSeconds(2)); }
        catch (AggregateException exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON connection cleanup failed: {exception.GetBaseException().Message}"); }
        if (acceptTask?.IsCompleted != false && connections.All(task => task.IsCompleted)) stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (Volatile.Read(ref disposed) == 0)
        {
            HttpListenerContext context;
            try { context = await listener!.GetContextAsync().ConfigureAwait(false); }
            catch (Exception exception) when (Volatile.Read(ref disposed) != 0 || exception is HttpListenerException || exception is ObjectDisposedException)
            { return; }
            catch (Exception exception)
            {
                Debug.WriteLine($"[LiveSplit.Bridge] JSON accept failed: {exception}");
                continue;
            }

            var connection = Task.Run(() => HandleContextAsync(context));
            lock (clientsGate) connectionTasks.Add(connection);
            _ = connection.ContinueWith(completed =>
            {
                lock (clientsGate) connectionTasks.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath;
        if (!context.Request.IsWebSocketRequest || (path != RpcPath && path != EventsPath))
        {
            try { context.Response.StatusCode = (path == RpcPath || path == EventsPath) ? 400 : 404; context.Response.Close(); }
            catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON HTTP rejection failed: {exception.Message}"); }
            return;
        }

        EventSubscription? eventSubscription = null;
        if (path == EventsPath)
        {
            // Register before completing the HTTP upgrade so a Runtime Event cannot fall into
            // the small scheduling gap between the client seeing 101 and the handler starting.
            eventSubscription = new EventSubscription(serializerOptions, RemoveSubscription, stop.Token);
            lock (clientsGate)
            {
                if (Volatile.Read(ref disposed) != 0) { eventSubscription.Dispose(); return; }
                subscriptions.Add(eventSubscription);
            }
        }

        WebSocket socket;
        try
        {
            var accepted = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
            socket = accepted.WebSocket;
        }
        catch (Exception exception)
        {
            if (eventSubscription != null)
            {
                RemoveSubscription(eventSubscription);
                eventSubscription.Dispose();
            }
            Debug.WriteLine($"[LiveSplit.Bridge] JSON WebSocket handshake failed: {exception}");
            return;
        }

        lock (clientsGate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                socket.Abort(); socket.Dispose();
                if (eventSubscription != null) { subscriptions.Remove(eventSubscription); eventSubscription.Dispose(); }
                return;
            }
            clients.Add(socket);
        }

        try
        {
            if (path == RpcPath)
                await HandleRpcAsync(socket, WebSocketOriginValidator.IsAllowed(context.Request.Headers["Origin"] ?? string.Empty)).ConfigureAwait(false);
            else
                await HandleEventsAsync(socket, eventSubscription!).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] JSON WebSocket connection failed: {exception}");
        }
        finally
        {
            EventSubscription[] owned;
            lock (clientsGate)
            {
                clients.Remove(socket);
                owned = subscriptions.Where(item => item.Owns(socket)).ToArray();
                foreach (var subscription in owned) subscriptions.Remove(subscription);
            }
            foreach (var subscription in owned) subscription.Dispose();
            socket.Dispose();
        }
    }

    private async Task HandleRpcAsync(WebSocket socket, bool allowControl)
    {
        while (socket.State == WebSocketState.Open && Volatile.Read(ref disposed) == 0)
        {
            var message = await ReceiveBoundedMessageAsync(socket).ConfigureAwait(false);
            if (message.CloseReceived)
            {
                await CloseNormallyAsync(socket).ConfigureAwait(false);
                return;
            }
            if (message.TooLarge)
            {
                await CloseAsync(socket, WebSocketCloseStatus.MessageTooBig, "RPC message exceeds 64 KiB.").ConfigureAwait(false);
                return;
            }
            if (!message.IsText) continue;

            var response = JsonWebSocketRpcHandler.Handle(message.Payload!, requestHandler!, invalidRequest!, allowControl, serializerOptions);
            if (response == null) continue;
            var bytes = JsonSerializer.SerializeToUtf8Bytes((object)response, serializerOptions);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, stop.Token).ConfigureAwait(false);
        }
    }

    private async Task HandleEventsAsync(WebSocket socket, EventSubscription subscription)
    {
        subscription.Bind(socket);
        try
        {
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open && Volatile.Read(ref disposed) == 0)
            {
                var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await CloseNormallyAsync(socket).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            RemoveSubscription(subscription);
            subscription.Dispose();
        }
    }

    private void RemoveSubscription(EventSubscription subscription)
    {
        lock (clientsGate) subscriptions.Remove(subscription);
    }

    private static async Task<ReceivedMessage> ReceiveBoundedMessageAsync(WebSocket socket)
    {
        var buffer = new byte[8192];
        using var accumulated = new MemoryStream();
        long totalBytes = 0;
        var isText = false;
        var firstChunk = true;
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close)
                return new ReceivedMessage(null, false, false, true);
            totalBytes += received.Count;
            if (totalBytes > MaximumRpcMessageBytes)
                return new ReceivedMessage(null, false, true, false);
            if (firstChunk) { isText = received.MessageType == WebSocketMessageType.Text; firstChunk = false; }
            if (received.MessageType == WebSocketMessageType.Text)
                accumulated.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);

        return isText
            ? new ReceivedMessage(accumulated.ToArray(), true, false, false)
            : new ReceivedMessage(null, false, false, false);
    }

    private static async Task CloseNormallyAsync(WebSocket socket)
    {
        if (socket.State != WebSocketState.Open && socket.State != WebSocketState.CloseReceived) return;
        try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON socket close failed: {exception.Message}"); }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        if (socket.State != WebSocketState.Open && socket.State != WebSocketState.CloseReceived) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await socket.CloseAsync(status, description, timeout.Token).ConfigureAwait(false); }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON socket close failed: {exception.Message}"); }
    }

    private void StopListener()
    {
        try { listener?.Close(); }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON listener stop failed: {exception.Message}"); }
        finally { listener = null; }
    }

    private readonly struct ReceivedMessage
    {
        internal ReceivedMessage(byte[]? payload, bool isText, bool tooLarge, bool closeReceived)
        { Payload = payload; IsText = isText; TooLarge = tooLarge; CloseReceived = closeReceived; }
        internal byte[]? Payload { get; }
        internal bool IsText { get; }
        internal bool TooLarge { get; }
        internal bool CloseReceived { get; }
    }

    internal enum EventEnqueueResult { Enqueued, Stopping, QueueFull }

    internal sealed class EventSubscription : IDisposable
    {
        private readonly object gate = new();
        private readonly Queue<Json.BridgeEvent> queue = new();
        private readonly SemaphoreSlim available = new(0);
        private readonly CancellationTokenSource cancellation;
        private WebSocket? socket;
        private readonly JsonSerializerOptions options;
        private readonly Action<EventSubscription> remove;
        private Task? sender;
        private bool completed;
        private bool disconnectRequested;
        private int disconnectStarted;
        private int ownerRemoved;
        private int socketAborted;
        private int resourcesDisposed;

        internal EventSubscription(JsonSerializerOptions options, Action<EventSubscription> remove, CancellationToken hostToken)
        {
            this.options = options;
            this.remove = remove;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        }

        internal bool Owns(WebSocket candidate) => ReferenceEquals(socket, candidate);

        internal void Bind(WebSocket acceptedSocket)
        {
            var reject = false;
            lock (gate)
            {
                if (completed || cancellation.IsCancellationRequested)
                {
                    completed = true;
                    reject = true;
                }
                else
                {
                    socket = acceptedSocket;
                    sender = Task.Run(SendLoopAsync);
                }
            }
            if (reject)
            {
                AbortSocket(acceptedSocket);
                acceptedSocket.Dispose();
                RemoveFromOwner();
                DisposeResources();
            }
        }

        internal EventEnqueueResult TryEnqueue(Json.BridgeEvent item)
        {
            lock (gate)
            {
                if (completed || disconnectRequested || cancellation.IsCancellationRequested) return EventEnqueueResult.Stopping;
                if (queue.Count >= EventQueueCapacity)
                {
                    disconnectRequested = true;
                    return EventEnqueueResult.QueueFull;
                }
                queue.Enqueue(item);
                available.Release();
                return EventEnqueueResult.Enqueued;
            }
        }

        internal void DisconnectSlowClient()
        {
            if (Interlocked.Exchange(ref disconnectStarted, 1) != 0) return;
            lock (gate)
            {
                if (!disconnectRequested || completed) return;
            }
            RemoveFromOwner();
            Dispose();
        }

        public void Dispose()
        {
            Task? currentSender;
            WebSocket? currentSocket;
            lock (gate)
            {
                if (completed) return;
                completed = true;
                queue.Clear();
                currentSender = sender;
                currentSocket = socket;
            }
            CancelAndAbort(currentSocket);
            if (currentSender == null) DisposeResources();
        }

        private async Task SendLoopAsync()
        {
            try
            {
                while (true)
                {
                    await available.WaitAsync(cancellation.Token).ConfigureAwait(false);
                    Json.BridgeEvent item;
                    lock (gate)
                    {
                        if (completed || queue.Count == 0) return;
                        item = queue.Dequeue();
                    }
                    var bytes = JsonSerializer.SerializeToUtf8Bytes((object)item, options);
                    await socket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON event send failed: {exception.Message}"); }
            catch (ObjectDisposedException) { }
            catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON event serialization failed: {exception}"); }
            finally
            {
                RemoveFromOwner();
                lock (gate)
                {
                    completed = true;
                    queue.Clear();
                }
                CancelAndAbort(socket);
                DisposeResources();
            }
        }

        private void CancelAndAbort(WebSocket? currentSocket)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON subscriber cancellation failed: {exception.Message}"); }
            if (currentSocket != null) AbortSocket(currentSocket);
        }

        private void RemoveFromOwner()
        {
            if (Interlocked.Exchange(ref ownerRemoved, 1) == 0) remove(this);
        }

        private void AbortSocket(WebSocket currentSocket)
        {
            if (Interlocked.Exchange(ref socketAborted, 1) != 0) return;
            try { currentSocket.Abort(); }
            catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON subscriber abort failed: {exception.Message}"); }
        }

        private void DisposeResources()
        {
            if (Interlocked.Exchange(ref resourcesDisposed, 1) != 0) return;
            cancellation.Dispose();
            available.Dispose();
        }
    }
}
