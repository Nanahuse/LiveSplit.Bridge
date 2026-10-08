#nullable enable
using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using LiveSplit.Bridge.Protocol.V3;
using LiveSplit.Model;

namespace LiveSplit.Bridge;

internal sealed class BridgeRuntime : IDisposable
{
    private const uint ProtocolVersion = 3;

    private readonly object controlGate = new();
    private readonly object eventSequenceGate = new();
    private readonly ILiveSplitAdapter adapter;
    private readonly ulong sessionId;
    private readonly LiveSplitState? state;
    private readonly RunStateCache? runStateCache;
    private WebSocketTransport? transport;
    private ContextState contextSnapshot;
    private ulong eventSequence;
    private int runManuallyModified;
    private int resetPending;

    public BridgeRuntime(LiveSplit.Model.LiveSplitState state)
        : this(state, new LiveSplitAdapter(state), null, null)
    {
    }

    public BridgeRuntime(LiveSplit.Model.LiveSplitState state, int port)
        : this(state, new LiveSplitAdapter(state), port, null)
    {
    }

    internal BridgeRuntime(LiveSplit.Model.LiveSplitState state, int port, Action<byte[]> eventSender)
        : this(state, new LiveSplitAdapter(state), port, eventSender)
    {
    }

    internal BridgeRuntime(ILiveSplitAdapter adapter)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        sessionId = GenerateSessionId();
        contextSnapshot = adapter.GetContextState();
    }

    private BridgeRuntime(LiveSplitState state, ILiveSplitAdapter adapter, int? port, Action<byte[]>? eventSender)
        : this(adapter)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        runStateCache = new RunStateCache(state);
        SubscribeToState();
        try
        {
            if (port.HasValue)
            {
                transport = new WebSocketTransport(port.Value, HandleRequest, eventSender);
                transport.Start();
            }
        }
        catch
        {
            UnsubscribeFromState();
            transport?.Dispose();
            transport = null;
            throw;
        }
    }

    internal ulong SessionId => sessionId;
    internal int Port => transport?.Port ?? 0;
    internal string? Endpoint => transport?.Endpoint;
    internal bool IsListening => transport?.IsListening ?? false;
    internal string? EventsEndpoint => transport?.EventsEndpoint;

    public void Dispose()
    {
        UnsubscribeFromState();
        transport?.Dispose();
        transport = null;
    }

    private void SubscribeToState()
    {
        if (state == null) return;
        state.OnStart += StateOnStart;
        state.OnSplit += StateOnSplit;
        state.OnSkipSplit += StateOnSkipSplit;
        state.OnUndoSplit += StateOnUndoSplit;
        state.OnReset += StateOnReset;
        state.OnPause += StateOnPause;
        state.OnResume += StateOnResume;
        state.RunManuallyModified += StateRunManuallyModified;
    }

    private void UnsubscribeFromState()
    {
        if (state == null) return;
        state.OnStart -= StateOnStart;
        state.OnSplit -= StateOnSplit;
        state.OnSkipSplit -= StateOnSkipSplit;
        state.OnUndoSplit -= StateOnUndoSplit;
        state.OnReset -= StateOnReset;
        state.OnPause -= StateOnPause;
        state.OnResume -= StateOnResume;
        state.RunManuallyModified -= StateRunManuallyModified;
    }

    internal void Update()
    {
        if (state == null || runStateCache == null) return;

        try
        {
            var runChanged = false;
            if (!ReferenceEquals(runStateCache.SourceRun, state.Run))
            {
                runChanged = runStateCache.Rebuild();
                Interlocked.Exchange(ref runManuallyModified, 0);
                Interlocked.Exchange(ref resetPending, 0);
            }
            else if (Interlocked.Exchange(ref resetPending, 0) != 0)
            {
                runChanged = runStateCache.UpdateAfterReset();
            }
            else if (Interlocked.Exchange(ref runManuallyModified, 0) != 0)
            {
                runChanged = runStateCache.Rebuild();
            }

            if (runChanged) PublishEvent(BridgeEventType.EventRunChanged, null);

            var currentContext = adapter.GetContextState();
            if (!contextSnapshot.Equals(currentContext))
            {
                contextSnapshot = currentContext;
                PublishEvent(BridgeEventType.EventContextChanged, null);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] Runtime update failed: {exception.Message}");
        }
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
            case Request.BodyOneofCase.GetRun:
                if (runStateCache == null)
                {
                    return MakeErrorResponse(request.RequestId, BridgeErrorCode.OperationFailed, "Run state is not available.");
                }
                return HandleQuery(request, () => new Response
                {
                    GetRun = new GetRunResponse { Run = runStateCache.Current },
                });
            case Request.BodyOneofCase.GetContextState:
                return HandleQuery(request, () => new Response
                {
                    GetContextState = new GetContextStateResponse { ContextState = adapter.GetContextState() },
                });
            case Request.BodyOneofCase.TimerOperation:
                return HandleTimerOperation(request);
            case Request.BodyOneofCase.GameTimeOperation:
                return HandleGameTimeOperation(request);
            default:
                return MakeErrorResponse(
                    request.RequestId,
                    BridgeErrorCode.InvalidRequest,
                    "Request body is missing or unsupported.");
        }
    }

    private void StateOnStart(object sender, EventArgs e) => PublishTimerEvent(BridgeEventType.EventTimerStarted);
    private void StateOnSplit(object sender, EventArgs e) => PublishTimerEvent(BridgeEventType.EventTimerSplit);
    private void StateOnSkipSplit(object sender, EventArgs e) => PublishTimerEvent(BridgeEventType.EventTimerSkipped);
    private void StateOnUndoSplit(object sender, EventArgs e) => PublishTimerEvent(BridgeEventType.EventTimerUndo);

    private void StateOnReset(object sender, LiveSplit.Model.TimerPhase phase)
    {
        Interlocked.Exchange(ref resetPending, 1);
        PublishTimerEvent(BridgeEventType.EventTimerReset);
    }

    private void StateOnPause(object sender, EventArgs e) => PublishTimerEvent(BridgeEventType.EventTimerPhaseChanged);
    private void StateOnResume(object sender, EventArgs e) => PublishTimerEvent(BridgeEventType.EventTimerPhaseChanged);
    private void StateRunManuallyModified(object sender, EventArgs e) => Interlocked.Exchange(ref runManuallyModified, 1);

    private void PublishTimerEvent(BridgeEventType type)
    {
        if (state == null) return;
        PublishEvent(type, adapter.GetTimerState());
    }

    private void PublishEvent(BridgeEventType type, TimerState? timerState)
    {
        lock (eventSequenceGate)
        {
            var bridgeEvent = new BridgeEvent
            {
                SessionId = sessionId,
                EventSequence = ++eventSequence,
                Type = type,
            };
            if (timerState != null) bridgeEvent.TimerState = timerState;
            transport?.Publish(bridgeEvent);
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
}
