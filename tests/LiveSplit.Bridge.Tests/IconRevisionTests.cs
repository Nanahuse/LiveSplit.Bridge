using System.Drawing;
using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class IconRevisionTests
{
    [Fact]
    public void DifferentImageInstanceWithSameContentStillAdvancesRunGeneration()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));

        using var first = CreateBitmap(Color.Red);
        run.GameIcon = first;
        var state = TestLiveSplitState.Create(run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        // Assign a distinct instance that carries identical pixel data.
        using var second = CreateBitmap(Color.Red);
        run.GameIcon = second;
        state.CallRunManuallyModified();
        runtime.Update();

        Assert.Equal(2UL, runtime.RunRevision);
    }

    [Fact]
    public void ChangedImageContentAdvancesRunRevision()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));

        using var icon = CreateBitmap(Color.Red);
        run.GameIcon = icon;
        var state = TestLiveSplitState.Create(run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        // Mutate the same image instance so its published PNG data changes.
        using (var graphics = Graphics.FromImage(icon))
        {
            graphics.Clear(Color.Blue);
        }

        state.CallRunManuallyModified();
        runtime.Update();

        Assert.Equal(2UL, runtime.RunRevision);
    }

    [Fact]
    public void DifferentSegmentImageInstanceWithSameContentStillAdvancesRunGeneration()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));

        using var first = CreateBitmap(Color.Green);
        run[0].Icon = first;
        var state = TestLiveSplitState.Create(run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        using var second = CreateBitmap(Color.Green);
        run[0].Icon = second;
        state.CallRunManuallyModified();
        runtime.Update();

        Assert.Equal(2UL, runtime.RunRevision);
    }

    [Fact]
    public void ChangedSegmentImageContentAdvancesRunRevision()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));

        using var icon = CreateBitmap(Color.Green);
        run[0].Icon = icon;
        var state = TestLiveSplitState.Create(run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        using (var graphics = Graphics.FromImage(icon))
        {
            graphics.Clear(Color.Yellow);
        }

        state.CallRunManuallyModified();
        runtime.Update();

        Assert.Equal(2UL, runtime.RunRevision);
    }

    [Fact]
    public void RunStateIconReflectsEdit()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));

        using var icon = CreateBitmap(Color.Red);
        run.GameIcon = icon;
        var state = TestLiveSplitState.Create(run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        var first = GetRun(runtime);
        Assert.Equal("image/png", first.GameIcon.MimeType);

        using (var graphics = Graphics.FromImage(icon))
        {
            graphics.Clear(Color.Blue);
        }

        state.CallRunManuallyModified();
        runtime.Update();

        var second = GetRun(runtime);
        Assert.NotEqual(first.GameIcon.Data, second.GameIcon.Data);
    }

    private static Bitmap CreateBitmap(Color color)
    {
        var bitmap = new Bitmap(2, 2);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
        return bitmap;
    }

    private static RunState GetRun(BridgeRuntime runtime)
    {
        var response = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 2,
            RequestId = 1,
            GetRun = new GetRunRequest(),
        });

        return response.GetRun.Run;
    }
}
