using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.UI;

namespace LiveSplit.Bridge.Tests;

[CollectionDefinition("BridgeRuntimeEndpoints")]
public sealed class BridgeRuntimeEndpointsCollection
{
}

[Collection("BridgeRuntimeEndpoints")]
public class ComponentLifecycleTests
{
    [Fact]
    public void LayoutSwitchReleasesPreviousRuntimeAndStartsNextRuntimeOnItsOwnPorts()
    {
        var state = CreateState();
        var ports = BridgeTestEndpoints.GetFreePorts(2);
        var (portA, portB) = (ports[0], ports[1]);

        var componentA = new Component(state);
        SetSettings(componentA, portA);
        Update(componentA, state);
        BridgeTestEndpoints.WaitForListener(portA, expected: true);

        var componentB = new Component(state);
        SetSettings(componentB, portB);

        componentA.Dispose();
        BridgeTestEndpoints.WaitForListener(portA, expected: false);

        Update(componentB, state);
        BridgeTestEndpoints.WaitForListener(portB, expected: true);

        Assert.False(BridgeTestEndpoints.IsLoopbackListening(portA));

        componentB.Dispose();
        BridgeTestEndpoints.WaitForListener(portB, expected: false);
    }

    [Fact]
    public void SetSettingsDoesNotBindEndpointUntilUpdate()
    {
        var state = CreateState();
        var port = BridgeTestEndpoints.GetFreePort();
        using var component = new Component(state);

        SetSettings(component, port);

        Assert.False(BridgeTestEndpoints.IsLoopbackListening(port));

        Update(component, state);
        BridgeTestEndpoints.WaitForListener(port, expected: true);
    }

    [Fact]
    public void DisposeReleasesEndpointForReuse()
    {
        var state = CreateState();
        var port = BridgeTestEndpoints.GetFreePort();

        var component = new Component(state);
        SetSettings(component, port);
        Update(component, state);
        BridgeTestEndpoints.WaitForListener(port, expected: true);

        component.Dispose();
        BridgeTestEndpoints.WaitForListener(port, expected: false);

        using var replacement = new Component(state);
        SetSettings(replacement, port);
        Update(replacement, state);
        BridgeTestEndpoints.WaitForListener(port, expected: true);
    }

    [Fact]
    public void ApplyRestartsRuntimeOnNewPortAndReleasesOldOne()
    {
        var state = CreateState();
        var ports = BridgeTestEndpoints.GetFreePorts(2);
        var (oldPort, newPort) = (ports[0], ports[1]);

        using var component = new Component(state);
        SetSettings(component, oldPort);
        var control = (BridgeSettingsControl)component.GetSettingsControl(LayoutMode.Vertical);
        Update(component, state);
        BridgeTestEndpoints.WaitForListener(oldPort, expected: true);

        var inputs = FindControls<NumericUpDown>(control).ToArray();
        Assert.Single(inputs);
        inputs[0].Value = newPort;
        FindControls<Button>(control).Single().PerformClick();

        BridgeTestEndpoints.WaitForListener(newPort, expected: true);
        BridgeTestEndpoints.WaitForListener(oldPort, expected: false);

        Update(component, state);
        Assert.Equal("Status: Running", GetStatusText(control));
    }

    [Fact]
    public void BindFailureMarksStatusFailedAndRetriesAfterDelay()
    {
        var state = CreateState();
        var port = BridgeTestEndpoints.GetFreePort();

        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Server.ExclusiveAddressUse = true;
        blocker.Start();

        using var component = new Component(state);
        SetSettings(component, port);
        var control = (BridgeSettingsControl)component.GetSettingsControl(LayoutMode.Vertical);
        Update(component, state);

        Assert.Equal("Status: Failed", GetStatusText(control));

        blocker.Stop();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !BridgeTestEndpoints.IsLoopbackListening(port))
        {
            Update(component, state);
            Thread.Sleep(200);
        }

        BridgeTestEndpoints.WaitForListener(port, expected: true);
        Assert.Equal("Status: Running", GetStatusText(control));
    }

    private static LiveSplitState CreateState()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Add(new Segment("Two"));
        return TestLiveSplitState.Create(run);
    }

    private static void Update(Component component, LiveSplitState state)
    {
        component.Update(null!, state, 0, 0, LayoutMode.Vertical);
    }

    private static void SetSettings(Component component, int port)
    {
        var document = new XmlDocument();
        document.LoadXml($"<Settings><Version>1</Version><WebSocketPort>{port}</WebSocketPort></Settings>");
        component.SetSettings(document.DocumentElement!);
    }

    private static string GetStatusText(BridgeSettingsControl control)
    {
        return FindControls<Label>(control)
            .Single(label => label.Text.StartsWith("Status:", StringComparison.Ordinal))
            .Text;
    }

    private static IEnumerable<TControl> FindControls<TControl>(Control root)
        where TControl : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is TControl match)
            {
                yield return match;
            }

            foreach (var descendant in FindControls<TControl>(child))
            {
                yield return descendant;
            }
        }
    }
}
