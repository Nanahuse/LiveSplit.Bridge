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
        run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));

        var settings = new Settings
        {
            HotkeyProfiles = new Dictionary<string, HotkeyProfile>
            {
                ["Default"] = new HotkeyProfile { GlobalHotkeysEnabled = true },
                ["Secondary"] = new HotkeyProfile { GlobalHotkeysEnabled = false },
            },
        };

        var state = TestLiveSplitState.Create(run, settings);
        state.CurrentHotkeyProfile = "Default";
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
}
