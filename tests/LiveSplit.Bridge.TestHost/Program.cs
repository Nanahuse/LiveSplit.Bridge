using System;
using System.Windows.Forms;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.Options;
using LiveSplit.UI;
using BridgeLayoutSettings = LiveSplit.Options.LayoutSettings;

namespace LiveSplit.Bridge.TestHost;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("First"));
        run.Add(new Segment("Second"));

        using var form = new Form();
        var layoutSettings = new BridgeLayoutSettings();
        var layout = new Layout { Settings = layoutSettings };
        var state = new LiveSplitState(run, form, layout, layoutSettings, new Settings());
        using var runtime = new BridgeRuntime(
            state,
            BridgeSettings.DefaultRpcPort,
            BridgeSettings.DefaultEventPort);

        Console.WriteLine("READY");
        Console.Out.Flush();
        Console.ReadLine();
    }
}
