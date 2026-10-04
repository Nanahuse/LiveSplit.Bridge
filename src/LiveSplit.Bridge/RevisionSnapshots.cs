using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace LiveSplit.Bridge;

internal readonly struct TimeSnapshot : IEquatable<TimeSnapshot>
{
    public TimeSnapshot(long? realTimeTicks, long? gameTimeTicks)
    {
        RealTimeTicks = realTimeTicks;
        GameTimeTicks = gameTimeTicks;
    }

    public long? RealTimeTicks { get; }
    public long? GameTimeTicks { get; }

    public bool Equals(TimeSnapshot other)
    {
        return RealTimeTicks == other.RealTimeTicks
            && GameTimeTicks == other.GameTimeTicks;
    }

    public override bool Equals(object obj)
    {
        return obj is TimeSnapshot other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return (RealTimeTicks.GetHashCode() * 397) ^ GameTimeTicks.GetHashCode();
        }
    }
}

internal readonly struct SegmentSnapshot : IEquatable<SegmentSnapshot>
{
    public SegmentSnapshot(
        uint index,
        string name,
        TimeSnapshot bestSegmentTime,
        IReadOnlyList<KeyValuePair<string, TimeSnapshot>> comparisons,
        int? iconIdentity)
    {
        Index = index;
        Name = name;
        BestSegmentTime = bestSegmentTime;
        Comparisons = comparisons;
        IconIdentity = iconIdentity;
    }

    public uint Index { get; }
    public string Name { get; }
    public TimeSnapshot BestSegmentTime { get; }
    public IReadOnlyList<KeyValuePair<string, TimeSnapshot>> Comparisons { get; }
    public int? IconIdentity { get; }

    public bool Equals(SegmentSnapshot other)
    {
        return Index == other.Index
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && BestSegmentTime.Equals(other.BestSegmentTime)
            && IconIdentity == other.IconIdentity
            && ComparisonsEqual(Comparisons, other.Comparisons);
    }

    public override bool Equals(object obj)
    {
        return obj is SegmentSnapshot other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = Index.GetHashCode();
            hashCode = (hashCode * 397) ^ Name.GetHashCode();
            hashCode = (hashCode * 397) ^ BestSegmentTime.GetHashCode();
            hashCode = (hashCode * 397) ^ IconIdentity.GetHashCode();
            return hashCode;
        }
    }

    private static bool ComparisonsEqual(
        IReadOnlyList<KeyValuePair<string, TimeSnapshot>> left,
        IReadOnlyList<KeyValuePair<string, TimeSnapshot>> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Key, right[index].Key, StringComparison.Ordinal)
                || !left[index].Value.Equals(right[index].Value))
            {
                return false;
            }
        }

        return true;
    }
}

internal readonly struct RunRevisionState : IEquatable<RunRevisionState>
{
    public RunRevisionState(
        string gameName,
        string categoryName,
        long offsetTicks,
        string filePath,
        string layoutPath,
        string runId,
        string platformName,
        string regionName,
        bool usesEmulator,
        IReadOnlyList<KeyValuePair<string, string>> variables,
        IReadOnlyList<string> comparisons,
        IReadOnlyList<SegmentSnapshot> segments,
        int? gameIconIdentity)
    {
        GameName = gameName;
        CategoryName = categoryName;
        OffsetTicks = offsetTicks;
        FilePath = filePath;
        LayoutPath = layoutPath;
        RunId = runId;
        PlatformName = platformName;
        RegionName = regionName;
        UsesEmulator = usesEmulator;
        Variables = variables;
        Comparisons = comparisons;
        Segments = segments;
        GameIconIdentity = gameIconIdentity;
    }

    public string GameName { get; }
    public string CategoryName { get; }
    public long OffsetTicks { get; }
    public string FilePath { get; }
    public string LayoutPath { get; }
    public string RunId { get; }
    public string PlatformName { get; }
    public string RegionName { get; }
    public bool UsesEmulator { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Variables { get; }
    public IReadOnlyList<string> Comparisons { get; }
    public IReadOnlyList<SegmentSnapshot> Segments { get; }
    public int? GameIconIdentity { get; }

    public bool Equals(RunRevisionState other)
    {
        return string.Equals(GameName, other.GameName, StringComparison.Ordinal)
            && string.Equals(CategoryName, other.CategoryName, StringComparison.Ordinal)
            && OffsetTicks == other.OffsetTicks
            && string.Equals(FilePath, other.FilePath, StringComparison.Ordinal)
            && string.Equals(LayoutPath, other.LayoutPath, StringComparison.Ordinal)
            && string.Equals(RunId, other.RunId, StringComparison.Ordinal)
            && string.Equals(PlatformName, other.PlatformName, StringComparison.Ordinal)
            && string.Equals(RegionName, other.RegionName, StringComparison.Ordinal)
            && UsesEmulator == other.UsesEmulator
            && GameIconIdentity == other.GameIconIdentity
            && MapEqual(Variables, other.Variables)
            && ListEqual(Comparisons, other.Comparisons)
            && ListEqual(Segments, other.Segments);
    }

    public override bool Equals(object obj)
    {
        return obj is RunRevisionState other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = GameName?.GetHashCode() ?? 0;
            hashCode = (hashCode * 397) ^ (CategoryName?.GetHashCode() ?? 0);
            hashCode = (hashCode * 397) ^ OffsetTicks.GetHashCode();
            hashCode = (hashCode * 397) ^ (FilePath?.GetHashCode() ?? 0);
            hashCode = (hashCode * 397) ^ (LayoutPath?.GetHashCode() ?? 0);
            hashCode = (hashCode * 397) ^ (RunId?.GetHashCode() ?? 0);
            hashCode = (hashCode * 397) ^ (PlatformName?.GetHashCode() ?? 0);
            hashCode = (hashCode * 397) ^ (RegionName?.GetHashCode() ?? 0);
            hashCode = (hashCode * 397) ^ UsesEmulator.GetHashCode();
            hashCode = (hashCode * 397) ^ (GameIconIdentity?.GetHashCode() ?? 0);
            hashCode = (hashCode * 397) ^ Segments.Count;
            return hashCode;
        }
    }

    private static bool MapEqual(
        IReadOnlyList<KeyValuePair<string, string>> left,
        IReadOnlyList<KeyValuePair<string, string>> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Key, right[index].Key, StringComparison.Ordinal)
                || !string.Equals(left[index].Value, right[index].Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ListEqual<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
        where T : IEquatable<T>
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!left[index].Equals(right[index]))
            {
                return false;
            }
        }

        return true;
    }
}

internal readonly struct AttemptSegmentSnapshot : IEquatable<AttemptSegmentSnapshot>
{
    public AttemptSegmentSnapshot(
        uint index,
        TimeSnapshot splitTime,
        IReadOnlyList<KeyValuePair<string, string>> customVariables)
    {
        Index = index;
        SplitTime = splitTime;
        CustomVariables = customVariables;
    }

    public uint Index { get; }
    public TimeSnapshot SplitTime { get; }
    public IReadOnlyList<KeyValuePair<string, string>> CustomVariables { get; }

    public bool Equals(AttemptSegmentSnapshot other)
    {
        return Index == other.Index
            && SplitTime.Equals(other.SplitTime)
            && MapEqual(CustomVariables, other.CustomVariables);
    }

    public override bool Equals(object obj)
    {
        return obj is AttemptSegmentSnapshot other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return (Index.GetHashCode() * 397) ^ SplitTime.GetHashCode();
        }
    }

    private static bool MapEqual(
        IReadOnlyList<KeyValuePair<string, string>> left,
        IReadOnlyList<KeyValuePair<string, string>> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Key, right[index].Key, StringComparison.Ordinal)
                || !string.Equals(left[index].Value, right[index].Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

internal readonly struct AttemptRevisionState : IEquatable<AttemptRevisionState>
{
    public AttemptRevisionState(
        uint attemptCount,
        uint completedCount,
        IReadOnlyList<AttemptSegmentSnapshot> segments)
    {
        AttemptCount = attemptCount;
        CompletedCount = completedCount;
        Segments = segments;
    }

    public uint AttemptCount { get; }
    public uint CompletedCount { get; }
    public IReadOnlyList<AttemptSegmentSnapshot> Segments { get; }

    public bool Equals(AttemptRevisionState other)
    {
        if (AttemptCount != other.AttemptCount
            || CompletedCount != other.CompletedCount
            || Segments == null
            || other.Segments == null
            || Segments.Count != other.Segments.Count)
        {
            return false;
        }

        for (var index = 0; index < Segments.Count; index++)
        {
            if (!Segments[index].Equals(other.Segments[index]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object obj)
    {
        return obj is AttemptRevisionState other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return ((AttemptCount.GetHashCode() * 397) ^ CompletedCount.GetHashCode());
        }
    }
}

internal readonly struct RuntimeRevisionState : IEquatable<RuntimeRevisionState>
{
    public RuntimeRevisionState(
        int timingMethod,
        string currentComparison,
        string currentHotkeyProfile,
        bool globalHotkeysEnabled,
        IReadOnlyList<KeyValuePair<string, string>> customVariables)
    {
        TimingMethod = timingMethod;
        CurrentComparison = currentComparison;
        CurrentHotkeyProfile = currentHotkeyProfile;
        GlobalHotkeysEnabled = globalHotkeysEnabled;
        CustomVariables = customVariables;
    }

    public int TimingMethod { get; }
    public string CurrentComparison { get; }
    public string CurrentHotkeyProfile { get; }
    public bool GlobalHotkeysEnabled { get; }
    public IReadOnlyList<KeyValuePair<string, string>> CustomVariables { get; }

    public bool Equals(RuntimeRevisionState other)
    {
        return TimingMethod == other.TimingMethod
            && string.Equals(CurrentComparison, other.CurrentComparison, StringComparison.Ordinal)
            && string.Equals(CurrentHotkeyProfile, other.CurrentHotkeyProfile, StringComparison.Ordinal)
            && GlobalHotkeysEnabled == other.GlobalHotkeysEnabled
            && MapEqual(CustomVariables, other.CustomVariables);
    }

    public override bool Equals(object obj)
    {
        return obj is RuntimeRevisionState other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = TimingMethod.GetHashCode();
            hashCode = (hashCode * 397) ^ (CurrentComparison?.GetHashCode() ?? 0);
            hashCode = (hashCode * 397) ^ (CurrentHotkeyProfile?.GetHashCode() ?? 0);
            hashCode = (hashCode * 397) ^ GlobalHotkeysEnabled.GetHashCode();
            return hashCode;
        }
    }

    private static bool MapEqual(
        IReadOnlyList<KeyValuePair<string, string>> left,
        IReadOnlyList<KeyValuePair<string, string>> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Key, right[index].Key, StringComparison.Ordinal)
                || !string.Equals(left[index].Value, right[index].Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

internal static class RevisionSnapshotFactory
{
    public static IReadOnlyList<KeyValuePair<string, string>> OrderMap(
        IEnumerable<KeyValuePair<string, string>> source)
    {
        if (source == null)
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }

        return source
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value ?? string.Empty))
            .ToList();
    }

    public static int? Identity(Image image)
    {
        return image == null ? (int?)null : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(image);
    }
}
