#nullable enable
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using Json = LiveSplit.Bridge.Json;

namespace LiveSplit.Bridge.Tests;

public sealed class JsonBridgeRuntimeTests
{
    [Fact]
    public void MapsQueriesAndControlResponsesWithSessionAndRequestIds()
    {
        var adapter = new FakeAdapter();
        var sink = new RecordingSink();
        using var runtime = new JsonBridgeRuntime(adapter, sink);

        var timer = Assert.IsType<Json.TimerStateResponse>(runtime.HandleRequest(new Json.GetTimerStateRequest { RequestId = 0 }, false));
        var run = Assert.IsType<Json.RunStateResponse>(runtime.HandleRequest(new Json.GetRunRequest { RequestId = uint.MaxValue }, false));
        var context = Assert.IsType<Json.ContextStateResponse>(runtime.HandleRequest(new Json.GetContextStateRequest { RequestId = 8 }, false));
        var attempt = Assert.IsType<Json.AttemptStateResponse>(runtime.HandleRequest(new Json.GetAttemptRequest { RequestId = 9 }, false));
        var completed = Assert.IsType<Json.CompletedCountResponse>(runtime.HandleRequest(new Json.GetCompletedCountRequest { RequestId = 10 }, false));
        var control = Assert.IsType<Json.ControlSuccessResponse>(runtime.HandleRequest(
            new Json.TimerOperationRequest { RequestId = 11, Params = new Json.TimerStartParams() }, true));

        Assert.Equal(0U, timer.RequestId);
        Assert.Equal(uint.MaxValue, run.RequestId);
        Assert.NotEqual(0U, runtime.SessionId);
        Assert.All(new Json.RpcResponse[] { timer, run, context, attempt, completed, control }, response => Assert.Equal(runtime.SessionId, response.SessionId));
        Assert.Same(runtime.PublishedRun, run.Result);
        Assert.Same(runtime.PublishedContext, context.Result);
        Assert.Equal(1, adapter.TimerOperationCalls);
        Assert.Contains("\"result\":null", JsonSerializer.Serialize<Json.RpcResponse>(control, Json.BridgeJsonOptions.Create()));
    }

    [Fact]
    public void DeniesControlBeforeParamsValidationAndMapsOperationErrors()
    {
        var adapter = new FakeAdapter { ThrowOnTimerOperation = true };
        using var runtime = new JsonBridgeRuntime(adapter, new RecordingSink());

        var denied = Assert.IsType<Json.ErrorResponse>(runtime.HandleRequest(
            new Json.TimerOperationRequest { RequestId = 21, Params = null! }, allowControl: false));
        var failed = Assert.IsType<Json.ErrorResponse>(runtime.HandleRequest(
            new Json.TimerOperationRequest { RequestId = 22, Params = new Json.TimerStartParams() }, allowControl: true));
        var invalid = Assert.IsType<Json.ErrorResponse>(runtime.HandleRequest(
            new Json.GameTimeOperationRequest { RequestId = 23, Params = null! }, allowControl: true));

        Assert.Equal(Json.BridgeErrorCode.PermissionDenied, denied.Error.Code);
        Assert.Equal(Json.BridgeErrorCode.OperationFailed, failed.Error.Code);
        Assert.Equal(Json.BridgeErrorCode.InvalidArgument, invalid.Error.Code);
        Assert.Equal(1, adapter.TimerOperationCalls);
    }

    [Fact]
    public void QueryExceptionReturnsInternalError()
    {
        var adapter = new FakeAdapter { ThrowOnAttempt = true };
        using var runtime = new JsonBridgeRuntime(adapter, new RecordingSink());

        var response = Assert.IsType<Json.ErrorResponse>(runtime.HandleRequest(new Json.GetAttemptRequest { RequestId = 19 }, false));

        Assert.Equal(19U, response.RequestId);
        Assert.Equal(Json.BridgeErrorCode.InternalError, response.Error.Code);
        Assert.Equal(runtime.SessionId, response.SessionId);
    }

    [Fact]
    public void QueryDoesNotWaitForControlOperationGate()
    {
        var adapter = new BlockingAdapter();
        using var runtime = new JsonBridgeRuntime(adapter, new RecordingSink());
        using var controlDone = new ManualResetEventSlim();
        using var queryDone = new ManualResetEventSlim();
        Json.RpcResponse? controlResponse = null;
        Json.RpcResponse? queryResponse = null;
        _ = Task.Factory.StartNew(() =>
        {
            try { controlResponse = runtime.HandleRequest(new Json.TimerOperationRequest { RequestId = 1, Params = new Json.TimerStartParams() }, true); }
            finally { controlDone.Set(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(adapter.ControlEntered.Wait(TimeSpan.FromSeconds(5)));

        _ = Task.Factory.StartNew(() =>
        {
            try { queryResponse = runtime.HandleRequest(new Json.GetTimerStateRequest { RequestId = 2 }, false); }
            finally { queryDone.Set(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(queryDone.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsType<Json.TimerStateResponse>(queryResponse);
        adapter.ReleaseControl.Set();
        Assert.True(controlDone.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsType<Json.ControlSuccessResponse>(controlResponse);
    }

    [Fact]
    public void ControlOperationsAreSerialized()
    {
        var adapter = new SerialAdapter();
        using var runtime = new JsonBridgeRuntime(adapter, new RecordingSink());
        using var firstDone = new ManualResetEventSlim();
        using var secondDone = new ManualResetEventSlim();
        Json.RpcResponse? firstResponse = null;
        Json.RpcResponse? secondResponse = null;
        StartControl(runtime, 1, response => { firstResponse = response; firstDone.Set(); });
        Assert.True(adapter.FirstEntered.Wait(TimeSpan.FromSeconds(5)));
        StartControl(runtime, 2, response => { secondResponse = response; secondDone.Set(); });
        Assert.False(adapter.SecondEntered.Wait(TimeSpan.FromMilliseconds(150)));
        adapter.ReleaseFirst.Set();
        Assert.True(adapter.SecondEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(firstDone.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(secondDone.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsType<Json.ControlSuccessResponse>(firstResponse);
        Assert.IsType<Json.ControlSuccessResponse>(secondResponse);

        static void StartControl(JsonBridgeRuntime runtime, uint id, Action<Json.RpcResponse> completed) => _ = Task.Factory.StartNew(
            () => completed(runtime.HandleRequest(new Json.TimerOperationRequest { RequestId = id, Params = new Json.TimerStartParams() }, true)),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    [Fact]
    public void TimerCallbacksEnqueueJsonEventsInOrderAndResetAllowsNullState()
    {
        var run = CreateRun();
        var state = TestLiveSplitState.Create(run);
        var adapter = new FakeAdapter { ThrowOnSecondTimerState = true };
        var sink = new RecordingSink();
        using var runtime = new JsonBridgeRuntime(adapter, sink, state);

        var timerModel = new TimerModel { CurrentState = state };
        timerModel.Start();
        timerModel.Reset();

        var started = Assert.IsType<Json.TimerStartedEvent>(sink.Take(TimeSpan.FromSeconds(3)));
        var reset = Assert.IsType<Json.TimerResetEvent>(sink.Take(TimeSpan.FromSeconds(3)));
        Assert.NotNull(started.TimerState);
        Assert.Null(reset.TimerState);
        Assert.Contains("\"timer_state\":null", JsonSerializer.Serialize<Json.BridgeEvent>(reset, Json.BridgeJsonOptions.Create()));
        Assert.Equal(1U, started.EventSequence);
        Assert.Equal(2U, reset.EventSequence);
        Assert.Equal(runtime.SessionId, reset.SessionId);
    }

    [Fact]
    public void JsonAdapterControlsUseLiveSplitTimerAndGameTimeOperations()
    {
        var state = TestLiveSplitState.Create(CreateRun());
        var adapter = new JsonLiveSplitAdapter(state);
        var timer = new TimerModel { CurrentState = state };

        timer.Start();
        adapter.ExecuteTimerOperation(new Json.TimerPauseParams());
        Assert.Equal(LiveSplit.Model.TimerPhase.Paused, state.CurrentPhase);
        adapter.ExecuteTimerOperation(new Json.TimerResumeParams());
        Assert.Equal(LiveSplit.Model.TimerPhase.Running, state.CurrentPhase);
        adapter.ExecuteGameTimeOperation(new Json.GameTimeInitializeParams());
        adapter.ExecuteGameTimeOperation(new Json.GameTimePauseParams());
        adapter.ExecuteGameTimeOperation(new Json.GameTimeSetParams { Ticks = -12345 });
        Assert.True(state.IsGameTimeInitialized);
        Assert.Equal(-12345, state.CurrentTime.GameTime?.Ticks);
        Assert.True(state.IsGameTimePaused);
        adapter.ExecuteGameTimeOperation(new Json.GameTimeResumeParams());
        Assert.False(state.IsGameTimePaused);
    }

    [Fact]
    public void TimerEventsMapAllStateCallbacksToJsonTypesInSequence()
    {
        var run = CreateRun();
        run.Add(new Segment("Three"));
        var state = TestLiveSplitState.Create(run);
        var sink = new RecordingSink();
        using var runtime = new JsonBridgeRuntime(new FakeAdapter(), sink, state);
        var timer = new TimerModel { CurrentState = state };

        timer.Start();
        timer.Pause();
        timer.Pause();
        timer.Split();
        timer.SkipSplit();
        timer.UndoSplit();
        timer.Reset();

        var events = Enumerable.Range(0, 7).Select(_ => sink.Take(TimeSpan.FromSeconds(3))).ToArray();
        Assert.Collection(events,
            value => Assert.IsType<Json.TimerStartedEvent>(value),
            value => Assert.IsType<Json.TimerPhaseChangedEvent>(value),
            value => Assert.IsType<Json.TimerPhaseChangedEvent>(value),
            value => Assert.IsType<Json.TimerSplitEvent>(value),
            value => Assert.IsType<Json.TimerSkippedEvent>(value),
            value => Assert.IsType<Json.TimerUndoEvent>(value),
            value => Assert.IsType<Json.TimerResetEvent>(value));
        Assert.Equal(Enumerable.Range(1, 7).Select(value => (uint)value), events.Select(value => value.EventSequence));
        Assert.All(events, value => Assert.Equal(runtime.SessionId, value.SessionId));
    }

    [Fact]
    public void TimerEventSequenceWrapsFromMaximumToOne()
    {
        Assert.Equal(1U, JsonBridgeRuntime.NextEventSequence(uint.MaxValue));
        Assert.Equal(2U, JsonBridgeRuntime.NextEventSequence(1));
    }

    [Fact]
    public void ContextChangesPublishAnImmutableSnapshotAndNullStateEvent()
    {
        var adapter = new FakeAdapter();
        var sink = new RecordingSink();
        using var runtime = new JsonBridgeRuntime(adapter, sink);
        adapter.ContextState = new Json.ContextState { CurrentComparison = "Best Segments" };
        Thread.Sleep(110);
        runtime.ObserveContextState();

        var changed = Assert.IsType<Json.ContextChangedEvent>(sink.Take(TimeSpan.FromSeconds(3)));
        Assert.Same(Json.JsonNullValue.Value, changed is Json.StateChangedBridgeEvent value ? value.TimerState : null);
        Assert.Contains("\"timer_state\":null", JsonSerializer.Serialize<Json.BridgeEvent>(changed, Json.BridgeJsonOptions.Create()));
        Assert.Equal("Best Segments", runtime.PublishedContext.CurrentComparison);

        Thread.Sleep(110);
        runtime.ObserveContextState();
        Assert.Equal(0, sink.Count);
        adapter.ContextState.CurrentComparison = "Personal Best";
        Thread.Sleep(110);
        runtime.ObserveContextState();
        var changedAgain = Assert.IsType<Json.ContextChangedEvent>(sink.Take(TimeSpan.FromSeconds(3)));
        Assert.Same(Json.JsonNullValue.Value, changedAgain.TimerState);
        Assert.Contains("\"timer_state\":null", JsonSerializer.Serialize<Json.BridgeEvent>(changedAgain, Json.BridgeJsonOptions.Create()));
        Assert.Equal("Personal Best", runtime.PublishedContext.CurrentComparison);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public async Task RunChangedIsQueuedAfterTheNewSnapshotIsPublished()
    {
        var run = CreateRun();
        var state = TestLiveSplitState.Create(run);
        var sink = new RecordingSink();
        using var runtime = new JsonBridgeRuntime(new JsonLiveSplitAdapter(state), sink, state, QueueCandidateOnDedicatedThread);
        run.GameName = "Updated";
        state.CallRunManuallyModified();

        var changed = Assert.IsType<Json.RunChangedEvent>(await sink.TakeAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("Updated", runtime.PublishedRun.GameName);
        Assert.Same(Json.JsonNullValue.Value, ((Json.StateChangedBridgeEvent)changed).TimerState);
        Assert.Contains("\"timer_state\":null", JsonSerializer.Serialize<Json.BridgeEvent>(changed, Json.BridgeJsonOptions.Create()));
    }

    [Fact]
    public void ResetPublishesTimingAndRunIdChangesBeforeRunChanged()
    {
        var run = CreateRun();
        run.Metadata.RunID = "before";
        var state = TestLiveSplitState.Create(run);
        var sink = new RecordingSink();
        using var runtime = new JsonBridgeRuntime(new JsonLiveSplitAdapter(state), sink, state);
        run.Metadata.RunID = null;
        run[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(7), null);
        var timerModel = new TimerModel { CurrentState = state };
        timerModel.Start();
        var started = Assert.IsType<Json.TimerStartedEvent>(sink.Take(TimeSpan.FromSeconds(3)));
        timerModel.Reset();

        var reset = Assert.IsType<Json.TimerResetEvent>(sink.Take(TimeSpan.FromSeconds(3)));
        var changed = Assert.IsType<Json.RunChangedEvent>(sink.Take(TimeSpan.FromSeconds(3)));
        Assert.Equal(started.EventSequence + 1, reset.EventSequence);
        Assert.Equal(reset.EventSequence + 1, changed.EventSequence);
        Assert.Null(runtime.PublishedRun.Metadata!.RunId);
        Assert.Equal(TimeSpan.FromSeconds(7).Ticks, runtime.PublishedRun.Segments[0].BestSegmentTime.RealTimeTicks);
    }

    [Fact]
    public void DisposingRuntimeSuppressesEventsFromLaterStateChanges()
    {
        var state = TestLiveSplitState.Create(CreateRun());
        var sink = new RecordingSink();
        var runtime = new JsonBridgeRuntime(new FakeAdapter(), sink, state);
        runtime.Dispose();

        new TimerModel { CurrentState = state }.Start();

        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public async Task NewerRunCandidateWinsWhenAnOlderBuildFinishesLater()
    {
        var run = CreateRun();
        var state = TestLiveSplitState.Create(run);
        var sink = new RecordingSink();
        var first = new GateCandidate(new Json.RunState { GameName = "stale" });
        var adapter = new FakeAdapter { CandidateFactory = () => first };
        using var runtime = new JsonBridgeRuntime(adapter, sink, state, QueueCandidateOnDedicatedThread);

        run.GameName = "first";
        state.CallRunManuallyModified();
        await first.BuildEntered.WaitAsync(TimeSpan.FromSeconds(5));

        adapter.CandidateFactory = () => new Candidate(new Json.RunState { GameName = "latest" });
        var replacementRun = CreateRun();
        replacementRun.GameName = "latest";
        state.Run = replacementRun;
        state.CallRunManuallyModified();
        Assert.IsType<Json.RunChangedEvent>(await sink.TakeAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("latest", runtime.PublishedRun.GameName);

        first.ReleaseBuild.Set();
        await first.PipelineFinished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, sink.Count);
        Assert.Equal("latest", runtime.PublishedRun.GameName);
    }

    [Fact]
    public async Task CandidateCompletingAfterResetKeepsTheResetTimingSnapshot()
    {
        var state = TestLiveSplitState.Create(CreateRun());
        var sink = new RecordingSink();
        var baseline = CreateTimingSnapshot(10);
        var latestAfterReset = CreateTimingSnapshot(99);
        latestAfterReset.Metadata!.RunId = null;
        var candidate = new GateCandidate(CreateTimingSnapshot(10));
        var adapter = new FakeAdapter
        {
            RunSnapshot = baseline,
            TimingUpdateSnapshot = latestAfterReset,
            CandidateFactory = () => candidate,
        };
        using var runtime = new JsonBridgeRuntime(adapter, sink, state, QueueCandidateOnDedicatedThread);
        var timerModel = new TimerModel { CurrentState = state };
        timerModel.Start();
        await sink.TakeAsync(TimeSpan.FromSeconds(3));
        state.CallRunManuallyModified();
        await candidate.BuildEntered.WaitAsync(TimeSpan.FromSeconds(5));

        timerModel.Reset();
        Assert.IsType<Json.TimerResetEvent>(await sink.TakeAsync(TimeSpan.FromSeconds(3)));
        Assert.IsType<Json.RunChangedEvent>(await sink.TakeAsync(TimeSpan.FromSeconds(3)));
        candidate.ReleaseBuild.Set();
        await candidate.PipelineFinished.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(99, runtime.PublishedRun.Segments[0].BestSegmentTime.RealTimeTicks);
        Assert.Null(runtime.PublishedRun.Metadata!.RunId);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public async Task DisposedRuntimeDoesNotPublishACompletedBackgroundCandidate()
    {
        var state = TestLiveSplitState.Create(CreateRun());
        var sink = new RecordingSink();
        var candidate = new GateCandidate(new Json.RunState { GameName = "late" });
        var adapter = new FakeAdapter { CandidateFactory = () => candidate };
        var runtime = new JsonBridgeRuntime(adapter, sink, state, QueueCandidateOnDedicatedThread);

        state.CallRunManuallyModified();
        await candidate.BuildEntered.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Dispose();
        candidate.ReleaseBuild.Set();
        await candidate.PipelineFinished.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, sink.Count);
        Assert.NotEqual("late", runtime.PublishedRun.GameName);
    }

    [Fact]
    public void RuntimeResponsesSerializeToTheResponseSchemaShape()
    {
        var adapter = new FakeAdapter();
        using var runtime = new JsonBridgeRuntime(adapter, new RecordingSink());
        var options = Json.BridgeJsonOptions.Create();
        var success = JsonSerializer.Serialize<Json.RpcResponse>(runtime.HandleRequest(new Json.GetRunRequest { RequestId = 55 }, false), options);
        var error = JsonSerializer.Serialize<Json.RpcResponse>(runtime.HandleRequest(
            new Json.TimerOperationRequest { RequestId = 56, Params = new Json.TimerStartParams() }, false), options);
        var reset = JsonSerializer.Serialize<Json.BridgeEvent>(new Json.TimerResetEvent { SessionId = runtime.SessionId, EventSequence = 1, TimerState = null }, options);
        var contextChanged = JsonSerializer.Serialize<Json.BridgeEvent>(new Json.ContextChangedEvent { SessionId = runtime.SessionId, EventSequence = 2 }, options);

        Assert.Contains("\"request_id\":55", success);
        Assert.Contains("\"session_id\":", success);
        Assert.Contains("\"result\":", success);
        Assert.Contains("\"error\":", error);
        Assert.DoesNotContain("\"result\":", error);
        Assert.Contains("\"timer_state\":null", reset);
        Assert.Contains("\"timer_state\":null", contextChanged);
    }

    private static Run CreateRun()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        return run;
    }

    private static Json.RunState CreateTimingSnapshot(long ticks) => new()
    {
        GameName = "static",
        CategoryName = "category",
        Metadata = new Json.RunMetadata { RunId = "id" },
        Comparisons = new() { "PB" },
        Segments = new()
        {
            new Json.SegmentInfo
            {
                Index = 0,
                Name = "One",
                Comparisons = new() { new Json.ComparisonTime { Name = "PB", Time = new Json.TimeValue { RealTimeTicks = ticks } } },
                BestSegmentTime = new Json.TimeValue { RealTimeTicks = ticks },
            },
        },
    };

    private static void QueueCandidateOnDedicatedThread(Action work) => _ = Task.Factory.StartNew(
        work,
        CancellationToken.None,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default);

    private class FakeAdapter : IJsonLiveSplitAdapter
    {
        public bool ThrowOnTimerOperation { get; set; }
        public bool ThrowOnTimerState { get; set; }
        public bool ThrowOnSecondTimerState { get; set; }
        public bool ThrowOnAttempt { get; set; }
        public Func<IJsonRunStateCandidate>? CandidateFactory { get; set; }
        public Json.ContextState ContextState { get; set; } = new();
        public Json.RunState RunSnapshot { get; set; } = new();
        public Json.RunState? TimingUpdateSnapshot { get; set; }
        private int timerStateCalls;
        public int TimerOperationCalls { get; private set; }
        public virtual Json.TimerState GetTimerState()
        {
            var call = Interlocked.Increment(ref timerStateCalls);
            if (ThrowOnTimerState || (ThrowOnSecondTimerState && call > 1)) throw new InvalidOperationException("timer capture failed");
            return new Json.TimerState { Phase = Json.TimerPhase.Running };
        }
        public Json.AttemptState GetAttempt() => ThrowOnAttempt ? throw new InvalidOperationException("attempt failed") : new();
        public Json.CompletedCount GetCompletedCount() => new();
        public Json.ContextState GetContextState() => new()
        {
            CurrentTimingMethod = ContextState.CurrentTimingMethod,
            CurrentComparison = ContextState.CurrentComparison,
            CustomVariables = new Dictionary<string, string>(ContextState.CustomVariables),
        };
        public Json.RunState GetRunState() => RunSnapshot;
        public IJsonRunStateCandidate CaptureRunState() => CandidateFactory?.Invoke() ?? new Candidate(new Json.RunState());
        public bool TryUpdateRunTimings(Json.RunState published, out Json.RunState updated)
        {
            updated = TimingUpdateSnapshot == null ? published : LiveSplit.Bridge.Json.RunSnapshot.CopyForTimingUpdate(TimingUpdateSnapshot);
            return true;
        }
        public bool IsTimerOnlyRun() => false;
        public virtual void ExecuteTimerOperation(Json.TimerOperationParams operation)
        {
            TimerOperationCalls++;
            if (ThrowOnTimerOperation) throw new InvalidOperationException("operation failed");
        }
        public void ExecuteGameTimeOperation(Json.GameTimeOperationParams operation) { }
    }

    private sealed class BlockingAdapter : FakeAdapter
    {
        internal ManualResetEventSlim ControlEntered { get; } = new();
        internal ManualResetEventSlim ReleaseControl { get; } = new();
        public override void ExecuteTimerOperation(Json.TimerOperationParams operation)
        {
            ControlEntered.Set();
            if (!ReleaseControl.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Control was not released.");
        }
    }

    private sealed class SerialAdapter : FakeAdapter
    {
        private int operationCount;
        internal ManualResetEventSlim FirstEntered { get; } = new();
        internal ManualResetEventSlim ReleaseFirst { get; } = new();
        internal ManualResetEventSlim SecondEntered { get; } = new();
        public override void ExecuteTimerOperation(Json.TimerOperationParams operation)
        {
            if (Interlocked.Increment(ref operationCount) == 1)
            {
                FirstEntered.Set();
                if (!ReleaseFirst.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("First control was not released.");
            }
            else SecondEntered.Set();
        }
    }

    private sealed class Candidate : IJsonRunStateCandidate
    {
        private Json.RunState? value;
        internal Candidate(Json.RunState value) => this.value = value;
        public Json.RunState Build() => Interlocked.Exchange(ref value, null) ?? throw new ObjectDisposedException(nameof(Candidate));
        public void Dispose() => Interlocked.Exchange(ref value, null);
    }

    private sealed class GateCandidate : IJsonRunStateCandidate
    {
        private readonly Json.RunState value;
        private int disposed;
        internal AsyncSignal BuildEntered { get; } = new();
        internal ManualResetEventSlim ReleaseBuild { get; } = new();
        internal AsyncSignal PipelineFinished { get; } = new();
        internal GateCandidate(Json.RunState value) => this.value = value;
        public Json.RunState Build()
        {
            BuildEntered.Set();
            if (!ReleaseBuild.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Candidate build was not released.");
            return value;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) PipelineFinished.Set();
        }
    }

    private sealed class AsyncSignal
    {
        private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Set() => completion.TrySetResult(true);
        internal async Task WaitAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(completion.Task, Task.Delay(timeout)).ConfigureAwait(false);
            Assert.Same(completion.Task, completed);
            await completion.Task.ConfigureAwait(false);
        }
    }

    private sealed class RecordingSink : IJsonEventSink
    {
        private readonly ConcurrentQueue<Json.BridgeEvent> events = new();
        private readonly SemaphoreSlim available = new(0);
        public int Count => events.Count;
        public void Enqueue(Json.BridgeEvent bridgeEvent) { events.Enqueue(bridgeEvent); available.Release(); }
        internal Json.BridgeEvent Take(TimeSpan timeout)
        {
            Assert.True(available.Wait(timeout));
            Assert.True(events.TryDequeue(out var result));
            return result!;
        }
        internal async Task<Json.BridgeEvent> TakeAsync(TimeSpan timeout)
        {
            Assert.True(await available.WaitAsync(timeout).ConfigureAwait(false));
            Assert.True(events.TryDequeue(out var result));
            return result!;
        }
    }
}
