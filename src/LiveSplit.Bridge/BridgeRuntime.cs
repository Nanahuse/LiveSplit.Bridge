using System;
using System.Security.Cryptography;
using LiveSplit.Bridge.Protocol.V3;

namespace LiveSplit.Bridge;

internal sealed class BridgeRuntime
{
    private const uint ProtocolVersion = 3;

    private readonly object controlGate = new();
    private readonly IV3LiveSplitAdapter adapter;
    private readonly ulong sessionId;

    public BridgeRuntime(LiveSplit.Model.LiveSplitState state)
        : this(new V3LiveSplitAdapter(state))
    {
    }

    internal BridgeRuntime(IV3LiveSplitAdapter adapter)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        sessionId = GenerateSessionId();
    }

    internal ulong SessionId => sessionId;

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
            case Request.BodyOneofCase.GetContextState:
                return MakeErrorResponse(
                    request.RequestId,
                    BridgeErrorCode.OperationFailed,
                    "This query is not available yet.");
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
}
