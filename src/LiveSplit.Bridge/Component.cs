using System;
using System.Collections.Generic;
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

    public Component(LiveSplitState state)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public string ComponentName => "LiveSplit Bridge";

    public float HorizontalWidth => 20;
    public float VerticalHeight => 20;

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
        return new Panel();
    }

    public XmlNode GetSettings(XmlDocument document)
    {
        return document.CreateElement("Settings");
    }

    public void SetSettings(XmlNode settings)
    {
    }

    public void Update(
        IInvalidator invalidator,
        LiveSplitState state,
        float width,
        float height,
        LayoutMode mode)
    {
    }

    public void Dispose()
    {
    }
}
