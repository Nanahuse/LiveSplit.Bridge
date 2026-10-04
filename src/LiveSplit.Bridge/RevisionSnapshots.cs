using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using ProtoAttemptState = LiveSplit.Bridge.Protocol.V2.AttemptState;
using ProtoRunState = LiveSplit.Bridge.Protocol.V2.RunState;
using ProtoRuntimeState = LiveSplit.Bridge.Protocol.V2.RuntimeState;

namespace LiveSplit.Bridge;

/// <summary>
/// Result of a single UI-thread capture. <see cref="Revision"/> and
/// <see cref="State"/> are always produced from the same LiveSplit state read so the
/// returned State and its revision describe the same point in time.
/// </summary>
internal readonly struct CapturedRunState
{
    public CapturedRunState(RunRevisionState revision, ProtoRunState state)
    {
        Revision = revision;
        State = state;
    }

    public RunRevisionState Revision { get; }
    public ProtoRunState State { get; }
}

internal readonly struct CapturedAttemptState
{
    public CapturedAttemptState(AttemptRevisionState revision, ProtoAttemptState state)
    {
        Revision = revision;
        State = state;
    }

    public AttemptRevisionState Revision { get; }
    public ProtoAttemptState State { get; }
}

internal readonly struct CapturedRuntimeState
{
    public CapturedRuntimeState(RuntimeRevisionState revision, ProtoRuntimeState state)
    {
        Revision = revision;
        State = state;
    }

    public RuntimeRevisionState Revision { get; }
    public ProtoRuntimeState State { get; }
}

/// <summary>
/// Content based fingerprint of an icon so that different <see cref="Image"/> instances
/// with identical data compare equal, while a mutated image is detected as a change.
/// </summary>
internal readonly struct ImageFingerprint : IEquatable<ImageFingerprint>
{
    public static readonly ImageFingerprint None = default;

    private ImageFingerprint(bool hasValue, ulong hash)
    {
        HasValue = hasValue;
        Hash = hash;
    }

    public bool HasValue { get; }
    public ulong Hash { get; }

    public bool Equals(ImageFingerprint other)
    {
        return HasValue == other.HasValue && (!HasValue || Hash == other.Hash);
    }

    public override bool Equals(object obj)
    {
        return obj is ImageFingerprint other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return (HasValue.GetHashCode() * 397) ^ Hash.GetHashCode();
        }
    }

    public static ImageFingerprint FromImage(Image image)
    {
        if (image == null)
        {
            return None;
        }

        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return new ImageFingerprint(true, Fnv1a(stream.GetBuffer(), (int)stream.Length));
    }

    private static ulong Fnv1a(byte[] buffer, int length)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;
        for (var index = 0; index < length; index++)
        {
            hash ^= buffer[index];
            hash *= prime;
        }

        return hash;
    }
}

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
        ImageFingerprint icon)
    {
        Index = index;
        Name = name;
        BestSegmentTime = bestSegmentTime;
        Comparisons = comparisons;
        Icon = icon;
    }

    public uint Index { get; }
    public string Name { get; }
    public TimeSnapshot BestSegmentTime { get; }
    public IReadOnlyList<KeyValuePair<string, TimeSnapshot>> Comparisons { get; }
    public ImageFingerprint Icon { get; }

    public bool Equals(SegmentSnapshot other)
    {
        return Index == other.Index
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && BestSegmentTime.Equals(other.BestSegmentTime)
            && Icon.Equals(other.Icon)
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
            hashCode = (hashCode * 397) ^ Icon.GetHashCode();
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
        ImageFingerprint gameIcon)
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
        GameIcon = gameIcon;
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
    public ImageFingerprint GameIcon { get; }

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
            && GameIcon.Equals(other.GameIcon)
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
            hashCode = (hashCode * 397) ^ GameIcon.GetHashCode();
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
    private static readonly IReadOnlyList<KeyValuePair<string, string>> EmptyVariables =
        Array.Empty<KeyValuePair<string, string>>();

    public RuntimeRevisionState(
        int timingMethod,
        string currentComparison,
        bool globalHotkeysEnabled,
        IReadOnlyList<KeyValuePair<string, string>> customVariables)
    {
        TimingMethod = timingMethod;
        CurrentComparison = currentComparison;
        GlobalHotkeysEnabled = globalHotkeysEnabled;
        CustomVariables = customVariables ?? EmptyVariables;
    }

    public int TimingMethod { get; }
    public string CurrentComparison { get; }
    public bool GlobalHotkeysEnabled { get; }
    public IReadOnlyList<KeyValuePair<string, string>> CustomVariables { get; }

    public bool Equals(RuntimeRevisionState other)
    {
        return TimingMethod == other.TimingMethod
            && string.Equals(CurrentComparison, other.CurrentComparison, StringComparison.Ordinal)
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
    public static IReadOnlyList<KeyValuePair<string, string>> EmptyMap { get; } =
        Array.Empty<KeyValuePair<string, string>>();

    public static IReadOnlyList<KeyValuePair<string, string>> OrderMap(
        IEnumerable<KeyValuePair<string, string>> source)
    {
        if (source == null)
        {
            return EmptyMap;
        }

        return source
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value ?? string.Empty))
            .ToList();
    }
}
