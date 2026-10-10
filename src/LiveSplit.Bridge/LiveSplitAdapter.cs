#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V3;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using ModelTimingMethod = LiveSplit.Model.TimingMethod;
using ProtocolTimingMethod = LiveSplit.Bridge.Protocol.V3.TimingMethod;
using ProtocolTimerPhase = LiveSplit.Bridge.Protocol.V3.TimerPhase;

namespace LiveSplit.Bridge;

internal interface ILiveSplitAdapter
{
    TimerState GetTimerState();
    AttemptState GetAttempt();
    CompletedCount GetCompletedCount();
    RunState GetRunState();
    RunStateBuildCandidate CaptureRunState();
    bool TryUpdateRunTimings(RunState published, out RunState updated);
    bool IsTimerOnlyRun();
    ContextState GetContextState();
    void ExecuteTimerOperation(TimerOperationType operation);
    void ExecuteGameTimeOperation(GameTimeOperationType operation, long? ticks);
}

internal sealed class LiveSplitAdapter : ILiveSplitAdapter
{
    private readonly LiveSplitState state;
    private readonly TimerModel timerModel;

    public LiveSplitAdapter(LiveSplitState state)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        timerModel = new TimerModel { CurrentState = state };
    }

    public TimerState GetTimerState()
    {
        var currentTime = state.CurrentTime;
        var result = new TimerState
        {
            Phase = MapTimerPhase(state.CurrentPhase),
            SplitIndex = state.CurrentSplitIndex,
            RealTimeTicks = currentTime.RealTime?.Ticks ?? 0,
            IsGameTimeInitialized = state.IsGameTimeInitialized,
            IsGameTimePaused = state.IsGameTimePaused,
        };
        if (currentTime.GameTime.HasValue) result.GameTimeTicks = currentTime.GameTime.Value.Ticks;
        return result;
    }

    public AttemptState GetAttempt()
    {
        var run = state.Run;
        var result = new AttemptState { AttemptCount = run != null && run.AttemptCount > 0 ? (uint)run.AttemptCount : 0U };
        if (run == null) return result;
        for (var index = 0; index < run.Count; index++)
        {
            var segment = run[index];
            var attemptSegment = new AttemptSegment { Index = (uint)index, SplitTime = MapTime(segment.SplitTime) };
            if (segment.CustomVariableValues != null)
                foreach (var pair in segment.CustomVariableValues) attemptSegment.CustomVariables[pair.Key] = pair.Value ?? string.Empty;
            result.Segments.Add(attemptSegment);
        }
        return result;
    }

    public CompletedCount GetCompletedCount()
    {
        var attempts = state.Run?.AttemptHistory;
        return new CompletedCount { CompletedCount_ = attempts == null ? 0U : (uint)attempts.Count(attempt => attempt.Time.RealTime != null) };
    }

    public RunState GetRunState()
    {
        using var candidate = CaptureRunState();
        return candidate.Build();
    }

    public RunStateBuildCandidate CaptureRunState()
    {
        var run = state.Run;
        var result = new RunState
        {
            GameName = run?.GameName ?? string.Empty,
            CategoryName = run?.CategoryName ?? string.Empty,
            OffsetTicks = run?.Offset.Ticks ?? 0,
        };
        var images = new List<Bitmap?>();
        if (run == null) return new RunStateBuildCandidate(result, images);
        try
        {
            result.Metadata = new LiveSplit.Bridge.Protocol.V3.RunMetadata();
            if (!string.IsNullOrEmpty(run.FilePath)) result.FilePath = run.FilePath;
            if (!string.IsNullOrEmpty(run.LayoutPath)) result.LayoutPath = run.LayoutPath;
            var metadata = run.Metadata;
            if (metadata != null)
            {
                if (metadata.RunID != null) result.Metadata.RunId = metadata.RunID;
                if (!string.IsNullOrEmpty(metadata.PlatformName)) result.Metadata.PlatformName = metadata.PlatformName;
                if (!string.IsNullOrEmpty(metadata.RegionName)) result.Metadata.RegionName = metadata.RegionName;
                result.Metadata.UsesEmulator = metadata.UsesEmulator;
                // RunState.variables describe VariableValueNames, not current values.
                if (metadata.VariableValueNames != null)
                    foreach (var pair in metadata.VariableValueNames) result.Metadata.Variables[pair.Key] = pair.Value ?? string.Empty;
            }
            result.Comparisons.Add((run.Comparisons ?? Enumerable.Empty<string>()).Distinct());
            images.Add(CloneImage(run.GameIcon));
            for (var index = 0; index < run.Count; index++)
            {
                var segment = run[index];
                var info = new SegmentInfo { Index = (uint)index, Name = segment.Name ?? string.Empty };
                foreach (var comparison in result.Comparisons)
                {
                    info.Comparisons.Add(new ComparisonTime { Name = comparison, Time = MapTime(segment.Comparisons, comparison) });
                }
                info.BestSegmentTime = MapTime(segment.BestSegmentTime);
                images.Add(CloneImage(segment.Icon));
                result.Segments.Add(info);
            }
            return new RunStateBuildCandidate(result, images);
        }
        catch
        {
            foreach (var image in images) image?.Dispose();
            throw;
        }
    }

    public ContextState GetContextState()
    {
        var result = new ContextState
        {
            CurrentTimingMethod = state.CurrentTimingMethod switch
            {
                ModelTimingMethod.RealTime => ProtocolTimingMethod.RealTime,
                ModelTimingMethod.GameTime => ProtocolTimingMethod.GameTime,
                _ => ProtocolTimingMethod.Unspecified,
            },
            CurrentComparison = state.CurrentComparison ?? string.Empty,
        };
        var variables = state.Run?.Metadata?.CustomVariables;
        if (variables != null)
            foreach (var pair in variables) result.CustomVariables[pair.Key] = pair.Value?.Value ?? string.Empty;
        return result;
    }

    public bool TryUpdateRunTimings(RunState published, out RunState updated)
    {
        var run = state.Run;
        if (run == null || published.Segments.Count != run.Count)
        {
            updated = published;
            return false;
        }
        var currentComparisons = (run.Comparisons ?? Enumerable.Empty<string>()).Distinct().ToArray();
        if (currentComparisons.Length != published.Comparisons.Count)
        {
            updated = published;
            return false;
        }
        for (var comparisonIndex = 0; comparisonIndex < currentComparisons.Length; comparisonIndex++)
        {
            if (!string.Equals(currentComparisons[comparisonIndex], published.Comparisons[comparisonIndex], StringComparison.Ordinal))
            {
                updated = published;
                return false;
            }
        }
        for (var index = 0; index < run.Count; index++)
        {
            if (!string.Equals(published.Segments[index].Name, run[index].Name ?? string.Empty, StringComparison.Ordinal)
                || published.Segments[index].Comparisons.Count != published.Comparisons.Count)
            {
                updated = published;
                return false;
            }
            for (var comparisonIndex = 0; comparisonIndex < published.Comparisons.Count; comparisonIndex++)
                if (!string.Equals(published.Segments[index].Comparisons[comparisonIndex].Name, published.Comparisons[comparisonIndex], StringComparison.Ordinal))
                {
                    updated = published;
                    return false;
                }
        }
        var result = published.Clone();
        var comparisons = result.Comparisons;
        for (var index = 0; index < run.Count; index++)
        {
            var source = run[index];
            var target = result.Segments[index];
            target.BestSegmentTime = MapTime(source.BestSegmentTime);
            for (var comparisonIndex = 0; comparisonIndex < target.Comparisons.Count; comparisonIndex++)
            {
                var name = comparisonIndex < comparisons.Count ? comparisons[comparisonIndex] : target.Comparisons[comparisonIndex].Name;
                target.Comparisons[comparisonIndex].Time = MapTime(source.Comparisons, name);
            }
        }
        if (result.Metadata == null) result.Metadata = new LiveSplit.Bridge.Protocol.V3.RunMetadata();
        var runId = run.Metadata?.RunID;
        if (runId == null) result.Metadata.ClearRunId();
        else result.Metadata.RunId = runId;
        updated = result;
        return true;
    }

    internal static bool TryMergeRunTimings(RunState candidate, RunState latest, out RunState merged)
    {
        if (candidate.Segments.Count != latest.Segments.Count
            || candidate.Comparisons.Count != latest.Comparisons.Count)
        {
            merged = candidate;
            return false;
        }
        for (var comparisonIndex = 0; comparisonIndex < candidate.Comparisons.Count; comparisonIndex++)
        {
            if (!string.Equals(candidate.Comparisons[comparisonIndex], latest.Comparisons[comparisonIndex], StringComparison.Ordinal))
            {
                merged = candidate;
                return false;
            }
        }
        for (var segmentIndex = 0; segmentIndex < candidate.Segments.Count; segmentIndex++)
        {
            var targetSegment = candidate.Segments[segmentIndex];
            var sourceSegment = latest.Segments[segmentIndex];
            if (!string.Equals(targetSegment.Name, sourceSegment.Name, StringComparison.Ordinal)
                || targetSegment.Comparisons.Count != sourceSegment.Comparisons.Count)
            {
                merged = candidate;
                return false;
            }
            for (var comparisonIndex = 0; comparisonIndex < targetSegment.Comparisons.Count; comparisonIndex++)
            {
                if (!string.Equals(targetSegment.Comparisons[comparisonIndex].Name, sourceSegment.Comparisons[comparisonIndex].Name, StringComparison.Ordinal))
                {
                    merged = candidate;
                    return false;
                }
            }
        }

        merged = candidate.Clone();
        if (merged.Metadata == null) merged.Metadata = new LiveSplit.Bridge.Protocol.V3.RunMetadata();
        if (latest.Metadata?.HasRunId == true) merged.Metadata.RunId = latest.Metadata.RunId;
        else merged.Metadata.ClearRunId();
        for (var segmentIndex = 0; segmentIndex < merged.Segments.Count; segmentIndex++)
        {
            var targetSegment = merged.Segments[segmentIndex];
            var sourceSegment = latest.Segments[segmentIndex];
            targetSegment.BestSegmentTime = sourceSegment.BestSegmentTime.Clone();
            for (var comparisonIndex = 0; comparisonIndex < targetSegment.Comparisons.Count; comparisonIndex++)
                targetSegment.Comparisons[comparisonIndex].Time = sourceSegment.Comparisons[comparisonIndex].Time.Clone();
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

    private static Bitmap? CloneImage(System.Drawing.Image image) => image == null ? null : new Bitmap(image);

    private static TimeValue MapTime(IComparisons comparisons, string name)
    {
        return comparisons != null && comparisons.TryGetValue(name, out var time) ? MapTime(time) : new TimeValue();
    }

    internal static LiveSplit.Bridge.Protocol.V3.Image? MapImage(System.Drawing.Image image)
    {
        if (image == null) return null;
        // Encode from the detached bitmap snapshot, not the image owned by LiveSplit.
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return new LiveSplit.Bridge.Protocol.V3.Image { MimeType = "image/png", Data = ByteString.CopyFrom(stream.ToArray()), Width = (uint)image.Width, Height = (uint)image.Height };
    }

    public void ExecuteTimerOperation(TimerOperationType operation)
    {
        switch (operation)
        {
            case TimerOperationType.TimerStart: timerModel.Start(); break;
            case TimerOperationType.TimerSplit: timerModel.Split(); break;
            case TimerOperationType.TimerSkip: timerModel.SkipSplit(); break;
            case TimerOperationType.TimerUndo: timerModel.UndoSplit(); break;
            case TimerOperationType.TimerReset: timerModel.Reset(); break;
            case TimerOperationType.TimerPause:
            case TimerOperationType.TimerResume: timerModel.Pause(); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported timer operation.");
        }
    }

    public void ExecuteGameTimeOperation(GameTimeOperationType operation, long? ticks)
    {
        switch (operation)
        {
            case GameTimeOperationType.Initialize: timerModel.InitializeGameTime(); break;
            case GameTimeOperationType.Set:
                if (!ticks.HasValue) throw new ArgumentException("SET requires ticks.", nameof(ticks));
                state.SetGameTime(TimeSpan.FromTicks(ticks.Value));
                break;
            case GameTimeOperationType.GameTimePause: state.IsGameTimePaused = true; break;
            case GameTimeOperationType.GameTimeResume: state.IsGameTimePaused = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported game time operation.");
        }
    }

    private static TimeValue MapTime(Time time)
    {
        var result = new TimeValue();
        if (time.RealTime.HasValue) result.RealTimeTicks = time.RealTime.Value.Ticks;
        if (time.GameTime.HasValue) result.GameTimeTicks = time.GameTime.Value.Ticks;
        return result;
    }

    private static ProtocolTimerPhase MapTimerPhase(LiveSplit.Model.TimerPhase phase) => phase switch
    {
        LiveSplit.Model.TimerPhase.NotRunning => ProtocolTimerPhase.NotRunning,
        LiveSplit.Model.TimerPhase.Running => ProtocolTimerPhase.Running,
        LiveSplit.Model.TimerPhase.Paused => ProtocolTimerPhase.Paused,
        LiveSplit.Model.TimerPhase.Ended => ProtocolTimerPhase.Ended,
        _ => ProtocolTimerPhase.Unspecified,
    };
}

internal sealed class RunStateBuildCandidate : IDisposable
{
    private readonly List<Bitmap?> images;
    private bool disposed;

    internal RunStateBuildCandidate(RunState state, List<Bitmap?> images)
    {
        State = state;
        this.images = images;
    }

    internal RunState State { get; }

    internal RunState Build()
    {
        if (disposed) throw new ObjectDisposedException(nameof(RunStateBuildCandidate));
        try
        {
            if (images.Count > 0 && images[0] != null) State.GameIcon = LiveSplitAdapter.MapImage(images[0]!);
            for (var index = 1; index < images.Count; index++)
                if (images[index] != null) State.Segments[index - 1].Icon = LiveSplitAdapter.MapImage(images[index]!);
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
}
