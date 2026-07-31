using System;
using System.Reflection;
using LiveSplit.Model;
using LiveSplit.UI.Components;

namespace LiveSplit.Bridge;

public sealed class Factory : IComponentFactory
{
    public string ComponentName => "LiveSplit Bridge";

    public string Description =>
        "Controls and monitors LiveSplit from external applications.";

    public ComponentCategory Category => ComponentCategory.Control;

    public string UpdateName => ComponentName;

    public string XMLURL => string.Empty;

    public string UpdateURL => string.Empty;

    public Version Version =>
        Assembly.GetExecutingAssembly().GetName().Version
        ?? new Version(0, 1, 0);

    public IComponent Create(LiveSplitState state)
    {
        return new Component(state);
    }
}
