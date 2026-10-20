#nullable enable
using System;
using Json = LiveSplit.Bridge.Json;

namespace LiveSplit.Bridge;

internal interface IJsonLiveSplitAdapter
{
    Json.TimerState GetTimerState();
    Json.AttemptState GetAttempt();
    Json.CompletedCount GetCompletedCount();
    Json.ContextState GetContextState();
    Json.RunState GetRunState();
    IJsonRunStateCandidate CaptureRunState();
    bool TryUpdateRunTimings(Json.RunState published, out Json.RunState updated);
    bool IsTimerOnlyRun();
    void ExecuteTimerOperation(Json.TimerOperationParams operation);
    void ExecuteGameTimeOperation(Json.GameTimeOperationParams operation);
}

internal interface IJsonRunStateCandidate : IDisposable
{
    Json.RunState Build();
}

internal interface IJsonEventSink
{
    void Enqueue(Json.BridgeEvent bridgeEvent);
}
