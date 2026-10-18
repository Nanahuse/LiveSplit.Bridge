using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.Options;
using LiveSplit.UI;
using LiveSplit.UI.Components;
using BridgeLayoutSettings = LiveSplit.Options.LayoutSettings;

namespace LiveSplit.Bridge.TestHost;

internal static class Program
{
    private const int DefaultWebSocketPort = 54000;

    [STAThread]
    private static int Main(string[] args)
    {
        var port = DefaultWebSocketPort;
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

        using var component = new Component(state);
        var settingsDocument = new XmlDocument();
        var componentSettings = component.GetSettings(settingsDocument);
        componentSettings.SelectSingleNode("WebSocketPort")!.InnerText = port.ToString(CultureInfo.InvariantCulture);
        component.SetSettings(componentSettings);

        var stop = 0;
        var inputThread = new Thread(() =>
        {
            Console.ReadLine();
            Interlocked.Exchange(ref stop, 1);
        })
        {
            IsBackground = true,
            Name = "TestHost shutdown input",
        };
        inputThread.Start();

        component.Update(null, state, 0, 0, LayoutMode.Horizontal);
        Console.WriteLine("READY");
        Console.Out.Flush();

        while (Volatile.Read(ref stop) == 0)
        {
            Application.DoEvents();
            component.Update(null, state, 0, 0, LayoutMode.Horizontal);
            Thread.Sleep(10);
        }
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: LiveSplit.Bridge.TestHost [--port 1..65535]");
        return 2;
    }
}
