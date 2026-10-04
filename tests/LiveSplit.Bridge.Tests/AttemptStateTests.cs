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
        var started = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var ended = new DateTime(2026, 1, 1, 12, 5, 0, DateTimeKind.Utc);
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.AttemptHistory.Add(new Attempt(
            index: 1,
            time: new Time(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(9)),
            started: new AtomicDateTime(started, synced: false),
            ended: new AtomicDateTime(ended, synced: false),
            pauseTime: null));
        run.AttemptHistory.Add(new Attempt(
            index: 2,
            time: new Time(null, null),
            started: new AtomicDateTime(started, synced: false),
            ended: new AtomicDateTime(ended, synced: false),
            pauseTime: null));

        var resetAttempt = run.AttemptHistory.Single(history => history.Index == 2);
        Assert.True(resetAttempt.Ended.HasValue);
        Assert.Null(resetAttempt.Time.RealTime);

        var adapter = new LiveSplitAdapter(TestLiveSplitState.Create(run));

        var attempt = adapter.BuildAttemptState(attemptRevision: 1, sessionId: 1);

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
