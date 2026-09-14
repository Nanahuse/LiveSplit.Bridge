using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiveSplit.Bridge.Protocol.V1;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using ProtocolTimerPhase = LiveSplit.Bridge.Protocol.V1.TimerPhase;
using ModelTimerPhase = LiveSplit.Model.TimerPhase;
using ModelRunMetadata = LiveSplit.Model.RunMetadata;
using ProtoCustomVariable = LiveSplit.Bridge.Protocol.V1.CustomVariable;
using ProtoRunMetadata = LiveSplit.Bridge.Protocol.V1.RunMetadata;

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

        public TimerSnapshot BuildSnapshot(ulong stateRevision, ulong sessionId, ulong eventSequence, ulong runRevision)
        {
            return InvokeOnUiThread(() =>
            {
                var currentTime = state.CurrentTime;
                var snapshot = new TimerSnapshot
                {
                    StateRevision = stateRevision,
                    RunRevision = runRevision,
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

        public RunSnapshot BuildRunSnapshot(ulong runRevision, ulong stateRevision, ulong sessionId)
        {
            return InvokeOnUiThread(() =>
            {
                var snapshot = new RunSnapshot
                {
                    SessionId = sessionId,
                    RunRevision = runRevision,
                    CapturedStateRevision = stateRevision,
                };

                var run = state.Run;
                if (run == null)
                {
                    return snapshot;
                }

                snapshot.GameName = run.GameName ?? string.Empty;
                snapshot.CategoryName = run.CategoryName ?? string.Empty;
                snapshot.OffsetTicks = run.Offset.Ticks;

                if (!string.IsNullOrEmpty(run.FilePath))
                {
                    snapshot.FilePath = run.FilePath;
                }

                if (!string.IsNullOrEmpty(run.LayoutPath))
                {
                    snapshot.LayoutPath = run.LayoutPath;
                }

                snapshot.Metadata = BuildRunMetadata(run.Metadata);

                var comparisons = (run.Comparisons ?? Enumerable.Empty<string>())
                    .Distinct()
                    .ToList();
                snapshot.Comparisons.Add(comparisons);

                for (var index = 0; index < run.Count; index++)
                {
                    snapshot.Segments.Add(BuildSegmentInfo(run[index], index, comparisons));
                }

                snapshot.AttemptCount = run.AttemptCount > 0 ? (uint)run.AttemptCount : 0U;

                return snapshot;
            });
        }

        private static ProtoRunMetadata BuildRunMetadata(ModelRunMetadata metadata)
        {
            var result = new ProtoRunMetadata();
            if (metadata == null)
            {
                return result;
            }

            if (!string.IsNullOrEmpty(metadata.RunID))
            {
                result.RunId = metadata.RunID;
            }

            if (!string.IsNullOrEmpty(metadata.PlatformName))
            {
                result.PlatformName = metadata.PlatformName;
            }

            if (!string.IsNullOrEmpty(metadata.RegionName))
            {
                result.RegionName = metadata.RegionName;
            }

            result.UsesEmulator = metadata.UsesEmulator;

            if (metadata.VariableValueNames != null)
            {
                foreach (var pair in metadata.VariableValueNames)
                {
                    result.Variables[pair.Key] = pair.Value ?? string.Empty;
                }
            }

            if (metadata.CustomVariables != null)
            {
                foreach (var pair in metadata.CustomVariables)
                {
                    result.CustomVariables.Add(new ProtoCustomVariable
                    {
                        Name = pair.Key,
                        Value = pair.Value?.Value ?? string.Empty,
                        IsPermanent = pair.Value?.IsPermanent ?? false,
                    });
                }
            }

            return result;
        }

        private static SegmentInfo BuildSegmentInfo(ISegment segment, int index, IReadOnlyList<string> comparisons)
        {
            var info = new SegmentInfo
            {
                Index = (uint)index,
                Name = segment.Name ?? string.Empty,
                BestSegmentTime = MapTime(segment.BestSegmentTime),
            };

            foreach (var comparison in comparisons)
            {
                info.Comparisons.Add(new ComparisonTime
                {
                    Name = comparison,
                    Time = MapTime(segment.Comparisons, comparison),
                });
            }

            if (segment.CustomVariableValues != null)
            {
                foreach (var pair in segment.CustomVariableValues)
                {
                    info.CustomVariables[pair.Key] = pair.Value ?? string.Empty;
                }
            }

            return info;
        }

        private static TimeValue MapTime(Time time)
        {
            var value = new TimeValue();

            if (time.RealTime.HasValue)
            {
                value.RealTimeTicks = time.RealTime.Value.Ticks;
            }

            if (time.GameTime.HasValue)
            {
                value.GameTimeTicks = time.GameTime.Value.Ticks;
            }

            return value;
        }

        private static TimeValue MapTime(IComparisons comparisons, string name)
        {
            if (comparisons != null && comparisons.TryGetValue(name, out var time))
            {
                return MapTime(time);
            }

            return new TimeValue();
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
