using LiveSplit.Bridge.Protocol.V3;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class RunResetCacheTests
{
    [Fact]
    public void MidAttemptResetCacheMatchesFreshRunState()
    {
        var state = CreateState(out _);
        using var runtime = new BridgeRuntime(state);
        Send(runtime, TimerOperationType.TimerStart);
        Thread.Sleep(10);
        Send(runtime, TimerOperationType.TimerSplit);
        Send(runtime, TimerOperationType.TimerReset);

        runtime.Update();

        Assert.Equal(BuildFresh(state), QueryRun(runtime));
    }

    [Fact]
    public void CompletedAttemptResetWithoutNewPersonalBestMatchesFreshRunState()
    {
        var state = CreateState(out var run, personalBest: TimeSpan.FromMilliseconds(1));
        using var runtime = new BridgeRuntime(state);
        Send(runtime, TimerOperationType.TimerStart);
        Thread.Sleep(10);
        Send(runtime, TimerOperationType.TimerSplit);
        Thread.Sleep(10);
        Send(runtime, TimerOperationType.TimerSplit);
        Assert.Equal(LiveSplit.Model.TimerPhase.Ended, state.CurrentPhase);

        Send(runtime, TimerOperationType.TimerReset);
        runtime.Update();

        var cached = QueryRun(runtime);
        Assert.Equal("existing-run-id", cached.Metadata.RunId);
        Assert.Equal(BuildFresh(state), cached);
        Assert.Equal("existing-run-id", run.Metadata.RunID);
    }

    [Fact]
    public void NewPersonalBestResetRefreshesRunIdComparisonsAndBestSegments()
    {
        var state = CreateState(out var run, personalBest: TimeSpan.FromHours(1));
        using var runtime = new BridgeRuntime(state);
        Send(runtime, TimerOperationType.TimerStart);
        Thread.Sleep(10);
        Send(runtime, TimerOperationType.TimerSplit);
        Thread.Sleep(10);
        Send(runtime, TimerOperationType.TimerSplit);
        Send(runtime, TimerOperationType.TimerReset);
        runtime.Update();

        var cached = QueryRun(runtime);
        Assert.False(cached.Metadata.HasRunId);
        Assert.Equal(BuildFresh(state), cached);
        Assert.True(run[0].PersonalBestSplitTime.RealTime > TimeSpan.Zero);
        Assert.True(run[0].BestSegmentTime.RealTime > TimeSpan.Zero);
    }

    private static LiveSplitState CreateState(out Run run, TimeSpan? personalBest = null)
    {
        run = new Run(new StandardComparisonGeneratorsFactory())
        {
            GameName = "Reset Game",
            CategoryName = "Any%",
        };
        run.Add(new Segment("First", new Time(personalBest ?? TimeSpan.FromHours(1), null)));
        run.Add(new Segment("Final", new Time(TimeSpan.FromTicks((personalBest ?? TimeSpan.FromHours(1)).Ticks * 2), null)));
        run[0].BestSegmentTime = new Time(personalBest ?? TimeSpan.FromHours(1), null);
        run[1].BestSegmentTime = new Time(personalBest ?? TimeSpan.FromHours(1), null);
        run.Metadata.RunID = "existing-run-id";
        return TestLiveSplitState.Create(run);
    }

    private static RunState QueryRun(BridgeRuntime runtime)
    {
        var response = runtime.HandleRequest(new Request { ProtocolVersion = 3, GetRun = new GetRunRequest() });
        Assert.NotNull(response.GetRun);
        return response.GetRun.Run;
    }

    private static RunState BuildFresh(LiveSplitState state) => new RunStateCache(state).Current;

    private static void Send(BridgeRuntime runtime, TimerOperationType operation)
    {
        var response = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            TimerOperation = new TimerOperationRequest { Operation = operation },
        });
        Assert.NotNull(response.Operation);
    }
}
