using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

public class RunSnapshotTests
{
    [Fact]
    public void BuildRunSnapshot_MapsRepresentativeRun()
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

        var snapshot = adapter.BuildRunSnapshot(runRevision: 7, stateRevision: 42, sessionId: 999);

        Assert.Equal(999UL, snapshot.SessionId);
        Assert.Equal(7UL, snapshot.RunRevision);
        Assert.Equal(42UL, snapshot.CapturedStateRevision);
        Assert.Equal("Test Game", snapshot.GameName);
        Assert.Equal("Any%", snapshot.CategoryName);
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, snapshot.OffsetTicks);
        Assert.True(snapshot.HasFilePath);
        Assert.Equal(@"C:\runs\test.lss", snapshot.FilePath);
        Assert.True(snapshot.HasLayoutPath);
        Assert.Equal(@"C:\layouts\test.lsl", snapshot.LayoutPath);
        Assert.Equal(3U, snapshot.AttemptCount);

        Assert.NotNull(snapshot.Metadata);
        Assert.True(snapshot.Metadata.HasRunId);
        Assert.Equal("run-123", snapshot.Metadata.RunId);
        Assert.Equal("PC", snapshot.Metadata.PlatformName);
        Assert.Equal("USA", snapshot.Metadata.RegionName);
        Assert.True(snapshot.Metadata.UsesEmulator);
        Assert.Equal("hard", snapshot.Metadata.Variables["difficulty"]);
        var custom = Assert.Single(snapshot.Metadata.CustomVariables);
        Assert.Equal("custom", custom.Name);
        Assert.Equal("custom-value", custom.Value);
        Assert.False(custom.IsPermanent);

        Assert.Equal(
            new[] { "Personal Best", "Best Segments", "Average Segments" },
            snapshot.Comparisons);

        Assert.Equal(2, snapshot.Segments.Count);
        var firstInfo = snapshot.Segments[0];
        Assert.Equal(0U, firstInfo.Index);
        Assert.Equal("First", firstInfo.Name);
        Assert.Equal(TimeSpan.FromSeconds(8).Ticks, firstInfo.BestSegmentTime.RealTimeTicks);
        Assert.Equal(TimeSpan.FromSeconds(7).Ticks, firstInfo.BestSegmentTime.GameTimeTicks);
        Assert.Equal("segment_value", firstInfo.CustomVariables["segment_var"]);

        var secondInfo = snapshot.Segments[1];
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
    public void BuildRunSnapshot_OmitsOptionalValuesWhenUnavailable()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("Only"));

        var adapter = new LiveSplitAdapter(TestLiveSplitState.Create(run));

        var snapshot = adapter.BuildRunSnapshot(runRevision: 1, stateRevision: 1, sessionId: 1);

        Assert.False(snapshot.HasFilePath);
        Assert.False(snapshot.HasLayoutPath);
        Assert.False(snapshot.Metadata.HasRunId);
        Assert.False(snapshot.Metadata.HasPlatformName);
        Assert.False(snapshot.Metadata.HasRegionName);
        Assert.False(snapshot.Metadata.UsesEmulator);
        Assert.Empty(snapshot.Metadata.Variables);
        Assert.Empty(snapshot.Metadata.CustomVariables);

        var segment = Assert.Single(snapshot.Segments);
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
