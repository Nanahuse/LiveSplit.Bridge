using System;
using System.Collections.Generic;

namespace LiveSplit.Bridge.Json;

internal static class RunSnapshot
{
    public static RunState CopyForTimingUpdate(RunState source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        var copy = new RunState
        {
            GameName = source.GameName,
            CategoryName = source.CategoryName,
            OffsetTicks = source.OffsetTicks,
            FilePath = source.FilePath,
            LayoutPath = source.LayoutPath,
            Metadata = CopyMetadata(source.Metadata),
            Comparisons = new List<string>(source.Comparisons),
            Segments = new List<SegmentInfo>(source.Segments.Count),
            // Published image buffers are shared and must be treated as immutable.
            GameIcon = source.GameIcon,
        };

        foreach (var segment in source.Segments)
            copy.Segments.Add(CopySegment(segment));

        return copy;
    }

    public static bool ContentEquals(RunState? left, RunState? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        return StringEquals(left.GameName, right.GameName)
            && StringEquals(left.CategoryName, right.CategoryName)
            && left.OffsetTicks == right.OffsetTicks
            && StringEquals(left.FilePath, right.FilePath)
            && StringEquals(left.LayoutPath, right.LayoutPath)
            && MetadataEquals(left.Metadata, right.Metadata)
            && ListEquals(left.Comparisons, right.Comparisons, StringEquals)
            && ListEquals(left.Segments, right.Segments, SegmentEquals)
            && BytesEqual(left.GameIcon, right.GameIcon);
    }

    private static RunMetadata? CopyMetadata(RunMetadata? source)
    {
        if (source is null) return null;

        return new RunMetadata
        {
            RunId = source.RunId,
            PlatformName = source.PlatformName,
            RegionName = source.RegionName,
            UsesEmulator = source.UsesEmulator,
            Variables = new Dictionary<string, string>(source.Variables, source.Variables.Comparer),
        };
    }

    private static SegmentInfo CopySegment(SegmentInfo source)
    {
        var copy = new SegmentInfo
        {
            Index = source.Index,
            Name = source.Name,
            Comparisons = new List<ComparisonTime>(source.Comparisons.Count),
            BestSegmentTime = CopyTimeValue(source.BestSegmentTime),
            // Published image buffers are shared and must be treated as immutable.
            Icon = source.Icon,
        };

        foreach (var comparison in source.Comparisons)
            copy.Comparisons.Add(CopyComparison(comparison));

        return copy;
    }

    private static ComparisonTime CopyComparison(ComparisonTime source) => new()
    {
        Name = source.Name,
        Time = CopyTimeValue(source.Time),
    };

    private static TimeValue CopyTimeValue(TimeValue source) => new()
    {
        RealTimeTicks = source.RealTimeTicks,
        GameTimeTicks = source.GameTimeTicks,
    };

    private static bool MetadataEquals(RunMetadata? left, RunMetadata? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        return StringEquals(left.RunId, right.RunId)
            && StringEquals(left.PlatformName, right.PlatformName)
            && StringEquals(left.RegionName, right.RegionName)
            && left.UsesEmulator == right.UsesEmulator
            && DictionaryEquals(left.Variables, right.Variables);
    }

    private static bool SegmentEquals(SegmentInfo? left, SegmentInfo? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        return left.Index == right.Index
            && StringEquals(left.Name, right.Name)
            && ListEquals(left.Comparisons, right.Comparisons, ComparisonEquals)
            && TimeValueEquals(left.BestSegmentTime, right.BestSegmentTime)
            && BytesEqual(left.Icon, right.Icon);
    }

    private static bool ComparisonEquals(ComparisonTime? left, ComparisonTime? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        return StringEquals(left.Name, right.Name)
            && TimeValueEquals(left.Time, right.Time);
    }

    private static bool TimeValueEquals(TimeValue? left, TimeValue? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        return left.RealTimeTicks == right.RealTimeTicks
            && left.GameTimeTicks == right.GameTimeTicks;
    }

    private static bool DictionaryEquals(
        Dictionary<string, string>? left,
        Dictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;

        foreach (var pair in left)
        {
            var found = false;
            foreach (var candidate in right)
            {
                if (!StringEquals(pair.Key, candidate.Key)) continue;
                if (!StringEquals(pair.Value, candidate.Value)) return false;
                found = true;
                break;
            }

            if (!found) return false;
        }

        return true;
    }

    private static bool ListEquals<T>(
        List<T>? left,
        List<T>? right,
        Func<T?, T?, bool> elementEquals)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;

        for (var index = 0; index < left.Count; index++)
            if (!elementEquals(left[index], right[index])) return false;

        return true;
    }

    private static bool BytesEqual(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Length != right.Length) return false;

        for (var index = 0; index < left.Length; index++)
            if (left[index] != right[index]) return false;

        return true;
    }

    private static bool StringEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);
}

internal static class ContextSnapshot
{
    public static bool ContentEquals(ContextState? left, ContextState? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        return left.CurrentTimingMethod == right.CurrentTimingMethod
            && string.Equals(left.CurrentComparison, right.CurrentComparison, StringComparison.Ordinal)
            && ContextVariablesEqual(left.CustomVariables, right.CustomVariables);
    }

    private static bool ContextVariablesEqual(
        Dictionary<string, string>? left,
        Dictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;

        foreach (var pair in left)
        {
            var found = false;
            foreach (var candidate in right)
            {
                if (!string.Equals(pair.Key, candidate.Key, StringComparison.Ordinal)) continue;
                if (!string.Equals(pair.Value, candidate.Value, StringComparison.Ordinal)) return false;
                found = true;
                break;
            }

            if (!found) return false;
        }

        return true;
    }
}
