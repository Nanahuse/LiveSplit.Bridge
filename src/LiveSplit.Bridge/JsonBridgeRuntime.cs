#nullable enable
using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using Json = LiveSplit.Bridge.Json;
using LiveSplit.Model;

namespace LiveSplit.Bridge;

/// <summary>JSON protocol runtime, deliberately independent of WebSocket transport wiring.</summary>
internal sealed class JsonBridgeRuntime : IDisposable
{
    private readonly object controlGate = new();
    private readonly object eventGate = new();
    private readonly IJsonLiveSplitAdapter adapter;
    private readonly IJsonEventSink eventSink;
    private readonly LiveSplitState? state;
    private readonly uint sessionId;
    private Json.RunState publishedRun;
    private Json.ContextState publishedContext;
    private object? cachedRunReference;
    private bool runRefreshRequested;
    private long lastContextObservation;
    private uint eventSequence;
    private int disposed;

    internal JsonBridgeRuntime(LiveSplitState state, IJsonEventSink eventSink)
        : this(new JsonLiveSplitAdapter(state), eventSink, state, attachStateEvents: true) { }

    internal JsonBridgeRuntime(IJsonLiveSplitAdapter adapter, IJsonEventSink eventSink)
        : this(adapter, eventSink, null, attachStateEvents: false) { }

    internal JsonBridgeRuntime(IJsonLiveSplitAdapter adapter, IJsonEventSink eventSink, LiveSplitState state)
        : this(adapter, eventSink, state ?? throw new ArgumentNullException(nameof(state)), attachStateEvents: true) { }

    private JsonBridgeRuntime(IJsonLiveSplitAdapter adapter, IJsonEventSink eventSink, LiveSplitState? state, bool attachStateEvents)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        this.state = state;
        sessionId = GenerateSessionId();
        publishedRun = adapter.GetRunState();
        publishedContext = adapter.GetContextState();
        cachedRunReference = state?.Run;
        lastContextObservation = Stopwatch.GetTimestamp();
        if (attachStateEvents) AttachStateEvents();
    }

    /// <summary>Called by the host update loop to observe Run changes and throttled Context changes.</summary>
    internal void Update()
    {
        ObserveRunReference();
        ObserveContextState();
    }

    public void Dispose()
    {
        lock (eventGate)
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        DetachStateEvents();
    }

    internal Json.RpcResponse HandleRequest(Json.RpcRequest? request, bool allowControl)
    {
        if (request == null) return Error(0, Json.BridgeErrorCode.InvalidRequest, "Request is required.");
        try
        {
            switch (request)
            {
                case Json.QueryRequest { Method: Json.QueryMethod.GetTimerState }:
                    return new Json.Success<Json.TimerState> { RequestId = request.RequestId, SessionId = sessionId, Result = adapter.GetTimerState() };
                case Json.QueryRequest { Method: Json.QueryMethod.GetRun }:
                    return new Json.Success<Json.RunState> { RequestId = request.RequestId, SessionId = sessionId, Result = Volatile.Read(ref publishedRun) };
                case Json.QueryRequest { Method: Json.QueryMethod.GetRunIcons }:
                    return new Json.Success<Json.RunIcons> { RequestId = request.RequestId, SessionId = sessionId, Result = adapter.GetRunIcons() };
                case Json.QueryRequest { Method: Json.QueryMethod.GetAttempt }:
                    return new Json.Success<Json.AttemptState> { RequestId = request.RequestId, SessionId = sessionId, Result = adapter.GetAttempt() };
                case Json.QueryRequest { Method: Json.QueryMethod.GetContextState }:
                    return new Json.Success<Json.ContextState> { RequestId = request.RequestId, SessionId = sessionId, Result = Volatile.Read(ref publishedContext) };
                case Json.QueryRequest { Method: Json.QueryMethod.GetCompletedCount }:
                    return new Json.Success<Json.CompletedCount> { RequestId = request.RequestId, SessionId = sessionId, Result = adapter.GetCompletedCount() };
                case Json.TimerRequest timerRequest:
                    if (!allowControl) return Error(request.RequestId, Json.BridgeErrorCode.PermissionDenied, "Control permission is required.");
                    lock (controlGate)
                    {
                        try { adapter.ExecuteTimerOperation(timerRequest.Params.Operation); return Success(request.RequestId); }
                        catch (Exception exception) { return Error(request.RequestId, Json.BridgeErrorCode.OperationFailed, exception.Message); }
                    }
                case Json.GameTimeRequest<Json.GameControlParams> gameRequest:
                    return HandleGameTimeOperation(request.RequestId, gameRequest.Params, allowControl);
                case Json.GameTimeRequest<Json.GameSetParams> setRequest:
                    return HandleGameTimeOperation(request.RequestId, setRequest.Params, allowControl);
                default:
                    return Error(request.RequestId, Json.BridgeErrorCode.InvalidRequest, "Request is unsupported.");
            }
        }
        catch (Exception exception)
        {
            return Error(request.RequestId, Json.BridgeErrorCode.InternalError, exception.Message);
        }
    }

    private Json.RpcResponse Success(uint requestId) => new Json.ControlSuccessResponse { RequestId = requestId, SessionId = sessionId };
    internal Json.RpcResponse InvalidRequest(uint requestId, string message) => Error(requestId, Json.BridgeErrorCode.InvalidRequest, message);
    private Json.RpcResponse Error(uint requestId, Json.BridgeErrorCode code, string message) => new Json.ErrorResponse
    {
        RequestId = requestId,
        SessionId = sessionId,
        Error = new Json.BridgeError { Code = code, Message = message ?? string.Empty },
    };

    private Json.RpcResponse HandleGameTimeOperation(uint requestId, Json.GameTimeParams operation, bool allowControl)
    {
        if (!allowControl) return Error(requestId, Json.BridgeErrorCode.PermissionDenied, "Control permission is required.");
        lock (controlGate)
        {
            try { adapter.ExecuteGameTimeOperation(operation); return Success(requestId); }
            catch (Exception exception) { return Error(requestId, Json.BridgeErrorCode.OperationFailed, exception.Message); }
        }
    }

    internal void ObserveContextState()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var now = Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref lastContextObservation) < Stopwatch.Frequency / 10) return;
        Interlocked.Exchange(ref lastContextObservation, now);
        try
        {
            var next = adapter.GetContextState();
            lock (eventGate)
            {
                if (Volatile.Read(ref disposed) != 0
                    || Json.ContextSnapshot.ContentEquals(next, Volatile.Read(ref publishedContext))) return;
                Interlocked.Exchange(ref publishedContext, next);
                EnqueueEventLocked(new Json.ChangedEvent { Type = Json.ChangedEventKind.ContextChanged });
            }
        }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON context observation failed: {exception}"); }
    }

    internal void ObserveRunReference()
    {
        if (state == null) return;
        lock (eventGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (!runRefreshRequested && ReferenceEquals(state.Run, cachedRunReference)) return;
            try
            {
                if (RefreshRunLocked()) EnqueueEventLocked(new Json.ChangedEvent { Type = Json.ChangedEventKind.RunChanged });
            }
            catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON run update failed: {exception}"); }
        }
    }

    private void AttachStateEvents()
    {
        if (state == null) return;
        state.OnStart += StateOnStart;
        state.OnSplit += StateOnSplit;
        state.OnSkipSplit += StateOnSkipSplit;
        state.OnUndoSplit += StateOnUndoSplit;
        state.OnReset += StateOnReset;
        state.OnPause += StateOnPhaseChanged;
        state.OnResume += StateOnPhaseChanged;
        state.RunManuallyModified += StateRunManuallyModified;
    }

    private void DetachStateEvents()
    {
        if (state == null) return;
        state.OnStart -= StateOnStart;
        state.OnSplit -= StateOnSplit;
        state.OnSkipSplit -= StateOnSkipSplit;
        state.OnUndoSplit -= StateOnUndoSplit;
        state.OnReset -= StateOnReset;
        state.OnPause -= StateOnPhaseChanged;
        state.OnResume -= StateOnPhaseChanged;
        state.RunManuallyModified -= StateRunManuallyModified;
    }

    private void StateOnStart(object sender, EventArgs args) => PublishTimerEvent(Json.TimerEventKind.Started);
    private void StateOnSplit(object sender, EventArgs args) => PublishTimerEvent(Json.TimerEventKind.Split);
    private void StateOnSkipSplit(object sender, EventArgs args) => PublishTimerEvent(Json.TimerEventKind.Skipped);
    private void StateOnUndoSplit(object sender, EventArgs args) => PublishTimerEvent(Json.TimerEventKind.Undo);
    private void StateOnPhaseChanged(object sender, EventArgs args) => PublishTimerEvent(Json.TimerEventKind.PhaseChanged);
    private void StateRunManuallyModified(object sender, EventArgs args)
    {
        lock (eventGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            runRefreshRequested = true;
            ObserveRunReference();
        }
    }

    private void PublishTimerEvent(Json.TimerEventKind type)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try
        {
            var timerState = adapter.GetTimerState();
            lock (eventGate)
            {
                if (Volatile.Read(ref disposed) != 0) return;
                EnqueueEventLocked(new Json.TimerEvent { Type = type, TimerState = timerState });
            }
        }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON timer event failed: {exception}"); }
    }

    private void StateOnReset(object sender, TimerPhase previousPhase)
    {
        if (Volatile.Read(ref disposed) == 0) StateOnResetCore();
    }

    private void StateOnResetCore()
    {
        // Run capture and Reset publication share this lock. There is no background
        // candidate that can overwrite the timing snapshot published by Reset.
        lock (eventGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            Json.TimerState? timerState = null;
            try { timerState = adapter.GetTimerState(); }
            catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON timer state capture after reset failed: {exception}"); }

            var runChanged = false;
            try
            {
                var currentRun = state!.Run;
                if (!runRefreshRequested && ReferenceEquals(currentRun, cachedRunReference)
                    && adapter.TryUpdateRunTimings(publishedRun, out var updated)
                    && ReferenceEquals(currentRun, state.Run))
                    runChanged = PublishRunLocked(updated);
                else
                    runChanged = RefreshRunLocked();
            }
            catch (Exception exception)
            {
                runRefreshRequested = true;
                Debug.WriteLine($"[LiveSplit.Bridge] JSON run timing update after reset failed: {exception}");
            }
            EnqueueEventLocked(new Json.ResetEvent { TimerState = timerState });
            if (runChanged) EnqueueEventLocked(new Json.ChangedEvent { Type = Json.ChangedEventKind.RunChanged });
        }
    }

    private bool RefreshRunLocked()
    {
        runRefreshRequested = true;
        var run = state!.Run;
        var next = adapter.GetRunState();
        // A host-side replacement can occur outside Bridge's lock. Retry on the next
        // Update rather than publishing a snapshot captured from an obsolete Run.
        if (!ReferenceEquals(run, state.Run)) return false;
        var changed = PublishRunLocked(next);
        cachedRunReference = run;
        runRefreshRequested = false;
        return changed;
    }

    private bool PublishRunLocked(Json.RunState next)
    {
        if (Json.RunSnapshot.ContentEquals(next, publishedRun)) return false;
        Volatile.Write(ref publishedRun, next);
        return true;
    }

    private void EnqueueEventLocked(Json.BridgeEvent bridgeEvent)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        eventSequence = eventSequence == uint.MaxValue ? 1U : eventSequence + 1U;
        bridgeEvent.SessionId = sessionId;
        bridgeEvent.EventSequence = eventSequence;
        try { eventSink.Enqueue(bridgeEvent); }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON event sink failed: {exception}"); }
    }

    private static uint GenerateSessionId()
    {
        var bytes = new byte[4];
        using var rng = RandomNumberGenerator.Create();
        uint value;
        do { rng.GetBytes(bytes); value = BitConverter.ToUInt32(bytes, 0); } while (value == 0);
        return value;
    }
}
