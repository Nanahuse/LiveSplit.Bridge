using LiveSplit.Bridge.Protocol.V3;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using ProtocolTimingMethod = LiveSplit.Bridge.Protocol.V3.TimingMethod;
using ProtocolTimerPhase = LiveSplit.Bridge.Protocol.V3.TimerPhase;

namespace LiveSplit.Bridge.Tests;

[Collection("BridgeRuntimeEndpoints")]
public class BridgeRuntimeEventTests
{
    [Fact]
    public async Task TimerEventsFollowLiveSplitCallbacksAndCaptureCallbackState()
    {
        var state = CreateState(segmentCount: 4);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(runtime.Port));

        // PAUSE while not running invokes LiveSplit Start, so the event is STARTED.
        SendTimerOperation(runtime, TimerOperationType.TimerPause);
        runtime.Update();
        AssertEvent(await ReceiveEventAsync(events), 1, BridgeEventType.EventTimerStarted, ProtocolTimerPhase.Running);

        SendTimerOperation(runtime, TimerOperationType.TimerResume);
        runtime.Update();
        AssertEvent(await ReceiveEventAsync(events), 2, BridgeEventType.EventTimerPhaseChanged, ProtocolTimerPhase.Paused);

        SendTimerOperation(runtime, TimerOperationType.TimerPause);
        runtime.Update();
        AssertEvent(await ReceiveEventAsync(events), 3, BridgeEventType.EventTimerPhaseChanged, ProtocolTimerPhase.Running);

        SendTimerOperation(runtime, TimerOperationType.TimerSplit);
        runtime.Update();
        AssertEvent(await ReceiveEventAsync(events), 4, BridgeEventType.EventTimerSplit, ProtocolTimerPhase.Running);
        SendTimerOperation(runtime, TimerOperationType.TimerSkip);
        runtime.Update();
        AssertEvent(await ReceiveEventAsync(events), 5, BridgeEventType.EventTimerSkipped, ProtocolTimerPhase.Running);
        SendTimerOperation(runtime, TimerOperationType.TimerUndo);
        runtime.Update();
        AssertEvent(await ReceiveEventAsync(events), 6, BridgeEventType.EventTimerUndo, ProtocolTimerPhase.Running);

        SendTimerOperation(runtime, TimerOperationType.TimerSplit);
        runtime.Update();
        AssertEvent(await ReceiveEventAsync(events), 7, BridgeEventType.EventTimerSplit, ProtocolTimerPhase.Running);
        SendTimerOperation(runtime, TimerOperationType.TimerSplit);
        runtime.Update();
        AssertEvent(await ReceiveEventAsync(events), 8, BridgeEventType.EventTimerSplit, ProtocolTimerPhase.Running);
        SendTimerOperation(runtime, TimerOperationType.TimerSplit);
        runtime.Update();
        AssertEvent(await ReceiveEventAsync(events), 9, BridgeEventType.EventTimerSplit, ProtocolTimerPhase.Ended);

        SendTimerOperation(runtime, TimerOperationType.TimerUndo);
        runtime.Update();
        AssertEvent(await ReceiveEventAsync(events), 10, BridgeEventType.EventTimerUndo, ProtocolTimerPhase.Running);
        SendTimerOperation(runtime, TimerOperationType.TimerReset);
        AssertEvent(await ReceiveEventAsync(events), 11, BridgeEventType.EventTimerReset, ProtocolTimerPhase.NotRunning);
        runtime.Update();
        AssertNonTimerEvent(await ReceiveEventAsync(events), 12, BridgeEventType.EventRunChanged);
        var cachedRun = runtime.HandleRequest(new Request { ProtocolVersion = 3, GetRun = new GetRunRequest() });
        Assert.Equal(new RunStateCache(state).Current, cachedRun.GetRun.Run);

        await AssertNoEventAsync(events);
    }

    [Fact]
    public async Task RunAndContextEventsOnlyPublishOnPublicStateChanges()
    {
        var state = CreateState(segmentCount: 2);
        state.CurrentComparison = Run.PersonalBestComparisonName;
        state.Run.Metadata.GetOrAddCustomVariable("route").Value = "left";
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(runtime.Port));

        state.Run.GameName = "Edited Game";
        state.CallRunManuallyModified();
        runtime.Update();
        AssertNonTimerEvent(await ReceiveEventAsync(events), 1, BridgeEventType.EventRunChanged);
        var run = runtime.HandleRequest(new Request { ProtocolVersion = 3, GetRun = new GetRunRequest() });
        Assert.Equal("Edited Game", run.GetRun.Run.GameName);

        state.CallRunManuallyModified();
        runtime.Update();

        var replacement = CreateRun(segmentCount: 2);
        replacement.GameName = "Replacement Game";
        replacement.Metadata.GetOrAddCustomVariable("route").Value = "right";
        state.Run = replacement;
        runtime.Update();
        AssertNonTimerEvent(await ReceiveEventAsync(events), 2, BridgeEventType.EventRunChanged);
        AssertNonTimerEvent(await ReceiveEventAsync(events), 3, BridgeEventType.EventContextChanged);

        state.CurrentTimingMethod = LiveSplit.Model.TimingMethod.GameTime;
        state.CurrentComparison = "Best Segments";
        replacement.Metadata.GetOrAddCustomVariable("route").Value = "third";
        runtime.Update();
        AssertNonTimerEvent(await ReceiveEventAsync(events), 4, BridgeEventType.EventContextChanged);

        var context = runtime.HandleRequest(new Request { ProtocolVersion = 3, GetContextState = new GetContextStateRequest() });
        Assert.Equal(ProtocolTimingMethod.GameTime, context.GetContextState.ContextState.CurrentTimingMethod);
        Assert.Equal("Best Segments", context.GetContextState.ContextState.CurrentComparison);
        Assert.Equal("third", context.GetContextState.ContextState.CustomVariables["route"]);
        await AssertNoEventAsync(events);
    }

    [Fact]
    public async Task EventsConnectionDoesNotSendInitialSnapshotOrHeartbeat()
    {
        using var runtime = new BridgeRuntime(CreateState(segmentCount: 2), BridgeTestEndpoints.GetFreePort());
        using var events = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(runtime.Port));

        await AssertNoEventAsync(events, TimeSpan.FromMilliseconds(1200));
    }

    [Fact]
    public async Task EventPublisherFansOutInSequenceAndToleratesDisconnectedClients()
    {
        var state = CreateState(segmentCount: 2);
        using var runtime = new BridgeRuntime(state, BridgeTestEndpoints.GetFreePort());
        using var remaining = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(runtime.Port));
        var disconnected = await WebSocketTestClient.ConnectAsync(BridgeTestEndpoints.Events(runtime.Port));

        SendTimerOperation(runtime, TimerOperationType.TimerStart);
        var first = await ReceiveEventAsync(remaining);
        var firstOtherClient = await ReceiveEventAsync(disconnected);
        Assert.Equal(first, firstOtherClient);
        disconnected.Dispose();

        SendTimerOperation(runtime, TimerOperationType.TimerPause);
        var next = await ReceiveEventAsync(remaining);
        Assert.Equal(first.EventSequence + 1, next.EventSequence);
        Assert.Equal(BridgeEventType.EventTimerPhaseChanged, next.Type);
    }

    [Fact]
    public async Task ControlCompletesWhileEventNetworkSenderIsBlocked()
    {
        using var sendEntered = new ManualResetEventSlim();
        using var releaseSend = new ManualResetEventSlim();
        using var runtime = new BridgeRuntime(
            CreateState(segmentCount: 2),
            BridgeTestEndpoints.GetFreePort(),
            _ =>
            {
                sendEntered.Set();
                releaseSend.Wait(TimeSpan.FromSeconds(5));
            });

        SendTimerOperation(runtime, TimerOperationType.TimerStart);
        Assert.True(sendEntered.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            var control = Task.Run(() => runtime.HandleRequest(new Request
            {
                ProtocolVersion = 3,
                TimerOperation = new TimerOperationRequest { Operation = TimerOperationType.TimerPause },
            }));
            Assert.Same(control, await Task.WhenAny(control, Task.Delay(TimeSpan.FromSeconds(1))));
            Assert.NotNull((await control).Operation);
        }
        finally
        {
            releaseSend.Set();
        }
    }

    private static void SendTimerOperation(BridgeRuntime runtime, TimerOperationType operation)
    {
        var response = runtime.HandleRequest(new Request
        {
            ProtocolVersion = 3,
            TimerOperation = new TimerOperationRequest { Operation = operation },
        });
        Assert.NotNull(response.Operation);
    }

    private static async Task<BridgeEvent> ReceiveEventAsync(WebSocketTestClient client)
    {
        return BridgeEvent.Parser.ParseFrom(await client.ReceiveBinaryAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task AssertNoEventAsync(WebSocketTestClient client, TimeSpan? timeout = null)
    {
        var pending = client.ReceiveBinaryAsync();
        await Task.Delay(timeout ?? TimeSpan.FromMilliseconds(250));
        Assert.False(pending.IsCompleted, "Unexpected event received.");
        client.Dispose();
        _ = pending.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private static void AssertEvent(BridgeEvent bridgeEvent, ulong sequence, BridgeEventType type, ProtocolTimerPhase phase)
    {
        Assert.Equal(sequence, bridgeEvent.EventSequence);
        Assert.Equal(type, bridgeEvent.Type);
        Assert.NotNull(bridgeEvent.TimerState);
        Assert.Equal(phase, bridgeEvent.TimerState.Phase);
    }

    private static void AssertNonTimerEvent(BridgeEvent bridgeEvent, ulong sequence, BridgeEventType type)
    {
        Assert.Equal(sequence, bridgeEvent.EventSequence);
        Assert.Equal(type, bridgeEvent.Type);
        Assert.Null(bridgeEvent.TimerState);
    }

    private static LiveSplitState CreateState(int segmentCount) => TestLiveSplitState.Create(CreateRun(segmentCount));

    private static Run CreateRun(int segmentCount)
    {
        var run = new Run(new StandardComparisonGeneratorsFactory()) { GameName = "Game", CategoryName = "Any%" };
        for (var index = 0; index < segmentCount; index++) run.Add(new Segment($"Segment {index + 1}"));
        return run;
    }
}
