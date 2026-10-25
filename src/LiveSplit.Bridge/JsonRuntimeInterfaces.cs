#nullable enable
using Json = LiveSplit.Bridge.Json;

namespace LiveSplit.Bridge;

internal interface IJsonLiveSplitAdapter
{
    Json.TimerState GetTimerState();
    Json.AttemptState GetAttempt();
    Json.CompletedCount GetCompletedCount();
    Json.ContextState GetContextState();
    Json.RunState GetRunState();
    Json.RunIcons GetRunIcons();
    bool TryUpdateRunTimings(Json.RunState published, out Json.RunState updated);
    void ExecuteTimerOperation(Json.TimerOperation operation);
    void ExecuteGameTimeOperation(Json.GameTimeParams operation);
}

internal interface IJsonEventSink
{
    void Enqueue(Json.BridgeEvent bridgeEvent);
}
