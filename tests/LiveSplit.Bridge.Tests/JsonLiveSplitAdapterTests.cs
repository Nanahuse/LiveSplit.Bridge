using System.Drawing;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using Json = LiveSplit.Bridge.Json;

namespace LiveSplit.Bridge.Tests;

public class JsonLiveSplitAdapterTests
{
    [Fact]
    public void MapsTimerAttemptCompletedCountAndContext()
    {
        var run = CreateRun();
        run.AttemptCount = 4;
        run[0].SplitTime = new Time(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(8));
        run[0].CustomVariableValues["route"] = "left";
        run.AttemptHistory.Add(new Attempt(1, new Time(TimeSpan.FromSeconds(5), null), null, null, null));
        run.AttemptHistory.Add(new Attempt(2, new Time(null, TimeSpan.FromSeconds(4)), null, null, null));
        run.Metadata.CustomVariables["route"] = new CustomVariable("right", false);
        var state = TestLiveSplitState.Create(run);
        state.CurrentPhase = LiveSplit.Model.TimerPhase.Paused;
        state.CurrentSplitIndex = 1;
        state.TimePausedAt = TimeSpan.FromSeconds(4);
        state.CurrentComparison = "Personal Best";

        var adapter = new JsonLiveSplitAdapter(state);
        var timer = adapter.GetTimerState();
        var attempt = adapter.GetAttempt();
        var completed = adapter.GetCompletedCount();
        var context = adapter.GetContextState();

        Assert.Equal(Json.TimerPhase.Paused, timer.Phase);
        Assert.Equal(1, timer.SplitIndex);
        Assert.Equal(TimeSpan.FromSeconds(4).Ticks, timer.RealTimeTicks);
        Assert.Equal(4U, attempt.AttemptCount);
        Assert.Equal(TimeSpan.FromSeconds(12).Ticks, attempt.Segments[0].SplitTime.RealTimeTicks);
        Assert.Equal("left", attempt.Segments[0].CustomVariables["route"]);
        Assert.Equal(1U, completed.Value);
        Assert.Equal("right", context.CustomVariables["route"]);
        Assert.Equal("Personal Best", context.CurrentComparison);
    }

    [Fact]
    public void CapturesRunStateAndDetachedPngImages()
    {
        var run = CreateRun();
        run.GameName = "Game";
        run.CategoryName = "Any%";
        run.Offset = TimeSpan.FromTicks(-123);
        run.Metadata.RunID = "run-id";
        run.Metadata.VariableValueNames["character"] = "Character";
        run.Metadata.CustomVariables["character"] = new CustomVariable("A", false);
        run[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(6));
        var gameIcon = new Bitmap(2, 2);
        var segmentIcon = new Bitmap(2, 2);
        gameIcon.SetPixel(0, 0, Color.Magenta);
        segmentIcon.SetPixel(0, 0, Color.Cyan);
        run.GameIcon = gameIcon;
        run[0].Icon = segmentIcon;
        var adapter = new JsonLiveSplitAdapter(TestLiveSplitState.Create(run));

        using var candidate = adapter.CaptureRunState();
        gameIcon.Dispose();
        segmentIcon.Dispose();
        var result = candidate.Build();

        Assert.Equal("Game", result.GameName);
        Assert.Equal(-123, result.OffsetTicks);
        Assert.Equal("run-id", result.Metadata!.RunId);
        Assert.Equal("Character", result.Metadata.Variables["character"]);
        Assert.Equal("A", adapter.GetContextState().CustomVariables["character"]);
        Assert.Equal(run.Comparisons.Distinct(), result.Comparisons);
        Assert.Equal("One", result.Segments[0].Name);
        Assert.Equal(TimeSpan.FromSeconds(7).Ticks, result.Segments[0].BestSegmentTime.RealTimeTicks);
        AssertPng(result.GameIcon);
        AssertPng(result.Segments[0].Icon);
    }

    [Fact]
    public void TimingUpdateCopiesPublishedSnapshotAndRefreshesRunIdAndTimes()
    {
        var run = CreateRun();
        run.Metadata.RunID = "before";
        run[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(2), null);
        var adapter = new JsonLiveSplitAdapter(TestLiveSplitState.Create(run));
        var published = adapter.GetRunState();
        published.GameIcon = new byte[] { 1, 2, 3 };
        published.Segments[0].Icon = new byte[] { 4, 5, 6 };
        run.Metadata.RunID = null;
        run[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(9), null);

        Assert.True(adapter.TryUpdateRunTimings(published, out var updated));
        Assert.Equal("before", published.Metadata!.RunId);
        Assert.Null(updated.Metadata!.RunId);
        Assert.Equal(TimeSpan.FromSeconds(2).Ticks, published.Segments[0].BestSegmentTime.RealTimeTicks);
        Assert.Equal(TimeSpan.FromSeconds(9).Ticks, updated.Segments[0].BestSegmentTime.RealTimeTicks);
        Assert.Equal(new byte[] { 1, 2, 3 }, updated.GameIcon);
        Assert.Equal(new byte[] { 4, 5, 6 }, updated.Segments[0].Icon);

        updated.Segments.RemoveAt(updated.Segments.Count - 1);
        Assert.False(adapter.TryUpdateRunTimings(updated, out _));
    }

    [Fact]
    public void TreatsEmptyLiveSplitRunIdAsUnset()
    {
        var run = CreateRun();
        run.Metadata.RunID = string.Empty;
        var adapter = new JsonLiveSplitAdapter(TestLiveSplitState.Create(run));

        var published = adapter.GetRunState();
        Assert.Null(published.Metadata!.RunId);

        Assert.True(adapter.TryUpdateRunTimings(published, out var updated));
        Assert.Null(updated.Metadata!.RunId);
    }

    [Fact]
    public void MergeRunTimingsPreservesCandidateStaticDataAndRejectsStructureMismatch()
    {
        var candidate = CreateSnapshot();
        var latest = CreateSnapshot();
        latest.Metadata!.RunId = null;
        latest.Segments[0].BestSegmentTime.RealTimeTicks = 99;

        Assert.True(JsonLiveSplitAdapter.TryMergeRunTimings(candidate, latest, out var merged));
        Assert.Equal("Game", merged.GameName);
        Assert.Null(merged.Metadata!.RunId);
        Assert.Equal(99, merged.Segments[0].BestSegmentTime.RealTimeTicks);
        Assert.Equal(10, candidate.Segments[0].BestSegmentTime.RealTimeTicks);

        latest.Segments[0].Name = "Different";
        Assert.False(JsonLiveSplitAdapter.TryMergeRunTimings(candidate, latest, out _));
    }

    private static Run CreateRun()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        return run;
    }

    private static Json.RunState CreateSnapshot() => new()
    {
        GameName = "Game",
        Metadata = new Json.RunMetadata { RunId = "id" },
        Comparisons = new() { "Personal Best" },
        Segments = new()
        {
            new Json.SegmentInfo
            {
                Name = "One",
                Comparisons = new() { new Json.ComparisonTime { Name = "Personal Best", Time = new Json.TimeValue() } },
                BestSegmentTime = new Json.TimeValue { RealTimeTicks = 10 },
            },
        },
    };

    private static void AssertPng(byte[]? data)
    {
        Assert.NotNull(data);
        Assert.True(data!.Length > 8);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, data.Take(8));
    }
}
