using System.Drawing;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using Json = LiveSplit.Bridge.Json;

namespace LiveSplit.Bridge.Tests;

public class JsonLiveSplitAdapterTests
{
    [Fact]
    public void MapsTimerAttemptCompletedCountAndContext()
    {
        var run = CreateRun();
        run.AttemptCount = 4;
        run[0].SplitTime = new Time(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(8));
        run[0].CustomVariableValues["route"] = "left";
        run.AttemptHistory.Add(new Attempt(1, new Time(TimeSpan.FromSeconds(5), null), null, null, null));
        run.AttemptHistory.Add(new Attempt(2, new Time(null, TimeSpan.FromSeconds(4)), null, null, null));
        run.Metadata.CustomVariables["route"] = new CustomVariable("right", false);
        var state = TestLiveSplitState.Create(run);
        state.CurrentPhase = LiveSplit.Model.TimerPhase.Paused;
        state.CurrentSplitIndex = 1;
        state.TimePausedAt = TimeSpan.FromSeconds(4);
        state.CurrentComparison = "Personal Best";

        var adapter = new JsonLiveSplitAdapter(state);
        var timer = adapter.GetTimerState();
        var attempt = adapter.GetAttempt();
        var completed = adapter.GetCompletedCount();
        var context = adapter.GetContextState();

        Assert.Equal(Json.TimerPhase.Paused, timer.Phase);
        Assert.Equal(1, timer.SplitIndex);
        Assert.Equal(TimeSpan.FromSeconds(4).Ticks, timer.RealTimeTicks);
        Assert.Equal(4U, attempt.AttemptCount);
        Assert.Equal(TimeSpan.FromSeconds(12).Ticks, attempt.Segments[0].SplitTime.RealTimeTicks);
        Assert.Equal("left", attempt.Segments[0].CustomVariables["route"]);
        Assert.Equal(1U, completed.Value);
        Assert.Equal("right", context.CustomVariables["route"]);
        Assert.Equal("Personal Best", context.CurrentComparison);
    }

    [Fact]
    public void BuildsRunStateWithoutReadingImages()
    {
        var run = CreateRun();
        run.GameName = "Game";
        run.CategoryName = "Any%";
        run.Offset = TimeSpan.FromTicks(-123);
        run.Metadata.RunID = "run-id";
        run.Metadata.VariableValueNames["character"] = "Character";
        run.Metadata.CustomVariables["character"] = new CustomVariable("A", false);
        run[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(6));
        var gameIcon = new Bitmap(2, 2);
        var segmentIcon = new Bitmap(2, 2);
        gameIcon.SetPixel(0, 0, Color.Magenta);
        segmentIcon.SetPixel(0, 0, Color.Cyan);
        run.GameIcon = gameIcon;
        run[0].Icon = segmentIcon;
        var adapter = new JsonLiveSplitAdapter(TestLiveSplitState.Create(run));

        gameIcon.Dispose();
        segmentIcon.Dispose();
        var result = adapter.GetRunState();

        Assert.Equal("Game", result.GameName);
        Assert.Equal(-123, result.OffsetTicks);
        Assert.Equal("run-id", result.Metadata!.RunId);
        Assert.Equal("Character", result.Metadata.Variables["character"]);
        Assert.Equal("A", adapter.GetContextState().CustomVariables["character"]);
        Assert.Equal(run.Comparisons.Distinct(), result.Comparisons);
        Assert.Equal("One", result.Segments[0].Name);
        Assert.Equal(TimeSpan.FromSeconds(7).Ticks, result.Segments[0].BestSegmentTime.RealTimeTicks);
    }

    [Fact]
    public void TimingUpdateCopiesPublishedSnapshotAndRefreshesRunIdAndTimes()
    {
        var run = CreateRun();
        run.Metadata.RunID = "before";
        run[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(2), null);
        var adapter = new JsonLiveSplitAdapter(TestLiveSplitState.Create(run));
        var published = adapter.GetRunState();
        run.Metadata.RunID = null;
        run[0].BestSegmentTime = new Time(TimeSpan.FromSeconds(9), null);

        Assert.True(adapter.TryUpdateRunTimings(published, out var updated));
        Assert.Equal("before", published.Metadata!.RunId);
        Assert.Null(updated.Metadata!.RunId);
        Assert.Equal(TimeSpan.FromSeconds(2).Ticks, published.Segments[0].BestSegmentTime.RealTimeTicks);
        Assert.Equal(TimeSpan.FromSeconds(9).Ticks, updated.Segments[0].BestSegmentTime.RealTimeTicks);

        updated.Segments.RemoveAt(updated.Segments.Count - 1);
        Assert.False(adapter.TryUpdateRunTimings(updated, out _));
    }

    [Fact]
    public void TreatsEmptyLiveSplitRunIdAsUnset()
    {
        var run = CreateRun();
        run.Metadata.RunID = string.Empty;
        var adapter = new JsonLiveSplitAdapter(TestLiveSplitState.Create(run));

        var published = adapter.GetRunState();
        Assert.Null(published.Metadata!.RunId);

        Assert.True(adapter.TryUpdateRunTimings(published, out var updated));
        Assert.Null(updated.Metadata!.RunId);
    }

    [Fact]
    public void GetsCurrentIconsAsPngWithNullEntriesInSegmentOrder()
    {
        var run = CreateRun();
        using var game = new Bitmap(2, 2);
        using var segment = new Bitmap(2, 2);
        game.SetPixel(0, 0, Color.Magenta);
        segment.SetPixel(0, 0, Color.Cyan);
        run.GameIcon = game;
        run[0].Icon = segment;
        var state = TestLiveSplitState.Create(run);
        var adapter = new JsonLiveSplitAdapter(state);

        var icons = adapter.GetRunIcons();
        Assert.Equal(Color.Magenta.ToArgb(), ReadPixel(icons.GameIcon).ToArgb());
        Assert.Equal(2, icons.SegmentIcons.Count);
        Assert.Equal(Color.Cyan.ToArgb(), ReadPixel(icons.SegmentIcons[0]).ToArgb());
        Assert.Null(icons.SegmentIcons[1]);

        // Every request reads the current Run; there is no persistent icon snapshot.
        state.Run = CreateRun();
        var missing = adapter.GetRunIcons();
        Assert.Null(missing.GameIcon);
        Assert.Equal(new byte[]?[] { null, null }, missing.SegmentIcons);
    }

    [Fact]
    public async Task IconRequestWaitsForRunReplacementToFinishDisposingOldImages()
    {
        var ready = new TaskCompletionSource<LiveSplitState>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var disposedImage = new ManualResetEventSlim();
        using var finishReplacement = new ManualResetEventSlim();
        var ui = new Thread(() =>
        {
            var run = CreateRun();
            run.GameIcon = new Bitmap(2, 2);
            var state = TestLiveSplitState.Create(run);
            using var form = state.Form;
            _ = form.Handle;
            ready.SetResult(state);
            try { System.Windows.Forms.Application.Run(); }
            finally { state.Run.GameIcon?.Dispose(); }
        }) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        Assert.Same(ready.Task, await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        var state = await ready.Task;
        try
        {
            state.Form.BeginInvoke((Action)(() =>
            {
                // TimerForm.SetRun has this same interval: old images are disposed
                // while state.Run still points at the old Run.
                state.Run.GameIcon.Dispose();
                disposedImage.Set();
                if (!finishReplacement.Wait(TimeSpan.FromSeconds(5))) return;
                var replacement = CreateRun();
                var icon = new Bitmap(2, 2);
                icon.SetPixel(0, 0, Color.Magenta);
                replacement.GameIcon = icon;
                state.Run = replacement;
            }));
            Assert.True(disposedImage.Wait(TimeSpan.FromSeconds(5)));
            var request = Task.Run(() => new JsonLiveSplitAdapter(state).GetRunIcons());
            try
            {
                Assert.NotSame(request, await Task.WhenAny(request, Task.Delay(100)));
            }
            finally { finishReplacement.Set(); }
            Assert.Same(request, await Task.WhenAny(request, Task.Delay(TimeSpan.FromSeconds(5))));
            var icons = await request;
            Assert.Equal(Color.Magenta.ToArgb(), ReadPixel(icons.GameIcon).ToArgb());
            Assert.Equal(2, icons.SegmentIcons.Count);
            Assert.All(icons.SegmentIcons, Assert.Null);
        }
        finally
        {
            finishReplacement.Set();
            state.Form.BeginInvoke((Action)System.Windows.Forms.Application.ExitThread);
            Assert.True(ui.Join(TimeSpan.FromSeconds(5)));
        }
    }

    private static Run CreateRun()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        return run;
    }

    private static Color ReadPixel(byte[]? data)
    {
        Assert.NotNull(data);
        using var stream = new System.IO.MemoryStream(data!);
        using var bitmap = new Bitmap(stream);
        return bitmap.GetPixel(0, 0);
    }
}
