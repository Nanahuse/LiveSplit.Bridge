using System.Collections.Generic;
using System.Linq;
using LiveSplit.Bridge.Json;

namespace LiveSplit.Bridge.Tests;

public class SnapshotOperationsTests
{
    public static IEnumerable<object[]> RunStateMutations()
    {
        return new[]
        {
            "game name", "category name", "offset ticks", "file path", "layout path",
            "metadata null", "run id", "platform", "region", "uses emulator", "metadata variable",
            "comparison value", "comparison order", "segment added", "segment removed", "segment order",
            "segment index", "segment name", "segment comparison order", "best segment time",
            "comparison name", "comparison time",
        }.Select(mutation => new object[] { mutation });
    }

    [Fact]
    public void RunContentEquals_RecognizesEqualDistinctSnapshotsAndDictionaryOrder()
    {
        var left = CreateRunState();
        var right = CreateRunState();
        right.Metadata!.Variables = new Dictionary<string, string>
        {
            ["region"] = "US",
            ["character"] = "A",
        };

        Assert.True(RunSnapshot.ContentEquals(left, right));
        Assert.True(RunSnapshot.ContentEquals(null, null));
        Assert.False(RunSnapshot.ContentEquals(left, null));
        Assert.False(RunSnapshot.ContentEquals(null, right));

        right.Metadata.Variables = new Dictionary<string, string>(right.Metadata.Variables, StringComparer.OrdinalIgnoreCase);
        Assert.True(RunSnapshot.ContentEquals(left, right));
        right.Metadata.Variables.Remove("character");
        right.Metadata.Variables["Character"] = "A";
        Assert.False(RunSnapshot.ContentEquals(left, right));
    }

    [Theory]
    [MemberData(nameof(RunStateMutations))]
    public void RunContentEquals_DetectsChangesToEveryPublishedField(string mutation)
    {
        var original = CreateRunState();
        var changed = CreateRunState();

        ApplyMutation(changed, mutation);

        Assert.False(RunSnapshot.ContentEquals(original, changed), mutation);
    }

    [Fact]
    public void ContextContentEquals_UsesValuesAndIgnoresDictionaryInsertionOrder()
    {
        var left = CreateContextState();
        var right = CreateContextState();
        right.CustomVariables = new Dictionary<string, string>
        {
            ["region"] = "US",
            ["character"] = "A",
        };

        Assert.True(ContextSnapshot.ContentEquals(left, right));
        Assert.True(ContextSnapshot.ContentEquals(null, null));
        Assert.False(ContextSnapshot.ContentEquals(left, null));
        Assert.False(ContextSnapshot.ContentEquals(null, right));

        right.CurrentTimingMethod = TimingMethod.RealTime;
        Assert.False(ContextSnapshot.ContentEquals(left, right));
        right.CurrentTimingMethod = left.CurrentTimingMethod;
        right.CurrentComparison = "Best Segments";
        Assert.False(ContextSnapshot.ContentEquals(left, right));
        right.CurrentComparison = left.CurrentComparison;
        right.CustomVariables["character"] = "B";
        Assert.False(ContextSnapshot.ContentEquals(left, right));
        right.CustomVariables["character"] = "A";
        right.CustomVariables = new Dictionary<string, string>(right.CustomVariables, StringComparer.OrdinalIgnoreCase);
        Assert.True(ContextSnapshot.ContentEquals(left, right));
        right.CustomVariables.Remove("character");
        right.CustomVariables["Character"] = "A";
        Assert.False(ContextSnapshot.ContentEquals(left, right));
    }

    [Fact]
    public void CopyForTimingUpdate_IsolatesMutableState()
    {
        var source = CreateRunState();
        var originalValues = CreateRunState();
        var copy = RunSnapshot.CopyForTimingUpdate(source);

        Assert.True(RunSnapshot.ContentEquals(source, copy));

        copy.GameName = "Changed Game";
        copy.Comparisons.Add("New Comparison");
        copy.Metadata!.RunId = null;
        copy.Metadata.Variables["character"] = "B";
        copy.Metadata.Variables.Add("new", "value");
        copy.Segments[0].Name = "Changed Segment";
        copy.Segments[0].BestSegmentTime.RealTimeTicks = 999;
        copy.Segments[0].Comparisons[0].Name = "Changed Comparison";
        copy.Segments[0].Comparisons[0].Time.GameTimeTicks = 999;
        copy.Segments[0].Comparisons.RemoveAt(1);
        copy.Segments.Add(new SegmentInfo { Name = "New Segment" });

        Assert.True(RunSnapshot.ContentEquals(source, originalValues));
    }

    private static void ApplyMutation(RunState run, string mutation)
    {
        switch (mutation)
        {
            case "game name": run.GameName = "Other Game"; break;
            case "category name": run.CategoryName = "Other Category"; break;
            case "offset ticks": run.OffsetTicks++; break;
            case "file path": run.FilePath = null; break;
            case "layout path": run.LayoutPath = "other.lsl"; break;
            case "metadata null": run.Metadata = null; break;
            case "run id": run.Metadata!.RunId = null; break;
            case "platform": run.Metadata!.PlatformName = "Console"; break;
            case "region": run.Metadata!.RegionName = "JP"; break;
            case "uses emulator": run.Metadata!.UsesEmulator = true; break;
            case "metadata variable": run.Metadata!.Variables["character"] = "B"; break;
            case "comparison value": run.Comparisons[0] = "Other Comparison"; break;
            case "comparison order": run.Comparisons.Reverse(); break;
            case "segment added": run.Segments.Add(new SegmentInfo { Name = "Added" }); break;
            case "segment removed": run.Segments.RemoveAt(1); break;
            case "segment order": run.Segments.Reverse(); break;
            case "segment index": run.Segments[0].Index++; break;
            case "segment name": run.Segments[0].Name = "Other Segment"; break;
            case "segment comparison order": run.Segments[0].Comparisons.Reverse(); break;
            case "best segment time": run.Segments[0].BestSegmentTime.RealTimeTicks++; break;
            case "comparison name": run.Segments[0].Comparisons[0].Name = "Other Comparison"; break;
            case "comparison time": run.Segments[0].Comparisons[0].Time.GameTimeTicks++; break;
            default: throw new KeyNotFoundException(mutation);
        }
    }

    private static RunState CreateRunState() => new()
    {
        GameName = "Game",
        CategoryName = "Any%",
        OffsetTicks = -100,
        FilePath = "run.lss",
        LayoutPath = null,
        Metadata = new RunMetadata
        {
            RunId = "run-id",
            PlatformName = "PC",
            RegionName = "US",
            UsesEmulator = false,
            Variables = new Dictionary<string, string>
            {
                ["character"] = "A",
                ["region"] = "US",
            },
        },
        Comparisons = new List<string> { "Personal Best", "Best Segments" },
        Segments = new List<SegmentInfo>
        {
            CreateSegment(0, "Opening"),
            CreateSegment(1, "Finale"),
        },
    };

    private static SegmentInfo CreateSegment(uint index, string name) => new()
    {
        Index = index,
        Name = name,
        Comparisons = new List<ComparisonTime>
        {
            new() { Name = "Personal Best", Time = new TimeValue { RealTimeTicks = 100, GameTimeTicks = 90 } },
            new() { Name = "Best Segments", Time = new TimeValue { RealTimeTicks = 80, GameTimeTicks = null } },
        },
        BestSegmentTime = new TimeValue { RealTimeTicks = 70, GameTimeTicks = 60 },
    };

    private static ContextState CreateContextState() => new()
    {
        CurrentTimingMethod = TimingMethod.GameTime,
        CurrentComparison = "Personal Best",
        CustomVariables = new Dictionary<string, string>
        {
            ["character"] = "A",
            ["region"] = "US",
        },
    };
}
