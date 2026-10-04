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

    // Run / Attempt content is comparatively expensive to capture, so it is checked on
    // their dedicated events and by a low frequency fallback rather than every frame.
    internal static readonly TimeSpan ContentFallbackInterval = TimeSpan.FromMilliseconds(500);

    private static readonly long ContentFallbackIntervalTicks =
        ContentFallbackInterval.Ticks * System.Diagnostics.Stopwatch.Frequency / TimeSpan.TicksPerSecond;

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
    private RunRevisionState observedRunState;
    private AttemptRevisionState observedAttemptState;
    private RuntimeRevisionState observedRuntimeState;
    private long lastContentFallbackTimestamp;
    private int disposed;

    public BridgeRuntime(LiveSplitState state, int webSocketPort)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        adapter = new LiveSplitAdapter(state);
        observedGameTimeState = adapter.CaptureGameTimeRevisionState();
        observedRunState = adapter.CaptureRunRevisionState();
        observedAttemptState = adapter.CaptureAttemptRevisionState();
        observedRuntimeState = adapter.CaptureRuntimeRevisionState();

        var port = GetPort("LIVESPLIT_BRIDGE_WEBSOCKET_PORT", webSocketPort);
        sessionId = GenerateSessionId();
        stateRevision = 1;
        runRevision = 1;
        attemptRevision = 1;
        runtimeRevision = 1;
        // Allow the first fallback check immediately; subsequent checks are throttled.
        lastContentFallbackTimestamp = 0;

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
                        Run = BuildCurrentRunState()
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
                        Attempt = BuildCurrentAttemptState()
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
                        RuntimeState = BuildCurrentRuntimeState()
                    }
                };
            }

            if (request.TimerOperation != null)
            {
                var result = adapter.ExecuteTimerOperation(request.TimerOperation.Operation);
                if (result.Success)
                {
                    // Run content can change after the operation's own event has been raised
                    // (for example Reset updates PB / Best Segments in FixSplits afterwards).
                    DetectRunAndAttemptChanges();
                    DetectRuntimeChange();

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
                    DetectRunAndAttemptChanges();
                    DetectRuntimeChange();

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
        catch (Exception exception)
        {
            return MakeErrorResponse(request, 102, exception.Message);
        }
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

    private RunState BuildCurrentRunState()
    {
        return adapter.BuildRunState(ReadRunRevision(), sessionId);
    }

    private AttemptState BuildCurrentAttemptState()
    {
        return adapter.BuildAttemptState(ReadAttemptRevision(), sessionId);
    }

    private RuntimeState BuildCurrentRuntimeState()
    {
        return adapter.BuildRuntimeState(ReadRuntimeRevision(), sessionId);
    }

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

    /// <summary>
    /// High frequency observation invoked on every LiveSplit update. Only lightweight
    /// state (Game Time and RuntimeState) is captured here. Run / Attempt content is
    /// handled by <see cref="ObserveContentState"/> on a low frequency fallback.
    /// </summary>
    internal void ObserveExternalState()
    {
        DetectGameTimeChange();
        DetectRuntimeChange();
    }

    /// <summary>
    /// Low frequency fallback that catches Run / Attempt changes which did not flow
    /// through a dedicated LiveSplit event.
    /// </summary>
    internal void ObserveContentState()
    {
        if (!TryEnterContentFallbackWindow())
        {
            return;
        }

        DetectRunAndAttemptChanges();
    }

    private void DetectRuntimeChange()
    {
        if (DetectRuntimeChangeAndReport())
        {
            PublishEvent(BridgeEventType.EventRuntimeChanged);
        }
    }

    private void DetectRunAndAttemptChanges()
    {
        var changes = ApplyRunAndAttemptRevisionChanges();

        if (changes.Run)
        {
            PublishEvent(BridgeEventType.EventRunChanged);
        }
    }

    private bool TryEnterContentFallbackWindow()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref lastContentFallbackTimestamp);
        if (now - previous < ContentFallbackIntervalTicks)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref lastContentFallbackTimestamp, now, previous) == previous;
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

    private readonly struct ContentChanges
    {
        public ContentChanges(bool run, bool attempt, bool runtime)
        {
            Run = run;
            Attempt = attempt;
            Runtime = runtime;
        }

        public bool Run { get; }
        public bool Attempt { get; }
        public bool Runtime { get; }
    }

    private ContentChanges ApplyRunAndAttemptRevisionChanges()
    {
        var run = adapter.CaptureRunRevisionState();
        var attempt = adapter.CaptureAttemptRevisionState();

        bool runChanged;
        bool attemptChanged;

        lock (observedStateLock)
        {
            runChanged = !observedRunState.Equals(run);
            attemptChanged = !observedAttemptState.Equals(attempt);

            observedRunState = run;
            observedAttemptState = attempt;
        }

        if (attemptChanged)
        {
            IncrementAttemptRevision();
        }

        if (runChanged)
        {
            IncrementRunRevision();
        }

        return new ContentChanges(runChanged, attemptChanged, false);
    }

    private void PublishStateChangeEvent(BridgeEventType type)
    {
        RecordCurrentGameTimeState();

        // Refresh attempt / run / runtime revisions before publishing so the event's
        // TimerState carries the updated revision values. Run / runtime changes are
        // published as their own events after the primary (timer / game time) event.
        var contentChanges = ApplyRunAndAttemptRevisionChanges();
        var runtimeChanged = DetectRuntimeChangeAndReport();

        IncrementStateRevision();
        PublishEvent(type);

        if (contentChanges.Run)
        {
            PublishEvent(BridgeEventType.EventRunChanged);
        }

        if (runtimeChanged)
        {
            PublishEvent(BridgeEventType.EventRuntimeChanged);
        }
    }

    private bool DetectRuntimeChangeAndReport()
    {
        var current = adapter.CaptureRuntimeRevisionState();

        lock (observedStateLock)
        {
            if (observedRuntimeState.Equals(current))
            {
                return false;
            }

            observedRuntimeState = current;
        }

        IncrementRuntimeRevision();
        return true;
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

    private void IncrementRunRevision()
    {
        Interlocked.Increment(ref runRevision);
    }

    private ulong ReadAttemptRevision()
    {
        return unchecked((ulong)Interlocked.Read(ref attemptRevision));
    }

    private void IncrementAttemptRevision()
    {
        Interlocked.Increment(ref attemptRevision);
    }

    private ulong ReadRuntimeRevision()
    {
        return unchecked((ulong)Interlocked.Read(ref runtimeRevision));
    }

    private void IncrementRuntimeRevision()
    {
        Interlocked.Increment(ref runtimeRevision);
    }

    private void AdapterGameTimeChanged(GameTimeOperationType operation)
    {
        RecordCurrentGameTimeState();
        PublishGameTimeEvent(operation);
    }

    private void StateOnStart(object sender, EventArgs args)
    {
        PublishStateChangeEvent(BridgeEventType.EventTimerStarted);
    }

    private void StateOnSplit(object sender, EventArgs args)
    {
        PublishStateChangeEvent(BridgeEventType.EventTimerSplit);
    }

    private void StateOnSkipSplit(object sender, EventArgs args)
    {
        PublishStateChangeEvent(BridgeEventType.EventTimerSkipped);
    }

    private void StateOnUndoSplit(object sender, EventArgs args)
    {
        PublishStateChangeEvent(BridgeEventType.EventTimerUndo);
    }

    private void StateOnReset(object sender, LiveSplit.Model.TimerPhase previousPhase)
    {
        // ResetSplits has already cleared split times at this point, but LiveSplit runs
        // FixSplits (which can rewrite PB / Best Segments) after this event. The run
        // change is picked up by the post-operation detection in HandleRequest or by
        // ObserveExternalState.
        PublishStateChangeEvent(BridgeEventType.EventTimerReset);
    }

    private void StateOnPause(object sender, EventArgs args)
    {
        PublishStateChangeEvent(BridgeEventType.EventTimerPaused);
    }

    private void StateOnResume(object sender, EventArgs args)
    {
        PublishStateChangeEvent(BridgeEventType.EventTimerResumed);
    }

    private void StateRunManuallyModified(object sender, EventArgs args)
    {
        RecordCurrentGameTimeState();

        // Run content determines run_revision; runtime custom variables are handled by
        // the high frequency runtime observation. No state_revision is bumped for run
        // only changes.
        DetectRunAndAttemptChanges();
    }

    private void StateComparisonRenamed(object sender, EventArgs args)
    {
        RecordCurrentGameTimeState();
        DetectRunAndAttemptChanges();
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
