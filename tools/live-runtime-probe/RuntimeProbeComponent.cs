using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.Model;
using LiveSplit.UI;
using LiveSplit.UI.Components;

[assembly: ComponentFactory(typeof(Factory))]

public sealed class Factory : IComponentFactory
{
    public string ComponentName => "LiveSplit JSON Runtime Probe";
    public string Description => "Verifies System.Text.Json inside the LiveSplit process.";
    public ComponentCategory Category => ComponentCategory.Other;
    public string UpdateName => ComponentName;
    public string XMLURL => string.Empty;
    public string UpdateURL => string.Empty;
    public Version Version => new(1, 0, 0);
    public IComponent Create(LiveSplitState state)
    {
        RuntimeProbeComponent.RecordStage("FACTORY_CREATE");
        return new RuntimeProbeComponent();
    }
}

public sealed class RuntimeProbeComponent : IComponent
{
    private static readonly string logPath = CreateLogPath();
    private static int logWriteFailed;
    private readonly Stopwatch heartbeat = Stopwatch.StartNew();
    private bool heartbeatRecorded;

    public RuntimeProbeComponent()
    {
        RecordStage("COMPONENT_CONSTRUCTOR");
        try
        {
            WriteHeader();
            RunJsonChecks();
        }
        catch (Exception exception)
        {
            Append($"FAIL COMPONENT_CONSTRUCTOR: {exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception.StackTrace}");
        }
    }

    public string ComponentName => "LiveSplit JSON Runtime Probe";
    public float HorizontalWidth => 0;
    public float VerticalHeight => 0;
    public float MinimumWidth => 0;
    public float MinimumHeight => 0;
    public float PaddingTop => 0;
    public float PaddingBottom => 0;
    public float PaddingLeft => 0;
    public float PaddingRight => 0;
    public IDictionary<string, Action> ContextMenuControls { get; } = new Dictionary<string, Action>();

    public void DrawHorizontal(System.Drawing.Graphics graphics, LiveSplitState state, float height, System.Drawing.Region clipRegion) { }
    public void DrawVertical(System.Drawing.Graphics graphics, LiveSplitState state, float width, System.Drawing.Region clipRegion) { }
    public Control GetSettingsControl(LayoutMode mode) => new Panel();
    public XmlNode GetSettings(XmlDocument document) => document.CreateElement("Settings");
    public void SetSettings(XmlNode node) { }

    public void Update(IInvalidator invalidator, LiveSplitState state, float width, float height, LayoutMode mode)
    {
        if (heartbeatRecorded || heartbeat.Elapsed < TimeSpan.FromSeconds(5)) return;
        heartbeatRecorded = true;
        Append("UPDATE_HEARTBEAT LiveSplit update loop remained active for at least five seconds after JSON checks.");
    }

    public void Dispose() { }

    private void WriteHeader()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var exePath = process.MainModule?.FileName ?? "unavailable";
            var info = File.Exists(exePath) ? FileVersionInfo.GetVersionInfo(exePath) : null;
            Append($"LiveSplit runtime probe started: {DateTime.UtcNow:O}");
            Append($"Log path: {logPath}");
            Append($"Process: {exePath}");
            Append($"ProductVersion: {info?.ProductVersion ?? "unavailable"}; FileVersion: {info?.FileVersion ?? "unavailable"}");
            Append($"TargetFramework: {AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName}; CLR: {Environment.Version}");
        }
        catch (Exception exception)
        {
            Append($"FAIL process metadata ({exception.GetType().FullName}: {exception.Message})");
        }
    }

    private void RunJsonChecks()
    {
        RecordStage("JSON_OPTIONS_START");
        JsonSerializerOptions options;
        try
        {
            options = BridgeJsonOptions.Create();
            RecordStage("JSON_OPTIONS_SUCCESS");
        }
        catch (Exception exception)
        {
            Append($"FAIL JSON_OPTIONS_INITIALIZATION: {exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception.StackTrace}");
            return;
        }

        RecordStage("JSON_CHECKS_START");
        var state = new TimerState
        {
            Phase = TimerPhase.Running,
            SplitIndex = 2,
            RealTimeTicks = 123456,
            GameTimeTicks = null,
            IsGameTimeInitialized = false,
            IsGameTimePaused = false,
        };

        Check("Request method and timer operation params", () =>
        {
            const string json = "{\"request_id\":4294967295,\"method\":\"timer_operation\",\"params\":{\"operation\":\"start\"}}";
            return JsonSerializer.Deserialize<RpcRequest>(json, options) is TimerOperationRequest request
                && request.RequestId == uint.MaxValue && request.Params is TimerStartParams;
        });
        Check("Game Time set params and signed ticks", () =>
        {
            const string json = "{\"method\":\"game_time_operation\",\"request_id\":0,\"params\":{\"ticks\":-123456,\"operation\":\"set\"}}";
            return JsonSerializer.Deserialize<RpcRequest>(json, options) is GameTimeOperationRequest request
                && request.RequestId == 0 && request.Params is GameTimeSetParams { Ticks: -123456 };
        });
        Check("Query success and uint32 response identifiers", () =>
        {
            var response = new TimerStateResponse { RequestId = uint.MaxValue, SessionId = uint.MaxValue, Result = state };
            var json = JsonSerializer.Serialize<RpcResponse>(response, options);
            var parsed = JsonSerializer.Deserialize<TimerStateResponse>(json, options);
            return parsed?.RequestId == uint.MaxValue && parsed.SessionId == uint.MaxValue && parsed.Result.RealTimeTicks == state.RealTimeTicks;
        });
        Check("Control success emits result null", () => JsonSerializer.Serialize<RpcResponse>(new ControlSuccessResponse { RequestId = 0, SessionId = 1 }, options).Contains("\"result\":null"));
        Check("Error response serializes", () => JsonSerializer.Serialize<RpcResponse>(new ErrorResponse { RequestId = 1, SessionId = 2, Error = new BridgeError { Code = BridgeErrorCode.PermissionDenied, Message = "denied" } }, options).Contains("\"permission_denied\""));
        Check("Timer event and reset nullable state serialize", () =>
        {
            var withState = JsonSerializer.Serialize<BridgeEvent>(new TimerStartedEvent { SessionId = 1, EventSequence = 1, TimerState = state }, options);
            var resetWithoutState = JsonSerializer.Serialize<BridgeEvent>(new TimerResetEvent { SessionId = uint.MaxValue, EventSequence = uint.MaxValue, TimerState = null }, options);
            return withState.Contains("\"timer_started\"") && resetWithoutState.Contains("\"timer_state\":null")
                && JsonSerializer.Deserialize<BridgeEvent>(resetWithoutState, options) is TimerResetEvent { SessionId: uint.MaxValue, EventSequence: uint.MaxValue, TimerState: null };
        });
        Check("Run and Context changed events emit null-only timer state", () =>
        {
            var runChanged = JsonSerializer.Serialize<BridgeEvent>(new RunChangedEvent { SessionId = 2, EventSequence = 1 }, options);
            var contextChanged = JsonSerializer.Serialize<BridgeEvent>(new ContextChangedEvent { SessionId = 2, EventSequence = 2 }, options);
            return runChanged.Contains("\"timer_state\":null") && contextChanged.Contains("\"timer_state\":null");
        });

        RecordLoadedAssemblies();
        RecordStage("JSON_CHECKS_COMPLETE");
    }

    private void Check(string name, Func<bool> check)
    {
        try
        {
            if (check()) Append($"PASS {name}");
            else Append($"FAIL {name} (check returned false)");
        }
        catch (Exception exception)
        {
            Append($"FAIL {name}: {exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception.StackTrace}");
        }
    }

    private void RecordLoadedAssemblies()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LiveSplit", "LiveSplit.Core", "LiveSplit.View", "LiveSplit.JsonRuntimeProbe",
            "System.Text.Json", "System.Text.Encodings.Web", "Microsoft.Bcl.AsyncInterfaces",
            "System.Buffers", "System.IO.Pipelines", "System.Memory", "System.Numerics.Vectors",
            "System.Runtime.CompilerServices.Unsafe", "System.Threading.Tasks.Extensions", "System.ValueTuple",
        };
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => names.Contains(assembly.GetName().Name ?? string.Empty)))
        {
            try
            {
                var identity = assembly.GetName();
                Append($"LOADED {identity.Name}, {identity.Version}; {assembly.Location}");
            }
            catch (Exception exception)
            {
                Append($"LOADED {assembly.FullName}; location unavailable: {exception.Message}");
            }
        }
    }

    public static void RecordStage(string stage) => Append(stage);

    private static string CreateLogPath()
    {
        try
        {
            var processId = Process.GetCurrentProcess().Id;
            var runId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'");
            return Path.Combine(Path.GetTempPath(), $"LiveSplit.JsonRuntimeProbe-{processId}-{runId}.log");
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.JsonRuntimeProbe] Could not determine temporary log path: {exception}");
            return string.Empty;
        }
    }

    private static void Append(string line)
    {
        if (Volatile.Read(ref logWriteFailed) != 0)
        {
            Debug.WriteLine($"[LiveSplit.JsonRuntimeProbe] {line}");
            return;
        }

        try
        {
            if (string.IsNullOrEmpty(logPath)) throw new InvalidOperationException("The temporary log path is unavailable.");
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref logWriteFailed, 1);
            Debug.WriteLine($"[LiveSplit.JsonRuntimeProbe] Log write failed for '{logPath}': {exception}");
            Debug.WriteLine($"[LiveSplit.JsonRuntimeProbe] {line}");
        }
    }
}
