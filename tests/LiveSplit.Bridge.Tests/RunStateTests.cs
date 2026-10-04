using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

public class RunStateTests
{
    [Fact]
    public void BuildRunState_MapsRepresentativeRun()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory())
        {
            GameName = "Test Game",
            CategoryName = "Any%",
            Offset = TimeSpan.FromSeconds(5),
            AttemptCount = 3,
            FilePath = @"C:\runs\test.lss",
            LayoutPath = @"C:\layouts\test.lsl",
        };

        var first = new Segment("First")
        {
            BestSegmentTime = new Time(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(7)),
        };
        first.Comparisons[Run.PersonalBestComparisonName] =
            new Time(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11));
        first.Comparisons["Best Segments"] = new Time(TimeSpan.FromSeconds(9), null);
        first.CustomVariableValues["segment_var"] = "segment_value";
        run.Add(first);
        run.Add(new Segment("Second"));

        run.Metadata.RunID = "run-123";
        run.Metadata.PlatformName = "PC";
        run.Metadata.RegionName = "USA";
        run.Metadata.UsesEmulator = true;
        run.Metadata.VariableValueNames["difficulty"] = "hard";
        run.Metadata.GetOrAddCustomVariable("custom").Value = "custom-value";

        var adapter = new LiveSplitAdapter(TestLiveSplitState.Create(run));

        var runState = adapter.BuildRunState(runRevision: 7, sessionId: 999);

        Assert.Equal(999UL, runState.SessionId);
        Assert.Equal(7UL, runState.RunRevision);
        Assert.Equal("Test Game", runState.GameName);
        Assert.Equal("Any%", runState.CategoryName);
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, runState.OffsetTicks);
        Assert.True(runState.HasFilePath);
        Assert.Equal(@"C:\runs\test.lss", runState.FilePath);
        Assert.True(runState.HasLayoutPath);
        Assert.Equal(@"C:\layouts\test.lsl", runState.LayoutPath);
        Assert.Null(runState.GameIcon);

        Assert.NotNull(runState.Metadata);
        Assert.True(runState.Metadata.HasRunId);
        Assert.Equal("run-123", runState.Metadata.RunId);
        Assert.Equal("PC", runState.Metadata.PlatformName);
        Assert.Equal("USA", runState.Metadata.RegionName);
        Assert.True(runState.Metadata.UsesEmulator);
        Assert.Equal("hard", runState.Metadata.Variables["difficulty"]);

        Assert.Equal(
            new[] { "Personal Best", "Best Segments", "Average Segments" },
            runState.Comparisons);

        Assert.Equal(2, runState.Segments.Count);
        var firstInfo = runState.Segments[0];
        Assert.Equal(0U, firstInfo.Index);
        Assert.Equal("First", firstInfo.Name);
        Assert.Equal(TimeSpan.FromSeconds(8).Ticks, firstInfo.BestSegmentTime.RealTimeTicks);
        Assert.Equal(TimeSpan.FromSeconds(7).Ticks, firstInfo.BestSegmentTime.GameTimeTicks);
        Assert.Null(firstInfo.Icon);

        var secondInfo = runState.Segments[1];
        Assert.Equal(1U, secondInfo.Index);
        Assert.Equal("Second", secondInfo.Name);

        var personalBest = firstInfo.Comparisons.Single(x => x.Name == "Personal Best");
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, personalBest.Time.RealTimeTicks);
        Assert.Equal(TimeSpan.FromSeconds(11).Ticks, personalBest.Time.GameTimeTicks);

        var bestSegments = firstInfo.Comparisons.Single(x => x.Name == "Best Segments");
        Assert.Equal(TimeSpan.FromSeconds(9).Ticks, bestSegments.Time.RealTimeTicks);
        Assert.False(bestSegments.Time.HasGameTimeTicks);

        var average = firstInfo.Comparisons.Single(x => x.Name == "Average Segments");
        Assert.False(average.Time.HasRealTimeTicks);
        Assert.False(average.Time.HasGameTimeTicks);

        Assert.All(
            secondInfo.Comparisons,
            comparison =>
            {
                Assert.False(comparison.Time.HasRealTimeTicks);
                Assert.False(comparison.Time.HasGameTimeTicks);
            });
    }

    [Fact]
    public void BuildRunState_OmitsOptionalValuesWhenUnavailable()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("Only"));

        var adapter = new LiveSplitAdapter(TestLiveSplitState.Create(run));

        var runState = adapter.BuildRunState(runRevision: 1, sessionId: 1);

        Assert.False(runState.HasFilePath);
        Assert.False(runState.HasLayoutPath);
        Assert.Null(runState.GameIcon);
        Assert.False(runState.Metadata.HasRunId);
        Assert.False(runState.Metadata.HasPlatformName);
        Assert.False(runState.Metadata.HasRegionName);
        Assert.False(runState.Metadata.UsesEmulator);
        Assert.Empty(runState.Metadata.Variables);

        var segment = Assert.Single(runState.Segments);
        Assert.Null(segment.Icon);
        Assert.False(segment.BestSegmentTime.HasRealTimeTicks);
        Assert.False(segment.BestSegmentTime.HasGameTimeTicks);
        Assert.All(
            segment.Comparisons,
            comparison =>
            {
                Assert.False(comparison.Time.HasRealTimeTicks);
                Assert.False(comparison.Time.HasGameTimeTicks);
            });
    }
}
