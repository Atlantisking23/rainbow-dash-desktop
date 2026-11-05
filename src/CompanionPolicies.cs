using System;

namespace RainbowDashDesktop
{
    // These controllers own only short visual attention and focus policy. They
    // never alter the real task state or account quota refresh schedule.
    internal sealed class FocusController
    {
        public const long EnterDelayMs = 1000, LeaveDelayMs = 1500;
        private bool automaticActive, suppressed, previousEffective;
        private bool? pending;
        private long pendingAt, lastNow;
        private long clearAt = -1;
        public bool ManualEnabled { get; private set; }
        public bool AutomaticActive { get { return automaticActive; } }
        public bool IsActive { get { return ManualEnabled || automaticActive; } }
        public void SetManual(bool value) { ManualEnabled = value; }
        public void SuppressAutomaticUntilClear()
        { suppressed = true; automaticActive = false; pending = null; clearAt = -1; }
        public bool Update(bool foregroundFullscreen, bool autoEnabled, long nowMs)
        {
            long now = lastNow = Math.Max(lastNow, Math.Max(0, nowMs));
            if (!foregroundFullscreen)
            {
                if (clearAt < 0) clearAt = now;
                if (now - clearAt >= LeaveDelayMs) suppressed = false;
            }
            else clearAt = -1;
            bool desired = autoEnabled && foregroundFullscreen && !suppressed;
            if (!autoEnabled || suppressed)
            { automaticActive = false; pending = null; }
            else if (desired == automaticActive) pending = null;
            else
            {
                if (!pending.HasValue || pending.Value != desired)
                { pending = desired; pendingAt = now; }
                if (now - pendingAt >= (desired ? EnterDelayMs : LeaveDelayMs))
                { automaticActive = desired; pending = null; }
            }
            bool changed = IsActive != previousEffective;
            previousEffective = IsActive;
            return changed;
        }
    }

    internal sealed class GazeController
    {
        public const double ApproachDistance = 150, ReleaseDistance = 175, CenterDeadZone = 12;
        public const long HeadDelayMs = 180, HoldMs = 2500, ReturnMs = 160, CooldownMs = 8000;
        private int direction;
        private long started, returningAt = -1, cooldownUntil, lastNow;
        public int Sample(bool eligible, double distance, double relativeX, long nowMs)
        {
            long now = lastNow = Math.Max(lastNow, Math.Max(0, nowMs));
            if (!eligible || Double.IsNaN(distance) || Double.IsInfinity(distance) || distance < 0 ||
                Double.IsNaN(relativeX) || Double.IsInfinity(relativeX))
            { if (direction != 0) Cancel(now); return -1; }
            if (direction == 0)
            {
                if (now < cooldownUntil || distance > ApproachDistance || Math.Abs(relativeX) <= CenterDeadZone) return -1;
                direction = relativeX < 0 ? -1 : 1; started = now; returningAt = -1;
            }
            if (returningAt < 0 && (now - started >= HoldMs || distance > ReleaseDistance)) returningAt = now;
            if (returningAt >= 0)
            {
                if (now - returningAt < ReturnMs) return 5;
                Cancel(now); return -1;
            }
            // Latch one direction for the short glance. Crossing the mouse rapidly
            // cannot keep swivelling the head or restart the hold timer.
            return direction < 0 ? (now - started < HeadDelayMs ? 1 : 2) : (now - started < HeadDelayMs ? 3 : 4);
        }
        public void Reset(long nowMs)
        { long now = lastNow = Math.Max(lastNow, Math.Max(0, nowMs)); Cancel(now); }
        private void Cancel(long now)
        { direction = 0; returningAt = -1; cooldownUntil = now + CooldownMs; }
    }

    internal sealed class QuietPersonalityScheduler
    {
        private readonly Random random;
        private bool wasEligible;
        private long nextDue, lastNow;
        private int lastAction = -1;
        private static readonly string[] Actions = { "hair", "preen", "proud" };
        public QuietPersonalityScheduler() : this(Environment.TickCount) { }
        internal QuietPersonalityScheduler(int seed) { random = new Random(seed); }
        public string Sample(bool eligible, long nowMs)
        {
            long now = lastNow = Math.Max(lastNow, Math.Max(0, nowMs));
            if (!eligible) { wasEligible = false; return null; }
            if (!wasEligible) { wasEligible = true; nextDue = now + random.Next(25000, 40001); }
            if (now < nextDue) return null;
            int action = random.Next(Actions.Length);
            if (action == lastAction) action = (action + 1 + random.Next(Actions.Length - 1)) % Actions.Length;
            lastAction = action; nextDue = now + random.Next(45000, 90001);
            return Actions[action];
        }
        public void Postpone(long nowMs)
        {
            long now = lastNow = Math.Max(lastNow, Math.Max(0, nowMs));
            wasEligible = true; nextDue = now + random.Next(45000, 90001);
        }
    }
}
