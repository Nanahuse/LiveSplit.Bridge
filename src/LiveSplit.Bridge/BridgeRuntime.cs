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
    private long controlEpoch;
    private long mutationEpoch;
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
        catch (SnapshotUnstableException)
        {
            return MakeErrorResponse(request, 103, "State changed while snapshot was being captured. Retry the request.");
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
            Interlocked.Increment(ref controlEpoch);
            try
            {
                result = adapter.ExecuteTimerOperation(request.TimerOperation.Operation);
                if (result.Success)
                {
                    // The response reflects the state captured immediately after
                    // the mutation, still inside the control gate.
                    result.TimerState = BuildCurrentTimerState();
                }
            }
            finally
            {
                Interlocked.Increment(ref controlEpoch);
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
            Interlocked.Increment(ref controlEpoch);
            try
            {
                execution = adapter.ExecuteGameTimeOperation(
                    request.GameTimeOperation.Operation,
                    request.GameTimeOperation.HasTicks ? (long?)request.GameTimeOperation.Ticks : null);

                if (execution.Response.Success)
                {
                    execution.Response.TimerState = BuildCurrentTimerState();
                }
            }
            finally
            {
                Interlocked.Increment(ref controlEpoch);
            }
        }

        return new Response
        {
            ProtocolVersion = ProtocolVersion,
            RequestId = request.RequestId,
            Operation = execution.Response
        };
    }

    // Query snapshots are only accepted while the control plane is stable and the
    // target revision is unchanged for the whole build. A bounded retry loop yields
    // between attempts without ever taking the control gate. If no stable snapshot
    // can be produced, the query fails explicitly instead of returning unverified
    // state.
    private RunState BuildRunStateConsistent()
    {
        for (var attempt = 0; attempt < MaxSnapshotAttempts; attempt++)
        {
            var controlBefore = ReadControlEpoch();
            var mutationBefore = ReadMutationEpoch();
            if (IsUnstableEpoch(controlBefore, mutationBefore))
            {
                Thread.Yield();
                continue;
            }

            var revisionBefore = ReadRunRevision();
            var snapshot = adapter.BuildRunState(revisionBefore, sessionId);
            var revisionAfter = ReadRunRevision();
            var controlAfter = ReadControlEpoch();
            var mutationAfter = ReadMutationEpoch();

            if (controlBefore == controlAfter
                && mutationBefore == mutationAfter
                && !IsUnstableEpoch(controlAfter, mutationAfter)
                && revisionBefore == revisionAfter)
            {
                return snapshot;
            }

            Thread.Yield();
        }

        throw new SnapshotUnstableException("Run state changed while the snapshot was being captured.");
    }

    private AttemptState BuildAttemptStateConsistent()
    {
        for (var attempt = 0; attempt < MaxSnapshotAttempts; attempt++)
        {
            var controlBefore = ReadControlEpoch();
            var mutationBefore = ReadMutationEpoch();
            if (IsUnstableEpoch(controlBefore, mutationBefore))
            {
                Thread.Yield();
                continue;
            }

            var revisionBefore = ReadAttemptRevision();
            var snapshot = adapter.BuildAttemptState(revisionBefore, sessionId);
            var revisionAfter = ReadAttemptRevision();
            var controlAfter = ReadControlEpoch();
            var mutationAfter = ReadMutationEpoch();

            if (controlBefore == controlAfter
                && mutationBefore == mutationAfter
                && !IsUnstableEpoch(controlAfter, mutationAfter)
                && revisionBefore == revisionAfter)
            {
                return snapshot;
            }

            Thread.Yield();
        }

        throw new SnapshotUnstableException("Attempt state changed while the snapshot was being captured.");
    }

    // RuntimeState does not depend on Timer mutations, so only its own revision is
    // verified rather than the control epoch.
    private RuntimeState BuildRuntimeStateConsistent()
    {
        for (var attempt = 0; attempt < MaxSnapshotAttempts; attempt++)
        {
            var revisionBefore = ReadRuntimeRevision();
            var snapshot = adapter.BuildRuntimeState(revisionBefore, sessionId);
            if (ReadRuntimeRevision() == revisionBefore)
            {
                return snapshot;
            }

            Thread.Yield();
        }

        throw new SnapshotUnstableException("Runtime state changed while the snapshot was being captured.");
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

    // Timer events fire on the thread that performs the mutation, whether that is
    // a Bridge control operation or an external caller such as LiveSplit's own
    // command server. Bracketing the callback with mutationEpoch lets queries see
    // that an event-origin mutation is in progress even when it did not come
    // through the control gate.
    private void PublishMutationEvent(BridgeEventType type, RevisionChange change, Action? barrier = null)
    {
        Interlocked.Increment(ref mutationEpoch);
        try
        {
            PublishEvent(type, change);
            barrier?.Invoke();
        }
        finally
        {
            Interlocked.Increment(ref mutationEpoch);
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

    private long ReadControlEpoch()
    {
        return Interlocked.Read(ref controlEpoch);
    }

    private long ReadMutationEpoch()
    {
        return Interlocked.Read(ref mutationEpoch);
    }

    private static bool IsUnstableEpoch(long controlEpoch, long mutationEpoch)
    {
        return (controlEpoch & 1) != 0 || (mutationEpoch & 1) != 0;
    }

    internal LiveSplitAdapter Adapter => adapter;

    // Test seams: allow a test to pause inside a control mutation to exercise
    // query/snapshot consistency.
    internal Action? ResetBarrier { get; set; }

    internal Action? SplitBarrier { get; set; }

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
        PublishMutationEvent(BridgeEventType.EventTimerStarted, RevisionChange.Timer);
    }

    private void StateOnSplit(object sender, EventArgs args)
    {
        PublishMutationEvent(BridgeEventType.EventTimerSplit, RevisionChange.Timer, SplitBarrier);
    }

    private void StateOnSkipSplit(object sender, EventArgs args)
    {
        PublishMutationEvent(BridgeEventType.EventTimerSkipped, RevisionChange.Timer);
    }

    private void StateOnUndoSplit(object sender, EventArgs args)
    {
        PublishMutationEvent(BridgeEventType.EventTimerUndo, RevisionChange.Timer);
    }

    private void StateOnReset(object sender, LiveSplit.Model.TimerPhase previousPhase)
    {
        // OnReset precedes FixSplits, so the run generation is advanced here rather
        // than on a later UI turn. A single EVENT_TIMER_RESET expresses the timer,
        // attempt, and run invalidation; no separate EVENT_RUN_CHANGED is emitted.
        // The mutation marker stays set through this callback, so a query can tell
        // that the run generation was advanced but the run is not yet settled.
        PublishMutationEvent(
            BridgeEventType.EventTimerReset,
            RevisionChange.State | RevisionChange.Attempt | RevisionChange.Run,
            ResetBarrier);
    }

    private void StateOnPause(object sender, EventArgs args)
    {
        PublishMutationEvent(BridgeEventType.EventTimerPaused, RevisionChange.State);
    }

    private void StateOnResume(object sender, EventArgs args)
    {
        PublishMutationEvent(BridgeEventType.EventTimerResumed, RevisionChange.State);
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

// Raised when a heavy query cannot produce a snapshot that is consistent with the
// current generations after the bounded retry budget is exhausted.
internal sealed class SnapshotUnstableException : Exception
{
    public SnapshotUnstableException(string message)
        : base(message)
    {
    }
}
