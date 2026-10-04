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
    private readonly object sequenceLock = new();
    private readonly object observedStateLock = new();
    private readonly LiveSplitState state;
    private readonly ulong sessionId;
    private long stateRevision;
    private long runRevision;
    private long attemptRevision;
    private long runtimeRevision;
    private GameTimeRevisionState observedGameTimeState;
    private RuntimeRevisionState observedRuntimeState;
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

    internal Response HandleRequest(Request request)
    {
        if (request.ProtocolVersion != ProtocolVersion)
        {
            return MakeErrorResponse(request, 100, $"Unsupported protocol version {request.ProtocolVersion}");
        }

        try
        {
            return adapter.InvokeOnUiThread(() => HandleRequestOnUiThread(request));
        }
        catch (Exception exception)
        {
            return MakeErrorResponse(request, 102, exception.Message);
        }
    }

    private Response HandleRequestOnUiThread(Request request)
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
                    Run = adapter.BuildRunState(ReadRunRevision(), sessionId)
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
                    Attempt = adapter.BuildAttemptState(ReadAttemptRevision(), sessionId)
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
                    RuntimeState = adapter.BuildRuntimeState(ReadRuntimeRevision(), sessionId)
                }
            };
        }

        if (request.TimerOperation != null)
        {
            var result = adapter.ExecuteTimerOperation(request.TimerOperation.Operation);
            if (result.Success)
            {
                result.TimerState = BuildCurrentTimerState();
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
            var execution = adapter.ExecuteGameTimeOperation(
                request.GameTimeOperation.Operation,
                request.GameTimeOperation.HasTicks ? (long?)request.GameTimeOperation.Ticks : null);

            if (execution.Response.Success)
            {
                execution.Response.TimerState = BuildCurrentTimerState();
            }

            return new Response
            {
                ProtocolVersion = ProtocolVersion,
                RequestId = request.RequestId,
                Operation = execution.Response
            };
        }

        return MakeErrorResponse(request, 101, "Unknown request type.");
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

    private bool UpdateObservedRuntimeState(RuntimeRevisionState captured, out ulong revision)
    {
        lock (observedStateLock)
        {
            if (observedRuntimeState.Equals(captured))
            {
                revision = ReadRuntimeRevision();
                return false;
            }

            observedRuntimeState = captured;
            revision = unchecked((ulong)Interlocked.Increment(ref runtimeRevision));
            return true;
        }
    }

    internal LiveSplitAdapter Adapter => adapter;

    internal ulong StateRevision => ReadStateRevision();

    internal ulong RunRevision => ReadRunRevision();

    internal ulong AttemptRevision => ReadAttemptRevision();

    internal ulong RuntimeRevision => ReadRuntimeRevision();

    private void PublishGameTimeEvent(GameTimeOperationType operation)
    {
        var eventType = operation switch
        {
            GameTimeOperationType.Initialize => BridgeEventType.EventGameTimeInitialized,
            GameTimeOperationType.Set => BridgeEventType.EventGameTimeSet,
            GameTimeOperationType.GameTimePause => BridgeEventType.EventGameTimePaused,
            GameTimeOperationType.GameTimeResume => BridgeEventType.EventGameTimeResumed,
            _ => BridgeEventType.EventGameTimeSet,
        };

        PublishStateChangeEvent(eventType);
    }

    // Only fields without comprehensive LiveSplit events need lightweight observation.
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

        PublishStateChangeEvent(eventType);
    }

    private void RecordCurrentGameTimeState()
    {
        var current = adapter.CaptureGameTimeRevisionState();
        lock (observedStateLock)
        {
            observedGameTimeState = current;
        }
    }

    private bool SyncRuntimeState()
    {
        return UpdateObservedRuntimeState(adapter.CaptureRuntimeRevisionState(), out _);
    }

    private void SyncRuntimeStateAndPublish()
    {
        if (SyncRuntimeState())
        {
            PublishEvent(BridgeEventType.EventRuntimeChanged);
        }
    }

    private void PublishStateChangeEvent(BridgeEventType type)
    {
        RecordCurrentGameTimeState();

        IncrementStateRevision();
        PublishEvent(type);
    }

    private void PublishAttemptEvent(BridgeEventType type)
    {
        Interlocked.Increment(ref attemptRevision);
        PublishStateChangeEvent(type);
    }

    private void PublishEvent(BridgeEventType type)
    {
        var timerState = BuildCurrentTimerState();

        lock (sequenceLock)
        {
            var sequence = eventSequence.Begin();

            var bridgeEvent = new BridgeEvent
            {
                SessionId = sessionId,
                EventSequence = sequence,
                Type = type,
                TimerState = timerState
            };

            transport.Publish(bridgeEvent);
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

    private void IncrementStateRevision()
    {
        Interlocked.Increment(ref stateRevision);
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

    private void AdapterGameTimeChanged(GameTimeOperationType operation)
    {
        PublishGameTimeEvent(operation);
    }

    private void StateOnStart(object sender, EventArgs args)
    {
        PublishAttemptEvent(BridgeEventType.EventTimerStarted);
    }

    private void StateOnSplit(object sender, EventArgs args)
    {
        PublishAttemptEvent(BridgeEventType.EventTimerSplit);
    }

    private void StateOnSkipSplit(object sender, EventArgs args)
    {
        PublishAttemptEvent(BridgeEventType.EventTimerSkipped);
    }

    private void StateOnUndoSplit(object sender, EventArgs args)
    {
        PublishAttemptEvent(BridgeEventType.EventTimerUndo);
    }

    private void StateOnReset(object sender, LiveSplit.Model.TimerPhase previousPhase)
    {
        PublishAttemptEvent(BridgeEventType.EventTimerReset);
        // OnReset precedes FixSplits. Always publish the run generation on the next
        // UI turn, for both RPC resets and resets initiated by LiveSplit itself.
        state.Form.BeginInvoke((Action)(() =>
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                PublishRunChange();
            }
        }));
    }

    private void StateOnPause(object sender, EventArgs args)
    {
        PublishStateChangeEvent(BridgeEventType.EventTimerPaused);
    }

    private void StateOnResume(object sender, EventArgs args)
    {
        PublishStateChangeEvent(BridgeEventType.EventTimerResumed);
    }

    private void PublishRunChange()
    {
        Interlocked.Increment(ref runRevision);
        PublishEvent(BridgeEventType.EventRunChanged);
    }

    private void StateRunManuallyModified(object sender, EventArgs args)
    {
        PublishRunChange();
    }

    private void StateComparisonRenamed(object sender, EventArgs args)
    {
        // RunEdited raises RunManuallyModified after ComparisonRenamed. Defer the
        // runtime check to preserve RUN_CHANGED, RUNTIME_CHANGED ordering.
        state.Form.BeginInvoke((Action)(() =>
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                SyncRuntimeStateAndPublish();
            }
        }));
    }

    private void StateComparisonSwitched(object sender, EventArgs args)
    {
        SyncRuntimeStateAndPublish();
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
