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

        public event Action<GameTimeOperationType> GameTimeChanged;

        public LiveSplitAdapter(LiveSplitState state)
        {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.timerModel = new TimerModel { CurrentState = state };
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

        public GameTimeRevisionState CaptureGameTimeRevisionState()
        {
            return InvokeOnUiThread(() => new GameTimeRevisionState(
                state.IsGameTimeInitialized,
                state.IsGameTimePaused,
                state.LoadingTimes.Ticks,
                state.GameTimePauseTime?.Ticks));
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

        public GameTimeOperationExecution ExecuteGameTimeOperation(
            GameTimeOperationType operation,
            long? ticks)
        {
            return InvokeOnUiThread(() =>
            {
                try
                {
                    var changed = false;

                    switch (operation)
                    {
                        case GameTimeOperationType.Initialize:
                            changed = !state.IsGameTimeInitialized;
                            if (changed)
                            {
                                timerModel.InitializeGameTime();
                            }
                            break;
                        case GameTimeOperationType.Set:
                            if (!ticks.HasValue)
                            {
                                return GameTimeOperationExecution.Failure(
                                    "Game time set operation requires ticks.");
                            }

                            var gameTime = TimeSpan.FromTicks(ticks.Value);
                            changed = state.CurrentTime.GameTime != gameTime;
                            if (changed)
                            {
                                state.SetGameTime(gameTime);
                            }
                            break;
                        case GameTimeOperationType.GameTimePause:
                            changed = !state.IsGameTimePaused;
                            if (changed)
                            {
                                state.IsGameTimePaused = true;
                            }
                            break;
                        case GameTimeOperationType.GameTimeResume:
                            changed = state.IsGameTimePaused;
                            if (changed)
                            {
                                state.IsGameTimePaused = false;
                            }
                            break;
                        default:
                            return GameTimeOperationExecution.Failure(
                                $"Unsupported game time operation: {operation}");
                    }

                    if (changed)
                    {
                        GameTimeChanged?.Invoke(operation);
                    }

                    return GameTimeOperationExecution.Success(changed);
                }
                catch (Exception exception)
                {
                    return GameTimeOperationExecution.Failure(exception.Message);
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

    internal sealed class GameTimeOperationExecution
    {
        private GameTimeOperationExecution(OperationResponse response, bool changed)
        {
            Response = response;
            Changed = changed;
        }

        public OperationResponse Response { get; }
        public bool Changed { get; }

        public static GameTimeOperationExecution Success(bool changed)
        {
            return new GameTimeOperationExecution(
                new OperationResponse { Success = true, Message = "OK" },
                changed);
        }

        public static GameTimeOperationExecution Failure(string message)
        {
            return new GameTimeOperationExecution(
                new OperationResponse { Success = false, Message = message },
                false);
        }
    }

    internal readonly struct GameTimeRevisionState : IEquatable<GameTimeRevisionState>
    {
        public GameTimeRevisionState(
            bool isInitialized,
            bool isPaused,
            long loadingTimeTicks,
            long? pauseTimeTicks)
        {
            IsInitialized = isInitialized;
            IsPaused = isPaused;
            LoadingTimeTicks = loadingTimeTicks;
            PauseTimeTicks = pauseTimeTicks;
        }

        public bool IsInitialized { get; }
        public bool IsPaused { get; }
        public long LoadingTimeTicks { get; }
        public long? PauseTimeTicks { get; }

        public bool Equals(GameTimeRevisionState other)
        {
            return IsInitialized == other.IsInitialized
                && IsPaused == other.IsPaused
                && LoadingTimeTicks == other.LoadingTimeTicks
                && PauseTimeTicks == other.PauseTimeTicks;
        }

        public override bool Equals(object obj)
        {
            return obj is GameTimeRevisionState other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = IsInitialized.GetHashCode();
                hashCode = (hashCode * 397) ^ IsPaused.GetHashCode();
                hashCode = (hashCode * 397) ^ LoadingTimeTicks.GetHashCode();
                hashCode = (hashCode * 397) ^ PauseTimeTicks.GetHashCode();
                return hashCode;
            }
        }
    }
}
