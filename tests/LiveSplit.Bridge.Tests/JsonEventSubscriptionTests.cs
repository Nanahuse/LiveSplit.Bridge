using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LiveSplit.Bridge;
using Json = LiveSplit.Bridge.Json;

namespace LiveSplit.Bridge.Tests;

public sealed class JsonEventSubscriptionTests
{
    [Fact]
    public async Task FullQueueDisconnectsOnlySlowSubscriberAndKeepsFastSubscriberOrdered()
    {
        var slowRemoved = 0;
        var fastRemoved = 0;
        using var hostStop = new CancellationTokenSource();
        using var slowSocket = new ControlledWebSocket(blockSends: true);
        using var fastSocket = new ControlledWebSocket(blockSends: false);
        using var slow = new JsonWebSocketTransport.EventSubscription(Json.BridgeJsonOptions.Create(), _ => Interlocked.Increment(ref slowRemoved), hostStop.Token);
        using var fast = new JsonWebSocketTransport.EventSubscription(Json.BridgeJsonOptions.Create(), _ => Interlocked.Increment(ref fastRemoved), hostStop.Token);
        slow.Bind(slowSocket);
        fast.Bind(fastSocket);

        Assert.Equal(JsonWebSocketTransport.EventEnqueueResult.Enqueued, slow.TryEnqueue(CreateEvent(1)));
        Assert.Equal(JsonWebSocketTransport.EventEnqueueResult.Enqueued, fast.TryEnqueue(CreateEvent(1)));
        await AwaitWithTimeout(slowSocket.SendStarted.Task, TimeSpan.FromSeconds(5));
        await fastSocket.WaitForSentCountAsync(1, TimeSpan.FromSeconds(5));

        for (uint sequence = 2; sequence <= JsonWebSocketTransport.EventQueueCapacity + 1; sequence++)
        {
            Assert.Equal(JsonWebSocketTransport.EventEnqueueResult.Enqueued, slow.TryEnqueue(CreateEvent(sequence)));
            Assert.Equal(JsonWebSocketTransport.EventEnqueueResult.Enqueued, fast.TryEnqueue(CreateEvent(sequence)));
            if (sequence % 16 == 0)
                await fastSocket.WaitForSentCountAsync((int)sequence, TimeSpan.FromSeconds(5));
        }

        Assert.Equal(JsonWebSocketTransport.EventEnqueueResult.QueueFull, slow.TryEnqueue(CreateEvent(258)));
        Assert.Equal(JsonWebSocketTransport.EventEnqueueResult.Enqueued, fast.TryEnqueue(CreateEvent(258)));
        Assert.Equal(JsonWebSocketTransport.EventEnqueueResult.Stopping, slow.TryEnqueue(CreateEvent(259)));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(slow.DisconnectSlowClient)));
        await fastSocket.WaitForSentCountAsync(258, TimeSpan.FromSeconds(5));
        await AwaitWithTimeout(slow.SenderTask!, TimeSpan.FromSeconds(5));

        Assert.Equal(1, slowRemoved);
        Assert.True(fastRemoved == 0, $"The fast subscription removed itself after sending {fastSocket.SentPayloads.Length} Events; socket state is {fastSocket.State}, sender completed: {fast.SenderTask!.IsCompleted}.");
        Assert.Equal(1, slowSocket.AbortCount);
        Assert.Equal(Enumerable.Range(1, 258).Select(value => (uint)value), fastSocket.SentPayloads.Select(ReadSequence));
        Assert.Equal(WebSocketMessageType.Text, fastSocket.LastMessageType);

        fast.Dispose();
        await AwaitWithTimeout(fast.SenderTask!, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposeAbortsBlockedSenderOnceAndDoesNotWaitForItSynchronously()
    {
        using var hostStop = new CancellationTokenSource();
        using var socket = new ControlledWebSocket(blockSends: true);
        using var subscription = new JsonWebSocketTransport.EventSubscription(Json.BridgeJsonOptions.Create(), _ => { }, hostStop.Token);
        subscription.Bind(socket);
        Assert.Equal(JsonWebSocketTransport.EventEnqueueResult.Enqueued, subscription.TryEnqueue(CreateEvent(1)));
        await AwaitWithTimeout(socket.SendStarted.Task, TimeSpan.FromSeconds(5));

        subscription.Dispose();
        subscription.Dispose();
        await AwaitWithTimeout(subscription.SenderTask!, TimeSpan.FromSeconds(5));

        Assert.Equal(1, socket.AbortCount);
        Assert.Equal(0, socket.DisposeCount);
        Assert.Equal(JsonWebSocketTransport.EventEnqueueResult.Stopping, subscription.TryEnqueue(CreateEvent(2)));
    }

    [Fact]
    public void OverflowBeforeSocketBindCannotBeRevivedByLateBind()
    {
        using var hostStop = new CancellationTokenSource();
        using var subscription = new JsonWebSocketTransport.EventSubscription(Json.BridgeJsonOptions.Create(), _ => { }, hostStop.Token);
        for (uint sequence = 1; sequence <= JsonWebSocketTransport.EventQueueCapacity; sequence++)
            Assert.Equal(JsonWebSocketTransport.EventEnqueueResult.Enqueued, subscription.TryEnqueue(CreateEvent(sequence)));
        Assert.Equal(JsonWebSocketTransport.EventEnqueueResult.QueueFull, subscription.TryEnqueue(CreateEvent(257)));

        subscription.DisconnectSlowClient();
        using var lateSocket = new ControlledWebSocket(blockSends: false);
        subscription.Bind(lateSocket);

        Assert.Null(subscription.SenderTask);
        Assert.Equal(1, lateSocket.AbortCount);
        Assert.Equal(1, lateSocket.DisposeCount);
        Assert.Equal(JsonWebSocketTransport.EventEnqueueResult.Stopping, subscription.TryEnqueue(CreateEvent(258)));
    }

    private static Json.TimerStartedEvent CreateEvent(uint sequence) => new() { SessionId = 10, EventSequence = sequence };

    private static uint ReadSequence(byte[] payload)
    {
        using var json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty("event_sequence").GetUInt32();
    }

    private static async Task AwaitWithTimeout(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        Assert.Same(task, completed);
        await task.ConfigureAwait(false);
    }

    private sealed class ControlledWebSocket : WebSocket
    {
        private readonly bool blockSends;
        private readonly TaskCompletionSource<bool> releaseSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<byte[]> sentPayloads = new();
        private readonly SemaphoreSlim sent = new(0);
        private WebSocketState state = WebSocketState.Open;
        private int abortCount;
        private int disposeCount;

        internal ControlledWebSocket(bool blockSends) => this.blockSends = blockSends;
        internal TaskCompletionSource<bool> SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int AbortCount => Volatile.Read(ref abortCount);
        internal int DisposeCount => Volatile.Read(ref disposeCount);
        internal byte[][] SentPayloads => sentPayloads.ToArray();
        internal WebSocketMessageType LastMessageType { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;

        internal async Task WaitForSentCountAsync(int count, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (sentPayloads.Count < count)
                await sent.WaitAsync(cancellation.Token);
        }

        public override void Abort()
        {
            Interlocked.Increment(ref abortCount);
            state = WebSocketState.Aborted;
            releaseSend.TrySetResult(true);
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            Interlocked.Increment(ref disposeCount);
            state = WebSocketState.Closed;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (blockSends)
            {
                SendStarted.TrySetResult(true);
                await releaseSend.Task.ConfigureAwait(false);
            }
            if (state == WebSocketState.Aborted) throw new WebSocketException("Socket was aborted.");
            sentPayloads.Enqueue(buffer.Take(buffer.Count).ToArray());
            LastMessageType = messageType;
            sent.Release();
        }
    }
}
