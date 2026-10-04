using System.Windows.Forms;
using LiveSplit.Model;
using LiveSplit.Options;
using LiveSplit.UI;
using BridgeLayoutSettings = LiveSplit.Options.LayoutSettings;

namespace LiveSplit.Bridge.Tests;

internal static class TestLiveSplitState
{
    public static LiveSplitState Create(IRun run)
    {
        return Create(run, new Settings());
    }

    public static LiveSplitState Create(IRun run, Settings settings)
    {
        var form = new Form();
        var layoutSettings = new BridgeLayoutSettings();
        var layout = new Layout { Settings = layoutSettings };
        return new LiveSplitState(run, form, layout, layoutSettings, settings);
    }
}
