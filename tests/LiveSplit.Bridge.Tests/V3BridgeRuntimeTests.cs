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
        var firstControl = Task.Factory.StartNew(
            () => SendTimerOperation(runtime),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(adapter.FirstControlEntered.Wait(TimeSpan.FromSeconds(5)));

        var query = Task.Factory.StartNew(
            () => runtime.HandleRequest(new Request
            {
                ProtocolVersion = 3,
                GetTimerState = new GetTimerStateRequest(),
            }),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.Same(query, await Task.WhenAny(query, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.NotNull((await query).GetTimerState);

        var secondControl = Task.Factory.StartNew(
            () => SendTimerOperation(runtime),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.False(adapter.SecondControlEntered.Wait(TimeSpan.FromMilliseconds(150)));

        adapter.ReleaseFirstControl.Set();
        var controls = Task.WhenAll(firstControl, secondControl);
        Assert.Same(controls, await Task.WhenAny(controls, Task.Delay(TimeSpan.FromSeconds(5))));
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
    public void RunAndContextQueriesReturnInitialSnapshots()
    {
        var state = CreateState(out var initialRun);
        initialRun.GameName = "Game";
        initialRun.CategoryName = "Any%";
        initialRun[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(9), null);
        initialRun.Metadata.CustomVariables["route"] = new LiveSplit.Model.CustomVariable("left", false);
        state.CurrentComparison = "Personal Best";
        var runtime = new BridgeRuntime(state);

        var runResponse = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            GetRun = new GetRunRequest(),
        });
        var context = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            GetContextState = new GetContextStateRequest(),
        });

        Assert.Equal("Game", runResponse.GetRun.Run.GameName);
        Assert.Equal("Any%", runResponse.GetRun.Run.CategoryName);
        Assert.Equal(3, runResponse.GetRun.Run.Segments.Count);
        Assert.Equal(TimeSpan.FromSeconds(9).Ticks, runResponse.GetRun.Run.Segments[0].BestSegmentTime.RealTimeTicks);
        Assert.Equal("Personal Best", context.GetContextState.ContextState.CurrentComparison);
        Assert.Equal("left", context.GetContextState.ContextState.CustomVariables["route"]);
        Assert.Equal(runtime.SessionId, runResponse.SessionId);
        Assert.Equal(runtime.SessionId, context.SessionId);
    }

    [Fact]
    public void TimingRefreshUpdatesRunIdAndRejectsStructuralMismatchWithoutRebuilding()
    {
        var state = CreateState(out var run);
        run.Metadata.RunID = "before-reset";
        var adapter = new LiveSplitAdapter(state);
        var published = adapter.GetRunState();
        run.Metadata.RunID = null;
        run[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(7), null);

        Assert.True(adapter.TryUpdateRunTimings(published, out var updated));
        Assert.False(updated.Metadata.HasRunId);
        Assert.Equal(TimeSpan.FromSeconds(7).Ticks, updated.Segments[0].BestSegmentTime.RealTimeTicks);

        var malformed = published.Clone();
        malformed.Segments.RemoveAt(malformed.Segments.Count - 1);
        Assert.False(adapter.TryUpdateRunTimings(malformed, out var unchanged));
        Assert.Same(malformed, unchanged);
    }

    [Fact]
    public void CapturedRunIconIsIndependentOfTheLiveSplitImageLifetime()
    {
        var state = CreateState(out var run);
        var source = new System.Drawing.Bitmap(2, 2);
        var segmentSource = new System.Drawing.Bitmap(2, 2);
        source.SetPixel(0, 0, System.Drawing.Color.Magenta);
        run.GameIcon = source;
        run[0].Icon = segmentSource;
        using var candidate = new LiveSplitAdapter(state).CaptureRunState();

        source.Dispose();
        segmentSource.Dispose();
        run.GameIcon = null;
        run[0].Icon = null;
        var completed = candidate.Build();

        Assert.NotNull(completed.GameIcon);
        Assert.Equal(2U, completed.GameIcon.Width);
        Assert.Equal("image/png", completed.GameIcon.MimeType);
        Assert.NotEmpty(completed.GameIcon.Data);
        Assert.NotNull(completed.Segments[0].Icon);
        Assert.Equal(2U, completed.Segments[0].Icon.Width);
    }

    [Fact]
    public async Task OlderSameRunBuildCannotOverwriteANewerEdit()
    {
        var state = CreateState(out var run);
        var adapter = new BlockingRunAdapter(state);
        var port = BridgeTestEndpoints.GetFreePort();
        using var runtime = new BridgeRuntime(adapter, state, port);
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        run.GameName = "Older edit";

        var olderBuild = Task.Factory.StartNew(
            state.CallRunManuallyModified,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(adapter.FirstManualBuildEntered.Wait(TimeSpan.FromSeconds(5)));

        run.GameName = "Newer edit";
        state.CallRunManuallyModified();
        var changed = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        adapter.ReleaseFirstManualBuild.Set();
        await olderBuild;

        var response = runtime.HandleRequest(new Request { ProtocolVersion = 3, GetRun = new GetRunRequest() });
        Assert.Equal(BridgeEventType.EventRunChanged, changed.Type);
        Assert.Equal("Newer edit", response.GetRun.Run.GameName);
    }

    [Fact]
    public async Task OlderRunBuildCannotOverwriteAReplacementRun()
    {
        var state = CreateState(out var originalRun);
        var adapter = new BlockingRunAdapter(state);
        var port = BridgeTestEndpoints.GetFreePort();
        using var runtime = new BridgeRuntime(adapter, state, port);
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        originalRun.GameName = "Run A";

        var buildA = Task.Factory.StartNew(
            state.CallRunManuallyModified,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(adapter.FirstManualBuildEntered.Wait(TimeSpan.FromSeconds(5)));

        var replacement = new Run(new StandardComparisonGeneratorsFactory()) { GameName = "Run B" };
        replacement.Add(new Segment("Replacement"));
        state.Run = replacement;
        state.CallRunManuallyModified();
        var changed = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        adapter.ReleaseFirstManualBuild.Set();
        await buildA;

        var response = runtime.HandleRequest(new Request { ProtocolVersion = 3, GetRun = new GetRunRequest() });
        Assert.Equal(BridgeEventType.EventRunChanged, changed.Type);
        Assert.Equal("Run B", response.GetRun.Run.GameName);
        Assert.Equal("Replacement", response.GetRun.Run.Segments[0].Name);
    }

    [Fact]
    public async Task StaticBuildFinishingAfterResetKeepsResetTimingAndRunId()
    {
        var state = CreateState(out var run);
        run.Metadata.RunID = "before";
        var adapter = new BlockingRunAdapter(state);
        var port = BridgeTestEndpoints.GetFreePort();
        using var runtime = new BridgeRuntime(adapter, state, port);
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        run.GameName = "Edited during build";

        var staticBuild = Task.Factory.StartNew(
            state.CallRunManuallyModified,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(adapter.FirstManualBuildEntered.Wait(TimeSpan.FromSeconds(5)));

        var timer = new TimerModel { CurrentState = state };
        timer.Start();
        Assert.Equal(BridgeEventType.EventTimerStarted,
            BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5))).Type);
        run.Metadata.RunID = null;
        run[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(11), null);
        timer.Reset();
        var reset = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        var resetChanged = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(BridgeEventType.EventTimerReset, reset.Type);
        Assert.Equal(BridgeEventType.EventRunChanged, resetChanged.Type);
        var resetResponse = runtime.HandleRequest(new Request { ProtocolVersion = 3, GetRun = new GetRunRequest() });
        Assert.False(resetResponse.GetRun.Run.Metadata.HasRunId);
        Assert.Equal(TimeSpan.FromSeconds(11).Ticks, resetResponse.GetRun.Run.Segments[0].BestSegmentTime.RealTimeTicks);
        adapter.ReleaseFirstManualBuild.Set();
        await staticBuild;

        var changed = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        var response = runtime.HandleRequest(new Request { ProtocolVersion = 3, GetRun = new GetRunRequest() });
        Assert.Equal(BridgeEventType.EventRunChanged, changed.Type);
        Assert.Equal("Edited during build", response.GetRun.Run.GameName);
        Assert.False(response.GetRun.Run.Metadata.HasRunId);
        Assert.Equal(TimeSpan.FromSeconds(11).Ticks, response.GetRun.Run.Segments[0].BestSegmentTime.RealTimeTicks);
    }

    [Fact]
    public async Task ReplacementRunBuildFinishingAfterResetDoesNotReusePreviousRunTimings()
    {
        var state = CreateState(out var runA);
        runA.GameName = "Run A";
        runA.Metadata.RunID = "run-a-id";
        runA[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(7), null);
        var adapter = new BlockingRunAdapter(state);
        var port = BridgeTestEndpoints.GetFreePort();
        using var runtime = new BridgeRuntime(adapter, state, port);
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));

        var runB = new Run(new StandardComparisonGeneratorsFactory())
        {
            GameName = "Run B",
            CategoryName = "Any%",
        };
        runB.Add(new Segment("Segment 1"));
        runB.Add(new Segment("Segment 2"));
        runB.Add(new Segment("Segment 3"));
        runB.Metadata.RunID = "run-b-before-reset";
        runB[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(19), null);
        state.Run = runB;
        var staticBuild = Task.Factory.StartNew(
            state.CallRunManuallyModified,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(adapter.FirstManualBuildEntered.Wait(TimeSpan.FromSeconds(5)));

        var timer = new TimerModel { CurrentState = state };
        timer.Start();
        Assert.Equal(BridgeEventType.EventTimerStarted,
            BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5))).Type);
        runB.Metadata.RunID = null;
        runB[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(31), null);
        timer.Reset();
        var reset = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(BridgeEventType.EventTimerReset, reset.Type);

        adapter.ReleaseFirstManualBuild.Set();
        await staticBuild;
        var changed = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(BridgeEventType.EventRunChanged, changed.Type);
        var response = runtime.HandleRequest(new Request { ProtocolVersion = 3, GetRun = new GetRunRequest() });
        Assert.Equal("Run B", response.GetRun.Run.GameName);
        Assert.Equal("Any%", response.GetRun.Run.CategoryName);
        Assert.Equal("Segment 1", response.GetRun.Run.Segments[0].Name);
        Assert.False(response.GetRun.Run.Metadata.HasRunId);
        Assert.Equal(TimeSpan.FromSeconds(31).Ticks, response.GetRun.Run.Segments[0].BestSegmentTime.RealTimeTicks);
        Assert.NotEqual(TimeSpan.FromSeconds(7).Ticks, response.GetRun.Run.Segments[0].BestSegmentTime.RealTimeTicks);
    }

    [Fact]
    public async Task ResetTimerStateCaptureFailureOmitsPayloadAndDoesNotFailReset()
    {
        var state = CreateState(out _);
        var port = BridgeTestEndpoints.GetFreePort();
        using var runtime = new BridgeRuntime(new FailingAdapter(), state, port);
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        var timer = new TimerModel { CurrentState = state };

        timer.Start();
        timer.Reset();
        var reset = BridgeEvent.Parser.ParseFrom(await events.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(BridgeEventType.EventTimerReset, reset.Type);
        Assert.Null(reset.TimerState);
        Assert.Equal(LiveSplit.Model.TimerPhase.NotRunning, state.CurrentPhase);
    }

    [Fact]
    public async Task RuntimeDisposeRejectsAnInFlightRunCandidate()
    {
        var state = CreateState(out var run);
        var adapter = new BlockingRunAdapter(state);
        using var runtime = new BridgeRuntime(adapter, state, BridgeTestEndpoints.GetFreePort());
        run.GameName = "Must not publish after dispose";

        var update = Task.Factory.StartNew(
            state.CallRunManuallyModified,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(adapter.FirstManualBuildEntered.Wait(TimeSpan.FromSeconds(5)));
        runtime.Dispose();
        adapter.ReleaseFirstManualBuild.Set();
        await update;

        var response = runtime.HandleRequest(new Request { ProtocolVersion = 3, GetRun = new GetRunRequest() });
        Assert.Equal(string.Empty, response.GetRun.Run.GameName);
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
        public RunState GetRunState() => new();
        public RunStateBuildCandidate CaptureRunState() => new(new RunState(), new System.Collections.Generic.List<System.Drawing.Bitmap?>());
        public bool TryUpdateRunTimings(RunState published, out RunState updated) { updated = published.Clone(); return true; }
        public bool IsTimerOnlyRun() => false;
        public ContextState GetContextState() => new();
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

    private sealed class BlockingRunAdapter : ILiveSplitAdapter
    {
        private readonly LiveSplitState state;
        private int runBuildCount;

        public BlockingRunAdapter(LiveSplitState state) => this.state = state;
        public ManualResetEventSlim FirstManualBuildEntered { get; } = new();
        public ManualResetEventSlim ReleaseFirstManualBuild { get; } = new();
        public TimerState GetTimerState() => new();
        public AttemptState GetAttempt() => new();
        public CompletedCount GetCompletedCount() => new();
        public ContextState GetContextState() => new();
        public void ExecuteTimerOperation(TimerOperationType operation) { }
        public void ExecuteGameTimeOperation(GameTimeOperationType operation, long? ticks) { }
        public RunStateBuildCandidate CaptureRunState()
        {
            var result = BuildRunState();
            if (Interlocked.Increment(ref runBuildCount) == 1)
            {
                FirstManualBuildEntered.Set();
                ReleaseFirstManualBuild.Wait(TimeSpan.FromSeconds(10));
            }
            return new RunStateBuildCandidate(result, new System.Collections.Generic.List<System.Drawing.Bitmap?>());
        }
        public bool TryUpdateRunTimings(RunState published, out RunState updated)
        {
            var run = state.Run;
            if (run == null || published.Segments.Count != run.Count)
            {
                updated = published;
                return false;
            }
            updated = published.Clone();
            if (run.Metadata.RunID == null) updated.Metadata.ClearRunId();
            else updated.Metadata.RunId = run.Metadata.RunID;
            for (var index = 0; index < run.Count; index++)
            {
                var time = run[index].BestSegmentTime;
                updated.Segments[index].BestSegmentTime = new TimeValue();
                if (time.RealTime.HasValue) updated.Segments[index].BestSegmentTime.RealTimeTicks = time.RealTime.Value.Ticks;
                if (time.GameTime.HasValue) updated.Segments[index].BestSegmentTime.GameTimeTicks = time.GameTime.Value.Ticks;
            }
            return true;
        }
        public bool IsTimerOnlyRun() => false;

        public RunState GetRunState() => BuildRunState();

        private RunState BuildRunState()
        {
            var run = state.Run;
            var result = new RunState
            {
                GameName = run.GameName ?? string.Empty,
                CategoryName = run.CategoryName ?? string.Empty,
                Metadata = new LiveSplit.Bridge.Protocol.V3.RunMetadata(),
            };
            if (run.Metadata.RunID != null) result.Metadata.RunId = run.Metadata.RunID;
            for (var index = 0; index < run.Count; index++)
                result.Segments.Add(new SegmentInfo { Index = (uint)index, Name = run[index].Name ?? string.Empty });
            return result;
        }
    }

    private sealed class FailingAdapter : ILiveSplitAdapter
    {
        public TimerState GetTimerState() => throw new InvalidOperationException("query failure");
        public AttemptState GetAttempt() => new();
        public CompletedCount GetCompletedCount() => new();
        public RunState GetRunState() => new();
        public RunStateBuildCandidate CaptureRunState() => new(new RunState(), new System.Collections.Generic.List<System.Drawing.Bitmap?>());
        public bool TryUpdateRunTimings(RunState published, out RunState updated) { updated = published.Clone(); return true; }
        public bool IsTimerOnlyRun() => false;
        public ContextState GetContextState() => new();
        public void ExecuteTimerOperation(TimerOperationType operation) => throw new InvalidOperationException("control failure");
        public void ExecuteGameTimeOperation(GameTimeOperationType operation, long? ticks) => throw new InvalidOperationException("control failure");
    }
}

