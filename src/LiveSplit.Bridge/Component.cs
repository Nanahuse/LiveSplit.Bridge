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

public sealed class Component : IComponent
{
    private readonly LiveSplitState state;
    private readonly BridgeSettings settings = new();
    private BridgeSettingsControl? settingsControl;
    private static readonly object RuntimeLock = new();
    private static BridgeRuntime? ActiveRuntime;
    private static int ActiveComponentCount;

    public Component(LiveSplitState state)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));

        lock (RuntimeLock)
        {
            ActiveComponentCount++;
            if (ActiveRuntime == null)
            {
                ActiveRuntime = CreateRuntime();
            }
            else
            {
                Debug.WriteLine("[LiveSplit.Bridge] A Bridge runtime is already active. This component will not start a second runtime.");
            }
        }
    }

    public string ComponentName => "LiveSplit Bridge";

    public float HorizontalWidth => 0;
    public float VerticalHeight => 0;

    public float MinimumWidth => 0;
    public float MinimumHeight => 0;

    public float PaddingTop => 0;
    public float PaddingBottom => 0;
    public float PaddingLeft => 0;
    public float PaddingRight => 0;

    public IDictionary<string, Action> ContextMenuControls { get; } =
        new Dictionary<string, Action>();

    public void DrawHorizontal(
        Graphics graphics,
        LiveSplitState state,
        float height,
        Region clipRegion)
    {
        // The Bridge does not render anything.
    }

    public void DrawVertical(
        Graphics graphics,
        LiveSplitState state,
        float width,
        Region clipRegion)
    {
        // The Bridge does not render anything.
    }

    public Control GetSettingsControl(LayoutMode mode)
    {
        settingsControl ??= new BridgeSettingsControl(settings);
        settingsControl.PortsChanged -= SettingsControlOnPortsChanged;
        settingsControl.PortsChanged += SettingsControlOnPortsChanged;
        settingsControl.SetValues(settings);
        return settingsControl;
    }

    public XmlNode GetSettings(XmlDocument document)
    {
        var element = document.CreateElement("Settings");
        settings.WriteTo(element);
        return element;
    }

    public void SetSettings(XmlNode settings)
    {
        var previousRpcPort = this.settings.RpcPort;
        var previousEventPort = this.settings.EventPort;
        this.settings.ReadFrom(settings);
        settingsControl?.SetValues(this.settings);

        if (previousRpcPort != this.settings.RpcPort || previousEventPort != this.settings.EventPort)
        {
            RestartRuntime();
        }
    }

    public void Update(
        IInvalidator invalidator,
        LiveSplitState state,
        float width,
        float height,
        LayoutMode mode)
    {
        lock (RuntimeLock)
        {
            ActiveRuntime?.ObserveExternalState();
        }
    }

    public void Dispose()
    {
        lock (RuntimeLock)
        {
            ActiveComponentCount = Math.Max(0, ActiveComponentCount - 1);
            if (ActiveComponentCount == 0)
            {
                ActiveRuntime?.Dispose();
                ActiveRuntime = null;
            }
        }
    }

    private BridgeRuntime CreateRuntime()
    {
        return new BridgeRuntime(state, settings.RpcPort, settings.EventPort);
    }

    private void SettingsControlOnPortsChanged(object sender, EventArgs e)
    {
        if (settingsControl == null)
        {
            return;
        }

        if (settingsControl.RpcPort == settingsControl.EventPort)
        {
            MessageBox.Show(
                "RPC port and event port must be different.",
                ComponentName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        settings.RpcPort = settingsControl.RpcPort;
        settings.EventPort = settingsControl.EventPort;
        RestartRuntime();
    }

    private void RestartRuntime()
    {
        lock (RuntimeLock)
        {
            ActiveRuntime?.Dispose();
            ActiveRuntime = CreateRuntime();
        }
    }
}
