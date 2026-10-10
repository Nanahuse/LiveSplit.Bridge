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
    private long staticUpdateGeneration;
    private long timingUpdateGeneration;
    private int staticRefreshRequested;
    private int resetPublicationDepth;
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

    private BridgeRuntime(ILiveSplitAdapter adapter, LiveSplit.Model.LiveSplitState state, int port) : this(adapter, state, attachStateEvents: true)
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
            if (!forceRefresh && ReferenceEquals(current, requestedRunReference)) return;
            requestedRunReference = current;
            generation = ++staticUpdateGeneration;
        }
        try
        {
            state.Form.BeginInvoke((Action)(() => CaptureObservedRunCandidate(current, generation)));
        }
        catch (Exception exception)
        {
            ClearRequestedRunReference(current, generation);
            Debug.WriteLine($"[LiveSplit.Bridge] Run replacement observation failed: {exception}");
        }
    }

    private void CaptureObservedRunCandidate(object? runReference, long generation)
    {
        if (state == null || Volatile.Read(ref disposed) != 0 || generation != Interlocked.Read(ref staticUpdateGeneration)) return;
        if (!ReferenceEquals(runReference, state.Run))
        {
            ClearRequestedRunReference(runReference, generation);
            return;
        }
        RunStateBuildCandidate candidate;
        var capturedTimingGeneration = Interlocked.Read(ref timingUpdateGeneration);
        try
        {
            candidate = adapter.CaptureRunState();
            if (!ReferenceEquals(runReference, state.Run))
            {
                candidate.Dispose();
                ClearRequestedRunReference(runReference, generation);
                return;
            }
            _ = Task.Run(() => BuildAndPublishRunCandidate(candidate, runReference, generation, capturedTimingGeneration));
        }
        catch (Exception exception)
        {
            ClearRequestedRunReference(runReference, generation);
            Debug.WriteLine($"[LiveSplit.Bridge] Run replacement capture failed: {exception}");
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

    private void StateOnStart(object sender, EventArgs args) => PublishTimerEvent(BridgeEventType.EventTimerStarted);
    private void StateOnSplit(object sender, EventArgs args) => PublishTimerEvent(BridgeEventType.EventTimerSplit);
    private void StateOnSkipSplit(object sender, EventArgs args) => PublishTimerEvent(BridgeEventType.EventTimerSkipped);
    private void StateOnUndoSplit(object sender, EventArgs args) => PublishTimerEvent(BridgeEventType.EventTimerUndo);
    private void StateOnPhaseChanged(object sender, EventArgs args) => PublishTimerEvent(BridgeEventType.EventTimerPhaseChanged);

    private void StateOnReset(object sender, LiveSplit.Model.TimerPhase previousPhase)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        Interlocked.Increment(ref resetPublicationDepth);
        try { StateOnResetCore(); }
        finally { Interlocked.Decrement(ref resetPublicationDepth); }
    }

    private void StateOnResetCore()
    {
        var currentRun = state!.Run;
        Interlocked.Increment(ref timingUpdateGeneration);
        RunState? resetCandidate = null;
        var candidateForReplacement = false;
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
                    }
                }
            }
            else if (adapter.TryUpdateRunTimings(Volatile.Read(ref publishedRun), out var updated))
            {
                resetCandidate = updated;
            }
        }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] Run timing update after reset failed: {exception}"); }

        try
        {
            var timerState = adapter.GetTimerState();
            var previous = Volatile.Read(ref publishedRun);
            var runChanged = resetCandidate != null && !resetCandidate.Equals(previous);
            lock (eventGate)
            {
                if (resetCandidate != null && ReferenceEquals(currentRun, state.Run)
                    && ReferenceEquals(previous, publishedRun)
                    && (candidateForReplacement || ReferenceEquals(currentRun, cachedRunReference)))
                {
                    if (runChanged) Interlocked.Exchange(ref publishedRun, resetCandidate);
                    if (candidateForReplacement) cachedRunReference = currentRun;
                    if (ReferenceEquals(requestedRunReference, currentRun)) requestedRunReference = null;
                }
                else if (resetCandidate == null || !ReferenceEquals(currentRun, state.Run) || !ReferenceEquals(previous, publishedRun))
                {
                    runChanged = false;
                    Interlocked.Exchange(ref staticRefreshRequested, 1);
                }
                EnqueueEventLocked(BridgeEventType.EventTimerReset, timerState);
                if (runChanged) EnqueueEventLocked(BridgeEventType.EventRunChanged, null);
            }
        }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] Reset event callback failed: {exception}"); }
    }

    private void StateRunManuallyModified(object sender, EventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        // Some Comparison Generator changes made in LiveSplit settings do not raise this event.
        // We intentionally avoid scanning/comparing the run on every update to preserve timer latency.
        try
        {
            RequestSynchronousRunRefresh(state!.Run);
        }
        catch (Exception exception)
        {
            lock (eventGate) requestedRunReference = null;
            Debug.WriteLine($"[LiveSplit.Bridge] Run cache update failed: {exception}");
        }
    }

    private void RequestSynchronousRunRefresh(object? runReference)
    {
        long generation;
        lock (eventGate)
        {
            generation = ++staticUpdateGeneration;
            requestedRunReference = runReference;
        }
        try
        {
            var capturedTimingGeneration = Interlocked.Read(ref timingUpdateGeneration);
            var candidate = adapter.GetRunState();
            PublishRunCandidate(candidate, runReference, generation, capturedTimingGeneration);
        }
        catch
        {
            ClearRequestedRunReference(runReference, generation);
            throw;
        }
    }

    private void BuildAndPublishRunCandidate(RunStateBuildCandidate candidate, object? runReference, long generation, long capturedTimingGeneration)
    {
        try { PublishRunCandidate(candidate.Build(), runReference, generation, capturedTimingGeneration); }
        catch (Exception exception)
        {
            ClearRequestedRunReference(runReference, generation);
            Debug.WriteLine($"[LiveSplit.Bridge] Background Run cache build failed: {exception}");
        }
        finally { candidate.Dispose(); }
    }

    private void ClearRequestedRunReference(object? runReference, long generation)
    {
        lock (eventGate)
            if (generation == staticUpdateGeneration && ReferenceEquals(requestedRunReference, runReference)) requestedRunReference = null;
    }

    private void PublishRunCandidate(RunState candidate, object? runReference, long generation, long capturedTimingGeneration)
    {
        if (state == null) return;
        while (Volatile.Read(ref disposed) == 0)
        {
            if (Volatile.Read(ref resetPublicationDepth) != 0)
            {
                Thread.Yield();
                continue;
            }
            if (!ReferenceEquals(runReference, state.Run)) return;
            var timingGeneration = Interlocked.Read(ref timingUpdateGeneration);
            var timingsUpdated = adapter.TryUpdateRunTimings(candidate, out var withLatestTiming);
            if (!timingsUpdated)
            {
                // A complete static candidate can carry a changed segment/comparison layout.
                // Use it only if no Reset occurred since it captured its own timing and Run ID.
                if (timingGeneration != capturedTimingGeneration)
                {
                    Interlocked.Exchange(ref staticRefreshRequested, 1);
                    return;
                }
                withLatestTiming = candidate;
            }
            var previous = Volatile.Read(ref publishedRun);
            var changed = !withLatestTiming.Equals(previous);
            lock (eventGate)
            {
                if (Volatile.Read(ref disposed) != 0
                    || generation != staticUpdateGeneration
                    || !ReferenceEquals(runReference, state.Run)) return;
                if (Volatile.Read(ref resetPublicationDepth) != 0
                    || timingGeneration != Interlocked.Read(ref timingUpdateGeneration)
                    || (!timingsUpdated && timingGeneration != capturedTimingGeneration))
                {
                    Thread.Yield();
                    continue;
                }
                if (!ReferenceEquals(previous, publishedRun))
                {
                    continue;
                }
                if (changed)
                {
                    Interlocked.Exchange(ref publishedRun, withLatestTiming);
                    EnqueueEventLocked(BridgeEventType.EventRunChanged, null);
                }
                cachedRunReference = runReference;
                if (ReferenceEquals(requestedRunReference, runReference)) requestedRunReference = null;
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
