using System;
using System.Security.Cryptography;
using System.Threading;
using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;

namespace LiveSplit.Bridge;

internal sealed class BridgeRuntime : IDisposable
{
    private const uint ProtocolVersion = 2;
    private const int MaxSnapshotAttempts = 5;
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    [Flags]
    private enum RevisionChange
    {
        None = 0,
        State = 1,
        Attempt = 2,
        Run = 4,
        Runtime = 8,
        Timer = State | Attempt,
    }

    private readonly LiveSplitAdapter adapter;
    private readonly WebSocketTransport transport;
    private readonly EventSequence eventSequence = new();
    private readonly object eventStateLock = new();
    private readonly object observedStateLock = new();
    private readonly object controlGate = new();
    private readonly LiveSplitState state;
    private readonly ulong sessionId;
    private long stateRevision;
    private long runRevision;
    private long attemptRevision;
    private long runtimeRevision;
    private GameTimeRevisionState observedGameTimeState;
    private RuntimeRevisionState observedRuntimeState;
    private int runtimeChangePending;
    private int disposed;

    public BridgeRuntime(LiveSplitState state, int webSocketPort)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        adapter = new LiveSplitAdapter(state);
        observedGameTimeState = adapter.CaptureGameTimeRevisionState();
        observedRuntimeState = adapter.CaptureRuntimeRevisionState();

        var port = GetPort("LIVESPLIT_BRIDGE_WEBSOCKET_PORT", webSocketPort);
        sessionId = GenerateSessionId();
        stateRevision = 1;
        runRevision = 1;
        attemptRevision = 1;
        runtimeRevision = 1;

        transport = new WebSocketTransport(
            port,
            HeartbeatInterval,
            HandleRequest,
            CreateHeartbeatEvent,
            OnEventSettled);

        transport.Start();
        AttachStateEvents();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        DetachStateEvents();
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
        state.ComparisonRenamed += StateComparisonRenamed;
        state.OnSwitchComparisonNext += StateComparisonSwitched;
        state.OnSwitchComparisonPrevious += StateComparisonSwitched;
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
        state.ComparisonRenamed -= StateComparisonRenamed;
        state.OnSwitchComparisonNext -= StateComparisonSwitched;
        state.OnSwitchComparisonPrevious -= StateComparisonSwitched;
        adapter.GameTimeChanged -= AdapterGameTimeChanged;
    }

    // Each request type chooses its own execution model. Timer and GameTime
    // mutations are serialized only against each other by the control gate; heavy
    // queries never take that gate, so they cannot delay a timer operation.
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
                        TimerState = BuildCurrentTimerState()
                    }
                };
            }

            if (request.GetTimerState != null)
            {
                return new Response
                {
                    ProtocolVersion = ProtocolVersion,
                    RequestId = request.RequestId,
                    GetTimerState = new GetTimerStateResponse
                    {
                        TimerState = BuildCurrentTimerState()
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
                        Run = BuildRunStateConsistent()
                    }
                };
            }

            if (request.GetAttempt != null)
            {
                return new Response
                {
                    ProtocolVersion = ProtocolVersion,
                    RequestId = request.RequestId,
                    GetAttempt = new GetAttemptResponse
                    {
                        Attempt = BuildAttemptStateConsistent()
                    }
                };
            }

            if (request.GetRuntimeState != null)
            {
                return new Response
                {
                    ProtocolVersion = ProtocolVersion,
                    RequestId = request.RequestId,
                    GetRuntimeState = new GetRuntimeStateResponse
                    {
                        RuntimeState = BuildRuntimeStateConsistent()
                    }
                };
            }

            if (request.TimerOperation != null)
            {
                return HandleTimerOperation(request);
            }

            if (request.GameTimeOperation != null)
            {
                return HandleGameTimeOperation(request);
            }

            return MakeErrorResponse(request, 101, "Unknown request type.");
        }
        catch (Exception exception)
        {
            return MakeErrorResponse(request, 102, exception.Message);
        }
    }

    private Response HandleTimerOperation(Request request)
    {
        OperationResponse result;
        lock (controlGate)
        {
            result = adapter.ExecuteTimerOperation(request.TimerOperation.Operation);
            if (result.Success)
            {
                // The response reflects the state captured immediately after the
                // mutation, still inside the control gate.
                result.TimerState = BuildCurrentTimerState();
            }
        }

        return new Response
        {
            ProtocolVersion = ProtocolVersion,
            RequestId = request.RequestId,
            Operation = result
        };
    }

    private Response HandleGameTimeOperation(Request request)
    {
        GameTimeOperationExecution execution;
        lock (controlGate)
        {
            execution = adapter.ExecuteGameTimeOperation(
                request.GameTimeOperation.Operation,
                request.GameTimeOperation.HasTicks ? (long?)request.GameTimeOperation.Ticks : null);

            if (execution.Response.Success)
            {
                execution.Response.TimerState = BuildCurrentTimerState();
            }
        }

        return new Response
        {
            ProtocolVersion = ProtocolVersion,
            RequestId = request.RequestId,
            Operation = execution.Response
        };
    }

    // Query snapshots retry, bounded, when the target generation moves while the
    // state is being built so a returned revision never describes older content.
    private RunState BuildRunStateConsistent()
    {
        for (var attempt = 0; attempt < MaxSnapshotAttempts; attempt++)
        {
            var before = ReadRunRevision();
            var snapshot = adapter.BuildRunState(before, sessionId);
            if (ReadRunRevision() == before)
            {
                return snapshot;
            }
        }

        return adapter.BuildRunState(ReadRunRevision(), sessionId);
    }

    private AttemptState BuildAttemptStateConsistent()
    {
        for (var attempt = 0; attempt < MaxSnapshotAttempts; attempt++)
        {
            var before = ReadAttemptRevision();
            var snapshot = adapter.BuildAttemptState(before, sessionId);
            if (ReadAttemptRevision() == before)
            {
                return snapshot;
            }
        }

        return adapter.BuildAttemptState(ReadAttemptRevision(), sessionId);
    }

    private RuntimeState BuildRuntimeStateConsistent()
    {
        for (var attempt = 0; attempt < MaxSnapshotAttempts; attempt++)
        {
            var before = ReadRuntimeRevision();
            var snapshot = adapter.BuildRuntimeState(before, sessionId);
            if (ReadRuntimeRevision() == before)
            {
                return snapshot;
            }
        }

        return adapter.BuildRuntimeState(ReadRuntimeRevision(), sessionId);
    }

    private TimerState BuildCurrentTimerState()
    {
        return adapter.BuildTimerState(
            ReadStateRevision(),
            sessionId,
            ReadRunRevision(),
            ReadAttemptRevision(),
            ReadRuntimeRevision());
    }

    // Revision updates, TimerState capture, and sequence assignment happen in one
    // critical section so every event reports a single logical point in time.
    private void PublishEvent(BridgeEventType type, RevisionChange change)
    {
        lock (eventStateLock)
        {
            if ((change & RevisionChange.Attempt) != 0)
            {
                Interlocked.Increment(ref attemptRevision);
            }

            if ((change & RevisionChange.Run) != 0)
            {
                Interlocked.Increment(ref runRevision);
            }

            if ((change & RevisionChange.Runtime) != 0)
            {
                Interlocked.Increment(ref runtimeRevision);
            }

            if ((change & RevisionChange.State) != 0)
            {
                Interlocked.Increment(ref stateRevision);
            }

            var sequence = eventSequence.Begin();

            var bridgeEvent = new BridgeEvent
            {
                SessionId = sessionId,
                EventSequence = sequence,
                Type = type,
                TimerState = BuildCurrentTimerState()
            };

            // Enqueue while holding the lock to keep broadcast order identical to
            // the assigned event_sequence order. Sending stays on the publisher thread.
            transport.Publish(bridgeEvent);
        }
    }

    // Only fields without comprehensive LiveSplit events need lightweight
    // observation. This is called from the layout Update loop, never from a timer
    // operation, and never holds the control gate.
    internal void ObserveExternalState()
    {
        DetectGameTimeChange();
        SyncRuntimeStateAndPublish();
    }

    private void DetectGameTimeChange()
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

        var eventType = current.IsInitialized != previous.IsInitialized
            ? (current.IsInitialized
                ? BridgeEventType.EventGameTimeInitialized
                : BridgeEventType.EventGameTimeSet)
            : current.IsPaused != previous.IsPaused
                ? (current.IsPaused
                    ? BridgeEventType.EventGameTimePaused
                    : BridgeEventType.EventGameTimeResumed)
                : BridgeEventType.EventGameTimeSet;

        PublishEvent(eventType, RevisionChange.State);
    }

    private void RecordCurrentGameTimeState()
    {
        var current = adapter.CaptureGameTimeRevisionState();
        lock (observedStateLock)
        {
            observedGameTimeState = current;
        }
    }

    private void SyncRuntimeStateAndPublish()
    {
        var captured = adapter.CaptureRuntimeRevisionState();
        bool changed;

        lock (observedStateLock)
        {
            changed = !observedRuntimeState.Equals(captured);
            if (changed)
            {
                observedRuntimeState = captured;
            }
        }

        if (changed)
        {
            PublishEvent(BridgeEventType.EventRuntimeChanged, RevisionChange.Runtime);
        }
    }

    // Comparison rename / switch only needs to compare the current comparison; the
    // remaining RuntimeState fields are covered by the lightweight observation.
    private void SyncComparisonAndPublish()
    {
        var comparison = adapter.CaptureCurrentComparison();
        bool changed;

        lock (observedStateLock)
        {
            changed = !string.Equals(observedRuntimeState.CurrentComparison, comparison, StringComparison.Ordinal);
            if (changed)
            {
                observedRuntimeState = observedRuntimeState.WithCurrentComparison(comparison);
            }
        }

        if (changed)
        {
            PublishEvent(BridgeEventType.EventRuntimeChanged, RevisionChange.Runtime);
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

    private ulong ReadRunRevision()
    {
        return unchecked((ulong)Interlocked.Read(ref runRevision));
    }

    private ulong ReadAttemptRevision()
    {
        return unchecked((ulong)Interlocked.Read(ref attemptRevision));
    }

    private ulong ReadRuntimeRevision()
    {
        return unchecked((ulong)Interlocked.Read(ref runtimeRevision));
    }

    internal LiveSplitAdapter Adapter => adapter;

    internal ulong StateRevision => ReadStateRevision();

    internal ulong RunRevision => ReadRunRevision();

    internal ulong AttemptRevision => ReadAttemptRevision();

    internal ulong RuntimeRevision => ReadRuntimeRevision();

    private void AdapterGameTimeChanged(GameTimeOperationType operation)
    {
        var eventType = operation switch
        {
            GameTimeOperationType.Initialize => BridgeEventType.EventGameTimeInitialized,
            GameTimeOperationType.Set => BridgeEventType.EventGameTimeSet,
            GameTimeOperationType.GameTimePause => BridgeEventType.EventGameTimePaused,
            GameTimeOperationType.GameTimeResume => BridgeEventType.EventGameTimeResumed,
            _ => BridgeEventType.EventGameTimeSet,
        };

        RecordCurrentGameTimeState();
        PublishEvent(eventType, RevisionChange.State);
    }

    private void StateOnStart(object sender, EventArgs args)
    {
        PublishEvent(BridgeEventType.EventTimerStarted, RevisionChange.Timer);
    }

    private void StateOnSplit(object sender, EventArgs args)
    {
        PublishEvent(BridgeEventType.EventTimerSplit, RevisionChange.Timer);
    }

    private void StateOnSkipSplit(object sender, EventArgs args)
    {
        PublishEvent(BridgeEventType.EventTimerSkipped, RevisionChange.Timer);
    }

    private void StateOnUndoSplit(object sender, EventArgs args)
    {
        PublishEvent(BridgeEventType.EventTimerUndo, RevisionChange.Timer);
    }

    private void StateOnReset(object sender, LiveSplit.Model.TimerPhase previousPhase)
    {
        // OnReset precedes FixSplits, so the run generation is advanced here rather
        // than on a later UI turn. A single EVENT_TIMER_RESET expresses the timer,
        // attempt, and run invalidation; no separate EVENT_RUN_CHANGED is emitted.
        PublishEvent(
            BridgeEventType.EventTimerReset,
            RevisionChange.State | RevisionChange.Attempt | RevisionChange.Run);
    }

    private void StateOnPause(object sender, EventArgs args)
    {
        PublishEvent(BridgeEventType.EventTimerPaused, RevisionChange.State);
    }

    private void StateOnResume(object sender, EventArgs args)
    {
        PublishEvent(BridgeEventType.EventTimerResumed, RevisionChange.State);
    }

    private void StateRunManuallyModified(object sender, EventArgs args)
    {
        PublishEvent(BridgeEventType.EventRunChanged, RevisionChange.Run);

        // ComparisonRenamed precedes RunManuallyModified. When the rename also
        // changed the current comparison, publish RUNTIME_CHANGED in the same call
        // stack so RUN_CHANGED is always observed before RUNTIME_CHANGED.
        if (Interlocked.Exchange(ref runtimeChangePending, 0) != 0)
        {
            SyncComparisonAndPublish();
        }
    }

    private void StateComparisonRenamed(object sender, EventArgs args)
    {
        // RunEdited raises RunManuallyModified after ComparisonRenamed. Remember
        // the pending comparison check and let that event publish it.
        Interlocked.Exchange(ref runtimeChangePending, 1);
    }

    private void StateComparisonSwitched(object sender, EventArgs args)
    {
        SyncComparisonAndPublish();
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

    private static int GetPort(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var port) && port >= 1 && port <= 65535
            ? port
            : defaultValue;
    }
}
