using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.Options;
using ProtocolTimingMethod = LiveSplit.Bridge.Protocol.V2.TimingMethod;

namespace LiveSplit.Bridge.Tests;

public class RuntimeStateTests
{
    [Fact]
    public void BuildRuntimeState_ReflectsCurrentHotkeyProfile()
    {
        var settings = CreateSettings(("Default", true), ("Secondary", false));
        var state = CreateState(settings, "Default");
        var adapter = new LiveSplitAdapter(state);

        var initial = adapter.BuildRuntimeState(runtimeRevision: 1, sessionId: 1);
        Assert.True(initial.GlobalHotkeysEnabled);

        state.CurrentHotkeyProfile = "Secondary";
        var switched = adapter.BuildRuntimeState(runtimeRevision: 2, sessionId: 1);
        Assert.False(switched.GlobalHotkeysEnabled);
    }

    [Fact]
    public void BuildRuntimeState_ReturnsFalseWhenProfileMissing()
    {
        var settings = CreateSettings(("Default", true));
        var state = CreateState(settings, "Unknown");
        var adapter = new LiveSplitAdapter(state);

        var runtime = adapter.BuildRuntimeState(runtimeRevision: 1, sessionId: 1);

        Assert.False(runtime.GlobalHotkeysEnabled);
    }

    [Fact]
    public void BuildRuntimeState_MapsCurrentConfigurationAndCustomVariables()
    {
        var run = new Run(new StandardComparisonGeneratorsFactory());
        run.Add(new Segment("One"));
        run.Metadata.GetOrAddCustomVariable("custom").Value = "custom-value";

        var settings = CreateSettings(("Default", true));
        var state = CreateState(settings, "Default", run);
        var adapter = new LiveSplitAdapter(state);

        var runtime = adapter.BuildRuntimeState(runtimeRevision: 5, sessionId: 7);

        Assert.Equal(7UL, runtime.SessionId);
        Assert.Equal(5UL, runtime.RuntimeRevision);
        Assert.Equal(ProtocolTimingMethod.RealTime, runtime.CurrentTimingMethod);
        Assert.True(runtime.GlobalHotkeysEnabled);
        Assert.Equal("custom-value", runtime.CustomVariables["custom"]);
    }

    private static Settings CreateSettings(params (string Name, bool Enabled)[] profiles)
    {
        var settings = new Settings
        {
            HotkeyProfiles = new Dictionary<string, HotkeyProfile>(),
        };
        foreach (var (name, enabled) in profiles)
        {
            settings.HotkeyProfiles[name] = new HotkeyProfile { GlobalHotkeysEnabled = enabled };
        }

        return settings;
    }

    private static LiveSplitState CreateState(
        Settings settings,
        string currentProfile,
        IRun? run = null)
    {
        run ??= new Run(new StandardComparisonGeneratorsFactory());
        var state = TestLiveSplitState.Create(run, settings);
        state.CurrentHotkeyProfile = currentProfile;
        return state;
    }
}
