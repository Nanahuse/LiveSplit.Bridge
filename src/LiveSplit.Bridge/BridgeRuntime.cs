using System;
using System.Security.Cryptography;
using System.Threading;
using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;

namespace LiveSplit.Bridge;

internal sealed class BridgeRuntime : IDisposable
{
    private const uint ProtocolVersion = 2;
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    private readonly LiveSplitAdapter adapter;
    private readonly WebSocketTransport transport;
    private readonly EventSequence eventSequence = new();
    private readonly object eventStateLock = new();
    private readonly object observedStateLock = new();
    private readonly object projectionLock = new();
    private readonly object controlGate = new();
    private readonly ProjectionStore projectionStore;
    private readonly LiveSplitState state;
    private readonly ulong sessionId;
    private long stateRevision;
    private int runDirty;
    private int attemptDirty;
    private int runtimeDirty;
    private int runtimeChangePending;
    private GameTimeRevisionState observedGameTimeState;
    private RuntimeRevisionState observedRuntimeState;
    private int disposed;

    public BridgeRuntime(LiveSplitState state, int webSocketPort)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        adapter = new LiveSplitAdapter(state);
        sessionId = GenerateSessionId();
        observedGameTimeState = adapter.CaptureGameTimeRevisionState();
        observedRuntimeState = adapter.CaptureRuntimeRevisionState();
        stateRevision = 1;

        // Publish the first complete projection before the transport starts, so a
        // client that queries immediately after startup always gets valid state.
        var initialRun = adapter.BuildRunState(1, sessionId);
        var initialAttempt = adapter.BuildAttemptState(1, sessionId);
        var initialRuntime = adapter.BuildRuntimeState(1, sessionId);
        projectionStore = new ProjectionStore(
            new ProjectionSnapshot(initialRun, initialAttempt, initialRuntime, 1, 1, 1));

        var port = GetPort("LIVESPLIT_BRIDGE_WEBSOCKET_PORT", webSocketPort);

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
    // mutations are serialized only against each other by the control gate; queries
    // read the published projection and never take that gate, so they cannot delay
    // a timer operation.
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
                        Run = projectionStore.Current.Run
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
                        Attempt = projectionStore.Current.Attempt
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
                        RuntimeState = projectionStore.Current.Runtime
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
                // The response reflects the lightweight timer state captured right
                // after the mutation. Detailed projections are committed later by
                // Update(), so the run/attempt revisions may still be the previous
                // published values.
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

    private TimerState BuildCurrentTimerState()
    {
        var snapshot = projectionStore.Current;
        return adapter.BuildTimerState(
            ReadStateRevision(),
            sessionId,
            snapshot.RunRevision,
            snapshot.AttemptRevision,
            snapshot.RuntimeRevision);
    }

    // The projection capture point. Component.Update runs this on the UI thread,
    // which serializes it with LiveSplit's own UI / hotkey mutations. It detects
    // lightweight external changes, then rebuilds and commits any dirty projection
    // and publishes the corresponding projection changed events.
    internal void Update()
    {
        DetectGameTimeChange();
        DetectRuntimeChange();
        ProcessDirtyProjections();
    }

    private void ProcessDirtyProjections()
    {
        var runDirty = Interlocked.Exchange(ref this.runDirty, 0) != 0;
        var attemptDirty = Interlocked.Exchange(ref this.attemptDirty, 0) != 0;
        var runtimeDirty = Interlocked.Exchange(ref this.runtimeDirty, 0) != 0;
        if (!runDirty && !attemptDirty && !runtimeDirty)
        {
            return;
        }

        lock (projectionLock)
        {
            var current = projectionStore.Current;
            var runRevision = current.RunRevision;
            var attemptRevision = current.AttemptRevision;
            var runtimeRevision = current.RuntimeRevision;
            var run = current.Run;
            var attempt = current.Attempt;
            var runtime = current.Runtime;

            try
            {
                if (runDirty)
                {
                    runRevision++;
                    run = adapter.BuildRunState(runRevision, sessionId);
                }

                if (attemptDirty)
                {
                    attemptRevision++;
                    attempt = adapter.BuildAttemptState(attemptRevision, sessionId);
                }

                if (runtimeDirty)
                {
                    runtimeRevision++;
                    runtime = adapter.BuildRuntimeState(runtimeRevision, sessionId);
                }
            }
            catch (Exception exception)
            {
                // Keep the last completed projection intact and retry on a later
                // Update instead of publishing a partially built generation.
                System.Diagnostics.Debug.WriteLine(
                    $"[LiveSplit.Bridge] Projection build failed: {exception}");
                if (runDirty) Interlocked.Exchange(ref this.runDirty, 1);
                if (attemptDirty) Interlocked.Exchange(ref this.attemptDirty, 1);
                if (runtimeDirty) Interlocked.Exchange(ref this.runtimeDirty, 1);
                return;
            }

            var next = new ProjectionSnapshot(
                run,
                attempt,
                runtime,
                runRevision,
                attemptRevision,
                runtimeRevision);

            // The swap happens before the events are published, so an event always
            // refers to a projection that is already queryable.
            projectionStore.Commit(next);

            if (attemptDirty)
            {
                PublishProjectionChangedEvent(BridgeEventType.EventAttemptChanged);
            }

            if (runDirty)
            {
                PublishProjectionChangedEvent(BridgeEventType.EventRunChanged);
            }

            if (runtimeDirty)
            {
                PublishProjectionChangedEvent(BridgeEventType.EventRuntimeChanged);
            }
        }
    }

    private void PublishTimerTransitionEvent(BridgeEventType type, Action? barrier = null)
    {
        lock (eventStateLock)
        {
            Interlocked.Increment(ref stateRevision);
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

        barrier?.Invoke();
    }

    private void PublishProjectionChangedEvent(BridgeEventType type)
    {
        lock (eventStateLock)
        {
            var sequence = eventSequence.Begin();

            var bridgeEvent = new BridgeEvent
            {
                SessionId = sessionId,
                EventSequence = sequence,
                Type = type,
                TimerState = BuildCurrentTimerState()
            };

            transport.Publish(bridgeEvent);
        }
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

        PublishTimerTransitionEvent(eventType);
    }

    private void RecordCurrentGameTimeState()
    {
        var current = adapter.CaptureGameTimeRevisionState();
        lock (observedStateLock)
        {
            observedGameTimeState = current;
        }
    }

    // Only fields without comprehensive LiveSplit events need lightweight
    // observation. This is called from Update, never from a timer operation, and
    // only marks RuntimeState dirty; the projection is rebuilt at commit time.
    private void DetectRuntimeChange()
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
            Interlocked.Exchange(ref runtimeDirty, 1);
        }
    }

    private void MarkRuntimeDirtyIfComparisonChanged()
    {
        var comparison = adapter.CaptureCurrentComparison();

        lock (observedStateLock)
        {
            if (string.Equals(observedRuntimeState.CurrentComparison, comparison, StringComparison.Ordinal))
            {
                return;
            }

            observedRuntimeState = observedRuntimeState.WithCurrentComparison(comparison);
        }

        Interlocked.Exchange(ref runtimeDirty, 1);
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

    internal LiveSplitAdapter Adapter => adapter;

    // Test seams: allow a test to pause inside a timer mutation to exercise
    // query/projection consistency.
    internal Action? ResetBarrier { get; set; }

    internal Action? SplitBarrier { get; set; }

    internal ulong StateRevision => ReadStateRevision();

    internal ulong RunRevision => projectionStore.Current.RunRevision;

    internal ulong AttemptRevision => projectionStore.Current.AttemptRevision;

    internal ulong RuntimeRevision => projectionStore.Current.RuntimeRevision;

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
        PublishTimerTransitionEvent(eventType);
    }

    private void StateOnStart(object sender, EventArgs args)
    {
        Interlocked.Exchange(ref attemptDirty, 1);
        PublishTimerTransitionEvent(BridgeEventType.EventTimerStarted);
    }

    private void StateOnSplit(object sender, EventArgs args)
    {
        Interlocked.Exchange(ref attemptDirty, 1);
        PublishTimerTransitionEvent(BridgeEventType.EventTimerSplit, SplitBarrier);
    }

    private void StateOnSkipSplit(object sender, EventArgs args)
    {
        Interlocked.Exchange(ref attemptDirty, 1);
        PublishTimerTransitionEvent(BridgeEventType.EventTimerSkipped);
    }

    private void StateOnUndoSplit(object sender, EventArgs args)
    {
        Interlocked.Exchange(ref attemptDirty, 1);
        PublishTimerTransitionEvent(BridgeEventType.EventTimerUndo);
    }

    private void StateOnReset(object sender, LiveSplit.Model.TimerPhase previousPhase)
    {
        // OnReset precedes FixSplits, so the new RunProjection is not complete yet.
        // Only mark the affected projections dirty; the revisions advance when the
        // projections are actually rebuilt and committed. The immediate
        // EVENT_TIMER_RESET keeps reporting the previously published generations.
        Interlocked.Exchange(ref attemptDirty, 1);
        Interlocked.Exchange(ref runDirty, 1);
        PublishTimerTransitionEvent(BridgeEventType.EventTimerReset, ResetBarrier);
    }

    private void StateOnPause(object sender, EventArgs args)
    {
        PublishTimerTransitionEvent(BridgeEventType.EventTimerPaused);
    }

    private void StateOnResume(object sender, EventArgs args)
    {
        PublishTimerTransitionEvent(BridgeEventType.EventTimerResumed);
    }

    private void StateRunManuallyModified(object sender, EventArgs args)
    {
        Interlocked.Exchange(ref runDirty, 1);

        // ComparisonRenamed precedes RunManuallyModified. When the rename also
        // changed the current comparison, RuntimeState must be refreshed in the
        // same commit so RUN_CHANGED is still observed before RUNTIME_CHANGED.
        if (Interlocked.Exchange(ref runtimeChangePending, 0) != 0)
        {
            MarkRuntimeDirtyIfComparisonChanged();
        }
    }

    private void StateComparisonRenamed(object sender, EventArgs args)
    {
        // RunEdited raises RunManuallyModified after ComparisonRenamed. Remember
        // the pending comparison check and let that event resolve it.
        Interlocked.Exchange(ref runtimeChangePending, 1);
    }

    private void StateComparisonSwitched(object sender, EventArgs args)
    {
        MarkRuntimeDirtyIfComparisonChanged();
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
