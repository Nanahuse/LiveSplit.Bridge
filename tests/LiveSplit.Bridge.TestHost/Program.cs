using System;
using System.Globalization;
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
    private static int Main(string[] args)
    {
        var port = BridgeSettings.DefaultWebSocketPort;
        if (args.Length > 2 || (args.Length > 0 && args[0] != "--port"))
            return Usage();
        if (args.Length == 1)
            return Usage();
        if (
            args.Length == 2
            && (!int.TryParse(
                    args[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out port
                )
                || port is < 1 or > 65535)
        )
            return Usage();

        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.GameName = "Bridge Test Game";
        run.CategoryName = "Any%";
        run.Add(new Segment("First"));
        run.Add(new Segment("Second"));
        run.Metadata.GetOrAddCustomVariable("host_var").Value = "host-value";

        using var form = new Form();
        var layoutSettings = new BridgeLayoutSettings();
        var layout = new Layout { Settings = layoutSettings };
        var state = new LiveSplitState(run, form, layout, layoutSettings, new Settings());
        state.CurrentComparison = "Personal Best";
        try
        {
            using var runtime = new BridgeRuntime(state, port);

            Console.WriteLine("READY");
            Console.Out.Flush();
            Console.ReadLine();
            return 0;
        }
        catch (BridgeTransportStartException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: LiveSplit.Bridge.TestHost [--port 1..65535]");
        return 2;
    }
}

