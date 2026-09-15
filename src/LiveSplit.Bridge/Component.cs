using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.Model;
using LiveSplit.UI;
using LiveSplit.UI.Components;

namespace LiveSplit.Bridge;

internal enum BridgeRuntimeStatus { Starting, Running, Failed, Stopped }

public sealed class Component : IComponent
{
    private readonly LiveSplitState state;
    private readonly BridgeSettings settings = new();
    private readonly object runtimeLock = new();
    private BridgeSettingsControl? settingsControl;
    private BridgeRuntime? runtime;
    private BridgeRuntimeStatus status = BridgeRuntimeStatus.Starting;
    private string? lastError;
    private long retryAt;

    public Component(LiveSplitState state) { this.state = state ?? throw new ArgumentNullException(nameof(state)); }
    public string ComponentName => "LiveSplit Bridge";
    public float HorizontalWidth => 0; public float VerticalHeight => 0; public float MinimumWidth => 0; public float MinimumHeight => 0;
    public float PaddingTop => 0; public float PaddingBottom => 0; public float PaddingLeft => 0; public float PaddingRight => 0;
    public IDictionary<string, Action> ContextMenuControls { get; } = new Dictionary<string, Action>();
    public void DrawHorizontal(Graphics graphics, LiveSplitState state, float height, Region clipRegion) { }
    public void DrawVertical(Graphics graphics, LiveSplitState state, float width, Region clipRegion) { }
    public Control GetSettingsControl(LayoutMode mode) { settingsControl ??= new BridgeSettingsControl(settings); settingsControl.PortsChanged -= SettingsControlOnPortsChanged; settingsControl.PortsChanged += SettingsControlOnPortsChanged; settingsControl.SetValues(settings); UpdateControl(); return settingsControl; }
    public XmlNode GetSettings(XmlDocument document) { var e = document.CreateElement("Settings"); settings.WriteTo(e); return e; }
    public void SetSettings(XmlNode node) { settings.ReadFrom(node); settingsControl?.SetValues(settings); }
    public void Update(IInvalidator invalidator, LiveSplitState state, float width, float height, LayoutMode mode)
    {
        lock (runtimeLock)
        {
            if (status == BridgeRuntimeStatus.Stopped) return;
            if (runtime == null && (status != BridgeRuntimeStatus.Failed || Stopwatch.GetTimestamp() >= retryAt)) TryStartRuntime();
            runtime?.ObserveExternalState(); UpdateControl();
        }
    }
    public void Dispose() { lock (runtimeLock) { runtime?.Dispose(); runtime = null; status = BridgeRuntimeStatus.Stopped; lastError = null; retryAt = 0; } }
    private void TryStartRuntime()
    {
        status = BridgeRuntimeStatus.Starting; UpdateControl();
        try { runtime = new BridgeRuntime(state, settings.RpcPort, settings.EventPort); status = BridgeRuntimeStatus.Running; lastError = null; Debug.WriteLine("[LiveSplit.Bridge] Bridge runtime recovered successfully."); }
        catch (BridgeTransportStartException ex) { runtime = null; status = BridgeRuntimeStatus.Failed; lastError = $"Failed to bind {ex.EndpointKind} endpoint:\r\n{ex.Endpoint}\r\n\r\nThe port may already be in use.\r\nRetrying automatically every 5 seconds."; retryAt = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency; Debug.WriteLine($"[LiveSplit.Bridge] {ex.Message}: {ex.InnerException?.Message}"); }
        catch (Exception ex) { runtime = null; status = BridgeRuntimeStatus.Failed; lastError = $"Runtime startup failed:\r\n{ex.Message}\r\n\r\nRetrying automatically every 5 seconds."; retryAt = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency; Debug.WriteLine($"[LiveSplit.Bridge] Runtime startup failed: {ex}"); }
    }
    private void SettingsControlOnPortsChanged(object sender, EventArgs e) { if (settingsControl == null) return; if (settingsControl.RpcPort == settingsControl.EventPort) { settingsControl.SetValidationError("RPC port and Event port must be different."); return; } settings.RpcPort = settingsControl.RpcPort; settings.EventPort = settingsControl.EventPort; lock (runtimeLock) { runtime?.Dispose(); runtime = null; status = BridgeRuntimeStatus.Starting; lastError = null; retryAt = 0; TryStartRuntime(); } }
    private void UpdateControl() => settingsControl?.SetRuntimeStatus(status.ToString(), status == BridgeRuntimeStatus.Failed ? lastError : null);
}
