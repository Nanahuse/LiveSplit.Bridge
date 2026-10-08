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
using ModelImage = System.Drawing.Image;
using ProtocolImage = LiveSplit.Bridge.Protocol.V3.Image;
using ProtocolRunMetadata = LiveSplit.Bridge.Protocol.V3.RunMetadata;

namespace LiveSplit.Bridge;

internal sealed class RunStateCache
{
    private readonly LiveSplitState state;
    private readonly Func<ModelImage?, ProtocolImage?> encodeImage;
    private IRun? sourceRun;
    private RunState cached;
    private int imageEncodeCount;

    internal RunStateCache(LiveSplitState state, Func<ModelImage?, ProtocolImage?>? encodeImage = null)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.encodeImage = encodeImage ?? EncodeImage;
        sourceRun = state.Run;
        cached = Build(state.Run);
    }

    internal RunState Current => cached;
    internal IRun? SourceRun => sourceRun;
    internal int ImageEncodeCount => imageEncodeCount;

    internal bool Rebuild()
    {
        var run = state.Run;
        var candidate = Build(run);
        sourceRun = run;
        if (cached.Equals(candidate)) return false;
        cached = candidate;
        return true;
    }

    internal bool UpdateAfterReset()
    {
        var run = state.Run;
        if (!ReferenceEquals(sourceRun, run)) return Rebuild();
        if (run == null) return false;

        var comparisons = run.Comparisons.ToList();
        if (!HasSamePublishedStructure(run, comparisons)) return Rebuild();

        var candidate = cached.Clone();
        UpdateRunId(candidate.Metadata, run.Metadata.RunID);
        for (var index = 0; index < run.Count; index++)
        {
            var liveSegment = run[index];
            var cachedSegment = candidate.Segments[index];
            cachedSegment.Comparisons.Clear();
            foreach (var comparison in comparisons)
            {
                cachedSegment.Comparisons.Add(new ComparisonTime
                {
                    Name = comparison,
                    Time = MapTime(liveSegment.Comparisons, comparison),
                });
            }

            cachedSegment.BestSegmentTime = MapTime(liveSegment.BestSegmentTime);
        }

        if (cached.Equals(candidate)) return false;
        cached = candidate;
        return true;
    }

    private bool HasSamePublishedStructure(IRun run, IReadOnlyList<string> comparisons)
    {
        if (cached.Segments.Count != run.Count || cached.Comparisons.Count != comparisons.Count) return false;
        for (var index = 0; index < comparisons.Count; index++)
        {
            if (!string.Equals(cached.Comparisons[index], comparisons[index], StringComparison.Ordinal)) return false;
        }

        for (var index = 0; index < run.Count; index++)
        {
            var segment = run[index];
            var cachedSegment = cached.Segments[index];
            if (cachedSegment.Index != (uint)index
                || !string.Equals(cachedSegment.Name, segment.Name ?? string.Empty, StringComparison.Ordinal)
                || cachedSegment.Comparisons.Count != comparisons.Count)
            {
                return false;
            }

            for (var comparisonIndex = 0; comparisonIndex < comparisons.Count; comparisonIndex++)
            {
                if (!string.Equals(cachedSegment.Comparisons[comparisonIndex].Name, comparisons[comparisonIndex], StringComparison.Ordinal)) return false;
            }
        }

        return true;
    }

    private RunState Build(IRun? run)
    {
        var result = new RunState();
        if (run == null) return result;

        result.GameName = run.GameName ?? string.Empty;
        result.CategoryName = run.CategoryName ?? string.Empty;
        result.OffsetTicks = run.Offset.Ticks;
        if (!string.IsNullOrEmpty(run.FilePath)) result.FilePath = run.FilePath;
        if (!string.IsNullOrEmpty(run.LayoutPath)) result.LayoutPath = run.LayoutPath;

        var metadata = run.Metadata;
        if (metadata != null)
        {
            result.Metadata = BuildMetadata(metadata);
        }

        var comparisons = (run.Comparisons ?? Enumerable.Empty<string>()).ToList();
        result.Comparisons.Add(comparisons);
        for (var index = 0; index < run.Count; index++)
        {
            var segment = run[index];
            var info = new SegmentInfo
            {
                Index = (uint)index,
                Name = segment.Name ?? string.Empty,
                BestSegmentTime = MapTime(segment.BestSegmentTime),
            };
            foreach (var comparison in comparisons)
            {
                info.Comparisons.Add(new ComparisonTime
                {
                    Name = comparison,
                    Time = MapTime(segment.Comparisons, comparison),
                });
            }

            var icon = Encode(segment.Icon);
            if (icon != null) info.Icon = icon;
            result.Segments.Add(info);
        }

        var gameIcon = Encode(run.GameIcon);
        if (gameIcon != null) result.GameIcon = gameIcon;
        return result;
    }

    private ProtocolRunMetadata BuildMetadata(LiveSplit.Model.RunMetadata metadata)
    {
        var result = new ProtocolRunMetadata
        {
            UsesEmulator = metadata.UsesEmulator,
        };
        if (!string.IsNullOrEmpty(metadata.RunID)) result.RunId = metadata.RunID;
        if (!string.IsNullOrEmpty(metadata.PlatformName)) result.PlatformName = metadata.PlatformName;
        if (!string.IsNullOrEmpty(metadata.RegionName)) result.RegionName = metadata.RegionName;
        if (metadata.VariableValueNames != null)
        {
            foreach (var pair in metadata.VariableValueNames) result.Variables[pair.Key] = pair.Value ?? string.Empty;
        }

        return result;
    }

    private ProtocolImage? Encode(ModelImage? image)
    {
        if (image != null) imageEncodeCount++;
        return encodeImage(image);
    }

    private static ProtocolImage? EncodeImage(ModelImage? image)
    {
        if (image == null) return null;
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return new ProtocolImage
        {
            MimeType = "image/png",
            Data = ByteString.CopyFrom(stream.ToArray()),
            Width = (uint)image.Width,
            Height = (uint)image.Height,
        };
    }

    private static void UpdateRunId(ProtocolRunMetadata metadata, string? runId)
    {
        metadata.ClearRunId();
        if (!string.IsNullOrEmpty(runId)) metadata.RunId = runId;
    }

    private static TimeValue MapTime(Time time)
    {
        var result = new TimeValue();
        if (time.RealTime.HasValue) result.RealTimeTicks = time.RealTime.Value.Ticks;
        if (time.GameTime.HasValue) result.GameTimeTicks = time.GameTime.Value.Ticks;
        return result;
    }

    private static TimeValue MapTime(LiveSplit.Model.Comparisons.IComparisons comparisons, string name)
    {
        return comparisons != null && comparisons.TryGetValue(name, out var time) ? MapTime(time) : new TimeValue();
    }
}
