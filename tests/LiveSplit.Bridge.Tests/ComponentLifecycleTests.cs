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
        var ports = GetFreePorts(4);
        var (rpcA, eventA, rpcB, eventB) = (ports[0], ports[1], ports[2], ports[3]);

        var componentA = new Component(state);
        SetSettings(componentA, rpcA, eventA);
        Update(componentA, state);
        WaitForListener(rpcA, expected: true);
        WaitForListener(eventA, expected: true);

        var componentB = new Component(state);
        SetSettings(componentB, rpcB, eventB);

        componentA.Dispose();
        WaitForListener(rpcA, expected: false);
        WaitForListener(eventA, expected: false);

        Update(componentB, state);
        WaitForListener(rpcB, expected: true);
        WaitForListener(eventB, expected: true);

        Assert.False(IsLoopbackListening(rpcA));
        Assert.False(IsLoopbackListening(eventA));

        componentB.Dispose();
        WaitForListener(rpcB, expected: false);
        WaitForListener(eventB, expected: false);
    }

    [Fact]
    public void SetSettingsDoesNotBindEndpointsUntilUpdate()
    {
        var state = CreateState();
        var ports = GetFreePorts(2);
        using var component = new Component(state);

        SetSettings(component, ports[0], ports[1]);

        Assert.False(IsLoopbackListening(ports[0]));
        Assert.False(IsLoopbackListening(ports[1]));

        Update(component, state);
        WaitForListener(ports[0], expected: true);
        WaitForListener(ports[1], expected: true);
    }

    [Fact]
    public void DisposeReleasesEndpointsForReuse()
    {
        var state = CreateState();
        var ports = GetFreePorts(2);

        var component = new Component(state);
        SetSettings(component, ports[0], ports[1]);
        Update(component, state);
        WaitForListener(ports[0], expected: true);
        WaitForListener(ports[1], expected: true);

        component.Dispose();
        WaitForListener(ports[0], expected: false);
        WaitForListener(ports[1], expected: false);

        using var replacement = new Component(state);
        SetSettings(replacement, ports[0], ports[1]);
        Update(replacement, state);
        WaitForListener(ports[0], expected: true);
        WaitForListener(ports[1], expected: true);
    }

    [Fact]
    public void ApplyRestartsRuntimeOnNewPortsAndReleasesOldOnes()
    {
        var state = CreateState();
        var ports = GetFreePorts(4);
        var (oldRpc, oldEvent, newRpc, newEvent) = (ports[0], ports[1], ports[2], ports[3]);

        using var component = new Component(state);
        SetSettings(component, oldRpc, oldEvent);
        var control = (BridgeSettingsControl)component.GetSettingsControl(LayoutMode.Vertical);
        Update(component, state);
        WaitForListener(oldRpc, expected: true);
        WaitForListener(oldEvent, expected: true);

        var inputs = FindControls<NumericUpDown>(control).ToArray();
        Assert.Equal(2, inputs.Length);
        inputs[0].Value = newRpc;
        inputs[1].Value = newEvent;
        FindControls<Button>(control).Single().PerformClick();

        WaitForListener(newRpc, expected: true);
        WaitForListener(newEvent, expected: true);
        WaitForListener(oldRpc, expected: false);
        WaitForListener(oldEvent, expected: false);

        Update(component, state);
        Assert.Equal("Status: Running", GetStatusText(control));
    }

    [Fact]
    public void ApplyWithMatchingPortsReportsValidationErrorAndKeepsRuntime()
    {
        var state = CreateState();
        var ports = GetFreePorts(2);

        using var component = new Component(state);
        SetSettings(component, ports[0], ports[1]);
        var control = (BridgeSettingsControl)component.GetSettingsControl(LayoutMode.Vertical);
        Update(component, state);
        WaitForListener(ports[0], expected: true);

        var inputs = FindControls<NumericUpDown>(control).ToArray();
        inputs[0].Value = ports[0];
        inputs[1].Value = ports[0];
        FindControls<Button>(control).Single().PerformClick();

        Assert.Contains(
            FindControls<Label>(control),
            label => label.Text == "RPC port and Event port must be different.");
        Assert.True(IsLoopbackListening(ports[0]));
    }

    [Fact]
    public void BindFailureMarksStatusFailedAndRetriesAfterDelay()
    {
        var state = CreateState();
        var ports = GetFreePorts(2);
        var (rpcPort, eventPort) = (ports[0], ports[1]);

        var blocker = new TcpListener(IPAddress.Loopback, rpcPort);
        blocker.Server.ExclusiveAddressUse = true;
        blocker.Start();

        using var component = new Component(state);
        SetSettings(component, rpcPort, eventPort);
        var control = (BridgeSettingsControl)component.GetSettingsControl(LayoutMode.Vertical);
        Update(component, state);

        Assert.Equal("Status: Failed", GetStatusText(control));
        WaitForListener(eventPort, expected: false);

        blocker.Stop();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !(IsLoopbackListening(rpcPort) && IsLoopbackListening(eventPort)))
        {
            Update(component, state);
            Thread.Sleep(200);
        }

        WaitForListener(rpcPort, expected: true);
        WaitForListener(eventPort, expected: true);
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

    private static void SetSettings(Component component, int rpcPort, int eventPort)
    {
        var document = new XmlDocument();
        document.LoadXml($"<Settings><RpcPort>{rpcPort}</RpcPort><EventPort>{eventPort}</EventPort></Settings>");
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

    private static void WaitForListener(int port, bool expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (IsLoopbackListening(port) == expected)
            {
                return;
            }

            Thread.Sleep(50);
        }

        Assert.Equal(expected, IsLoopbackListening(port));
    }

    private static bool IsLoopbackListening(int port)
    {
        return IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port && IPAddress.IsLoopback(endpoint.Address));
    }

    private static int[] GetFreePorts(int count)
    {
        var ports = new HashSet<int>();
        while (ports.Count < count)
        {
            ports.Add(GetFreePort());
        }

        return ports.ToArray();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
