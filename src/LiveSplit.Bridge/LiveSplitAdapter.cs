using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V2;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using ModelTimingMethod = LiveSplit.Model.TimingMethod;
using ProtocolTimingMethod = LiveSplit.Bridge.Protocol.V2.TimingMethod;
using ProtocolTimerPhase = LiveSplit.Bridge.Protocol.V2.TimerPhase;
using ModelTimerPhase = LiveSplit.Model.TimerPhase;
using ModelRunMetadata = LiveSplit.Model.RunMetadata;
using ProtoImage = LiveSplit.Bridge.Protocol.V2.Image;
using ProtoRunMetadata = LiveSplit.Bridge.Protocol.V2.RunMetadata;

namespace LiveSplit.Bridge
{
    internal sealed class LiveSplitAdapter
    {
        private readonly LiveSplitState state;
        private readonly TimerModel timerModel;
        private long uiThreadDispatchCount;
        private int uiInvocationDepth;

        public event Action<GameTimeOperationType> GameTimeChanged;

        /// <summary>
        /// Number of outer UI invocations, including direct calls on the UI thread.
        /// Nested reads and event callbacks belong to the same invocation.
        /// </summary>
        internal long UiThreadDispatchCount => System.Threading.Interlocked.Read(ref uiThreadDispatchCount);

        public LiveSplitAdapter(LiveSplitState state)
        {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.timerModel = new TimerModel { CurrentState = state };
        }

        // TimerState is intentionally UI-thread independent so it can be captured
        // directly on the timer operation / LiveSplit event critical path.
        public TimerState BuildTimerState(
            ulong stateRevision,
            ulong sessionId,
            ulong runRevision,
            ulong attemptRevision,
            ulong runtimeRevision)
        {
            var currentTime = state.CurrentTime;
            var timerState = new TimerState
            {
                StateRevision = stateRevision,
                SessionId = sessionId,
                Phase = MapTimerPhase(state.CurrentPhase),
                SplitIndex = state.CurrentSplitIndex,
                IsGameTimeInitialized = state.IsGameTimeInitialized,
                IsGameTimePaused = state.IsGameTimePaused,
                RunRevision = runRevision,
                AttemptRevision = attemptRevision,
                RuntimeRevision = runtimeRevision,
            };

            if (currentTime.RealTime.HasValue)
            {
                timerState.RealTimeTicks = currentTime.RealTime.Value.Ticks;
            }

            if (currentTime.GameTime.HasValue)
            {
                timerState.GameTimeTicks = currentTime.GameTime.Value.Ticks;
            }

            return timerState;
        }

        // Test seams: allow a test to mutate state while a heavy query is being
        // built, and to hold a query open to prove timer control is not serialized
        // behind it.
        internal Action? BeforeBuildRunState { get; set; }

        internal Action? BeforeBuildAttemptState { get; set; }

        internal Action? BeforeBuildRuntimeState { get; set; }

        public RunState BuildRunState(ulong runRevision, ulong sessionId)
        {
            return InvokeOnUiThread(() =>
            {
                BeforeBuildRunState?.Invoke();
                return BuildRunStateCore(state.Run, runRevision, sessionId);
            });
        }

        public AttemptState BuildAttemptState(ulong attemptRevision, ulong sessionId)
        {
            return InvokeOnUiThread(() =>
            {
                BeforeBuildAttemptState?.Invoke();
                return BuildAttemptStateCore(state.Run, attemptRevision, sessionId);
            });
        }

        public RuntimeState BuildRuntimeState(ulong runtimeRevision, ulong sessionId)
        {
            return InvokeOnUiThread(() =>
            {
                BeforeBuildRuntimeState?.Invoke();
                return BuildRuntimeStateCore(state.Run, runtimeRevision, sessionId);
            });
        }

        public RuntimeRevisionState CaptureRuntimeRevisionState()
        {
            return BuildRuntimeRevisionStateCore(state.Run);
        }

        // Only CurrentComparison is consulted for comparison rename / switch events.
        public string CaptureCurrentComparison()
        {
            return state.CurrentComparison ?? string.Empty;
        }

        private RunState BuildRunStateCore(IRun run, ulong runRevision, ulong sessionId)
        {
            var runState = new RunState
            {
                SessionId = sessionId,
                RunRevision = runRevision,
            };

            if (run == null)
            {
                return runState;
            }

            runState.GameName = run.GameName ?? string.Empty;
            runState.CategoryName = run.CategoryName ?? string.Empty;
            runState.OffsetTicks = run.Offset.Ticks;

            if (!string.IsNullOrEmpty(run.FilePath))
            {
                runState.FilePath = run.FilePath;
            }

            if (!string.IsNullOrEmpty(run.LayoutPath))
            {
                runState.LayoutPath = run.LayoutPath;
            }

            runState.Metadata = BuildRunMetadata(run.Metadata);

            var gameIcon = MapImage(run.GameIcon);
            if (gameIcon != null)
            {
                runState.GameIcon = gameIcon;
            }

            var comparisons = (run.Comparisons ?? Enumerable.Empty<string>())
                .Distinct()
                .ToList();
            runState.Comparisons.Add(comparisons);

            for (var index = 0; index < run.Count; index++)
            {
                runState.Segments.Add(BuildSegmentInfo(run[index], index, comparisons));
            }

            return runState;
        }

        private AttemptState BuildAttemptStateCore(IRun run, ulong attemptRevision, ulong sessionId)
        {
            var attemptState = new AttemptState
            {
                SessionId = sessionId,
                AttemptRevision = attemptRevision,
            };

            if (run == null)
            {
                return attemptState;
            }

            attemptState.AttemptCount = run.AttemptCount > 0 ? (uint)run.AttemptCount : 0U;
            attemptState.CompletedCount = run.AttemptHistory == null
                ? 0U
                : (uint)run.AttemptHistory.Count(attempt => attempt.Time.RealTime != null);

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

                attemptState.Segments.Add(attemptSegment);
            }

            return attemptState;
        }

        private RuntimeState BuildRuntimeStateCore(IRun run, ulong runtimeRevision, ulong sessionId)
        {
            var runtimeState = new RuntimeState
            {
                SessionId = sessionId,
                RuntimeRevision = runtimeRevision,
                CurrentTimingMethod = MapTimingMethod(state.CurrentTimingMethod),
                CurrentComparison = state.CurrentComparison ?? string.Empty,
                GlobalHotkeysEnabled = ReadGlobalHotkeysEnabled(state),
            };

            if (run?.Metadata?.CustomVariables != null)
            {
                foreach (var pair in run.Metadata.CustomVariables)
                {
                    runtimeState.CustomVariables[pair.Key] = pair.Value?.Value ?? string.Empty;
                }
            }

            return runtimeState;
        }

        private RuntimeRevisionState BuildRuntimeRevisionStateCore(IRun run)
        {
            var customVariables = run?.Metadata?.CustomVariables;
            var variables = customVariables == null || customVariables.Count == 0
                ? RevisionSnapshotFactory.EmptyMap
                : RevisionSnapshotFactory.OrderMap(
                    customVariables.Select(pair => new KeyValuePair<string, string>(
                        pair.Key,
                        pair.Value?.Value)));

            return new RuntimeRevisionState(
                (int)state.CurrentTimingMethod,
                state.CurrentComparison ?? string.Empty,
                ReadGlobalHotkeysEnabled(state),
                variables);
        }

        private static bool ReadGlobalHotkeysEnabled(LiveSplitState state)
        {
            var settings = state.Settings;
            if (settings?.HotkeyProfiles == null || settings.HotkeyProfiles.Count == 0)
            {
                return false;
            }

            var profileName = state.CurrentHotkeyProfile;
            if (string.IsNullOrEmpty(profileName)
                || !settings.HotkeyProfiles.TryGetValue(profileName, out var profile)
                || profile == null)
            {
                return false;
            }

            return profile.GlobalHotkeysEnabled;
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

            var icon = MapImage(segment.Icon);
            if (icon != null)
            {
                info.Icon = icon;
            }

            return info;
        }

        private static ProtoImage MapImage(System.Drawing.Image image)
        {
            if (image == null)
            {
                return null;
            }

            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Png);
            return new ProtoImage
            {
                MimeType = "image/png",
                Data = ByteString.CopyFrom(stream.ToArray()),
                Width = (uint)image.Width,
                Height = (uint)image.Height,
            };
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
            return new GameTimeRevisionState(
                state.IsGameTimeInitialized,
                state.IsGameTimePaused,
                state.LoadingTimes.Ticks,
                state.GameTimePauseTime?.Ticks);
        }

        // Timer mutations run on the request thread (like LiveSplit's own command
        // server). Serialization is provided by the caller's control gate.
        public OperationResponse ExecuteTimerOperation(TimerOperationType operation)
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
                        if (state.CurrentPhase == ModelTimerPhase.Running)
                        {
                            timerModel.Pause();
                        }
                        break;
                    case TimerOperationType.TimerResume:
                        if (state.CurrentPhase == ModelTimerPhase.Paused)
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
        }

        public GameTimeOperationExecution ExecuteGameTimeOperation(
            GameTimeOperationType operation,
            long? ticks)
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
        }

        internal T InvokeOnUiThread<T>(Func<T> callback)
        {
            if (state.Form.InvokeRequired)
            {
                return (T)state.Form.Invoke((Func<T>)(() => InvokeOnUiThread(callback)));
            }

            if (uiInvocationDepth++ == 0)
            {
                System.Threading.Interlocked.Increment(ref uiThreadDispatchCount);
            }

            try
            {
                return callback();
            }
            finally
            {
                uiInvocationDepth--;
            }
        }

        private static ProtocolTimerPhase MapTimerPhase(ModelTimerPhase phase)
        {
            return phase switch
            {
                ModelTimerPhase.NotRunning => ProtocolTimerPhase.NotRunning,
                ModelTimerPhase.Running => ProtocolTimerPhase.Running,
                ModelTimerPhase.Paused => ProtocolTimerPhase.Paused,
                ModelTimerPhase.Ended => ProtocolTimerPhase.Ended,
                _ => ProtocolTimerPhase.Unspecified,
            };
        }

        private static ProtocolTimingMethod MapTimingMethod(ModelTimingMethod method)
        {
            return method switch
            {
                ModelTimingMethod.RealTime => ProtocolTimingMethod.RealTime,
                ModelTimingMethod.GameTime => ProtocolTimingMethod.GameTime,
                _ => ProtocolTimingMethod.Unspecified,
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
