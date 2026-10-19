using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);

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
        var startupProbe = Task.Run(() => ProbeRpcUntilReady(port, startupTimeout.Token));
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
            Console.Error.WriteLine(
                $"STARTUP_CHECK_FAILED: JSON v4 RPC did not become ready within {StartupTimeout.TotalSeconds:0} seconds."
            );
            return 1;
        }

        try
        {
            startupProbe.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"STARTUP_CHECK_FAILED: {exception.GetType().FullName}: {exception.Message}"
            );
            Console.Error.WriteLine(exception.StackTrace);
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

    private static void ProbeRpcUntilReady(int port, CancellationToken cancellationToken)
    {
        Exception lastError = new TimeoutException("The RPC endpoint did not accept a connection.");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ProbeRpc(port, cancellationToken);
                return;
            }
            catch (WebSocketException exception)
            {
                lastError = exception;
            }
            catch (SocketException exception)
            {
                lastError = exception;
            }
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException(
            $"JSON v4 RPC did not respond on port {port} before the startup timeout.",
            lastError
        );
    }

    private static void ProbeRpc(int port, CancellationToken cancellationToken)
    {
        using var client = new ClientWebSocket();
        client.ConnectAsync(
            new Uri($"ws://127.0.0.1:{port}/bridge/v4/rpc"),
            cancellationToken
        ).GetAwaiter().GetResult();

        var request = Encoding.UTF8.GetBytes("{\"request_id\":0,\"method\":\"get_timer_state\"}");
        client.SendAsync(
            new ArraySegment<byte>(request),
            WebSocketMessageType.Text,
            true,
            cancellationToken
        ).GetAwaiter().GetResult();

        using var response = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult received;
        do
        {
            received = client.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken
            ).GetAwaiter().GetResult();
            if (received.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("RPC startup probe received a non-text response.");
            response.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);

        using var document = JsonDocument.Parse(response.ToArray());
        var root = document.RootElement;
        if (
            !root.TryGetProperty("request_id", out var requestId)
            || !requestId.TryGetUInt32(out var requestIdValue)
            || requestIdValue != 0
            || !root.TryGetProperty("session_id", out var sessionId)
            || !sessionId.TryGetUInt32(out var sessionIdValue)
            || sessionIdValue == 0
            || !root.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object
            || root.TryGetProperty("error", out _)
            || !result.TryGetProperty("phase", out var phase)
            || phase.ValueKind != JsonValueKind.String
            || !result.TryGetProperty("split_index", out var splitIndex)
            || !splitIndex.TryGetInt32(out _)
            || !result.TryGetProperty("real_time_ticks", out var realTimeTicks)
            || !realTimeTicks.TryGetInt64(out _)
            || !result.TryGetProperty("game_time_ticks", out var gameTimeTicks)
            || (gameTimeTicks.ValueKind != JsonValueKind.Null && !gameTimeTicks.TryGetInt64(out _))
            || !result.TryGetProperty("is_game_time_initialized", out var initialized)
            || initialized.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !result.TryGetProperty("is_game_time_paused", out var paused)
            || paused.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
        )
            throw new InvalidDataException("RPC startup probe received an invalid get_timer_state response.");
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: LiveSplit.Bridge.TestHost [--port 1..65535]");
        return 2;
    }
}
