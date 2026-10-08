using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V3;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using ModelImage = System.Drawing.Image;
using ProtocolImage = LiveSplit.Bridge.Protocol.V3.Image;

namespace LiveSplit.Bridge.Tests;

public class RunStateCacheTests
{
    [Fact]
    public void FullBuildMapsRunFieldsMetadataComparisonTimesAndImages()
    {
        using var icon = new Bitmap(1, 1);
        var run = CreateRun();
        run.GameIcon = icon;
        run.FilePath = "run.lss";
        run.LayoutPath = "layout.lsl";
        run.Offset = TimeSpan.FromSeconds(-2);
        run.Metadata.RunID = "run-id";
        run.Metadata.PlatformName = "PC";
        run.Metadata.RegionName = "World";
        run.Metadata.UsesEmulator = true;
        run.Metadata.VariableValueNames["category-variable"] = "metadata-value";
        run.Metadata.GetOrAddCustomVariable("route").Value = "custom-value";
        run[2].Icon = icon;
        run[2].PersonalBestSplitTime = new Time(TimeSpan.FromSeconds(5), null);
        run[2].BestSegmentTime = new Time(TimeSpan.FromSeconds(5), null);

        var cache = new RunStateCache(TestLiveSplitState.Create(run));
        var published = cache.Current;

        Assert.Equal("Game", published.GameName);
        Assert.Equal("Any%", published.CategoryName);
        Assert.Equal(TimeSpan.FromSeconds(-2).Ticks, published.OffsetTicks);
        Assert.Equal("run.lss", published.FilePath);
        Assert.Equal("layout.lsl", published.LayoutPath);
        Assert.Equal("run-id", published.Metadata.RunId);
        Assert.Equal("PC", published.Metadata.PlatformName);
        Assert.Equal("World", published.Metadata.RegionName);
        Assert.True(published.Metadata.UsesEmulator);
        Assert.Equal("metadata-value", published.Metadata.Variables["category-variable"]);
        Assert.DoesNotContain("route", published.Metadata.Variables.Keys);
        Assert.Equal(3, published.Comparisons.Count);
        Assert.Equal(2U, published.Segments[2].Index);
        Assert.Equal("Third", published.Segments[2].Name);
        Assert.Contains(published.Segments[2].Comparisons, entry => entry.Name == Run.PersonalBestComparisonName);
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, published.Segments[2].BestSegmentTime.RealTimeTicks);
        Assert.Equal("image/png", published.GameIcon.MimeType);
        Assert.Equal("image/png", published.Segments[2].Icon.MimeType);
        Assert.Equal(2, cache.ImageEncodeCount);
    }

    [Fact]
    public void ResetUpdateRefreshesOnlyRunIdAndTimingWithoutEncodingImages()
    {
        using var icon = new Bitmap(1, 1);
        var run = CreateRun();
        run.GameIcon = icon;
        run[0].Icon = icon;
        run.Metadata.RunID = "old-id";
        var encoded = 0;
        var cache = new RunStateCache(TestLiveSplitState.Create(run), image => EncodeAndCount(image, ref encoded));
        Assert.Equal(2, encoded);

        run.Metadata.RunID = null;
        run[0].PersonalBestSplitTime = new Time(TimeSpan.FromSeconds(7), null);
        run[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(7), null);
        run[1].PersonalBestSplitTime = new Time(TimeSpan.FromSeconds(15), null);
        run[1].BestSegmentTime = new Time(TimeSpan.FromSeconds(8), null);

        Assert.True(cache.UpdateAfterReset());
        Assert.False(cache.Current.Metadata.HasRunId);
        Assert.Equal(TimeSpan.FromSeconds(7).Ticks, cache.Current.Segments[0].Comparisons[0].Time.RealTimeTicks);
        Assert.Equal(TimeSpan.FromSeconds(7).Ticks, cache.Current.Segments[0].BestSegmentTime.RealTimeTicks);
        Assert.Equal(2, encoded);

        var fresh = new RunStateCache(TestLiveSplitState.Create(run), image => EncodeAndCount(image, ref encoded));
        Assert.Equal(fresh.Current, cache.Current);
        Assert.Equal(4, encoded);
    }

    [Fact]
    public void FullRebuildReportsOnlyChangesToPublishedRunState()
    {
        var run = CreateRun();
        var state = TestLiveSplitState.Create(run);
        var cache = new RunStateCache(state);

        Assert.False(cache.Rebuild());
        run.HasChanged = true;
        Assert.False(cache.Rebuild());
        run.GameName = "Changed";
        Assert.True(cache.Rebuild());
        Assert.False(cache.Rebuild());

        var replacement = CreateRun();
        replacement.GameName = "Replacement";
        state.Run = replacement;
        Assert.True(cache.Rebuild());
        Assert.Same(replacement, cache.SourceRun);
    }

    [Fact]
    public void ResetUpdateFallsBackWhenPublishedStructureChanges()
    {
        var run = CreateRun();
        var cache = new RunStateCache(TestLiveSplitState.Create(run));
        run.Add(new Segment("Fourth"));

        Assert.True(cache.UpdateAfterReset());
        Assert.Equal(4, cache.Current.Segments.Count);
    }

    private static Run CreateRun()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory())
        {
            GameName = "Game",
            CategoryName = "Any%",
        };
        run.Add(new Segment("First"));
        run.Add(new Segment("Second"));
        run.Add(new Segment("Third"));
        return run;
    }

    private static ProtocolImage? EncodeAndCount(ModelImage? image, ref int count)
    {
        if (image == null) return null;
        count++;
        return new ProtocolImage { MimeType = "image/png", Data = ByteString.CopyFrom([1]), Width = 1, Height = 1 };
    }
}
