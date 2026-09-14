using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V1;

namespace LiveSplit.Bridge.Tests;

public class ProtocolRunTests
{
    [Fact]
    public void GetRunRequestAndResponseRoundTrip()
    {
        var request = new Request
        {
            ProtocolVersion = 1,
            RequestId = 5,
            GetRun = new GetRunRequest(),
        };

        var parsedRequest = Request.Parser.ParseFrom(request.ToByteArray());

        Assert.NotNull(parsedRequest.GetRun);
        Assert.Equal(5UL, parsedRequest.RequestId);

        var response = new Response
        {
            ProtocolVersion = 1,
            RequestId = 5,
            GetRun = new GetRunResponse
            {
                Run = new RunSnapshot
                {
                    SessionId = 1,
                    RunRevision = 2,
                    CapturedStateRevision = 3,
                    GameName = "Game",
                    CategoryName = "Category",
                    OffsetTicks = -100,
                    FilePath = "run.lss",
                    AttemptCount = 4,
                    Metadata = new RunMetadata
                    {
                        RunId = "run-id",
                        PlatformName = "PC",
                        RegionName = "USA",
                        UsesEmulator = true,
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
        });
        run.Segments[0].Comparisons.Add(new ComparisonTime
        {
            Name = "Personal Best",
            Time = new TimeValue { GameTimeTicks = 20 },
        });
        run.Segments[0].CustomVariables["segment_key"] = "segment_value";
        run.Metadata.Variables["variable"] = "value";
        run.Metadata.CustomVariables.Add(new CustomVariable
        {
            Name = "custom",
            Value = "custom-value",
            IsPermanent = true,
        });

        var parsed = Response.Parser.ParseFrom(response.ToByteArray());

        Assert.NotNull(parsed.GetRun);
        var parsedRun = parsed.GetRun.Run;
        Assert.Equal(1UL, parsedRun.SessionId);
        Assert.Equal(2UL, parsedRun.RunRevision);
        Assert.Equal(3UL, parsedRun.CapturedStateRevision);
        Assert.Equal("Game", parsedRun.GameName);
        Assert.Equal("Category", parsedRun.CategoryName);
        Assert.Equal(-100L, parsedRun.OffsetTicks);
        Assert.True(parsedRun.HasFilePath);
        Assert.Equal("run.lss", parsedRun.FilePath);
        Assert.False(parsedRun.HasLayoutPath);
        Assert.Equal(4U, parsedRun.AttemptCount);
        Assert.True(parsedRun.Metadata.HasRunId);
        Assert.Equal("run-id", parsedRun.Metadata.RunId);
        Assert.Equal("PC", parsedRun.Metadata.PlatformName);
        Assert.Equal("USA", parsedRun.Metadata.RegionName);
        Assert.True(parsedRun.Metadata.UsesEmulator);
        Assert.Equal("value", parsedRun.Metadata.Variables["variable"]);
        var custom = Assert.Single(parsedRun.Metadata.CustomVariables);
        Assert.Equal("custom", custom.Name);
        Assert.Equal("custom-value", custom.Value);
        Assert.True(custom.IsPermanent);
        Assert.Equal(new[] { "Personal Best" }, parsedRun.Comparisons);
        var segment = Assert.Single(parsedRun.Segments);
        Assert.Equal(0U, segment.Index);
        Assert.Equal("Segment", segment.Name);
        Assert.Equal("segment_value", segment.CustomVariables["segment_key"]);
        Assert.True(segment.BestSegmentTime.HasRealTimeTicks);
        Assert.Equal(10L, segment.BestSegmentTime.RealTimeTicks);
        Assert.False(segment.BestSegmentTime.HasGameTimeTicks);
        Assert.Equal(20L, segment.Comparisons[0].Time.GameTimeTicks);
    }

    [Fact]
    public void TimerSnapshotRoundTripsRunRevision()
    {
        var snapshot = new TimerSnapshot
        {
            StateRevision = 10,
            RunRevision = 7,
        };

        var parsed = TimerSnapshot.Parser.ParseFrom(snapshot.ToByteArray());

        Assert.Equal(7UL, parsed.RunRevision);
        Assert.Equal(10UL, parsed.StateRevision);
    }
}
