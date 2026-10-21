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
        using var runtime = new JsonBridgeRuntime(adapter, new RecordingSink());
        var requests = new Json.RpcRequest[]
        {
            new Json.GetTimerStateRequest { RequestId = 0 }, new Json.GetRunRequest { RequestId = uint.MaxValue },
            new Json.GetContextStateRequest { RequestId = 8 }, new Json.GetAttemptRequest { RequestId = 9 },
            new Json.GetCompletedCountRequest { RequestId = 10 },
            new Json.TimerOperationRequest { RequestId = 11, Params = new Json.TimerStartParams() },
        };
        var responses = requests.Select(request => WireResponse(runtime.HandleRequest(request, true))).ToArray();
        var session = responses[0].GetProperty("session_id").GetUInt32();
        Assert.NotEqual(0U, session);
        for (var i = 0; i < requests.Length; i++)
        {
            Assert.Equal(requests[i].RequestId, responses[i].GetProperty("request_id").GetUInt32());
            Assert.Equal(session, responses[i].GetProperty("session_id").GetUInt32());
        }
        Assert.Equal(1, adapter.TimerOperationCalls);
    }

    [Fact]
    public void DeniesControlBeforeParamsValidationAndMapsOperationErrors()
    {
        var adapter = new FakeAdapter { ThrowOnTimerOperation = true };
        using var runtime = new JsonBridgeRuntime(adapter, new RecordingSink());

        var denied = WireResponse(runtime.HandleRequest(
            new Json.TimerOperationRequest { RequestId = 21, Params = null! }, allowControl: false));
        var failed = WireResponse(runtime.HandleRequest(
            new Json.TimerOperationRequest { RequestId = 22, Params = new Json.TimerStartParams() }, allowControl: true));
        var invalid = WireResponse(runtime.HandleRequest(
            new Json.GameTimeOperationRequest { RequestId = 23, Params = null! }, allowControl: true));

        Assert.Equal("permission_denied", denied.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("operation_failed", failed.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("invalid_argument", invalid.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, adapter.TimerOperationCalls);
    }

    [Fact]
    public void QueryExceptionReturnsInternalError()
    {
        using var runtime = new JsonBridgeRuntime(new FakeAdapter { ThrowOnAttempt = true }, new RecordingSink());
        var response = WireResponse(runtime.HandleRequest(new Json.GetAttemptRequest { RequestId = 19 }, false));

        Assert.Equal(19U, response.GetProperty("request_id").GetUInt32());
        Assert.Equal("internal_error", response.GetProperty("error").GetProperty("code").GetString());
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
        Assert.True(WireResponse(queryResponse!).GetProperty("result").TryGetProperty("phase", out _));
        adapter.ReleaseControl.Set();
        Assert.True(controlDone.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(JsonValueKind.Null, WireResponse(controlResponse!).GetProperty("result").ValueKind);
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
        Assert.Equal(JsonValueKind.Null, WireResponse(firstResponse!).GetProperty("result").ValueKind);
        Assert.Equal(JsonValueKind.Null, WireResponse(secondResponse!).GetProperty("result").ValueKind);

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

        var started = ReadEvent(sink.Take(TimeSpan.FromSeconds(3)), "timer_started");
        var reset = ReadEvent(sink.Take(TimeSpan.FromSeconds(3)), "timer_reset");
        Assert.Equal(JsonValueKind.Object, started.GetProperty("timer_state").ValueKind);
        Assert.Equal(JsonValueKind.Null, reset.GetProperty("timer_state").ValueKind);
        Assert.Equal(1U, started.GetProperty("event_sequence").GetUInt32());
        Assert.Equal(2U, reset.GetProperty("event_sequence").GetUInt32());
        Assert.Equal(started.GetProperty("session_id").GetUInt32(), reset.GetProperty("session_id").GetUInt32());
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
        // Seed only the otherwise unreachable boundary; assert emitted messages below.
        typeof(JsonBridgeRuntime).GetField("eventSequence", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(runtime, uint.MaxValue - 1);
        var timer = new TimerModel { CurrentState = state };

        timer.Start();
        timer.Pause();
        timer.Pause();
        timer.Split();
        timer.SkipSplit();
        timer.UndoSplit();
        timer.Reset();

        var events = Enumerable.Range(0, 7).Select(_ => WireEvent(sink.Take(TimeSpan.FromSeconds(3)))).ToArray();
        Assert.Equal(new[] { "timer_started", "timer_phase_changed", "timer_phase_changed", "timer_split", "timer_skipped", "timer_undo", "timer_reset" },
            events.Select(value => value.GetProperty("type").GetString()));
        Assert.Equal(new uint[] { uint.MaxValue, 1, 2, 3, 4, 5, 6 }, events.Select(value => value.GetProperty("event_sequence").GetUInt32()));
        var session = events[0].GetProperty("session_id").GetUInt32();
        Assert.All(events, value => Assert.Equal(session, value.GetProperty("session_id").GetUInt32()));
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

        ReadEvent(sink.Take(TimeSpan.FromSeconds(3)), "context_changed");
        var publishedContext = runtime.HandleRequest(new Json.GetContextStateRequest(), false);
        Assert.Equal("Best Segments", WireResponse(publishedContext).GetProperty("result").GetProperty("current_comparison").GetString());

        Thread.Sleep(110);
        runtime.ObserveContextState();
        Assert.Equal(0, sink.Count);
        adapter.ContextState.CurrentComparison = "Personal Best";
        Thread.Sleep(110);
        runtime.ObserveContextState();
        ReadEvent(sink.Take(TimeSpan.FromSeconds(3)), "context_changed");
        Assert.Equal("Personal Best", ReadContext(runtime).GetProperty("current_comparison").GetString());
        Assert.Equal("Best Segments", WireResponse(publishedContext).GetProperty("result").GetProperty("current_comparison").GetString());
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

        ReadEvent(await sink.TakeAsync(TimeSpan.FromSeconds(5)), "run_changed");

        Assert.Equal("Updated", ReadRun(runtime).GetProperty("game_name").GetString());
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
        var started = ReadEvent(sink.Take(TimeSpan.FromSeconds(3)), "timer_started");
        timerModel.Reset();

        var reset = ReadEvent(sink.Take(TimeSpan.FromSeconds(3)), "timer_reset");
        var changed = ReadEvent(sink.Take(TimeSpan.FromSeconds(3)), "run_changed");
        Assert.Equal(started.GetProperty("event_sequence").GetUInt32() + 1, reset.GetProperty("event_sequence").GetUInt32());
        Assert.Equal(reset.GetProperty("event_sequence").GetUInt32() + 1, changed.GetProperty("event_sequence").GetUInt32());
        Assert.Equal(JsonValueKind.Null, ReadRun(runtime).GetProperty("metadata").GetProperty("run_id").ValueKind);
        Assert.Equal(TimeSpan.FromSeconds(7).Ticks, ReadRun(runtime).GetProperty("segments")[0].GetProperty("best_segment_time").GetProperty("real_time_ticks").GetInt64());
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
        ReadEvent(await sink.TakeAsync(TimeSpan.FromSeconds(5)), "run_changed");
        Assert.Equal("latest", ReadRun(runtime).GetProperty("game_name").GetString());

        first.ReleaseBuild.Set();
        await first.PipelineFinished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, sink.Count);
        Assert.Equal("latest", ReadRun(runtime).GetProperty("game_name").GetString());
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
        ReadEvent(await sink.TakeAsync(TimeSpan.FromSeconds(3)), "timer_reset");
        ReadEvent(await sink.TakeAsync(TimeSpan.FromSeconds(3)), "run_changed");
        candidate.ReleaseBuild.Set();
        await candidate.PipelineFinished.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(99, ReadRun(runtime).GetProperty("segments")[0].GetProperty("best_segment_time").GetProperty("real_time_ticks").GetInt64());
        Assert.Equal(JsonValueKind.Null, ReadRun(runtime).GetProperty("metadata").GetProperty("run_id").ValueKind);
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
        Assert.NotEqual("late", ReadRun(runtime).GetProperty("game_name").GetString());
    }

    private static JsonElement WireResponse(Json.RpcResponse response) => JsonSerializer.SerializeToElement(response, Json.BridgeJsonOptions.Create());
    private static JsonElement WireEvent(Json.BridgeEvent value) => JsonSerializer.SerializeToElement(value, Json.BridgeJsonOptions.Create());
    private static JsonElement ReadRun(JsonBridgeRuntime runtime) => WireResponse(runtime.HandleRequest(new Json.GetRunRequest(), false)).GetProperty("result");
    private static JsonElement ReadContext(JsonBridgeRuntime runtime) => WireResponse(runtime.HandleRequest(new Json.GetContextStateRequest(), false)).GetProperty("result");

    private static JsonElement ReadEvent(Json.BridgeEvent value, string expectedType)
    {
        var message = WireEvent(value);
        Assert.Equal(expectedType, message.GetProperty("type").GetString());
        return message;
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
