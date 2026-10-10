#nullable enable
using System;
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
    RunState UpdateRunTimings(RunState published);
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
        var run = state.Run;
        var result = new RunState
        {
            GameName = run?.GameName ?? string.Empty,
            CategoryName = run?.CategoryName ?? string.Empty,
            OffsetTicks = run?.Offset.Ticks ?? 0,
        };
        if (run == null) return result;
        result.Metadata = new LiveSplit.Bridge.Protocol.V3.RunMetadata();
        if (!string.IsNullOrEmpty(run.FilePath)) result.FilePath = run.FilePath;
        if (!string.IsNullOrEmpty(run.LayoutPath)) result.LayoutPath = run.LayoutPath;
        var metadata = run.Metadata;
        if (metadata != null)
        {
            if (!string.IsNullOrEmpty(metadata.RunID)) result.Metadata.RunId = metadata.RunID;
            if (!string.IsNullOrEmpty(metadata.PlatformName)) result.Metadata.PlatformName = metadata.PlatformName;
            if (!string.IsNullOrEmpty(metadata.RegionName)) result.Metadata.RegionName = metadata.RegionName;
            result.Metadata.UsesEmulator = metadata.UsesEmulator;
            // RunState.variables describe VariableValueNames, not current values.
            if (metadata.VariableValueNames != null)
                foreach (var pair in metadata.VariableValueNames) result.Metadata.Variables[pair.Key] = pair.Value ?? string.Empty;
        }
        result.Comparisons.Add((run.Comparisons ?? Enumerable.Empty<string>()).Distinct());
        var gameIcon = MapImage(run.GameIcon);
        if (gameIcon != null) result.GameIcon = gameIcon;
        for (var index = 0; index < run.Count; index++)
        {
            var segment = run[index];
            var info = new SegmentInfo { Index = (uint)index, Name = segment.Name ?? string.Empty };
            foreach (var comparison in result.Comparisons)
            {
                info.Comparisons.Add(new ComparisonTime { Name = comparison, Time = MapTime(segment.Comparisons, comparison) });
            }
            info.BestSegmentTime = MapTime(segment.BestSegmentTime);
            var icon = MapImage(segment.Icon);
            if (icon != null) info.Icon = icon;
            result.Segments.Add(info);
        }
        return result;
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

    public RunState UpdateRunTimings(RunState published)
    {
        var run = state.Run;
        if (run == null || published.Segments.Count != run.Count) return GetRunState();
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
        return result;
    }

    private static TimeValue MapTime(IComparisons comparisons, string name)
    {
        return comparisons != null && comparisons.TryGetValue(name, out var time) ? MapTime(time) : new TimeValue();
    }

    private static Image? MapImage(System.Drawing.Image image)
    {
        if (image == null) return null;
        // Encode synchronously while the owning LiveSplit object is known to be alive.
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return new Image { MimeType = "image/png", Data = ByteString.CopyFrom(stream.ToArray()), Width = (uint)image.Width, Height = (uint)image.Height };
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
