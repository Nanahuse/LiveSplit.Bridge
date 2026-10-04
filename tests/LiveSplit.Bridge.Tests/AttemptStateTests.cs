using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

public class AttemptStateTests
{
    [Fact]
    public void BuildAttemptState_CountsOnlyFinishedAttempts()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.AttemptHistory.Add(new Attempt(
            index: 1,
            time: new Time(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(9)),
            started: null,
            ended: null,
            pauseTime: null));
        run.AttemptHistory.Add(new Attempt(
            index: 2,
            time: new Time(null, null),
            started: null,
            ended: null,
            pauseTime: null));

        var adapter = new LiveSplitAdapter(TestLiveSplitState.Create(run));

        var attempt = adapter.BuildAttemptState(attemptRevision: 1, sessionId: 1);

        Assert.Equal(1U, attempt.CompletedCount);
    }

    [Fact]
    public void BuildAttemptState_ExcludesAttemptResetMidRun()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.AttemptHistory.Add(new Attempt(
            index: 1,
            time: new Time(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(9)),
            started: null,
            ended: null,
            pauseTime: null));
        run.AttemptHistory.Add(new Attempt(
            index: 2,
            time: new Time(null, null),
            started: null,
            ended: null,
            pauseTime: null));

        var adapter = new LiveSplitAdapter(TestLiveSplitState.Create(run));

        var attempt = adapter.BuildAttemptState(attemptRevision: 1, sessionId: 1);

        Assert.DoesNotContain(
            run.AttemptHistory,
            history => history.Index == 2 && history.Time.RealTime != null);
        Assert.Equal(1U, attempt.CompletedCount);
    }

    [Fact]
    public void BuildAttemptState_ReturnsZeroForEmptyHistory()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));

        var adapter = new LiveSplitAdapter(TestLiveSplitState.Create(run));

        var attempt = adapter.BuildAttemptState(attemptRevision: 1, sessionId: 1);

        Assert.Empty(run.AttemptHistory);
        Assert.Equal(0U, attempt.CompletedCount);
    }
}
