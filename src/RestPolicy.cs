using System;

namespace RainbowDashDesktop
{
    internal enum RestStage { Awake, Falling, Sleeping, Waking }
    internal enum RestAttentionKind { Pointer, Click, Menu, Drag }

    // Only real task observations and deliberate user attention are inputs.
    // Rendering, quota refresh, gaze and personality actions must not call
    // NotifyInteraction. Pointer motion/distance filtering belongs to the UI.
    // This controller owns no window, playback controller or wall clock.
    internal sealed class RestController
    {
        public const long DefaultSleepDelayMs = 300000;
        public const long FallDurationMs = 1200;
        public const long WakeDurationMs = 1000;
        public const long DragWakeDurationMs = 200;
        public const long FallReturnMinMs = 120;
        public const long FallReturnMaxMs = 600;

        private long lastNow, quietSince;
        private int activeTasks;
        private string pendingTaskState = "idle";

        public RestStage Stage { get; private set; }
        public long SleepDelayMs { get; private set; }
        public long PhaseStartedAt { get; private set; }
        public long PhaseDuration { get; private set; }
        public long PhaseAgeMs { get { return lastNow - PhaseStartedAt; } }
        public string RequestedTaskState { get; private set; }
        public string PendingTaskState { get { return pendingTaskState; } }
        public long WakeCompletedVersion { get; private set; }
        public string LastWakeTarget { get; private set; }
        public bool WakeFromFalling { get; private set; }
        public double WakeFromFallProgress { get; private set; }
        public bool ManualSleepRequested { get; private set; }
        public long LastInteractionAt { get; private set; }
        public long QuietSince { get { return quietSince; } }
        public bool IsSleepingLike { get { return Stage != RestStage.Awake; } }
        public bool IsQuiet { get { return activeTasks == 0 && RequestedTaskState == "idle"; } }
        public bool CanRunIdleExtras { get { return Stage == RestStage.Awake && IsQuiet; } }

        public RestController() : this(0) { }
        public RestController(long startMs)
        {
            lastNow = quietSince = PhaseStartedAt = LastInteractionAt = Math.Max(0, startMs);
            Stage = RestStage.Awake;
            RequestedTaskState = LastWakeTarget = "idle";
            SleepDelayMs = DefaultSleepDelayMs;
        }

        // 0 means never. Merely repeating an unchanged setting is not attention.
        public void Configure(long delayMs, long nowMs)
        {
            long now = Clock(nowMs), delay = Math.Max(0, delayMs);
            bool wasDisabled = SleepDelayMs == 0;
            SleepDelayMs = delay;
            if (delay == 0 && !ManualSleepRequested && Stage != RestStage.Awake) BeginWake(now, false);
            else if (wasDisabled && delay > 0 && IsQuiet) quietSince = now;
            AdvanceCore(now);
        }

        public void ObserveTask(string state, int taskCount, long nowMs)
        {
            long now = Clock(nowMs);
            bool wasQuiet = IsQuiet;
            string previousState = RequestedTaskState;
            int previousCount = activeTasks;
            RequestedTaskState = String.IsNullOrWhiteSpace(state) ? "idle" : state.Trim().ToLowerInvariant();
            activeTasks = Math.Max(0, taskCount);
            // The existing task may keep running during a deliberate nap. Repeated
            // polls are not new work; a new non-idle state or added task is.
            if (ManualSleepRequested && (activeTasks > previousCount ||
                RequestedTaskState != "idle" && RequestedTaskState != previousState))
                ManualSleepRequested = false;
            if (IsQuiet && !wasQuiet) quietSince = now;

            string target = CurrentVisualTarget();
            // A short task can complete while the wake bridge is playing and
            // already be reported idle on the next poll. Keep that final reaction
            // for this wake only, while retaining the actual latest request above.
            if (Stage != RestStage.Waking || target != "idle" || !IsTerminal(pendingTaskState))
                pendingTaskState = target;
            if (!ManualSleepRequested && !IsQuiet && (Stage == RestStage.Falling || Stage == RestStage.Sleeping))
                BeginWake(now, false);
            AdvanceCore(now);
        }

        // A real nap, independent of the automatic timeout and backend activity.
        // During waking this flag queues one nap after the existing bridge;
        // repeated requests cannot restart either bridge or extend its deadline.
        public void RequestSleep(long nowMs)
        {
            long now = Clock(nowMs);
            ManualSleepRequested = true;
            AdvanceCore(now);
        }

        public void NotifyInteraction(RestAttentionKind kind, long nowMs)
        {
            long now = Clock(nowMs);
            ManualSleepRequested = false;
            LastInteractionAt = now;
            if (IsQuiet) quietSince = now;
            if (Stage != RestStage.Awake) BeginWake(now, kind == RestAttentionKind.Drag);
            AdvanceCore(now);
        }

        public void Advance(long nowMs) { AdvanceCore(Clock(nowMs)); }

        // Frame count is an artwork concern. A six-cel bridge holds its final cel
        // until its duration ends; it does not loop or start the next task early.
        public int PhaseFrame(long nowMs, int frameCount)
        {
            Advance(nowMs);
            if (frameCount <= 0) return -1;
            if (PhaseDuration <= 0) return 0;
            return Math.Min(frameCount - 1, (int)((double)PhaseAgeMs / PhaseDuration * frameCount));
        }

        private long Clock(long nowMs)
        { lastNow = Math.Max(lastNow, Math.Max(0, nowMs)); return lastNow; }

        private string CurrentVisualTarget()
        { return RequestedTaskState == "idle" && activeTasks > 0 ? "working" : RequestedTaskState; }

        private static bool IsTerminal(string state)
        { return state == "completed" || state == "interrupted" || state == "error"; }

        private void BeginWake(long now, bool fast)
        {
            if (Stage == RestStage.Waking)
            {
                // Drag capture and movement remain immediate in the owner. Only
                // shorten the artwork bridge; never extend or restart a wake.
                long age = now - PhaseStartedAt;
                if (fast && age < PhaseDuration)
                    PhaseDuration = Math.Min(PhaseDuration, age + DragWakeDurationMs);
                return;
            }
            // A half-settled pet must return from that pose, rather than jump to
            // the fully asleep first cel of the normal wake-up animation. The UI
            // reverses the fall artwork from this proportion to its awake pose.
            long fallAge = now - PhaseStartedAt;
            WakeFromFalling = Stage == RestStage.Falling && fallAge < PhaseDuration;
            WakeFromFallProgress = WakeFromFalling ? Math.Min(1, (double)fallAge / PhaseDuration) : 0;
            long duration = WakeFromFalling ?
                Math.Max(FallReturnMinMs, (long)Math.Ceiling(FallReturnMaxMs * WakeFromFallProgress)) : WakeDurationMs;
            if (fast) duration = Math.Min(duration, DragWakeDurationMs);
            pendingTaskState = CurrentVisualTarget();
            Stage = RestStage.Waking;
            PhaseStartedAt = now;
            PhaseDuration = duration;
        }

        private void AdvanceCore(long now)
        {
            if (Stage == RestStage.Waking && now - PhaseStartedAt >= PhaseDuration)
            {
                long finishedAt = PhaseStartedAt + PhaseDuration;
                LastWakeTarget = pendingTaskState;
                WakeCompletedVersion++;
                Stage = RestStage.Awake;
                WakeFromFalling = false;
                WakeFromFallProgress = 0;
                PhaseStartedAt = finishedAt;
                PhaseDuration = 0;
                pendingTaskState = CurrentVisualTarget();
                if (IsQuiet) quietSince = Math.Max(quietSince, finishedAt);
            }
            else if (Stage == RestStage.Falling && now - PhaseStartedAt >= PhaseDuration)
            {
                PhaseStartedAt += PhaseDuration;
                PhaseDuration = 0;
                Stage = RestStage.Sleeping;
            }

            if (Stage == RestStage.Awake && (ManualSleepRequested ||
                SleepDelayMs > 0 && IsQuiet && now - quietSince >= SleepDelayMs))
            {
                // Begin at the first observation of the threshold, so a delayed
                // timer still displays the whole settling bridge.
                Stage = RestStage.Falling;
                PhaseStartedAt = now;
                PhaseDuration = FallDurationMs;
            }
        }
    }
}
