#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);

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
        _ = form.Handle;
        var layoutSettings = new BridgeLayoutSettings();
        var layout = new Layout { Settings = layoutSettings };
        var state = new LiveSplitState(run, form, layout, layoutSettings, new Settings());
        state.CurrentComparison = "Personal Best";

        using var component = new Component(state);
        var settingsDocument = new XmlDocument();
        var componentSettings = component.GetSettings(settingsDocument);
        componentSettings.SelectSingleNode("WebSocketPort")!.InnerText = port.ToString(CultureInfo.InvariantCulture);
        component.SetSettings(componentSettings);

        try
        {
            EnsurePortAvailable(port);
        }
        catch (SocketException exception)
        {
            Console.Error.WriteLine(
                $"STARTUP_CHECK_FAILED: port {port} is already occupied: "
                + $"{exception.SocketErrorCode}: {exception.Message}"
            );
            Console.Error.WriteLine(exception);
            return 1;
        }

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
        using var startupTimeout = new CancellationTokenSource(StartupTimeout);
        var progress = new StartupProbeProgress();
        var startupProbe = Task.Run(() => ProbeRpcUntilReady(port, startupTimeout.Token, progress));
        var stopwatch = Stopwatch.StartNew();
        while (!startupProbe.IsCompleted && stopwatch.Elapsed < StartupTimeout)
        {
            Application.DoEvents();
            component.Update(null, state, 0, 0, LayoutMode.Horizontal);
            Thread.Sleep(10);
        }

        if (!startupProbe.IsCompleted)
        {
            startupTimeout.Cancel();
            ReportStartupFailure(StartupFailure(port, progress), progress);
            return 1;
        }

        try
        {
            startupProbe.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            ReportStartupFailure(exception, progress);
            return 1;
        }

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

    private static void EnsurePortAvailable(int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ExclusiveAddressUse = true,
        };
        socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
    }

    private sealed class StartupProbeProgress
    {
        internal string Stage = "waiting to start";
        internal Exception? LastError;
    }

    private static TimeoutException StartupFailure(int port, StartupProbeProgress progress) =>
        new($"JSON v4 RPC did not respond on port {port} within {StartupTimeout.TotalSeconds:0} seconds.",
            Volatile.Read(ref progress.LastError));

    private static void ReportStartupFailure(Exception exception, StartupProbeProgress progress)
    {
        Console.Error.WriteLine($"STARTUP_CHECK_FAILED: RPC startup failed during {Volatile.Read(ref progress.Stage)}.");
        // Exception.ToString includes the inner connection/socket exception and stack.
        Console.Error.WriteLine(exception);
    }

    private static void ProbeRpcUntilReady(int port, CancellationToken cancellationToken, StartupProbeProgress progress)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ProbeRpc(port, cancellationToken, progress);
                return;
            }
            catch (Exception exception) when (exception is WebSocketException or SocketException or OperationCanceledException)
            {
                Volatile.Write(ref progress.LastError, new InvalidOperationException(
                    $"RPC startup probe failed during {Volatile.Read(ref progress.Stage)}.", exception));
                if (exception is OperationCanceledException) break;
            }
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(50));
        }

        throw StartupFailure(port, progress);
    }

    private static void ProbeRpc(int port, CancellationToken cancellationToken, StartupProbeProgress progress)
    {
        using var client = new ClientWebSocket();
        Volatile.Write(ref progress.Stage, "connecting");
        client.ConnectAsync(
            new Uri($"ws://127.0.0.1:{port}/bridge/v4/rpc"),
            cancellationToken
        ).GetAwaiter().GetResult();

        var request = Encoding.UTF8.GetBytes("{\"request_id\":0,\"method\":\"get_timer_state\"}");
        Volatile.Write(ref progress.Stage, "sending get_timer_state");
        client.SendAsync(
            new ArraySegment<byte>(request),
            WebSocketMessageType.Text,
            true,
            cancellationToken
        ).GetAwaiter().GetResult();

        var buffer = new byte[4096];
        WebSocketReceiveResult received;
        Volatile.Write(ref progress.Stage, "receiving get_timer_state response");
        do
        {
            received = client.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken
            ).GetAwaiter().GetResult();
            if (received.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException("RPC startup probe received a non-text response.");
        }
        while (!received.EndOfMessage);
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: LiveSplit.Bridge.TestHost [--port 1..65535]");
        return 2;
    }
}
