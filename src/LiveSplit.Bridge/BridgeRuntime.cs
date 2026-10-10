#nullable enable
using System;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Threading;
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
    private long lastContextObservation;
    private long eventSequence;
    private readonly object eventGate = new();
    private int disposed;

    public BridgeRuntime(LiveSplit.Model.LiveSplitState state)
        : this(new LiveSplitAdapter(state), state)
    {
    }

    public BridgeRuntime(LiveSplit.Model.LiveSplitState state, int port)
        : this(new LiveSplitAdapter(state), state, port)
    {
    }

    internal BridgeRuntime(ILiveSplitAdapter adapter)
        : this(adapter, null)
    {
    }

    private BridgeRuntime(ILiveSplitAdapter adapter, LiveSplit.Model.LiveSplitState? state)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.state = state;
        sessionId = GenerateSessionId();
        publishedRun = adapter.GetRunState();
        publishedContext = adapter.GetContextState();
        cachedRunReference = state?.Run;
        lastContextObservation = Stopwatch.GetTimestamp();
        AttachStateEvents();
    }

    private BridgeRuntime(ILiveSplitAdapter adapter, LiveSplit.Model.LiveSplitState state, int port) : this(adapter, state)
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
        if (ReferenceEquals(current, cachedRunReference)) return;
        try
        {
            var next = adapter.GetRunState();
            if (!ReferenceEquals(current, state.Run)) return;
            cachedRunReference = current;
            var previous = Volatile.Read(ref publishedRun);
            if (next.Equals(previous)) return;
            Interlocked.Exchange(ref publishedRun, next);
            QueueEvent(BridgeEventType.EventRunChanged, null);
        }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] Run replacement observation failed: {exception}"); }
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
        bool runChanged = false;
        try
        {
            var currentRun = state!.Run;
            var old = Volatile.Read(ref publishedRun);
            var next = ReferenceEquals(currentRun, cachedRunReference)
                ? adapter.UpdateRunTimings(old)
                : adapter.GetRunState();
            cachedRunReference = currentRun;
            if (!next.Equals(old))
            {
                Interlocked.Exchange(ref publishedRun, next);
                runChanged = true;
            }
        }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] Run timing update after reset failed: {exception}"); }
        PublishTimerEvent(BridgeEventType.EventTimerReset);
        if (runChanged) QueueEvent(BridgeEventType.EventRunChanged, null);
    }

    private void StateRunManuallyModified(object sender, EventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        // Some Comparison Generator changes made in LiveSplit settings do not raise this event.
        // We intentionally avoid scanning/comparing the run on every update to preserve timer latency.
        try
        {
            var currentRun = state!.Run;
            var next = adapter.GetRunState();
            if (!ReferenceEquals(currentRun, state.Run)) return;
            cachedRunReference = currentRun;
            var previous = Volatile.Read(ref publishedRun);
            if (!next.Equals(previous))
            {
                Interlocked.Exchange(ref publishedRun, next);
                QueueEvent(BridgeEventType.EventRunChanged, null);
            }
        }
        catch (Exception exception) { Debug.WriteLine($"[LiveSplit.Bridge] Run cache update failed: {exception}"); }
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
            var bridgeEvent = new BridgeEvent { SessionId = sessionId, EventSequence = unchecked((ulong)++eventSequence), Type = type };
            if (timerState != null) bridgeEvent.TimerState = timerState;
            transport?.Publish(bridgeEvent);
        }
    }
}
