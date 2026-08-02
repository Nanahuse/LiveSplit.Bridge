using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V1;
using LiveSplit.Model;
using NetMQ;
using NetMQ.Sockets;

namespace LiveSplit.Bridge;

internal sealed class BridgeRuntime : IDisposable
{
    private const uint ProtocolVersion = 1;
    private readonly LiveSplitAdapter adapter;
    private readonly string rpcEndpoint;
    private readonly string eventEndpoint;
    private readonly CancellationTokenSource cancellation = new();
    private readonly object publishLock = new();
    private readonly LiveSplitState state;
    private readonly Timer timerSnapshotTimer;
    private readonly Thread requestThread;
    private ResponseSocket? responder;
    private PublisherSocket? publisher;
    private ulong sessionId;
    private long eventSequence;
    private long stateRevision;

    public BridgeRuntime(LiveSplitState state)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        adapter = new LiveSplitAdapter(state);

        rpcEndpoint = GetEndpoint("LIVESPLIT_BRIDGE_RPC_ENDPOINT", "tcp://127.0.0.1:54000");
        eventEndpoint = GetEndpoint("LIVESPLIT_BRIDGE_EVENT_ENDPOINT", "tcp://127.0.0.1:54001");
        sessionId = GenerateSessionId();
        eventSequence = 0;
        stateRevision = 1;

        AttachStateEvents();
        StartTransport();

        timerSnapshotTimer = new Timer(
            _ => PublishPeriodicSnapshot(),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));

        requestThread = new Thread(RequestLoop)
        {
            IsBackground = true,
            Name = "LiveSplit.Bridge.RequestLoop"
        };

        requestThread.Start();
    }

    public void Dispose()
    {
        cancellation.Cancel();
        timerSnapshotTimer.Dispose();

        requestThread.Join(TimeSpan.FromSeconds(2));

        responder?.Close();
        responder?.Dispose();
        publisher?.Close();
        publisher?.Dispose();
        NetMQConfig.Cleanup(true);
    }

    private void AttachStateEvents()
    {
        state.OnStart += (_, _) => PublishTimerEvent(BridgeEventType.EventTimerStarted, "Timer started");
        state.OnSplit += (_, _) => PublishTimerEvent(BridgeEventType.EventTimerSplit, "Split");
        state.OnSkipSplit += (_, _) => PublishTimerEvent(BridgeEventType.EventTimerSkipped, "Skip split");
        state.OnUndoSplit += (_, _) => PublishTimerEvent(BridgeEventType.EventTimerUndo, "Undo split");
        state.OnReset += (_, _) => PublishTimerEvent(BridgeEventType.EventTimerReset, "Reset");
        state.OnPause += (_, _) => PublishTimerEvent(BridgeEventType.EventTimerPaused, "Timer paused");
        state.OnResume += (_, _) => PublishTimerEvent(BridgeEventType.EventTimerResumed, "Timer resumed");
        state.RunManuallyModified += (_, _) => PublishTimerEvent(BridgeEventType.EventRunChanged, "Run changed");
    }

    private void StartTransport()
    {
        try
        {
            responder = new ResponseSocket();
            responder.Bind(rpcEndpoint);
            publisher = new PublisherSocket();
            publisher.Bind(eventEndpoint);

            Debug.WriteLine($"[LiveSplit.Bridge] RPC endpoint bound to {rpcEndpoint}");
            Debug.WriteLine($"[LiveSplit.Bridge] Event endpoint bound to {eventEndpoint}");
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] Failed to start transport: {exception.Message}");
            throw;
        }
    }

    private void RequestLoop()
    {
        if (responder == null)
        {
            return;
        }

        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                if (!responder.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(100), out var requestData))
                {
                    continue;
                }

                var request = Request.Parser.ParseFrom(requestData);
                var response = HandleRequest(request);
                responder.SendFrame(response.ToByteArray());
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[LiveSplit.Bridge] Request loop error: {exception.Message}");
            }
        }
    }

    private Response HandleRequest(Request request)
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
                        Snapshot = adapter.BuildSnapshot((ulong)stateRevision, sessionId, (ulong)eventSequence)
                    }
                };
            }

            if (request.GetSnapshot != null)
            {
                return new Response
                {
                    ProtocolVersion = ProtocolVersion,
                    RequestId = request.RequestId,
                    GetSnapshot = new GetSnapshotResponse
                    {
                        Snapshot = adapter.BuildSnapshot((ulong)stateRevision, sessionId, (ulong)eventSequence)
                    }
                };
            }

            if (request.TimerOperation != null)
            {
                var result = adapter.ExecuteTimerOperation(request.TimerOperation.Operation);
                if (result.Success)
                {
                    IncrementStateRevision();
                    result.Snapshot = adapter.BuildSnapshot((ulong)stateRevision, sessionId, (ulong)eventSequence);
                }

                return new Response
                {
                    ProtocolVersion = ProtocolVersion,
                    RequestId = request.RequestId,
                    Operation = result
                };
            }

            if (request.GameTimeOperation != null)
            {
                var result = adapter.ExecuteGameTimeOperation(
                    request.GameTimeOperation.Operation,
                    request.GameTimeOperation.HasTicks ? (long?)request.GameTimeOperation.Ticks : null);

                if (result.Success)
                {
                    IncrementStateRevision();
                    result.Snapshot = adapter.BuildSnapshot((ulong)stateRevision, sessionId, (ulong)eventSequence);
                    PublishGameTimeEvent(request.GameTimeOperation.Operation);
                }

                return new Response
                {
                    ProtocolVersion = ProtocolVersion,
                    RequestId = request.RequestId,
                    Operation = result
                };
            }

            return MakeErrorResponse(request, 101, "Unknown request type.");
        }
        catch (Exception exception)
        {
            return MakeErrorResponse(request, 102, exception.Message);
        }
    }

    private void PublishTimerEvent(BridgeEventType type, string description)
    {
        PublishEvent(type, description);
    }

    private void PublishPeriodicSnapshot()
    {
        PublishEvent(BridgeEventType.EventStateSnapshot, "Periodic snapshot");
    }

    private void PublishGameTimeEvent(GameTimeOperationType operation)
    {
        var eventType = operation switch
        {
            GameTimeOperationType.Initialize => BridgeEventType.EventGameTimeInitialized,
            GameTimeOperationType.Set => BridgeEventType.EventGameTimeSet,
            GameTimeOperationType.GameTimePause => BridgeEventType.EventGameTimePaused,
            GameTimeOperationType.GameTimeResume => BridgeEventType.EventGameTimeResumed,
            _ => BridgeEventType.EventStateSnapshot,
        };

        var description = operation switch
        {
            GameTimeOperationType.Initialize => "Game time initialized",
            GameTimeOperationType.Set => "Game time set",
            GameTimeOperationType.GameTimePause => "Game time paused",
            GameTimeOperationType.GameTimeResume => "Game time resumed",
            _ => "Game time operation completed",
        };

        PublishEvent(eventType, description);
    }

    private void PublishEvent(BridgeEventType type, string description)
    {
        if (publisher == null)
        {
            return;
        }

        var sequence = IncrementEventSequence();
        var snapshot = adapter.BuildSnapshot((ulong)stateRevision, sessionId, sequence);
        var bridgeEvent = new BridgeEvent
        {
            SessionId = sessionId,
            EventSequence = sequence,
            Type = type,
            Snapshot = snapshot,
            Description = description
        };

        lock (publishLock)
        {
            try
            {
                publisher.SendFrame(bridgeEvent.ToByteArray());
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[LiveSplit.Bridge] Event publish failed: {exception.Message}");
            }
        }
    }

    private ulong IncrementEventSequence()
    {
        return (ulong)Interlocked.Increment(ref eventSequence);
    }

    private void IncrementStateRevision()
    {
        Interlocked.Increment(ref stateRevision);
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
        rng.GetBytes(buffer);
        return BitConverter.ToUInt64(buffer, 0);
    }

    private static string GetEndpoint(string name, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }
}
