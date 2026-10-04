using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveSplit.Bridge;

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
