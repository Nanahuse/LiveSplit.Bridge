using System;
using System.Drawing;
using System.Windows.Forms;
using LiveSplit.Bridge.Protocol.V1;
using LiveSplit.Model;
using ProtocolTimerPhase = LiveSplit.Bridge.Protocol.V1.TimerPhase;
using ModelTimerPhase = LiveSplit.Model.TimerPhase;

namespace LiveSplit.Bridge
{
    internal sealed class LiveSplitAdapter
    {
        private readonly LiveSplitState state;
        private readonly TimerModel timerModel;

        public LiveSplitAdapter(LiveSplitState state)
        {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.timerModel = new TimerModel { CurrentState = state };
            this.state.RegisterTimerModel(timerModel);
        }

        public TimerSnapshot BuildSnapshot(ulong stateRevision, ulong sessionId, ulong eventSequence)
        {
            return InvokeOnUiThread(() =>
            {
                var currentTime = state.CurrentTime;
                var snapshot = new TimerSnapshot
                {
                    StateRevision = stateRevision,
                    SessionId = sessionId,
                    EventSequence = eventSequence,
                    Phase = MapTimerPhase(state.CurrentPhase),
                    SplitIndex = state.CurrentSplitIndex,
                    SplitCount = state.Run?.Count ?? 0,
                    IsPaused = state.CurrentPhase == ModelTimerPhase.Paused,
                    IsGameTimeInitialized = state.IsGameTimeInitialized,
                };

                if (currentTime.RealTime.HasValue)
                {
                    snapshot.RealTimeTicks = currentTime.RealTime.Value.Ticks;
                }

                if (currentTime.GameTime.HasValue)
                {
                    snapshot.GameTimeTicks = currentTime.GameTime.Value.Ticks;
                }

                return snapshot;
            });
        }

        public OperationResponse ExecuteTimerOperation(TimerOperationType operation)
        {
            return InvokeOnUiThread(() =>
            {
                try
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
                            if (state.CurrentPhase == LiveSplit.Model.TimerPhase.Running)
                            {
                                timerModel.Pause();
                            }
                            break;
                        case TimerOperationType.TimerResume:
                            if (state.CurrentPhase == LiveSplit.Model.TimerPhase.Paused)
                            {
                                timerModel.Pause();
                            }
                            break;
                        default:
                            return new OperationResponse { Success = false, Message = $"Unsupported timer operation: {operation}" };
                    }

                    return new OperationResponse
                    {
                        Success = true,
                        Message = "OK"
                    };
                }
                catch (Exception exception)
                {
                    return new OperationResponse
                    {
                        Success = false,
                        Message = exception.Message
                    };
                }
            });
        }

        public OperationResponse ExecuteGameTimeOperation(GameTimeOperationType operation, long? ticks)
        {
            return InvokeOnUiThread(() =>
            {
                try
                {
                    switch (operation)
                    {
                        case GameTimeOperationType.Initialize:
                            timerModel.InitializeGameTime();
                            break;
                        case GameTimeOperationType.Set:
                            if (!ticks.HasValue)
                            {
                                return new OperationResponse
                                {
                                    Success = false,
                                    Message = "Game time set operation requires ticks."
                                };
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
                            return new OperationResponse { Success = false, Message = $"Unsupported game time operation: {operation}" };
                    }

                    return new OperationResponse
                    {
                        Success = true,
                        Message = "OK"
                    };
                }
                catch (Exception exception)
                {
                    return new OperationResponse
                    {
                        Success = false,
                        Message = exception.Message
                    };
                }
            });
        }

        private T InvokeOnUiThread<T>(Func<T> callback)
        {
            if (state.Form.InvokeRequired)
            {
                return (T)state.Form.Invoke(callback);
            }

            return callback();
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
}
