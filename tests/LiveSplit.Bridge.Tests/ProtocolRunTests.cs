using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V2;
using ProtoImage = LiveSplit.Bridge.Protocol.V2.Image;

namespace LiveSplit.Bridge.Tests;

public class ProtocolRunTests
{
    [Fact]
    public void GetTimerStateRequestAndResponseRoundTrip()
    {
        var request = new Request
        {
            ProtocolVersion = 2,
            RequestId = 5,
            GetTimerState = new GetTimerStateRequest(),
        };

        var parsedRequest = Request.Parser.ParseFrom(request.ToByteArray());

        Assert.NotNull(parsedRequest.GetTimerState);
        Assert.Equal(5UL, parsedRequest.RequestId);

        var response = new Response
        {
            ProtocolVersion = 2,
            RequestId = 5,
            GetTimerState = new GetTimerStateResponse
            {
                TimerState = new TimerState
                {
                    SessionId = 1,
                    StateRevision = 2,
                    Phase = TimerPhase.Running,
                    SplitIndex = 3,
                    RealTimeTicks = 400,
                    GameTimeTicks = 500,
                    IsGameTimeInitialized = true,
                    IsGameTimePaused = true,
                    RunRevision = 6,
                    AttemptRevision = 7,
                    RuntimeRevision = 8,
                },
            },
        };

        var parsed = Response.Parser.ParseFrom(response.ToByteArray());

        Assert.NotNull(parsed.GetTimerState);
        var timerState = parsed.GetTimerState.TimerState;
        Assert.Equal(1UL, timerState.SessionId);
        Assert.Equal(2UL, timerState.StateRevision);
        Assert.Equal(TimerPhase.Running, timerState.Phase);
        Assert.Equal(3, timerState.SplitIndex);
        Assert.True(timerState.HasRealTimeTicks);
        Assert.Equal(400L, timerState.RealTimeTicks);
        Assert.True(timerState.HasGameTimeTicks);
        Assert.Equal(500L, timerState.GameTimeTicks);
        Assert.True(timerState.IsGameTimeInitialized);
        Assert.True(timerState.IsGameTimePaused);
        Assert.Equal(6UL, timerState.RunRevision);
        Assert.Equal(7UL, timerState.AttemptRevision);
        Assert.Equal(8UL, timerState.RuntimeRevision);
    }

    [Fact]
    public void GetRunRequestAndResponseRoundTrip()
    {
        var response = new Response
        {
            ProtocolVersion = 2,
            RequestId = 6,
            GetRun = new GetRunResponse
            {
                Run = new RunState
                {
                    SessionId = 1,
                    RunRevision = 2,
                    GameName = "Game",
                    CategoryName = "Category",
                    OffsetTicks = -100,
                    FilePath = "run.lss",
                    Metadata = new RunMetadata
                    {
                        RunId = "run-id",
                        PlatformName = "PC",
                        RegionName = "USA",
                        UsesEmulator = true,
                    },
                    GameIcon = new ProtoImage
                    {
                        MimeType = "image/png",
                        Data = ByteString.CopyFromUtf8("icon"),
                        Width = 16,
                        Height = 32,
                    },
                },
            },
        };

        var run = response.GetRun.Run;
        run.Comparisons.Add("Personal Best");
        run.Segments.Add(new SegmentInfo
        {
            Index = 0,
            Name = "Segment",
            BestSegmentTime = new TimeValue { RealTimeTicks = 10 },
            Icon = new ProtoImage
            {
                MimeType = "image/png",
                Width = 8,
                Height = 8,
            },
        });
        run.Segments[0].Comparisons.Add(new ComparisonTime
        {
            Name = "Personal Best",
            Time = new TimeValue { GameTimeTicks = 20 },
        });
        run.Metadata.Variables["variable"] = "value";

        var parsed = Response.Parser.ParseFrom(response.ToByteArray());

        Assert.NotNull(parsed.GetRun);
        var parsedRun = parsed.GetRun.Run;
        Assert.Equal(1UL, parsedRun.SessionId);
        Assert.Equal(2UL, parsedRun.RunRevision);
        Assert.Equal("Game", parsedRun.GameName);
        Assert.Equal("Category", parsedRun.CategoryName);
        Assert.Equal(-100L, parsedRun.OffsetTicks);
        Assert.True(parsedRun.HasFilePath);
        Assert.Equal("run.lss", parsedRun.FilePath);
        Assert.False(parsedRun.HasLayoutPath);
        Assert.Equal("image/png", parsedRun.GameIcon.MimeType);
        Assert.Equal("icon", parsedRun.GameIcon.Data.ToStringUtf8());
        Assert.Equal(16U, parsedRun.GameIcon.Width);
        Assert.Equal(32U, parsedRun.GameIcon.Height);
        Assert.True(parsedRun.Metadata.HasRunId);
        Assert.Equal("run-id", parsedRun.Metadata.RunId);
        Assert.Equal("PC", parsedRun.Metadata.PlatformName);
        Assert.Equal("USA", parsedRun.Metadata.RegionName);
        Assert.True(parsedRun.Metadata.UsesEmulator);
        Assert.Equal("value", parsedRun.Metadata.Variables["variable"]);
        Assert.Equal(new[] { "Personal Best" }, parsedRun.Comparisons);
        var segment = Assert.Single(parsedRun.Segments);
        Assert.Equal(0U, segment.Index);
        Assert.Equal("Segment", segment.Name);
        Assert.Equal("image/png", segment.Icon.MimeType);
        Assert.True(segment.BestSegmentTime.HasRealTimeTicks);
        Assert.Equal(10L, segment.BestSegmentTime.RealTimeTicks);
        Assert.False(segment.BestSegmentTime.HasGameTimeTicks);
        Assert.Equal(20L, segment.Comparisons[0].Time.GameTimeTicks);
    }

    [Fact]
    public void AttemptStateRoundTrip()
    {
        var attempt = new AttemptState
        {
            SessionId = 1,
            AttemptRevision = 2,
            AttemptCount = 3,
            CompletedCount = 4,
        };
        attempt.Segments.Add(new AttemptSegment
        {
            Index = 0,
            SplitTime = new TimeValue { RealTimeTicks = 100, GameTimeTicks = 90 },
        });
        attempt.Segments[0].CustomVariables["key"] = "value";

        var parsed = AttemptState.Parser.ParseFrom(attempt.ToByteArray());

        Assert.Equal(1UL, parsed.SessionId);
        Assert.Equal(2UL, parsed.AttemptRevision);
        Assert.Equal(3U, parsed.AttemptCount);
        Assert.Equal(4U, parsed.CompletedCount);
        var segment = Assert.Single(parsed.Segments);
        Assert.Equal(0U, segment.Index);
        Assert.Equal(100L, segment.SplitTime.RealTimeTicks);
        Assert.Equal(90L, segment.SplitTime.GameTimeTicks);
        Assert.Equal("value", segment.CustomVariables["key"]);
    }

    [Fact]
    public void RuntimeStateRoundTrip()
    {
        var runtime = new RuntimeState
        {
            SessionId = 1,
            RuntimeRevision = 2,
            CurrentTimingMethod = TimingMethod.GameTime,
            CurrentComparison = "Personal Best",
            GlobalHotkeysEnabled = true,
        };
        runtime.CustomVariables["custom"] = "value";

        var parsed = RuntimeState.Parser.ParseFrom(runtime.ToByteArray());

        Assert.Equal(1UL, parsed.SessionId);
        Assert.Equal(2UL, parsed.RuntimeRevision);
        Assert.Equal(TimingMethod.GameTime, parsed.CurrentTimingMethod);
        Assert.Equal("Personal Best", parsed.CurrentComparison);
        Assert.True(parsed.GlobalHotkeysEnabled);
        Assert.Equal("value", parsed.CustomVariables["custom"]);
    }

    [Fact]
    public void AttachResponseCarriesTimerState()
    {
        var response = new AttachResponse
        {
            SessionId = 1,
            TimerState = new TimerState { SessionId = 1, StateRevision = 1 },
        };

        var parsed = AttachResponse.Parser.ParseFrom(response.ToByteArray());

        Assert.Equal(1UL, parsed.SessionId);
        Assert.NotNull(parsed.TimerState);
        Assert.Equal(1UL, parsed.TimerState.StateRevision);
    }

    [Fact]
    public void OperationResponseCarriesTimerState()
    {
        var response = new OperationResponse
        {
            Success = true,
            Message = "OK",
            TimerState = new TimerState { StateRevision = 9 },
        };

        var parsed = OperationResponse.Parser.ParseFrom(response.ToByteArray());

        Assert.True(parsed.Success);
        Assert.Equal("OK", parsed.Message);
        Assert.Equal(9UL, parsed.TimerState.StateRevision);
    }

    [Fact]
    public void BridgeEventCarriesTimerState()
    {
        var bridgeEvent = new BridgeEvent
        {
            SessionId = 1,
            EventSequence = 2,
            Type = BridgeEventType.EventRuntimeChanged,
            TimerState = new TimerState { StateRevision = 3 },
        };

        var parsed = BridgeEvent.Parser.ParseFrom(bridgeEvent.ToByteArray());

        Assert.Equal(BridgeEventType.EventRuntimeChanged, parsed.Type);
        Assert.NotNull(parsed.TimerState);
        Assert.Equal(3UL, parsed.TimerState.StateRevision);
    }

    [Fact]
    public void BridgeEventTypeHasNoStateSnapshot()
    {
        var names = Enum.GetNames(typeof(BridgeEventType));

        Assert.DoesNotContain(names, name => name.Contains("STATE_SNAPSHOT", StringComparison.Ordinal));
        Assert.Contains("EventRuntimeChanged", names);
        Assert.Contains("EventHeartbeat", names);
    }

    [Fact]
    public void RunStateHasNoAttemptDependentData()
    {
        Assert.Null(typeof(RunState).GetProperty("AttemptCount"));
        Assert.Null(typeof(RunState).GetProperty("CapturedStateRevision"));
        Assert.Null(typeof(SegmentInfo).GetProperty("CustomVariables"));
    }

    [Fact]
    public void RunMetadataHasNoCustomVariableValues()
    {
        Assert.DoesNotContain(
            typeof(RunMetadata).GetProperties(),
            property => property.Name.Contains("CustomVariable", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(RunMetadata).GetProperties(),
            property => property.Name.Contains("CustomVariables", StringComparison.Ordinal));
    }

    [Fact]
    public void TimerStateHasNoEventOrSnapshotOnlyFields()
    {
        Assert.Null(typeof(TimerState).GetProperty("EventSequence"));
        Assert.Null(typeof(TimerState).GetProperty("SplitCount"));
        Assert.Null(typeof(TimerState).GetProperty("IsPaused"));
    }
}
