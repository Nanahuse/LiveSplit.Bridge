#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Json = LiveSplit.Bridge.Json;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using ModelTimingMethod = LiveSplit.Model.TimingMethod;

namespace LiveSplit.Bridge;

/// <summary>Maps LiveSplit state directly to the JSON protocol DTOs.</summary>
internal sealed class JsonLiveSplitAdapter : IJsonLiveSplitAdapter
{
    private readonly LiveSplitState state;
    private readonly TimerModel timerModel;

    public JsonLiveSplitAdapter(LiveSplitState state)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        timerModel = new TimerModel { CurrentState = state };
    }

    public Json.TimerState GetTimerState()
    {
        var currentTime = state.CurrentTime;
        return new Json.TimerState
        {
            Phase = MapTimerPhase(state.CurrentPhase),
            SplitIndex = state.CurrentSplitIndex,
            RealTimeTicks = currentTime.RealTime?.Ticks ?? 0,
            GameTimeTicks = currentTime.GameTime?.Ticks,
            IsGameTimeInitialized = state.IsGameTimeInitialized,
            IsGameTimePaused = state.IsGameTimePaused,
        };
    }

    public Json.AttemptState GetAttempt()
    {
        var run = state.Run;
        var result = new Json.AttemptState
        {
            AttemptCount = run != null && run.AttemptCount > 0 ? (uint)run.AttemptCount : 0U,
        };
        if (run == null) return result;

        for (var index = 0; index < run.Count; index++)
        {
            var segment = run[index];
            var item = new Json.AttemptSegment
            {
                Index = (uint)index,
                SplitTime = MapTime(segment.SplitTime),
            };
            if (segment.CustomVariableValues != null)
                foreach (var pair in segment.CustomVariableValues)
                    item.CustomVariables[pair.Key] = pair.Value ?? string.Empty;
            result.Segments.Add(item);
        }
        return result;
    }

    public Json.CompletedCount GetCompletedCount()
    {
        var attempts = state.Run?.AttemptHistory;
        return new Json.CompletedCount
        {
            Value = attempts == null ? 0U : (uint)attempts.Count(attempt => attempt.Time.RealTime != null),
        };
    }

    public Json.ContextState GetContextState()
    {
        var result = new Json.ContextState
        {
            CurrentTimingMethod = state.CurrentTimingMethod switch
            {
                ModelTimingMethod.RealTime => Json.TimingMethod.RealTime,
                ModelTimingMethod.GameTime => Json.TimingMethod.GameTime,
                _ => Json.TimingMethod.Unspecified,
            },
            CurrentComparison = state.CurrentComparison ?? string.Empty,
        };
        var variables = state.Run?.Metadata?.CustomVariables;
        if (variables != null)
            foreach (var pair in variables)
                result.CustomVariables[pair.Key] = pair.Value?.Value ?? string.Empty;
        return result;
    }

    public Json.RunState GetRunState()
    {
        using var candidate = CaptureRunState();
        return candidate.Build();
    }

    public IJsonRunStateCandidate CaptureRunState()
    {
        var run = state.Run;
        var result = new Json.RunState
        {
            GameName = run?.GameName ?? string.Empty,
            CategoryName = run?.CategoryName ?? string.Empty,
            OffsetTicks = run?.Offset.Ticks ?? 0,
        };
        var images = new List<Bitmap?>();
        if (run == null) return new JsonRunStateBuildCandidate(result, images);

        try
        {
            result.Metadata = new Json.RunMetadata();
            if (!string.IsNullOrEmpty(run.FilePath)) result.FilePath = run.FilePath;
            if (!string.IsNullOrEmpty(run.LayoutPath)) result.LayoutPath = run.LayoutPath;
            var metadata = run.Metadata;
            if (metadata != null)
            {
                result.Metadata.RunId = metadata.RunID;
                if (!string.IsNullOrEmpty(metadata.PlatformName)) result.Metadata.PlatformName = metadata.PlatformName;
                if (!string.IsNullOrEmpty(metadata.RegionName)) result.Metadata.RegionName = metadata.RegionName;
                result.Metadata.UsesEmulator = metadata.UsesEmulator;
                // RunState variables describe VariableValueNames, not current custom-variable values.
                if (metadata.VariableValueNames != null)
                    foreach (var pair in metadata.VariableValueNames)
                        result.Metadata.Variables[pair.Key] = pair.Value ?? string.Empty;
            }

            result.Comparisons.AddRange((run.Comparisons ?? Enumerable.Empty<string>()).Distinct());
            images.Add(CloneImage(run.GameIcon));
            for (var index = 0; index < run.Count; index++)
            {
                var segment = run[index];
                var info = new Json.SegmentInfo { Index = (uint)index, Name = segment.Name ?? string.Empty };
                foreach (var comparison in result.Comparisons)
                    info.Comparisons.Add(new Json.ComparisonTime
                    {
                        Name = comparison,
                        Time = MapTime(segment.Comparisons, comparison),
                    });
                info.BestSegmentTime = MapTime(segment.BestSegmentTime);
                images.Add(CloneImage(segment.Icon));
                result.Segments.Add(info);
            }
            return new JsonRunStateBuildCandidate(result, images);
        }
        catch
        {
            foreach (var image in images) image?.Dispose();
            throw;
        }
    }

    public bool TryUpdateRunTimings(Json.RunState published, out Json.RunState updated)
    {
        var run = state.Run;
        if (run == null || !MatchesRunStructure(published, run))
        {
            updated = published;
            return false;
        }

        var result = Json.RunSnapshot.CopyForTimingUpdate(published);
        for (var index = 0; index < run.Count; index++)
        {
            var source = run[index];
            var target = result.Segments[index];
            target.BestSegmentTime = MapTime(source.BestSegmentTime);
            for (var comparisonIndex = 0; comparisonIndex < target.Comparisons.Count; comparisonIndex++)
                target.Comparisons[comparisonIndex].Time = MapTime(source.Comparisons, target.Comparisons[comparisonIndex].Name);
        }

        result.Metadata ??= new Json.RunMetadata();
        result.Metadata.RunId = run.Metadata?.RunID;
        updated = result;
        return true;
    }

    public static bool TryMergeRunTimings(Json.RunState candidate, Json.RunState latest, out Json.RunState merged)
    {
        if (!MatchesSnapshotStructure(candidate, latest))
        {
            merged = candidate;
            return false;
        }

        merged = Json.RunSnapshot.CopyForTimingUpdate(candidate);
        merged.Metadata ??= new Json.RunMetadata();
        merged.Metadata.RunId = latest.Metadata?.RunId;
        for (var segmentIndex = 0; segmentIndex < merged.Segments.Count; segmentIndex++)
        {
            var target = merged.Segments[segmentIndex];
            var source = latest.Segments[segmentIndex];
            target.BestSegmentTime = CopyTime(source.BestSegmentTime);
            for (var comparisonIndex = 0; comparisonIndex < target.Comparisons.Count; comparisonIndex++)
                target.Comparisons[comparisonIndex].Time = CopyTime(source.Comparisons[comparisonIndex].Time);
        }
        return true;
    }

    public bool IsTimerOnlyRun()
    {
        var run = state.Run;
        return run != null
            && run.Count == 1
            && string.IsNullOrEmpty(run.GameName)
            && string.IsNullOrEmpty(run.CategoryName)
            && string.IsNullOrEmpty(run[0].Name)
            && run.GameIcon == null
            && run[0].Icon == null;
    }

    public void ExecuteTimerOperation(Json.TimerOperationParams operation)
    {
        switch (operation)
        {
            case Json.TimerStartParams: timerModel.Start(); break;
            case Json.TimerSplitParams: timerModel.Split(); break;
            case Json.TimerSkipParams: timerModel.SkipSplit(); break;
            case Json.TimerUndoParams: timerModel.UndoSplit(); break;
            case Json.TimerResetParams: timerModel.Reset(); break;
            case Json.TimerPauseParams:
            case Json.TimerResumeParams: timerModel.Pause(); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported timer operation.");
        }
    }

    public void ExecuteGameTimeOperation(Json.GameTimeOperationParams operation)
    {
        switch (operation)
        {
            case Json.GameTimeInitializeParams: timerModel.InitializeGameTime(); break;
            case Json.GameTimeSetParams set: state.SetGameTime(TimeSpan.FromTicks(set.Ticks)); break;
            case Json.GameTimePauseParams: state.IsGameTimePaused = true; break;
            case Json.GameTimeResumeParams: state.IsGameTimePaused = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported game time operation.");
        }
    }

    private static bool MatchesRunStructure(Json.RunState published, IRun run)
    {
        var comparisons = (run.Comparisons ?? Enumerable.Empty<string>()).Distinct().ToArray();
        if (published.Segments.Count != run.Count || published.Comparisons.Count != comparisons.Length) return false;
        for (var index = 0; index < comparisons.Length; index++)
            if (!string.Equals(comparisons[index], published.Comparisons[index], StringComparison.Ordinal)) return false;

        for (var segmentIndex = 0; segmentIndex < run.Count; segmentIndex++)
        {
            var segment = published.Segments[segmentIndex];
            if (!string.Equals(segment.Name, run[segmentIndex].Name ?? string.Empty, StringComparison.Ordinal)
                || segment.Comparisons.Count != published.Comparisons.Count) return false;
            for (var comparisonIndex = 0; comparisonIndex < published.Comparisons.Count; comparisonIndex++)
                if (!string.Equals(segment.Comparisons[comparisonIndex].Name, published.Comparisons[comparisonIndex], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool MatchesSnapshotStructure(Json.RunState left, Json.RunState right)
    {
        if (left.Segments.Count != right.Segments.Count || left.Comparisons.Count != right.Comparisons.Count) return false;
        for (var i = 0; i < left.Comparisons.Count; i++)
            if (!string.Equals(left.Comparisons[i], right.Comparisons[i], StringComparison.Ordinal)) return false;
        for (var segmentIndex = 0; segmentIndex < left.Segments.Count; segmentIndex++)
        {
            var a = left.Segments[segmentIndex];
            var b = right.Segments[segmentIndex];
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal) || a.Comparisons.Count != b.Comparisons.Count) return false;
            for (var comparisonIndex = 0; comparisonIndex < a.Comparisons.Count; comparisonIndex++)
                if (!string.Equals(a.Comparisons[comparisonIndex].Name, b.Comparisons[comparisonIndex].Name, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static Json.TimeValue MapTime(Time time) => new()
    {
        RealTimeTicks = time.RealTime?.Ticks,
        GameTimeTicks = time.GameTime?.Ticks,
    };

    private static Json.TimeValue MapTime(IComparisons? comparisons, string name) =>
        comparisons != null && comparisons.TryGetValue(name, out var time) ? MapTime(time) : new Json.TimeValue();

    private static Json.TimeValue CopyTime(Json.TimeValue source) => new()
    {
        RealTimeTicks = source.RealTimeTicks,
        GameTimeTicks = source.GameTimeTicks,
    };

    private static Bitmap? CloneImage(Image? image) => image == null ? null : new Bitmap(image);

    private static Json.TimerPhase MapTimerPhase(LiveSplit.Model.TimerPhase phase) => phase switch
    {
        LiveSplit.Model.TimerPhase.NotRunning => Json.TimerPhase.NotRunning,
        LiveSplit.Model.TimerPhase.Running => Json.TimerPhase.Running,
        LiveSplit.Model.TimerPhase.Paused => Json.TimerPhase.Paused,
        LiveSplit.Model.TimerPhase.Ended => Json.TimerPhase.Ended,
        _ => Json.TimerPhase.Unspecified,
    };
}

internal sealed class JsonRunStateBuildCandidate : IJsonRunStateCandidate
{
    private readonly List<Bitmap?> images;
    private bool disposed;

    internal JsonRunStateBuildCandidate(Json.RunState state, List<Bitmap?> images)
    {
        State = state;
        this.images = images;
    }

    internal Json.RunState State { get; }

    public Json.RunState Build()
    {
        if (disposed) throw new ObjectDisposedException(nameof(JsonRunStateBuildCandidate));
        try
        {
            if (images.Count > 0 && images[0] != null) State.GameIcon = EncodePng(images[0]!);
            for (var index = 1; index < images.Count; index++)
                if (images[index] != null) State.Segments[index - 1].Icon = EncodePng(images[index]!);
            return State;
        }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var image in images) image?.Dispose();
    }

    private static byte[] EncodePng(Bitmap image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
