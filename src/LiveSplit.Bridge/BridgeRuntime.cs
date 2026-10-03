using System;
using System.Security.Cryptography;
using System.Threading;
using LiveSplit.Bridge.Protocol.V1;
using LiveSplit.Model;

namespace LiveSplit.Bridge;

internal sealed class BridgeRuntime : IDisposable
{
    private const uint ProtocolVersion = 1;
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan PeriodicSnapshotInterval = TimeSpan.FromSeconds(30);

    private readonly LiveSplitAdapter adapter;
    private readonly IBridgeTransport transport;
    private readonly EventSequence eventSequence = new();
    private readonly object sequenceLock = new();
    private readonly object observedStateLock = new();
    private readonly LiveSplitState state;
    private readonly Timer timerSnapshotTimer;
    private readonly ulong sessionId;
    private long stateRevision;
    private long runRevision;
    private GameTimeRevisionState observedGameTimeState;
    private int periodicSnapshotPending;
    private int disposed;

    public BridgeRuntime(LiveSplitState state, int rpcPort, int eventPort)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        adapter = new LiveSplitAdapter(state);
        observedGameTimeState = adapter.CaptureGameTimeRevisionState();

        var rpcEndpoint = GetEndpoint("LIVESPLIT_BRIDGE_RPC_ENDPOINT", $"tcp://127.0.0.1:{rpcPort}");
        var eventEndpoint = GetEndpoint("LIVESPLIT_BRIDGE_EVENT_ENDPOINT", $"tcp://127.0.0.1:{eventPort}");
        sessionId = GenerateSessionId();
        stateRevision = 1;
        runRevision = 1;

        transport = new ZeroMqTransport(
            rpcEndpoint,
            eventEndpoint,
            HandleRequest,
            CreateHeartbeatEvent,
            OnEventSettled);

        transport.Start();
        AttachStateEvents();

        timerSnapshotTimer = new Timer(
            _ => PublishPeriodicSnapshot(),
            null,
            PeriodicSnapshotInterval,
            PeriodicSnapshotInterval);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        DetachStateEvents();
        timerSnapshotTimer.Dispose();
        transport.Dispose();
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

    internal Response HandleRequest(Request request)
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

            if (request.GetRun != null)
            {
                return new Response
                {
                    ProtocolVersion = ProtocolVersion,
                    RequestId = request.RequestId,
                    GetRun = new GetRunResponse
                    {
                        Run = BuildCurrentRunSnapshot()
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
            eventSequence.LastSettled,
            ReadRunRevision());
    }

    private RunSnapshot BuildCurrentRunSnapshot()
    {
        return adapter.BuildRunSnapshot(
            ReadRunRevision(),
            ReadStateRevision(),
            sessionId);
    }

    internal ulong StateRevision => ReadStateRevision();

    internal ulong RunRevision => ReadRunRevision();

    internal void PublishPeriodicSnapshot()
    {
        if (Interlocked.Exchange(ref periodicSnapshotPending, 1) != 0)
        {
            return;
        }

        try
        {
            PublishEvent(BridgeEventType.EventStateSnapshot, "Periodic snapshot");
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
        PublishEvent(type, description);
    }

    private void PublishEvent(BridgeEventType type, string description)
    {
        var snapshot = BuildCurrentSnapshot();

        lock (sequenceLock)
        {
            var sequence = eventSequence.Begin();
            snapshot.EventSequence = sequence;

            var bridgeEvent = new BridgeEvent
            {
                SessionId = sessionId,
                EventSequence = sequence,
                Type = type,
                Snapshot = snapshot,
                Description = description
            };

            transport.Publish(bridgeEvent);
        }
    }

    private BridgeEvent CreateHeartbeatEvent()
    {
        return new BridgeEvent
        {
            SessionId = sessionId,
            EventSequence = eventSequence.LastSettled,
            Type = BridgeEventType.EventHeartbeat
        };
    }

    private void OnEventSettled(ulong sequence)
    {
        eventSequence.Settle(sequence);
    }

    private ulong ReadStateRevision()
    {
        return unchecked((ulong)Interlocked.Read(ref stateRevision));
    }

    private void IncrementStateRevision()
    {
        Interlocked.Increment(ref stateRevision);
    }

    private ulong ReadRunRevision()
    {
        return unchecked((ulong)Interlocked.Read(ref runRevision));
    }

    private void IncrementRunRevision()
    {
        Interlocked.Increment(ref runRevision);
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
        IncrementRunRevision();
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
}
