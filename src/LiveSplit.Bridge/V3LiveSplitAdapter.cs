using System;
using System.Linq;
using LiveSplit.Bridge.Protocol.V3;
using LiveSplit.Model;
using ProtocolTimerPhase = LiveSplit.Bridge.Protocol.V3.TimerPhase;

namespace LiveSplit.Bridge;

internal interface IV3LiveSplitAdapter
{
    TimerState GetTimerState();
    AttemptState GetAttempt();
    CompletedCount GetCompletedCount();
    void ExecuteTimerOperation(TimerOperationType operation);
    void ExecuteGameTimeOperation(GameTimeOperationType operation, long? ticks);
}

internal sealed class V3LiveSplitAdapter : IV3LiveSplitAdapter
{
    private readonly LiveSplitState state;
    private readonly TimerModel timerModel;

    public V3LiveSplitAdapter(LiveSplitState state)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        timerModel = new TimerModel { CurrentState = state };
    }

    public TimerState GetTimerState()
    {
        var currentTime = state.CurrentTime;
        var result = new TimerState
        {
            Phase = MapTimerPhase(state.CurrentPhase),
            SplitIndex = state.CurrentSplitIndex,
            RealTimeTicks = currentTime.RealTime?.Ticks ?? 0,
            IsGameTimeInitialized = state.IsGameTimeInitialized,
            IsGameTimePaused = state.IsGameTimePaused,
        };

        if (currentTime.GameTime.HasValue)
        {
            result.GameTimeTicks = currentTime.GameTime.Value.Ticks;
        }

        return result;
    }

    public AttemptState GetAttempt()
    {
        var run = state.Run;
        var result = new AttemptState
        {
            AttemptCount = run != null && run.AttemptCount > 0
                ? (uint)run.AttemptCount
                : 0U,
        };

        if (run == null)
        {
            return result;
        }

        for (var index = 0; index < run.Count; index++)
        {
            var segment = run[index];
            var attemptSegment = new AttemptSegment
            {
                Index = (uint)index,
                SplitTime = MapTime(segment.SplitTime),
            };

            if (segment.CustomVariableValues != null)
            {
                foreach (var pair in segment.CustomVariableValues)
                {
                    attemptSegment.CustomVariables[pair.Key] = pair.Value ?? string.Empty;
                }
            }

            result.Segments.Add(attemptSegment);
        }

        return result;
    }

    public CompletedCount GetCompletedCount()
    {
        var attempts = state.Run?.AttemptHistory;
        return new CompletedCount
        {
            CompletedCount_ = attempts == null
                ? 0U
                : (uint)attempts.Count(attempt => attempt.Time.RealTime != null),
        };
    }

    public void ExecuteTimerOperation(TimerOperationType operation)
    {
        switch (operation)
        {
            case TimerOperationType.TimerStart:
                timerModel.Start();
                break;
            case TimerOperationType.TimerSplit:
                timerModel.Split();
                break;
            case TimerOperationType.TimerSkip:
                timerModel.SkipSplit();
                break;
            case TimerOperationType.TimerUndo:
                timerModel.UndoSplit();
                break;
            case TimerOperationType.TimerReset:
                timerModel.Reset();
                break;
            case TimerOperationType.TimerPause:
            case TimerOperationType.TimerResume:
                timerModel.Pause();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported timer operation.");
        }
    }

    public void ExecuteGameTimeOperation(GameTimeOperationType operation, long? ticks)
    {
        switch (operation)
        {
            case GameTimeOperationType.Initialize:
                timerModel.InitializeGameTime();
                break;
            case GameTimeOperationType.Set:
                if (!ticks.HasValue)
                {
                    throw new ArgumentException("SET requires ticks.", nameof(ticks));
                }

                state.SetGameTime(TimeSpan.FromTicks(ticks.Value));
                break;
            case GameTimeOperationType.GameTimePause:
                state.IsGameTimePaused = true;
                break;
            case GameTimeOperationType.GameTimeResume:
                state.IsGameTimePaused = false;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported game time operation.");
        }
    }

    private static TimeValue MapTime(Time time)
    {
        var result = new TimeValue();
        if (time.RealTime.HasValue)
        {
            result.RealTimeTicks = time.RealTime.Value.Ticks;
        }

        if (time.GameTime.HasValue)
        {
            result.GameTimeTicks = time.GameTime.Value.Ticks;
        }

        return result;
    }

    private static ProtocolTimerPhase MapTimerPhase(LiveSplit.Model.TimerPhase phase)
    {
        return phase switch
        {
            LiveSplit.Model.TimerPhase.NotRunning => ProtocolTimerPhase.NotRunning,
            LiveSplit.Model.TimerPhase.Running => ProtocolTimerPhase.Running,
            LiveSplit.Model.TimerPhase.Paused => ProtocolTimerPhase.Paused,
            LiveSplit.Model.TimerPhase.Ended => ProtocolTimerPhase.Ended,
            _ => ProtocolTimerPhase.Unspecified,
        };
    }
}
