using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.Options;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class RuntimeRevisionTests
{
    [Fact]
    public void CurrentComparisonChangeAdvancesRuntimeRevision()
    {
        var state = CreateState(out _);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        state.CurrentComparison = "Best Segments";
        runtime.ObserveExternalState();

        Assert.Equal(2UL, runtime.RuntimeRevision);
    }

    [Fact]
    public void TimingMethodChangeAdvancesRuntimeRevision()
    {
        var state = CreateState(out _);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        state.CurrentTimingMethod = LiveSplit.Model.TimingMethod.GameTime;
        runtime.ObserveExternalState();

        Assert.Equal(2UL, runtime.RuntimeRevision);
    }

    [Fact]
    public void HotkeyProfileChangeAdvancesRuntimeRevision()
    {
        var state = CreateState(out _);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        state.CurrentHotkeyProfile = "Secondary";
        runtime.ObserveExternalState();

        Assert.Equal(2UL, runtime.RuntimeRevision);
    }

    [Fact]
    public void GlobalHotkeysChangeAdvancesRuntimeRevision()
    {
        var state = CreateState(out _);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        state.Settings.HotkeyProfiles["Default"].GlobalHotkeysEnabled = false;
        runtime.ObserveExternalState();

        Assert.Equal(2UL, runtime.RuntimeRevision);
    }

    [Fact]
    public void ProfileSwitchWithSameGlobalHotkeysDoesNotAdvanceRuntimeRevision()
    {
        var state = CreateState(
            ("Default", true),
            ("Secondary", true));
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        state.CurrentHotkeyProfile = "Secondary";
        runtime.ObserveExternalState();

        // The profile name is not part of RuntimeState, so switching between profiles
        // with the same Global Hotkeys value must not change runtime_revision.
        Assert.Equal(1UL, runtime.RuntimeRevision);
    }

    [Fact]
    public async Task ProfileSwitchWithSameGlobalHotkeysDoesNotPublishRuntimeChangedEvent()
    {
        var state = CreateState(
            ("Default", true),
            ("Secondary", true));
        var port = BridgeTestEndpoints.GetFreePort();
        using var runtime = new BridgeRuntime(state, port);

        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        await ReceiveUntilAsync(events, BridgeEventType.EventHeartbeat);

        state.CurrentHotkeyProfile = "Secondary";
        runtime.ObserveExternalState();

        var duplicate = await TryReceiveNonHeartbeatAsync(events, TimeSpan.FromSeconds(1));
        Assert.Null(duplicate);
        Assert.Equal(1UL, runtime.RuntimeRevision);
    }

    [Fact]
    public void ProfileSwitchWithDifferentGlobalHotkeysAdvancesRuntimeRevision()
    {
        var state = CreateState(
            ("Default", true),
            ("Secondary", false));
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        state.CurrentHotkeyProfile = "Secondary";
        runtime.ObserveExternalState();

        Assert.Equal(2UL, runtime.RuntimeRevision);
    }

    [Fact]
    public void MetadataCustomVariableChangeAdvancesRuntimeRevisionOnly()
    {
        var state = CreateState(out var run);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        var runRevision = runtime.RunRevision;
        run.Metadata.GetOrAddCustomVariable("custom").Value = "changed";
        runtime.ObserveExternalState();

        Assert.Equal(2UL, runtime.RuntimeRevision);
        // Custom variable current values live in RuntimeState, so run_revision is unchanged.
        Assert.Equal(runRevision, runtime.RunRevision);
    }

    [Fact]
    public void UnchangedRuntimeStateDoesNotAdvanceRuntimeRevision()
    {
        var state = CreateState(out _);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());

        runtime.ObserveExternalState();
        runtime.ObserveExternalState();

        Assert.Equal(1UL, runtime.RuntimeRevision);
    }

    [Fact]
    public async Task RuntimeChangePublishesRuntimeChangedEventWithUpdatedRevision()
    {
        var state = CreateState(out _);
        var port = BridgeTestEndpoints.GetFreePort();
        using var runtime = new BridgeRuntime(state, port);

        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        await ReceiveUntilAsync(events, BridgeEventType.EventHeartbeat);

        state.CurrentComparison = "Best Segments";
        runtime.ObserveExternalState();

        var runtimeChanged = await ReceiveUntilAsync(events, BridgeEventType.EventRuntimeChanged);
        Assert.NotNull(runtimeChanged.TimerState);
        Assert.Equal(2UL, runtimeChanged.TimerState.RuntimeRevision);
    }

    [Fact]
    public async Task MetadataCustomVariableChangePublishesRuntimeChangedEvent()
    {
        var state = CreateState(out var run);
        var port = BridgeTestEndpoints.GetFreePort();
        using var runtime = new BridgeRuntime(state, port);

        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(port));
        await ReceiveUntilAsync(events, BridgeEventType.EventHeartbeat);

        run.Metadata.GetOrAddCustomVariable("custom").Value = "changed";
        runtime.ObserveExternalState();

        var runtimeChanged = await ReceiveUntilAsync(events, BridgeEventType.EventRuntimeChanged);
        Assert.Equal(2UL, runtimeChanged.TimerState.RuntimeRevision);
    }

    private static LiveSplitState CreateState(out Run run)
    {
        return CreateState(out run, ("Default", true), ("Secondary", false));
    }

    private static LiveSplitState CreateState(params (string Name, bool Enabled)[] profiles)
    {
        return CreateState(out _, profiles);
    }

    private static LiveSplitState CreateState(
        out Run run,
        params (string Name, bool Enabled)[] profiles)
    {
        run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));

        var hotkeyProfiles = new Dictionary<string, HotkeyProfile>();
        foreach (var (name, enabled) in profiles)
        {
            hotkeyProfiles[name] = new HotkeyProfile { GlobalHotkeysEnabled = enabled };
        }

        var settings = new Settings { HotkeyProfiles = hotkeyProfiles };

        var state = TestLiveSplitState.Create(run, settings);
        state.CurrentHotkeyProfile = profiles.Length > 0 ? profiles[0].Name : "Default";
        state.CurrentComparison = "Personal Best";
        return state;
    }

    private static async Task<BridgeEvent> ReceiveUntilAsync(WebSocketTestClient client, BridgeEventType type)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var data = await client.ReceiveBinaryAsync(TimeSpan.FromSeconds(3));
            var bridgeEvent = BridgeEvent.Parser.ParseFrom(data);
            if (bridgeEvent.Type == type)
            {
                return bridgeEvent;
            }
        }

        throw new TimeoutException($"Did not receive {type}.");
    }

    private static async Task<BridgeEvent?> TryReceiveNonHeartbeatAsync(
        WebSocketTestClient client,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var data = await client.ReceiveBinaryAsync(deadline - DateTime.UtcNow);
                var bridgeEvent = BridgeEvent.Parser.ParseFrom(data);
                if (bridgeEvent.Type != BridgeEventType.EventHeartbeat)
                {
                    return bridgeEvent;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        return null;
    }
}
