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
        var run = state.Run;
        var result = new Json.RunState
        {
            GameName = run?.GameName ?? string.Empty,
            CategoryName = run?.CategoryName ?? string.Empty,
            OffsetTicks = run?.Offset.Ticks ?? 0,
        };
        if (run == null) return result;

        result.FilePath = string.IsNullOrEmpty(run.FilePath) ? null : run.FilePath;
        result.LayoutPath = string.IsNullOrEmpty(run.LayoutPath) ? null : run.LayoutPath;
        result.Metadata = new Json.RunMetadata();
        var metadata = run.Metadata;
        if (metadata != null)
        {
            result.Metadata.RunId = NormalizeRunId(metadata.RunID);
            result.Metadata.PlatformName = string.IsNullOrEmpty(metadata.PlatformName) ? null : metadata.PlatformName;
            result.Metadata.RegionName = string.IsNullOrEmpty(metadata.RegionName) ? null : metadata.RegionName;
            result.Metadata.UsesEmulator = metadata.UsesEmulator;
            // RunState variables describe VariableValueNames, not current custom-variable values.
            if (metadata.VariableValueNames != null)
                foreach (var pair in metadata.VariableValueNames)
                    result.Metadata.Variables[pair.Key] = pair.Value ?? string.Empty;
        }

        result.Comparisons.AddRange((run.Comparisons ?? Enumerable.Empty<string>()).Distinct());
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
            result.Segments.Add(info);
        }
        return result;
    }

    public Json.RunIcons GetRunIcons()
    {
        // TimerForm.SetRun disposes old images before replacing state.Run, without a
        // shared lock. Clone on that same thread to exclude Dispose, then encode the
        // detached images on the calling RPC thread. Image encoding needs no UI affinity.
        var form = state.Form ?? throw new InvalidOperationException("The LiveSplit form is unavailable.");
        if (form.Disposing || form.IsDisposed) throw new ObjectDisposedException(nameof(state.Form));
        // InvokeRequired can also return false before the owning thread creates a handle.
        if (!form.IsHandleCreated) throw new InvalidOperationException("The LiveSplit form handle is not created.");
        var images = form.InvokeRequired
            ? (List<Bitmap?>)form.Invoke(new Func<List<Bitmap?>>(CloneRunIcons))
            : CloneRunIcons();
        try
        {
            var result = new Json.RunIcons
            {
                GameIcon = EncodePng(images[0]),
            };
            result.SegmentIcons.AddRange(images.Skip(1).Select(EncodePng));
            return result;
        }
        finally { foreach (var image in images) image?.Dispose(); }
    }

    private List<Bitmap?> CloneRunIcons()
    {
        var images = new List<Bitmap?>();
        try
        {
            var run = state.Run;
            images.Add(CloneImage(run?.GameIcon));
            if (run != null)
                foreach (var segment in run) images.Add(CloneImage(segment.Icon));
            return images;
        }
        catch
        {
            foreach (var image in images) image?.Dispose();
            throw;
        }
    }

    private static byte[]? EncodePng(Bitmap? image)
    {
        if (image == null) return null;
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
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
        result.Metadata.RunId = NormalizeRunId(run.Metadata?.RunID);
        updated = result;
        return true;
    }

    public void ExecuteTimerOperation(Json.TimerOperation operation)
    {
        switch (operation)
        {
            case Json.TimerOperation.Start: timerModel.Start(); break;
            case Json.TimerOperation.Split: timerModel.Split(); break;
            case Json.TimerOperation.Skip: timerModel.SkipSplit(); break;
            case Json.TimerOperation.Undo: timerModel.UndoSplit(); break;
            case Json.TimerOperation.Reset: timerModel.Reset(); break;
            case Json.TimerOperation.Pause:
            case Json.TimerOperation.Resume: timerModel.Pause(); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported timer operation.");
        }
    }

    public void ExecuteGameTimeOperation(Json.GameTimeParams operation)
    {
        switch (operation)
        {
            case Json.GameControlParams { Operation: Json.GameControlOperation.Initialize }: timerModel.InitializeGameTime(); break;
            case Json.GameSetParams set: state.SetGameTime(TimeSpan.FromTicks(set.Ticks)); break;
            case Json.GameControlParams { Operation: Json.GameControlOperation.Pause }: state.IsGameTimePaused = true; break;
            case Json.GameControlParams { Operation: Json.GameControlOperation.Resume }: state.IsGameTimePaused = false; break;
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

    private static string? NormalizeRunId(string? runId) => string.IsNullOrEmpty(runId) ? null : runId;

    private static Json.TimeValue MapTime(Time time) => new()
    {
        RealTimeTicks = time.RealTime?.Ticks,
        GameTimeTicks = time.GameTime?.Ticks,
    };

    private static Json.TimeValue MapTime(IComparisons? comparisons, string name) =>
        comparisons != null && comparisons.TryGetValue(name, out var time) ? MapTime(time) : new Json.TimeValue();

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
