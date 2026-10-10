#nullable enable
using System;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LiveSplit.Bridge.Protocol.V3;

namespace LiveSplit.Bridge;

internal sealed class BridgeRuntime : IDisposable
{
    private const uint ProtocolVersion = 3;

    private readonly object controlGate = new();
    private readonly ILiveSplitAdapter adapter;
    private readonly LiveSplit.Model.LiveSplitState? state;
    private readonly ulong sessionId;
    private WebSocketTransport? transport;
    private RunState publishedRun;
    private ContextState publishedContext;
    private object? cachedRunReference;
    private object? requestedRunReference;
    private bool runRefreshRequested;
    private long staticUpdateGeneration;
    private long timingUpdateGeneration;
    private long timingPublicationGeneration;
    private int staticRefreshRequested;
    private long lastContextObservation;
    private long eventSequence;
    private readonly object eventGate = new();
    private int disposed;

    public BridgeRuntime(LiveSplit.Model.LiveSplitState state)
        : this(new LiveSplitAdapter(state), state, attachStateEvents: true)
    {
    }

    public BridgeRuntime(LiveSplit.Model.LiveSplitState state, int port)
        : this(new LiveSplitAdapter(state), state, port)
    {
    }

    internal BridgeRuntime(ILiveSplitAdapter adapter)
        : this(adapter, null, attachStateEvents: true)
    {
    }

    internal BridgeRuntime(ILiveSplitAdapter adapter, LiveSplit.Model.LiveSplitState state)
        : this(adapter, state ?? throw new ArgumentNullException(nameof(state)), attachStateEvents: true)
    {
    }

    private BridgeRuntime(ILiveSplitAdapter adapter, LiveSplit.Model.LiveSplitState? state, bool attachStateEvents)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.state = state;
        sessionId = GenerateSessionId();
        publishedRun = adapter.GetRunState();
        publishedContext = adapter.GetContextState();
        cachedRunReference = state?.Run;
        lastContextObservation = Stopwatch.GetTimestamp();
        if (attachStateEvents) AttachStateEvents();
    }

    internal BridgeRuntime(ILiveSplitAdapter adapter, LiveSplit.Model.LiveSplitState state, int port) : this(adapter, state, attachStateEvents: true)
    {
        try
        {
            transport = new WebSocketTransport(port, HandleRequest);
            transport.Start();
        }
        catch
        {
            DetachStateEvents();
            transport?.Dispose();
            transport = null;
            Interlocked.Exchange(ref disposed, 1);
            throw;
        }
    }

    internal ulong SessionId => sessionId;
    internal string? Endpoint => transport?.Endpoint;
    internal bool IsListening => transport?.IsListening ?? false;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        DetachStateEvents();
        transport?.Dispose();
        transport = null;
    }

    internal Response HandleRequest(Request request)
    {
        if (request == null)
        {
            return MakeErrorResponse(0, BridgeErrorCode.InvalidRequest, "Request is required.");
        }

        if (request.ProtocolVersion != ProtocolVersion)
        {
            return MakeErrorResponse(
                request.RequestId,
                BridgeErrorCode.UnsupportedProtocolVersion,
                $"Unsupported protocol version {request.ProtocolVersion}.");
        }

        switch (request.BodyCase)
        {
            case Request.BodyOneofCase.GetTimerState:
                return HandleQuery(request, () => new Response
                {
                    GetTimerState = new GetTimerStateResponse { TimerState = adapter.GetTimerState() },
                });
            case Request.BodyOneofCase.GetAttempt:
                return HandleQuery(request, () => new Response
                {
                    GetAttempt = new GetAttemptResponse { Attempt = adapter.GetAttempt() },
                });
            case Request.BodyOneofCase.GetCompletedCount:
                return HandleQuery(request, () => new Response
                {
                    GetCompletedCount = new GetCompletedCountResponse
                    {
                        CompletedCount = adapter.GetCompletedCount(),
                    },
                });
            case Request.BodyOneofCase.TimerOperation:
                return HandleTimerOperation(request);
            case Request.BodyOneofCase.GameTimeOperation:
                return HandleGameTimeOperation(request);
            case Request.BodyOneofCase.GetRun:
                return HandleQuery(request, () => new Response { GetRun = new GetRunResponse { Run = Volatile.Read(ref publishedRun) } });
            case Request.BodyOneofCase.GetContextState:
                return HandleQuery(request, () => new Response { GetContextState = new GetContextStateResponse { ContextState = Volatile.Read(ref publishedContext) } });
            default:
                return MakeErrorResponse(
                    request.RequestId,
                    BridgeErrorCode.InvalidRequest,
                    "Request body is missing or unsupported.");
        }
    }

    private Response HandleQuery(Request request, Func<Response> query)
    {
        try
        {
            return MakeSuccessResponse(request.RequestId, query());
        }
        catch (Exception exception)
        {
            return MakeErrorResponse(request.RequestId, BridgeErrorCode.InternalError, exception.Message);
        }
    }

    private Response HandleTimerOperation(Request request)
    {
        var operation = request.TimerOperation.Operation;
        if (operation is not (TimerOperationType.TimerStart
            or TimerOperationType.TimerSplit
            or TimerOperationType.TimerSkip
            or TimerOperationType.TimerUndo
            or TimerOperationType.TimerReset
            or TimerOperationType.TimerPause
            or TimerOperationType.TimerResume))
        {
            return MakeErrorResponse(request.RequestId, BridgeErrorCode.InvalidArgument, "Timer operation is invalid.");
        }

        lock (controlGate)
        {
            try
            {
                adapter.ExecuteTimerOperation(operation);
                return MakeSuccessResponse(request.RequestId, new Response { Operation = new OperationResponse() });
            }
            catch (Exception exception)
            {
                return MakeErrorResponse(request.RequestId, BridgeErrorCode.OperationFailed, exception.Message);
            }
        }
    }

    private Response HandleGameTimeOperation(Request request)
    {
        var operation = request.GameTimeOperation.Operation;
        if (operation is not (GameTimeOperationType.Initialize
            or GameTimeOperationType.Set
            or GameTimeOperationType.GameTimePause
            or GameTimeOperationType.GameTimeResume))
        {
            return MakeErrorResponse(request.RequestId, BridgeErrorCode.InvalidArgument, "Game time operation is invalid.");
        }

        if (operation == GameTimeOperationType.Set && !request.GameTimeOperation.HasTicks)
        {
            return MakeErrorResponse(request.RequestId, BridgeErrorCode.InvalidArgument, "SET requires ticks.");
        }

        lock (controlGate)
        {
            try
            {
                adapter.ExecuteGameTimeOperation(
                    operation,
                    request.GameTimeOperation.HasTicks ? request.GameTimeOperation.Ticks : (long?)null);
                return MakeSuccessResponse(request.RequestId, new Response { Operation = new OperationResponse() });
            }
            catch (Exception exception)
            {
                return MakeErrorResponse(request.RequestId, BridgeErrorCode.OperationFailed, exception.Message);
            }
        }
    }

    private Response MakeSuccessResponse(ulong requestId, Response response)
    {
        response.ProtocolVersion = ProtocolVersion;
        response.RequestId = requestId;
        response.SessionId = sessionId;
        return response;
    }

    private Response MakeErrorResponse(ulong requestId, BridgeErrorCode code, string message)
    {
        return new Response
        {
            ProtocolVersion = ProtocolVersion,
            RequestId = requestId,
            SessionId = sessionId,
            Error = new BridgeError { Code = code, Message = message ?? string.Empty },
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

    internal void ObserveContextState()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var now = Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref lastContextObservation) < Stopwatch.Frequency / 10) return;
        Interlocked.Exchange(ref lastContextObservation, now);
        try
        {
            var next = adapter.GetContextState();
            var previous = Volatile.Read(ref publishedContext);
            if (next.Equals(previous)) return;
            Interlocked.Exchange(ref publishedContext, next);
            QueueEvent(BridgeEventType.EventContextChanged, null);
        }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] Context observation failed: {exception}"); }
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
        CaptureAndQueueRunCandidate(current, generation, "Run replacement observation");
    }

    private void CaptureAndQueueRunCandidate(object? runReference, long generation, string operation)
    {
        RunStateBuildCandidate? candidate = null;
        try
        {
            if (state == null || Volatile.Read(ref disposed) != 0 || generation != Interlocked.Read(ref staticUpdateGeneration)) return;
            if (!ReferenceEquals(runReference, state.Run))
            {
                ClearRequestedRunReference(runReference, generation);
                return;
            }
            var capturedTimingGeneration = Interlocked.Read(ref timingUpdateGeneration);
            candidate = adapter.CaptureRunState();
            if (Volatile.Read(ref disposed) != 0
                || generation != Interlocked.Read(ref staticUpdateGeneration)
                || !ReferenceEquals(runReference, state.Run))
            {
                candidate.Dispose();
                candidate = null;
                ClearRequestedRunReference(runReference, generation);
                return;
            }
            var queuedCandidate = candidate!;
            _ = Task.Run(() => BuildAndPublishRunCandidate(queuedCandidate, runReference, generation, capturedTimingGeneration));
            candidate = null;
        }
        catch (Exception exception)
        {
            candidate?.Dispose();
            ClearRequestedRunReference(runReference, generation);
            Interlocked.Exchange(ref staticRefreshRequested, 1);
            Debug.WriteLine($"[LiveSplit.Bridge] {operation} failed: {exception}");
        }
        finally { candidate?.Dispose(); }
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

    private void StateOnStart(object sender, EventArgs args) => PublishTimerEvent(BridgeEventType.EventTimerStarted);
    private void StateOnSplit(object sender, EventArgs args) => PublishTimerEvent(BridgeEventType.EventTimerSplit);
    private void StateOnSkipSplit(object sender, EventArgs args) => PublishTimerEvent(BridgeEventType.EventTimerSkipped);
    private void StateOnUndoSplit(object sender, EventArgs args) => PublishTimerEvent(BridgeEventType.EventTimerUndo);
    private void StateOnPhaseChanged(object sender, EventArgs args) => PublishTimerEvent(BridgeEventType.EventTimerPhaseChanged);

    private void StateOnReset(object sender, LiveSplit.Model.TimerPhase previousPhase)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        StateOnResetCore();
    }

    private void StateOnResetCore()
    {
        var currentRun = state!.Run;
        var resetTimingGeneration = Interlocked.Increment(ref timingUpdateGeneration);
        TimerState timerState;
        try
        {
            timerState = adapter.GetTimerState();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] Timer state capture after reset failed: {exception}");
            timerState = new TimerState();
        }

        while (Volatile.Read(ref disposed) == 0)
        {
            var basis = Volatile.Read(ref publishedRun);
            RunState? resetCandidate = null;
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
                        {
                            resetCandidate = latest;
                            candidateForReplacement = true;
                            timingUpdateCompatible = true;
                        }
                    }
                }
                else if (adapter.TryUpdateRunTimings(basis, out var updated))
                {
                    resetCandidate = updated;
                    timingUpdateCompatible = true;
                }
            }
            catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] Run timing update after reset failed: {exception}"); }

            var runChanged = resetCandidate != null && !resetCandidate.Equals(basis);
            var retry = false;
            lock (eventGate)
            {
                if (Volatile.Read(ref disposed) != 0) return;
                if (!ReferenceEquals(currentRun, state.Run))
                {
                    Interlocked.Exchange(ref staticRefreshRequested, 1);
                }
                else if (resetCandidate != null && ReferenceEquals(basis, publishedRun)
                    && (candidateForReplacement || ReferenceEquals(currentRun, cachedRunReference)))
                {
                    if (runChanged) Interlocked.Exchange(ref publishedRun, resetCandidate);
                    if (candidateForReplacement) cachedRunReference = currentRun;
                    if (runRefreshRequested && ReferenceEquals(requestedRunReference, currentRun))
                    {
                        requestedRunReference = null;
                        runRefreshRequested = false;
                    }
                    Interlocked.Exchange(ref timingPublicationGeneration, resetTimingGeneration);
                    EnqueueEventLocked(BridgeEventType.EventTimerReset, timerState);
                    if (runChanged) EnqueueEventLocked(BridgeEventType.EventRunChanged, null);
                    return;
                }
                else if (resetCandidate != null && !ReferenceEquals(basis, publishedRun))
                {
                    retry = true;
                }
                else
                {
                    if (ReferenceEquals(currentRun, cachedRunReference) && !timingUpdateCompatible)
                    {
                        // An in-flight candidate with the old layout cannot be paired with the
                        // Run after a structural edit. Invalidate it and recapture next Update.
                        Interlocked.Increment(ref staticUpdateGeneration);
                        requestedRunReference = null;
                        runRefreshRequested = false;
                    }
                    Interlocked.Exchange(ref staticRefreshRequested, 1);
                }

                if (retry) continue;
                Interlocked.Exchange(ref timingPublicationGeneration, resetTimingGeneration);
                EnqueueEventLocked(BridgeEventType.EventTimerReset, timerState);
                return;
            }
        }
    }

    private void StateRunManuallyModified(object sender, EventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        // Some Comparison Generator changes made in LiveSplit settings do not raise this event.
        // We intentionally avoid scanning/comparing the run on every update to preserve timer latency.
        try
        {
            RequestRunRefresh(state!.Run);
        }
        catch (Exception exception)
        {
            lock (eventGate)
            {
                requestedRunReference = null;
                runRefreshRequested = false;
            }
            Interlocked.Exchange(ref staticRefreshRequested, 1);
            Debug.WriteLine($"[LiveSplit.Bridge] Run cache update failed: {exception}");
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
        CaptureAndQueueRunCandidate(runReference, generation, "Run cache capture");
    }

    private void BuildAndPublishRunCandidate(RunStateBuildCandidate candidate, object? runReference, long generation, long capturedTimingGeneration)
    {
        try
        {
            if (Volatile.Read(ref disposed) != 0 || generation != Interlocked.Read(ref staticUpdateGeneration)) return;
            if (!ReferenceEquals(runReference, state?.Run))
            {
                ClearRequestedRunReference(runReference, generation);
                Interlocked.Exchange(ref staticRefreshRequested, 1);
                return;
            }
            PublishRunCandidate(candidate.Build(), runReference, generation, capturedTimingGeneration);
        }
        catch (Exception exception)
        {
            ClearRequestedRunReference(runReference, generation);
            Interlocked.Exchange(ref staticRefreshRequested, 1);
            Debug.WriteLine($"[LiveSplit.Bridge] Background Run cache build failed: {exception}");
        }
        finally { candidate.Dispose(); }
    }

    private void ClearRequestedRunReference(object? runReference, long generation)
    {
        lock (eventGate)
            if (generation == staticUpdateGeneration && runRefreshRequested && ReferenceEquals(requestedRunReference, runReference))
            {
                requestedRunReference = null;
                runRefreshRequested = false;
            }
    }

    private void PublishRunCandidate(RunState candidate, object? runReference, long generation, long capturedTimingGeneration)
    {
        if (state == null) return;
        while (Volatile.Read(ref disposed) == 0)
        {
            if (!ReferenceEquals(runReference, state.Run))
            {
                ClearRequestedRunReference(runReference, generation);
                Interlocked.Exchange(ref staticRefreshRequested, 1);
                return;
            }
            var timingGeneration = Interlocked.Read(ref timingUpdateGeneration);
            if (timingGeneration != capturedTimingGeneration
                && timingGeneration != Interlocked.Read(ref timingPublicationGeneration))
            {
                Interlocked.Exchange(ref staticRefreshRequested, 1);
                return;
            }
            var previous = Volatile.Read(ref publishedRun);
            var withLatestTiming = candidate;
            if (timingGeneration != capturedTimingGeneration
                && !LiveSplitAdapter.TryMergeRunTimings(candidate, previous, out withLatestTiming))
            {
                // The latest published timing has a different shape, so this snapshot cannot
                // safely combine with it. Component.Update will capture the current Run again.
                Interlocked.Exchange(ref staticRefreshRequested, 1);
                return;
            }
            var changed = !withLatestTiming.Equals(previous);
            lock (eventGate)
            {
                if (Volatile.Read(ref disposed) != 0
                    || generation != staticUpdateGeneration
                    || !ReferenceEquals(runReference, state.Run)) return;
                if (timingGeneration != Interlocked.Read(ref timingUpdateGeneration)
                    || (timingGeneration != capturedTimingGeneration
                        && timingGeneration != Interlocked.Read(ref timingPublicationGeneration))
                    || !ReferenceEquals(previous, publishedRun))
                {
                    continue;
                }
                if (changed)
                {
                    Interlocked.Exchange(ref publishedRun, withLatestTiming);
                    EnqueueEventLocked(BridgeEventType.EventRunChanged, null);
                }
                cachedRunReference = runReference;
                if (runRefreshRequested && ReferenceEquals(requestedRunReference, runReference))
                {
                    requestedRunReference = null;
                    runRefreshRequested = false;
                }
                return;
            }
        }
    }

    private void PublishTimerEvent(BridgeEventType type)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try { QueueEvent(type, adapter.GetTimerState()); }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] Timer event callback failed: {exception}"); }
    }

    private void QueueEvent(BridgeEventType type, TimerState? timerState)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        lock (eventGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            EnqueueEventLocked(type, timerState);
        }
    }

    private void EnqueueEventLocked(BridgeEventType type, TimerState? timerState)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var bridgeEvent = new BridgeEvent { SessionId = sessionId, EventSequence = unchecked((ulong)++eventSequence), Type = type };
        if (timerState != null) bridgeEvent.TimerState = timerState;
        transport?.Publish(bridgeEvent);
    }
}
