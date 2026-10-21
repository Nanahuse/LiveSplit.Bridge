#nullable enable
using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly Action<Action> queueCandidateWork;
    private readonly LiveSplitState? state;
    private readonly uint sessionId;
    private Json.RunState publishedRun;
    private Json.ContextState publishedContext;
    private object? cachedRunReference;
    private object? requestedRunReference;
    private bool runRefreshRequested;
    private long staticUpdateGeneration;
    private long timingUpdateGeneration;
    private long timingPublicationGeneration;
    private int staticRefreshRequested;
    private long lastContextObservation;
    private uint eventSequence;
    private int disposed;

    internal JsonBridgeRuntime(LiveSplitState state, IJsonEventSink eventSink)
        : this(new JsonLiveSplitAdapter(state), eventSink, state, attachStateEvents: true) { }

    internal JsonBridgeRuntime(IJsonLiveSplitAdapter adapter, IJsonEventSink eventSink)
        : this(adapter, eventSink, null, attachStateEvents: false) { }

    internal JsonBridgeRuntime(IJsonLiveSplitAdapter adapter, IJsonEventSink eventSink, LiveSplitState state)
        : this(adapter, eventSink, state ?? throw new ArgumentNullException(nameof(state)), attachStateEvents: true) { }

    internal JsonBridgeRuntime(IJsonLiveSplitAdapter adapter, IJsonEventSink eventSink, LiveSplitState state, Action<Action> queueCandidateWork)
        : this(adapter, eventSink, state ?? throw new ArgumentNullException(nameof(state)), attachStateEvents: true,
            queueCandidateWork ?? throw new ArgumentNullException(nameof(queueCandidateWork))) { }

    private JsonBridgeRuntime(IJsonLiveSplitAdapter adapter, IJsonEventSink eventSink, LiveSplitState? state, bool attachStateEvents,
        Action<Action>? queueCandidateWork = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        this.queueCandidateWork = queueCandidateWork ?? (work => _ = Task.Run(work));
        this.state = state;
        sessionId = GenerateSessionId();
        publishedRun = adapter.GetRunState();
        publishedContext = adapter.GetContextState();
        cachedRunReference = state?.Run;
        lastContextObservation = Stopwatch.GetTimestamp();
        if (attachStateEvents) AttachStateEvents();
    }

    /// <summary>Called by a future host update loop; it performs throttled context and Run observation.</summary>
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
        if (state == null || Volatile.Read(ref disposed) != 0) return;
        var current = state.Run;
        long generation;
        lock (eventGate)
        {
            var forceRefresh = Interlocked.Exchange(ref staticRefreshRequested, 0) != 0;
            if (ReferenceEquals(current, cachedRunReference) && !forceRefresh) return;
            if (!forceRefresh && runRefreshRequested && ReferenceEquals(current, requestedRunReference)) return;
            requestedRunReference = current;
            runRefreshRequested = true;
            generation = Interlocked.Increment(ref staticUpdateGeneration);
        }
        CaptureAndQueueRunCandidate(current, generation);
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
        if (Volatile.Read(ref disposed) != 0) return;
        try { RequestRunRefresh(state!.Run); }
        catch (Exception exception)
        {
            lock (eventGate) { requestedRunReference = null; runRefreshRequested = false; }
            Interlocked.Exchange(ref staticRefreshRequested, 1);
            Debug.WriteLine($"[LiveSplit.Bridge] JSON run cache update failed: {exception}");
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
        var currentRun = state!.Run;
        var resetTimingGeneration = Interlocked.Increment(ref timingUpdateGeneration);
        Json.TimerState? timerState = null;
        try { timerState = adapter.GetTimerState(); }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON timer state capture after reset failed: {exception}"); }

        while (Volatile.Read(ref disposed) == 0)
        {
            var basis = Volatile.Read(ref publishedRun);
            Json.RunState? resetCandidate = null;
            var candidateForReplacement = false;
            var timingUpdateCompatible = false;
            try
            {
                if (!ReferenceEquals(currentRun, Volatile.Read(ref cachedRunReference)))
                {
                    if (adapter.IsTimerOnlyRun())
                    {
                        var candidate = adapter.GetRunState();
                        if (adapter.TryUpdateRunTimings(candidate, out var latest))
                        { resetCandidate = latest; candidateForReplacement = true; timingUpdateCompatible = true; }
                    }
                }
                else if (adapter.TryUpdateRunTimings(basis, out var updated))
                { resetCandidate = updated; timingUpdateCompatible = true; }
            }
            catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] JSON run timing update after reset failed: {exception}"); }

            var runChanged = resetCandidate != null && !Json.RunSnapshot.ContentEquals(resetCandidate, basis);
            var retry = false;
            lock (eventGate)
            {
                if (Volatile.Read(ref disposed) != 0) return;
                if (!ReferenceEquals(currentRun, state.Run)) Interlocked.Exchange(ref staticRefreshRequested, 1);
                else if (resetCandidate != null && ReferenceEquals(basis, publishedRun)
                    && (candidateForReplacement || ReferenceEquals(currentRun, cachedRunReference)))
                {
                    if (runChanged) Interlocked.Exchange(ref publishedRun, resetCandidate);
                    if (candidateForReplacement) cachedRunReference = currentRun;
                    ClearRequestedRunReferenceLocked(currentRun);
                    Interlocked.Exchange(ref timingPublicationGeneration, resetTimingGeneration);
                    EnqueueEventLocked(new Json.ResetEvent { TimerState = timerState });
                    if (runChanged) EnqueueEventLocked(new Json.ChangedEvent { Type = Json.ChangedEventKind.RunChanged });
                    return;
                }
                else if (resetCandidate != null && !ReferenceEquals(basis, publishedRun)) retry = true;
                else
                {
                    if (ReferenceEquals(currentRun, cachedRunReference) && !timingUpdateCompatible)
                    {
                        Interlocked.Increment(ref staticUpdateGeneration);
                        requestedRunReference = null;
                        runRefreshRequested = false;
                    }
                    Interlocked.Exchange(ref staticRefreshRequested, 1);
                }
                if (retry) continue;
                Interlocked.Exchange(ref timingPublicationGeneration, resetTimingGeneration);
                EnqueueEventLocked(new Json.ResetEvent { TimerState = timerState });
                return;
            }
        }
    }

    private void RequestRunRefresh(object? runReference)
    {
        long generation;
        lock (eventGate)
        {
            generation = Interlocked.Increment(ref staticUpdateGeneration);
            requestedRunReference = runReference;
            runRefreshRequested = true;
            Interlocked.Exchange(ref staticRefreshRequested, 0);
        }
        CaptureAndQueueRunCandidate(runReference, generation);
    }

    private void CaptureAndQueueRunCandidate(object? runReference, long generation)
    {
        IJsonRunStateCandidate? candidate = null;
        try
        {
            if (state == null || Volatile.Read(ref disposed) != 0 || generation != Interlocked.Read(ref staticUpdateGeneration)) return;
            if (!ReferenceEquals(runReference, state.Run)) { ClearRequestedRunReference(runReference, generation); return; }
            var capturedTimingGeneration = Interlocked.Read(ref timingUpdateGeneration);
            candidate = adapter.CaptureRunState();
            if (Volatile.Read(ref disposed) != 0 || generation != Interlocked.Read(ref staticUpdateGeneration)
                || !ReferenceEquals(runReference, state.Run))
            { candidate.Dispose(); candidate = null; ClearRequestedRunReference(runReference, generation); return; }
            var queued = candidate;
            queueCandidateWork(() => BuildAndPublishRunCandidate(queued, runReference, generation, capturedTimingGeneration));
            candidate = null;
        }
        catch (Exception exception)
        {
            candidate?.Dispose();
            ClearRequestedRunReference(runReference, generation);
            Interlocked.Exchange(ref staticRefreshRequested, 1);
            Debug.WriteLine($"[LiveSplit.Bridge] JSON run candidate capture failed: {exception}");
        }
        finally { candidate?.Dispose(); }
    }

    private void BuildAndPublishRunCandidate(IJsonRunStateCandidate candidate, object? runReference, long generation, long capturedTimingGeneration)
    {
        try
        {
            if (Volatile.Read(ref disposed) != 0 || generation != Interlocked.Read(ref staticUpdateGeneration)) return;
            if (!ReferenceEquals(runReference, state?.Run))
            { ClearRequestedRunReference(runReference, generation); Interlocked.Exchange(ref staticRefreshRequested, 1); return; }
            PublishRunCandidate(candidate.Build(), runReference, generation, capturedTimingGeneration);
        }
        catch (Exception exception)
        {
            ClearRequestedRunReference(runReference, generation);
            Interlocked.Exchange(ref staticRefreshRequested, 1);
            Debug.WriteLine($"[LiveSplit.Bridge] JSON background run cache build failed: {exception}");
        }
        finally { candidate.Dispose(); }
    }

    private void PublishRunCandidate(Json.RunState candidate, object? runReference, long generation, long capturedTimingGeneration)
    {
        if (state == null) return;
        while (Volatile.Read(ref disposed) == 0)
        {
            if (!ReferenceEquals(runReference, state.Run)) { ClearRequestedRunReference(runReference, generation); Interlocked.Exchange(ref staticRefreshRequested, 1); return; }
            var timingGeneration = Interlocked.Read(ref timingUpdateGeneration);
            if (timingGeneration != capturedTimingGeneration && timingGeneration != Interlocked.Read(ref timingPublicationGeneration))
            { Interlocked.Exchange(ref staticRefreshRequested, 1); return; }
            var previous = Volatile.Read(ref publishedRun);
            var withLatestTiming = candidate;
            if (timingGeneration != capturedTimingGeneration)
            {
                if (ReferenceEquals(runReference, Volatile.Read(ref cachedRunReference)))
                {
                    if (!JsonLiveSplitAdapter.TryMergeRunTimings(candidate, previous, out withLatestTiming))
                    { Interlocked.Exchange(ref staticRefreshRequested, 1); return; }
                }
                else if (!adapter.TryUpdateRunTimings(candidate, out withLatestTiming))
                { Interlocked.Exchange(ref staticRefreshRequested, 1); return; }
            }
            var changed = !Json.RunSnapshot.ContentEquals(withLatestTiming, previous);
            lock (eventGate)
            {
                if (Volatile.Read(ref disposed) != 0 || generation != staticUpdateGeneration || !ReferenceEquals(runReference, state.Run)) return;
                if (timingGeneration != Interlocked.Read(ref timingUpdateGeneration)
                    || (timingGeneration != capturedTimingGeneration && timingGeneration != Interlocked.Read(ref timingPublicationGeneration))
                    || !ReferenceEquals(previous, publishedRun)) continue;
                if (changed)
                {
                    Interlocked.Exchange(ref publishedRun, withLatestTiming);
                    EnqueueEventLocked(new Json.ChangedEvent { Type = Json.ChangedEventKind.RunChanged });
                }
                cachedRunReference = runReference;
                ClearRequestedRunReferenceLocked(runReference);
                return;
            }
        }
    }

    private void ClearRequestedRunReference(object? runReference, long generation)
    {
        lock (eventGate)
            if (generation == staticUpdateGeneration && runRefreshRequested && ReferenceEquals(requestedRunReference, runReference))
            { requestedRunReference = null; runRefreshRequested = false; }
    }

    private void ClearRequestedRunReferenceLocked(object? runReference)
    {
        if (runRefreshRequested && ReferenceEquals(requestedRunReference, runReference))
        { requestedRunReference = null; runRefreshRequested = false; }
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
