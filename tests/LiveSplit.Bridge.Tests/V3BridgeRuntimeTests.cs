using LiveSplit.Bridge.Protocol.V3;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using ModelTimerPhase = LiveSplit.Model.TimerPhase;
using ProtocolTimerPhase = LiveSplit.Bridge.Protocol.V3.TimerPhase;

namespace LiveSplit.Bridge.Tests;

public class BridgeRuntimeTests
{
    [Fact]
    public void QueryResponsesUseStableV3EnvelopeAndCurrentTimerState()
    {
        var state = CreateState(out _);
        state.CurrentPhase = ModelTimerPhase.Paused;
        state.CurrentSplitIndex = 1;
        state.TimePausedAt = TimeSpan.FromSeconds(4);
        var runtime = new BridgeRuntime(state);

        var first = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            RequestId = 41,
            GetTimerState = new GetTimerStateRequest(),
        });
        var second = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            RequestId = 42,
            GetCompletedCount = new GetCompletedCountRequest(),
        });

        Assert.Equal(3U, first.ProtocolVersion);
        Assert.Equal(41UL, first.RequestId);
        Assert.Equal(runtime.SessionId, first.SessionId);
        Assert.Equal(runtime.SessionId, second.SessionId);
        Assert.NotEqual(0UL, runtime.SessionId);
        Assert.Equal(ProtocolTimerPhase.Paused, first.GetTimerState.TimerState.Phase);
        Assert.Equal(1, first.GetTimerState.TimerState.SplitIndex);
        Assert.Equal(TimeSpan.FromSeconds(4).Ticks, first.GetTimerState.TimerState.RealTimeTicks);
        Assert.Equal(0U, second.GetCompletedCount.CompletedCount.CompletedCount_);
        Assert.DoesNotContain(TimerState.Descriptor.Fields.InDeclarationOrder(), field =>
            field.Name == "session_id" || field.Name.EndsWith("revision", StringComparison.Ordinal));
    }

    [Fact]
    public void GetAttemptBuildsCurrentSegmentsWithoutCompletedCountOrRevisionState()
    {
        var state = CreateState(out var run);
        run[0].SplitTime = new Time(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(8));
        run[0].CustomVariableValues["route"] = "left";
        var runtime = new BridgeRuntime(state);

        var response = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            RequestId = 9,
            GetAttempt = new GetAttemptRequest(),
        });
        var attempt = response.GetAttempt.Attempt;
        Assert.Equal(3, attempt.Segments.Count);
        var segment = attempt.Segments[0];

        Assert.Equal(0U, attempt.AttemptCount);
        Assert.Equal(0U, segment.Index);
        Assert.Equal(TimeSpan.FromSeconds(12).Ticks, segment.SplitTime.RealTimeTicks);
        Assert.Equal(TimeSpan.FromSeconds(8).Ticks, segment.SplitTime.GameTimeTicks);
        Assert.Equal("left", segment.CustomVariables["route"]);
        Assert.DoesNotContain(AttemptState.Descriptor.Fields.InDeclarationOrder(), field =>
            field.Name is "session_id" or "attempt_revision" or "completed_count");
    }

    [Fact]
    public void CompletedCountCountsOnlyAttemptsWithRealTime()
    {
        var state = CreateState(out var run);
        run.AttemptHistory.Add(new Attempt(1, new Time(TimeSpan.FromSeconds(5), null), null, null, null));
        run.AttemptHistory.Add(new Attempt(2, new Time(null, TimeSpan.FromSeconds(4)), null, null, null));
        run.AttemptHistory.Add(new Attempt(3, new Time(TimeSpan.FromSeconds(8), null), null, null, null));
        var runtime = new BridgeRuntime(state);

        var response = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            GetCompletedCount = new GetCompletedCountRequest(),
        });

        Assert.Equal(2U, response.GetCompletedCount.CompletedCount.CompletedCount_);
    }

    [Fact]
    public void TimerOperationsDelegateToTimerModelAndReturnEmptyOperationResponse()
    {
        var state = CreateState(out _);
        var runtime = new BridgeRuntime(state);

        AssertOperationSuccess(runtime, TimerOperationType.TimerStart);
        Assert.Equal(ModelTimerPhase.Running, state.CurrentPhase);
        Assert.Equal(1, state.Run.AttemptCount);

        AssertOperationSuccess(runtime, TimerOperationType.TimerSplit);
        Assert.Equal(1, state.CurrentSplitIndex);
        AssertOperationSuccess(runtime, TimerOperationType.TimerSkip);
        Assert.Equal(2, state.CurrentSplitIndex);
        AssertOperationSuccess(runtime, TimerOperationType.TimerUndo);
        Assert.Equal(1, state.CurrentSplitIndex);
        AssertOperationSuccess(runtime, TimerOperationType.TimerReset);
        Assert.Equal(ModelTimerPhase.NotRunning, state.CurrentPhase);

        Assert.Empty(OperationResponse.Descriptor.Fields.InDeclarationOrder());
    }

    [Theory]
    [InlineData(TimerOperationType.TimerPause)]
    [InlineData(TimerOperationType.TimerResume)]
    public void PauseAndResumeDelegateWithoutRuntimePhaseGuard(TimerOperationType operation)
    {
        var state = CreateState(out _);
        var runtime = new BridgeRuntime(state);

        AssertOperationSuccess(runtime, operation);

        // LiveSplit's TimerModel.Pause starts a timer when called while not running.
        Assert.Equal(ModelTimerPhase.Running, state.CurrentPhase);
    }

    [Fact]
    public void GameTimeOperationsAreAppliedWithoutChangedGuards()
    {
        var state = CreateState(out _);
        var runtime = new BridgeRuntime(state);

        AssertGameTimeSuccess(runtime, GameTimeOperationType.Initialize);
        AssertGameTimeSuccess(runtime, GameTimeOperationType.Initialize);
        Assert.True(state.IsGameTimeInitialized);

        var ticks = TimeSpan.FromSeconds(3).Ticks;
        AssertGameTimeSuccess(runtime, GameTimeOperationType.Set, ticks);
        AssertGameTimeSuccess(runtime, GameTimeOperationType.Set, ticks);
        Assert.Equal(TimeSpan.FromSeconds(3), state.CurrentTime.GameTime);

        AssertGameTimeSuccess(runtime, GameTimeOperationType.GameTimePause);
        AssertGameTimeSuccess(runtime, GameTimeOperationType.GameTimePause);
        Assert.True(state.IsGameTimePaused);
        AssertGameTimeSuccess(runtime, GameTimeOperationType.GameTimeResume);
        AssertGameTimeSuccess(runtime, GameTimeOperationType.GameTimeResume);
        Assert.False(state.IsGameTimePaused);
    }

    [Fact]
    public void SetGameTimeWithoutTicksReturnsInvalidArgument()
    {
        var runtime = new BridgeRuntime(CreateState(out _));

        var response = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            RequestId = 3,
            GameTimeOperation = new GameTimeOperationRequest { Operation = GameTimeOperationType.Set },
        });

        Assert.Equal(BridgeErrorCode.InvalidArgument, response.Error.Code);
        AssertEnvelope(response, runtime, 3);
    }

    [Fact]
    public void UnspecifiedControlOperationsReturnInvalidArgument()
    {
        var runtime = new BridgeRuntime(CreateState(out _));

        var timer = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerOperationUnspecified },
        });
        var gameTime = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            GameTimeOperation = new GameTimeOperationRequest { Operation = GameTimeOperationType.GameTimeOperationUnspecified },
        });

        Assert.Equal(BridgeErrorCode.InvalidArgument, timer.Error.Code);
        Assert.Equal(BridgeErrorCode.InvalidArgument, gameTime.Error.Code);
    }

    [Fact]
    public void InvalidVersionAndMissingBodyReturnProtocolErrors()
    {
        var runtime = new BridgeRuntime(CreateState(out _));

        var unsupported = runtime.HandleRequest(new Request { ProtocolVersion = 2, RequestId = 7 });
        var invalid = runtime.HandleRequest(new Request { ProtocolVersion = 3, RequestId = 8 });

        Assert.Equal(BridgeErrorCode.UnsupportedProtocolVersion, unsupported.Error.Code);
        Assert.Equal(BridgeErrorCode.InvalidRequest, invalid.Error.Code);
        AssertEnvelope(unsupported, runtime, 7);
        AssertEnvelope(invalid, runtime, 8);
    }

    [Fact]
    public async Task QueryDoesNotWaitForControlAndControlsAreSerialized()
    {
        var adapter = new BlockingAdapter();
        var runtime = new BridgeRuntime(adapter);
        var firstControl = Task.Run(() => SendTimerOperation(runtime));
        Assert.True(await Task.Run(() => adapter.FirstControlEntered.Wait(TimeSpan.FromSeconds(3))));

        var query = Task.Run(() => runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            GetTimerState = new GetTimerStateRequest(),
        }));
        Assert.Same(query, await Task.WhenAny(query, Task.Delay(TimeSpan.FromSeconds(1))));
        Assert.NotNull((await query).GetTimerState);

        var secondControl = Task.Run(() => SendTimerOperation(runtime));
        Assert.False(await Task.Run(() => adapter.SecondControlEntered.Wait(TimeSpan.FromMilliseconds(150))));

        adapter.ReleaseFirstControl.Set();
        var controls = Task.WhenAll(firstControl, secondControl);
        Assert.Same(controls, await Task.WhenAny(controls, Task.Delay(TimeSpan.FromSeconds(3))));
        await controls;
        Assert.True(adapter.SecondControlEntered.IsSet);
        Assert.Equal(2, adapter.ControlCallCount);
        Assert.Equal(1, adapter.MaximumConcurrentControls);
    }

    [Fact]
    public void LiveSplitFailuresUseOperationFailedAndQueryFailuresUseInternalError()
    {
        var adapter = new FailingAdapter();
        var runtime = new BridgeRuntime(adapter);

        var control = SendTimerOperation(runtime);
        var query = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            GetTimerState = new GetTimerStateRequest(),
        });

        Assert.Equal(BridgeErrorCode.OperationFailed, control.Error.Code);
        Assert.Equal(BridgeErrorCode.InternalError, query.Error.Code);
    }

    [Fact]
    public void RecognizedButDeferredQueriesReturnAnEnvelopeError()
    {
        var runtime = new BridgeRuntime(CreateState(out _));

        var run = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            GetRun = new GetRunRequest(),
        });
        var context = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            GetContextState = new GetContextStateRequest(),
        });

        Assert.Equal(BridgeErrorCode.OperationFailed, run.Error.Code);
        Assert.Equal(BridgeErrorCode.OperationFailed, context.Error.Code);
    }

    private static LiveSplitState CreateState(out Run run)
    {
        run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        run.Add(new Segment("Three"));
        return TestLiveSplitState.Create(run);
    }

    private static void AssertOperationSuccess(BridgeRuntime runtime, TimerOperationType operation)
    {
        var response = SendTimerOperation(runtime, operation);
        Assert.NotNull(response.Operation);
        AssertEnvelope(response, runtime);
    }

    private static Response SendTimerOperation(BridgeRuntime runtime, TimerOperationType operation = TimerOperationType.TimerStart)
    {
        return runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            RequestId = 17,
            TimerOperation = new TimerOperationRequest { Operation = operation },
        });
    }

    private static void AssertGameTimeSuccess(BridgeRuntime runtime, GameTimeOperationType operation, long? ticks = null)
    {
        var request = new GameTimeOperationRequest { Operation = operation };
        if (ticks.HasValue)
        {
            request.Ticks = ticks.Value;
        }

        var response = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            GameTimeOperation = request,
        });

        Assert.NotNull(response.Operation);
        AssertEnvelope(response, runtime);
    }

    private static void AssertEnvelope(Response response, BridgeRuntime runtime, ulong? requestId = null)
    {
        Assert.Equal(3U, response.ProtocolVersion);
        Assert.Equal(runtime.SessionId, response.SessionId);
        if (requestId.HasValue)
        {
            Assert.Equal(requestId.Value, response.RequestId);
        }
    }

    private sealed class BlockingAdapter : ILiveSplitAdapter
    {
        private int activeControls;
        private int maximumConcurrentControls;
        private int controlCallCount;

        public ManualResetEventSlim FirstControlEntered { get; } = new();
        public ManualResetEventSlim SecondControlEntered { get; } = new();
        public ManualResetEventSlim ReleaseFirstControl { get; } = new();
        public int ControlCallCount => Volatile.Read(ref controlCallCount);
        public int MaximumConcurrentControls => Volatile.Read(ref maximumConcurrentControls);

        public TimerState GetTimerState() => new();
        public AttemptState GetAttempt() => new();
        public CompletedCount GetCompletedCount() => new();
        public void ExecuteGameTimeOperation(GameTimeOperationType operation, long? ticks) { }

        public void ExecuteTimerOperation(TimerOperationType operation)
        {
            var call = Interlocked.Increment(ref controlCallCount);
            if (call > 1)
            {
                SecondControlEntered.Set();
            }

            var active = Interlocked.Increment(ref activeControls);
            int observed;
            do
            {
                observed = Volatile.Read(ref maximumConcurrentControls);
                if (active <= observed)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref maximumConcurrentControls, active, observed) != observed);

            if (call == 1)
            {
                FirstControlEntered.Set();
                ReleaseFirstControl.Wait(TimeSpan.FromSeconds(5));
            }

            Interlocked.Decrement(ref activeControls);
        }
    }

    private sealed class FailingAdapter : ILiveSplitAdapter
    {
        public TimerState GetTimerState() => throw new InvalidOperationException("query failure");
        public AttemptState GetAttempt() => new();
        public CompletedCount GetCompletedCount() => new();
        public void ExecuteTimerOperation(TimerOperationType operation) => throw new InvalidOperationException("control failure");
        public void ExecuteGameTimeOperation(GameTimeOperationType operation, long? ticks) => throw new InvalidOperationException("control failure");
    }
}

