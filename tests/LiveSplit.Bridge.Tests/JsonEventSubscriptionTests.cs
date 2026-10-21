using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using LiveSplit.Bridge;
using Json = LiveSplit.Bridge.Json;

namespace LiveSplit.Bridge.Tests;

public sealed class JsonEventSubscriptionTests
{
    [Fact]
    public async Task FullQueueDisconnectsOnlySlowSubscriberAndKeepsFastSubscriberOrdered()
    {
        var slowRemoved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastRemoved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hostStop = new CancellationTokenSource();
        using var slowSocket = new ControlledWebSocket(blockSends: true);
        using var fastSocket = new ControlledWebSocket(blockSends: false);
        using var slow = new JsonWebSocketTransport.EventSubscription(Json.BridgeJsonOptions.Create(), _ => slowRemoved.TrySetResult(true), hostStop.Token);
        using var fast = new JsonWebSocketTransport.EventSubscription(Json.BridgeJsonOptions.Create(), _ => fastRemoved.TrySetResult(true), hostStop.Token);
        slow.Bind(slowSocket);
        fast.Bind(fastSocket);

        slow.TryEnqueue(CreateEvent(1));
        fast.TryEnqueue(CreateEvent(1));
        await AwaitWithTimeout(slowSocket.SendStarted.Task, TimeSpan.FromSeconds(5));
        await fastSocket.WaitForSentCountAsync(1, TimeSpan.FromSeconds(5));

        for (uint sequence = 2; sequence <= JsonWebSocketTransport.EventQueueCapacity + 1; sequence++)
        {
            slow.TryEnqueue(CreateEvent(sequence));
            fast.TryEnqueue(CreateEvent(sequence));
            if (sequence % 16 == 0)
                await fastSocket.WaitForSentCountAsync((int)sequence, TimeSpan.FromSeconds(5));
        }

        var overflowSequence = (uint)JsonWebSocketTransport.EventQueueCapacity + 2;
        slow.TryEnqueue(CreateEvent(overflowSequence));
        fast.TryEnqueue(CreateEvent(overflowSequence));
        slow.TryEnqueue(CreateEvent(overflowSequence + 1));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(slow.DisconnectSlowClient)));
        await fastSocket.WaitForSentCountAsync((int)overflowSequence, TimeSpan.FromSeconds(5));
        await AwaitWithTimeout(slowSocket.SendFinished.Task, TimeSpan.FromSeconds(5));
        await AwaitWithTimeout(slowRemoved.Task, TimeSpan.FromSeconds(5));

        Assert.False(fastRemoved.Task.IsCompleted);
        Assert.Empty(slowSocket.SentPayloads);
        Assert.Equal(Enumerable.Range(1, (int)overflowSequence).Select(value => (uint)value), fastSocket.SentPayloads.Select(ReadSequence));
        Assert.Equal(WebSocketMessageType.Text, fastSocket.LastMessageType);

        fast.Dispose();
        await AwaitWithTimeout(fastRemoved.Task, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposeReleasesBlockedSendWithoutWaitingSynchronously()
    {
        using var hostStop = new CancellationTokenSource();
        using var socket = new ControlledWebSocket(blockSends: true);
        using var subscription = new JsonWebSocketTransport.EventSubscription(Json.BridgeJsonOptions.Create(), _ => { }, hostStop.Token);
        subscription.Bind(socket);
        subscription.TryEnqueue(CreateEvent(1));
        await AwaitWithTimeout(socket.SendStarted.Task, TimeSpan.FromSeconds(5));

        await AwaitWithTimeout(Task.Run(() => { subscription.Dispose(); subscription.Dispose(); }), TimeSpan.FromSeconds(5));
        await AwaitWithTimeout(socket.SendFinished.Task, TimeSpan.FromSeconds(5));

        subscription.TryEnqueue(CreateEvent(2));
        Assert.Empty(socket.SentPayloads);
    }

    [Fact]
    public void OverflowBeforeSocketBindCannotBeRevivedByLateBind()
    {
        using var hostStop = new CancellationTokenSource();
        using var subscription = new JsonWebSocketTransport.EventSubscription(Json.BridgeJsonOptions.Create(), _ => { }, hostStop.Token);
        for (uint sequence = 1; sequence <= JsonWebSocketTransport.EventQueueCapacity; sequence++)
            subscription.TryEnqueue(CreateEvent(sequence));
        subscription.TryEnqueue(CreateEvent((uint)JsonWebSocketTransport.EventQueueCapacity + 1));

        subscription.DisconnectSlowClient();
        using var lateSocket = new ControlledWebSocket(blockSends: false);
        subscription.Bind(lateSocket);

        subscription.TryEnqueue(CreateEvent((uint)JsonWebSocketTransport.EventQueueCapacity + 2));
        Assert.NotEqual(WebSocketState.Open, lateSocket.State);
        Assert.Empty(lateSocket.SentPayloads);
    }

    private static Json.TimerEvent CreateEvent(uint sequence) => new() { Type = Json.TimerEventKind.Started, SessionId = 10, EventSequence = sequence };

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

        internal ControlledWebSocket(bool blockSends) => this.blockSends = blockSends;
        internal TaskCompletionSource<bool> SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> SendFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            state = WebSocketState.Closed;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            try
            {
                if (blockSends)
                {
                    SendStarted.TrySetResult(true);
                    await releaseSend.Task.ConfigureAwait(false);
                }
                if (state != WebSocketState.Open) throw new WebSocketException("Socket was closed.");
                sentPayloads.Enqueue(buffer.Take(buffer.Count).ToArray());
                LastMessageType = messageType;
                sent.Release();
            }
            finally { SendFinished.TrySetResult(true); }
        }
    }
}
