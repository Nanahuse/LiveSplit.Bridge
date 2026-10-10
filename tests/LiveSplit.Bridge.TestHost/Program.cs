using System;
using System.Globalization;
using System.Threading;
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
            string command;
            while ((command = Console.ReadLine()) is not null && command.Length > 0)
                WaitForEvents(runtime, command);
            return 0;
        }
        catch (BridgeTransportStartException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void WaitForEvents(BridgeRuntime runtime, string command)
    {
        var parts = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (
            parts.Length != 3
            || parts[0] != "WAIT_EVENTS"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expected)
            || expected < 0
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var timeoutMilliseconds)
            || timeoutMilliseconds < 1
        )
        {
            Console.WriteLine("ERROR invalid command");
            Console.Out.Flush();
            return;
        }

        var reached = SpinWait.SpinUntil(
            () => runtime.EventsSessionCount == expected,
            timeoutMilliseconds
        );
        Console.WriteLine(reached ? $"EVENTS_READY {expected}" : $"EVENTS_TIMEOUT {runtime.EventsSessionCount}");
        Console.Out.Flush();
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: LiveSplit.Bridge.TestHost [--port 1..65535]");
        return 2;
    }
}

