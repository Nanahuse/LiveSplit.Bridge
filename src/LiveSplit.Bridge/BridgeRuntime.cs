using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V1;
using LiveSplit.Model;
using NetMQ;
using NetMQ.Sockets;

namespace LiveSplit.Bridge;

internal sealed class BridgeRuntime : IDisposable
{
    private const uint ProtocolVersion = 1;
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan PeriodicSnapshotInterval = TimeSpan.FromSeconds(30);

    private readonly LiveSplitAdapter adapter;
    private readonly string rpcEndpoint;
    private readonly string eventEndpoint;
    private readonly CancellationTokenSource cancellation = new();
    private readonly BlockingCollection<PublishWorkItem> publishQueue = new();
    private readonly ManualResetEventSlim publisherReady = new(false);
    private readonly EventSequence eventSequence = new();
    private readonly object observedStateLock = new();
    private readonly LiveSplitState state;
    private readonly Timer timerSnapshotTimer;
    private readonly Thread publisherThread;
    private readonly Thread requestThread;
    private ResponseSocket? responder;
    private Exception? publisherStartException;
    private readonly ulong sessionId;
    private long stateRevision;
    private GameTimeRevisionState observedGameTimeState;
    private int periodicSnapshotPending;
    private int disposed;

    public BridgeRuntime(LiveSplitState state, int rpcPort, int eventPort)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        adapter = new LiveSplitAdapter(state);
        observedGameTimeState = adapter.CaptureGameTimeRevisionState();

        rpcEndpoint = GetEndpoint("LIVESPLIT_BRIDGE_RPC_ENDPOINT", $"tcp://127.0.0.1:{rpcPort}");
        eventEndpoint = GetEndpoint("LIVESPLIT_BRIDGE_EVENT_ENDPOINT", $"tcp://127.0.0.1:{eventPort}");
        sessionId = GenerateSessionId();
        stateRevision = 1;

        publisherThread = new Thread(PublisherLoop)
        {
            IsBackground = true,
            Name = "LiveSplit.Bridge.PublisherLoop"
        };
        requestThread = new Thread(RequestLoop)
        {
            IsBackground = true,
            Name = "LiveSplit.Bridge.RequestLoop"
        };

        StartTransport();
        AttachStateEvents();

        timerSnapshotTimer = new Timer(
            _ => PublishPeriodicSnapshot(),
            null,
            PeriodicSnapshotInterval,
            PeriodicSnapshotInterval);

        requestThread.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        DetachStateEvents();
        timerSnapshotTimer.Dispose();
        cancellation.Cancel();
        publishQueue.CompleteAdding();

        requestThread.Join(TimeSpan.FromSeconds(2));
        publisherThread.Join(TimeSpan.FromSeconds(2));

        responder?.Close();
        responder?.Dispose();
        publisherReady.Dispose();
        publishQueue.Dispose();
        cancellation.Dispose();
        NetMQConfig.Cleanup(true);
    }

    private void AttachStateEvents()
    {
        state.OnStart += StateOnStart;
        state.OnSplit += StateOnSplit;
        state.OnSkipSplit += StateOnSkipSplit;
        state.OnUndoSplit += StateOnUndoSplit;
        state.OnReset += StateOnReset;
        state.OnPause += StateOnPause;
        state.OnResume += StateOnResume;
        state.RunManuallyModified += StateRunManuallyModified;
        adapter.GameTimeChanged += AdapterGameTimeChanged;
    }

    private void DetachStateEvents()
    {
        state.OnStart -= StateOnStart;
        state.OnSplit -= StateOnSplit;
        state.OnSkipSplit -= StateOnSkipSplit;
        state.OnUndoSplit -= StateOnUndoSplit;
        state.OnReset -= StateOnReset;
        state.OnPause -= StateOnPause;
        state.OnResume -= StateOnResume;
        state.RunManuallyModified -= StateRunManuallyModified;
        adapter.GameTimeChanged -= AdapterGameTimeChanged;
    }

    private void StartTransport()
    {
        publisherThread.Start();
        if (!publisherReady.Wait(TimeSpan.FromSeconds(5)))
        {
            StopPublisherAfterStartFailure();
            throw new TimeoutException("Timed out while binding the event endpoint.");
        }

        if (publisherStartException != null)
        {
            StopPublisherAfterStartFailure();
            throw new InvalidOperationException(
                $"Failed to bind the event endpoint {eventEndpoint}.",
                publisherStartException);
        }

        try
        {
            responder = new ResponseSocket();
            responder.Bind(rpcEndpoint);
            Debug.WriteLine($"[LiveSplit.Bridge] RPC endpoint bound to {rpcEndpoint}");
        }
        catch
        {
            responder?.Close();
            responder?.Dispose();
            responder = null;
            StopPublisherAfterStartFailure();
            throw;
        }
    }

    private void StopPublisherAfterStartFailure()
    {
        cancellation.Cancel();
        publishQueue.CompleteAdding();
        publisherThread.Join(TimeSpan.FromSeconds(2));
    }

    private void RequestLoop()
    {
        if (responder == null)
        {
            return;
        }

        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                if (!responder.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(100), out var requestData))
                {
                    continue;
                }

                var request = Request.Parser.ParseFrom(requestData);
                var response = HandleRequest(request);
                responder.SendFrame(response.ToByteArray());
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[LiveSplit.Bridge] Request loop error: {exception.Message}");
            }
        }
    }

    private void PublisherLoop()
    {
        PublisherSocket? publisher = null;

        try
        {
            publisher = new PublisherSocket();
            publisher.Bind(eventEndpoint);
            Debug.WriteLine($"[LiveSplit.Bridge] Event endpoint bound to {eventEndpoint}");
            publisherReady.Set();

            var clock = Stopwatch.StartNew();
            var nextHeartbeat = HeartbeatInterval;

            while (!cancellation.IsCancellationRequested)
            {
                var remaining = nextHeartbeat - clock.Elapsed;
                var waitMilliseconds = remaining <= TimeSpan.Zero
                    ? 0
                    : (int)Math.Min(Math.Ceiling(remaining.TotalMilliseconds), int.MaxValue);

                if (publishQueue.TryTake(
                    out var workItem,
                    waitMilliseconds,
                    cancellation.Token))
                {
                    PublishSequencedEvent(publisher, workItem);
                }

                if (clock.Elapsed >= nextHeartbeat)
                {
                    PublishHeartbeat(publisher);
                    do
                    {
                        nextHeartbeat += HeartbeatInterval;
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
            if (!publisherReady.IsSet)
            {
                publisherStartException = exception;
            }
            else
            {
                Debug.WriteLine($"[LiveSplit.Bridge] Publisher loop error: {exception.Message}");
            }
        }
        finally
        {
            publisherReady.Set();
            publisher?.Close();
            publisher?.Dispose();
        }
    }

    private Response HandleRequest(Request request)
    {
        if (request.ProtocolVersion != ProtocolVersion)
        {
            return MakeErrorResponse(request, 100, $"Unsupported protocol version {request.ProtocolVersion}");
        }

        try
        {
            if (request.Attach != null)
            {
                return new Response
                {
                    ProtocolVersion = ProtocolVersion,
                    RequestId = request.RequestId,
                    Attach = new AttachResponse
                    {
                        SessionId = sessionId,
                        Snapshot = BuildCurrentSnapshot()
                    }
                };
            }

            if (request.GetSnapshot != null)
            {
                return new Response
                {
                    ProtocolVersion = ProtocolVersion,
                    RequestId = request.RequestId,
                    GetSnapshot = new GetSnapshotResponse
                    {
                        Snapshot = BuildCurrentSnapshot()
                    }
                };
            }

            if (request.TimerOperation != null)
            {
                var result = adapter.ExecuteTimerOperation(request.TimerOperation.Operation);
                if (result.Success)
                {
                    result.Snapshot = BuildCurrentSnapshot();
                }

                return new Response
                {
                    ProtocolVersion = ProtocolVersion,
                    RequestId = request.RequestId,
                    Operation = result
                };
            }

            if (request.GameTimeOperation != null)
            {
                var execution = adapter.ExecuteGameTimeOperation(
                    request.GameTimeOperation.Operation,
                    request.GameTimeOperation.HasTicks ? (long?)request.GameTimeOperation.Ticks : null);

                if (execution.Response.Success)
                {
                    execution.Response.Snapshot = BuildCurrentSnapshot();
                }

                return new Response
                {
                    ProtocolVersion = ProtocolVersion,
                    RequestId = request.RequestId,
                    Operation = execution.Response
                };
            }

            return MakeErrorResponse(request, 101, "Unknown request type.");
        }
        catch (Exception exception)
        {
            return MakeErrorResponse(request, 102, exception.Message);
        }
    }

    private TimerSnapshot BuildCurrentSnapshot()
    {
        return adapter.BuildSnapshot(
            ReadStateRevision(),
            sessionId,
            eventSequence.LastSettled);
    }

    private void PublishPeriodicSnapshot()
    {
        if (Interlocked.Exchange(ref periodicSnapshotPending, 1) != 0)
        {
            return;
        }

        try
        {
            QueueSnapshotEvent(BridgeEventType.EventStateSnapshot, "Periodic snapshot");
        }
        finally
        {
            Volatile.Write(ref periodicSnapshotPending, 0);
        }
    }

    private void PublishGameTimeEvent(GameTimeOperationType operation)
    {
        var eventType = operation switch
        {
            GameTimeOperationType.Initialize => BridgeEventType.EventGameTimeInitialized,
            GameTimeOperationType.Set => BridgeEventType.EventGameTimeSet,
            GameTimeOperationType.GameTimePause => BridgeEventType.EventGameTimePaused,
            GameTimeOperationType.GameTimeResume => BridgeEventType.EventGameTimeResumed,
            _ => BridgeEventType.EventStateSnapshot,
        };

        var description = operation switch
        {
            GameTimeOperationType.Initialize => "Game time initialized",
            GameTimeOperationType.Set => "Game time set",
            GameTimeOperationType.GameTimePause => "Game time paused",
            GameTimeOperationType.GameTimeResume => "Game time resumed",
            _ => "Game time operation completed",
        };

        PublishStateChangeEvent(eventType, description);
    }

    internal void ObserveExternalState()
    {
        var current = adapter.CaptureGameTimeRevisionState();
        GameTimeRevisionState previous;

        lock (observedStateLock)
        {
            if (observedGameTimeState.Equals(current))
            {
                return;
            }

            previous = observedGameTimeState;
            observedGameTimeState = current;
        }

        if (previous.IsInitialized != current.IsInitialized)
        {
            if (current.IsInitialized)
            {
                PublishStateChangeEvent(
                    BridgeEventType.EventGameTimeInitialized,
                    "Game time initialized");
            }
            else
            {
                PublishStateChangeEvent(
                    BridgeEventType.EventStateSnapshot,
                    "Game time deinitialized");
            }

            return;
        }

        if (previous.IsPaused != current.IsPaused)
        {
            PublishStateChangeEvent(
                current.IsPaused
                    ? BridgeEventType.EventGameTimePaused
                    : BridgeEventType.EventGameTimeResumed,
                current.IsPaused ? "Game time paused" : "Game time resumed");
            return;
        }

        PublishStateChangeEvent(BridgeEventType.EventGameTimeSet, "Game time set");
    }

    private void RecordCurrentGameTimeState()
    {
        var current = adapter.CaptureGameTimeRevisionState();
        lock (observedStateLock)
        {
            observedGameTimeState = current;
        }
    }

    private void PublishStateChangeEvent(BridgeEventType type, string description)
    {
        IncrementStateRevision();
        QueueSnapshotEvent(type, description);
    }

    private void QueueSnapshotEvent(BridgeEventType type, string description)
    {
        if (cancellation.IsCancellationRequested || publishQueue.IsAddingCompleted)
        {
            return;
        }

        var snapshot = BuildCurrentSnapshot();
        var workItem = new PublishWorkItem(type, snapshot, description);

        try
        {
            publishQueue.Add(workItem, cancellation.Token);
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

    private void PublishSequencedEvent(PublisherSocket publisher, PublishWorkItem workItem)
    {
        var sequence = eventSequence.Begin();

        try
        {
            workItem.Snapshot.SessionId = sessionId;
            workItem.Snapshot.EventSequence = sequence;

            var bridgeEvent = new BridgeEvent
            {
                SessionId = sessionId,
                EventSequence = sequence,
                Type = workItem.Type,
                Snapshot = workItem.Snapshot,
                Description = workItem.Description
            };

            publisher.SendFrame(bridgeEvent.ToByteArray());
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] Event publish failed: {exception.Message}");
        }
        finally
        {
            eventSequence.Settle(sequence);
        }
    }

    private void PublishHeartbeat(PublisherSocket publisher)
    {
        var heartbeat = new BridgeEvent
        {
            SessionId = sessionId,
            EventSequence = eventSequence.LastSettled,
            Type = BridgeEventType.EventHeartbeat
        };

        try
        {
            publisher.SendFrame(heartbeat.ToByteArray());
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] Heartbeat publish failed: {exception.Message}");
        }
    }

    private ulong ReadStateRevision()
    {
        return unchecked((ulong)Interlocked.Read(ref stateRevision));
    }

    private void IncrementStateRevision()
    {
        Interlocked.Increment(ref stateRevision);
    }

    private void AdapterGameTimeChanged(GameTimeOperationType operation)
    {
        RecordCurrentGameTimeState();
        PublishGameTimeEvent(operation);
    }

    private void StateOnStart(object sender, EventArgs args)
    {
        RecordCurrentGameTimeState();
        PublishStateChangeEvent(BridgeEventType.EventTimerStarted, "Timer started");
    }

    private void StateOnSplit(object sender, EventArgs args)
    {
        RecordCurrentGameTimeState();
        PublishStateChangeEvent(BridgeEventType.EventTimerSplit, "Split");
    }

    private void StateOnSkipSplit(object sender, EventArgs args)
    {
        RecordCurrentGameTimeState();
        PublishStateChangeEvent(BridgeEventType.EventTimerSkipped, "Skip split");
    }

    private void StateOnUndoSplit(object sender, EventArgs args)
    {
        RecordCurrentGameTimeState();
        PublishStateChangeEvent(BridgeEventType.EventTimerUndo, "Undo split");
    }

    private void StateOnReset(object sender, LiveSplit.Model.TimerPhase previousPhase)
    {
        RecordCurrentGameTimeState();
        PublishStateChangeEvent(BridgeEventType.EventTimerReset, "Reset");
    }

    private void StateOnPause(object sender, EventArgs args)
    {
        RecordCurrentGameTimeState();
        PublishStateChangeEvent(BridgeEventType.EventTimerPaused, "Timer paused");
    }

    private void StateOnResume(object sender, EventArgs args)
    {
        RecordCurrentGameTimeState();
        PublishStateChangeEvent(BridgeEventType.EventTimerResumed, "Timer resumed");
    }

    private void StateRunManuallyModified(object sender, EventArgs args)
    {
        RecordCurrentGameTimeState();
        PublishStateChangeEvent(BridgeEventType.EventRunChanged, "Run changed");
    }

    private static Response MakeErrorResponse(Request request, int code, string message)
    {
        return new Response
        {
            ProtocolVersion = ProtocolVersion,
            RequestId = request.RequestId,
            Error = new BridgeError { Code = code, Message = message }
        };
    }

    private static ulong GenerateSessionId()
    {
        var buffer = new byte[8];
        using var rng = RandomNumberGenerator.Create();
        ulong value;

        do
        {
            rng.GetBytes(buffer);
            value = BitConverter.ToUInt64(buffer, 0);
        }
        while (value == 0);

        return value;
    }

    private static string GetEndpoint(string name, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private sealed class PublishWorkItem
    {
        public PublishWorkItem(
            BridgeEventType type,
            TimerSnapshot snapshot,
            string description)
        {
            Type = type;
            Snapshot = snapshot;
            Description = description;
        }

        public BridgeEventType Type { get; }
        public TimerSnapshot Snapshot { get; }
        public string Description { get; }
    }
}
