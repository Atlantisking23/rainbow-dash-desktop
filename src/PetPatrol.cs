using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace RainbowDashDesktop
{
    internal sealed class PetPatrolRoute
    {
        public const long DurationMs = 1510;
        public const long TakeoffLeadMs = 130;
        public const long LandingMs = 170;
        private const double MinDistance = 72;
        private const double MaxDistance = 168;
        private const double EdgeMargin = 24;
        private const double Lift = 26;

        public readonly Point Start, Landing;
        public readonly long StartedAt;
        public bool FacesRight { get { return Landing.X > Start.X; } }

        private PetPatrolRoute(Point start, Point landing, long now)
        {
            Start = start;
            Landing = landing;
            StartedAt = now;
        }

        public static PetPatrolRoute Plan(Rect window, Rect work, long now)
        {
            if (window.IsEmpty || work.IsEmpty || !work.Contains(window)) return null;
            double left = work.Left + EdgeMargin;
            double right = work.Right - window.Width - EdgeMargin;
            double top = work.Top + EdgeMargin;
            double bottom = work.Bottom - window.Height - EdgeMargin;
            if (right < left || bottom < top) return null;

            Point start = window.TopLeft;
            // Prefer a nearby edge and turn back inward when there is no room
            // for a full hop in that direction.
            bool towardRight = start.X > (left + right) / 2;
            double aim = towardRight ? right : left;
            if (Math.Abs(aim - start.X) < MinDistance) aim = towardRight ? left : right;
            double x = Limit(start.X + Limit(aim - start.X, -MaxDistance, MaxDistance), left, right);
            if (Math.Abs(x - start.X) < MinDistance) return null;

            double y = start.Y > (top + bottom) / 2 ? bottom : top;
            y = Limit(start.Y + Limit(y - start.Y, -EdgeMargin, EdgeMargin), top, bottom);
            return new PetPatrolRoute(start, new Point(x, y), now);
        }

        public long Age(long now) { return Math.Max(0, now - StartedAt); }

        public Point Position(long now)
        {
            double ease = FlightProgress(now);
            double x = Start.X + (Landing.X - Start.X) * ease;
            double y = Start.Y + (Landing.Y - Start.Y) * ease;
            return new Point(x, y);
        }

        // The window follows the landing anchor. Only the drawing gains lift,
        // so another state can take over at the current safe position.
        public double VerticalOffset(long now)
        {
            double progress = FlightProgress(now);
            return progress <= 0 || progress >= 1 ? 0 : -Lift * Math.Sin(Math.PI * progress);
        }

        private double FlightProgress(long now)
        {
            double t = Limit((double)(Age(now) - TakeoffLeadMs) / (DurationMs - TakeoffLeadMs), 0, 1);
            return t * t * (3 - 2 * t);
        }

        public Rect CursorAvoidanceArea(Size windowSize)
        {
            Rect routeBounds = Rect.Union(new Rect(Start, windowSize), new Rect(Landing, windowSize));
            routeBounds.Inflate(24, Lift + 24);
            return routeBounds;
        }

        private static double Limit(double value, double minimum, double maximum)
        { return Math.Max(minimum, Math.Min(maximum, value)); }
    }

    internal sealed partial class PetWindow
    {
        private const int PatrolRetryMs = 10000;
        private readonly Random patrolTiming = new Random();
        private readonly TranslateTransform patrolLift = new TranslateTransform();
        private PetPatrolRoute patrolRoute;
        private long patrolReadyAt;

        private bool PatrolInProgress { get { return patrolRoute != null; } }

        private MenuItem BuildPatrolMenu()
        {
            MenuItem item = new MenuItem {
                Header = "待机时偶尔短程飞行", IsCheckable = true, IsChecked = settings.AutonomousPatrol,
                ToolTip = "清醒待机时偶尔在同一屏幕飞一小段。收到任务、拖动或进入专注模式时会停下。"
            };
            item.Click += delegate
            {
                settings.AutonomousPatrol = item.IsChecked;
                if (!item.IsChecked) CancelPatrol(interactionClock.ElapsedMilliseconds, false);
                patrolReadyAt = 0;
                SaveSettings();
                PresentSprite(interactionClock.ElapsedMilliseconds);
            };
            return item;
        }

        private bool CanPatrol()
        {
            return settings.AutonomousPatrol && !ReduceMotionEnabled && !demo && !smokeTest && positioned && IsVisible && !closing &&
                !IsDocked && !dockLeaving && !focus.IsActive && !drag.IsPressed && !MenuIsOpen() &&
                rest.CanRunIdleExtras && wakeVisualHoldState == null && EffectiveRestPreview == null &&
                manualState == null && shownState == "idle" && playback.VisualState == "idle" &&
                (lastSnapshot == null || lastSnapshot.ActiveTasks == 0);
        }

        private void AdvancePatrol(long now)
        {
            if (!CanPatrol())
            {
                CancelPatrol(now);
                patrolReadyAt = 0;
                return;
            }

            if (patrolRoute == null)
            {
                if (patrolReadyAt == 0) SchedulePatrol(now);
                if (now < patrolReadyAt || enhancedAction.Active || gazeFrame >= 0 || PetTouchInProgress) return;
                patrolRoute = PlanPatrol(now);
                if (patrolRoute == null) { patrolReadyAt = now + PatrolRetryMs; return; }
                saveTimer.Stop();
                patrolLift.BeginAnimation(TranslateTransform.YProperty, null);
                spriteImage.RenderTransform = patrolLift;
            }

            Point next = patrolRoute.Position(now);
            Left = next.X;
            Top = next.Y;
            patrolLift.Y = patrolRoute.VerticalOffset(now);
            if (patrolRoute.Age(now) >= PetPatrolRoute.DurationMs) CancelPatrol(now);
        }

        private PetPatrolRoute PlanPatrol(long now)
        {
            AnimationClip clip;
            if (!clips.TryGetValue("dragging", out clip) || clip.Frames.Count == 0 ||
                ActualWidth <= 0 || ActualHeight <= 0 || Double.IsNaN(Left) || Double.IsNaN(Top)) return null;
            Size size = new Size(ActualWidth, ActualHeight);
            Rect current = new Rect(new Point(Left, Top), size);
            Point center = new Point(Left + size.Width / 2, Top + size.Height / 2);
            foreach (EdgeMonitor screen in GetEdgeMonitors())
            {
                if (!screen.Bounds.Contains(center)) continue;
                Rect work = Rect.Intersect(screen.Bounds, screen.WorkArea);
                PetPatrolRoute route = PetPatrolRoute.Plan(current, work, now);
                if (route == null) return null;

                Point physical;
                if (PresentationSource.FromVisual(this) == null || !DesktopInterop.TryGetCursor(out physical)) return null;
                Point local = PointFromScreen(physical);
                Point pointer = new Point(Left + local.X, Top + local.Y);
                return route.CursorAvoidanceArea(size).Contains(pointer) ? null : route;
            }
            return null;
        }

        private void SchedulePatrol(long now)
        { patrolReadyAt = now + patrolTiming.Next(55000, 90001); }

        private void CancelPatrol(long now, bool save = true)
        {
            if (patrolRoute == null)
            {
                if (ReduceMotionEnabled && patrolLift.HasAnimatedProperties)
                    patrolLift.BeginAnimation(TranslateTransform.YProperty, null);
                return;
            }
            patrolRoute = null;
            double height = patrolLift.Y;
            patrolLift.Y = 0;
            if (height < -.25 && IsVisible && !closing && !ReduceMotionEnabled)
            {
                // Task and pointer states can already draw while the shared
                // visual surface finishes this short, position-only landing.
                var settle = new DoubleAnimation(height, 0, TimeSpan.FromMilliseconds(120)) {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
                };
                settle.Completed += delegate { patrolLift.BeginAnimation(TranslateTransform.YProperty, null); };
                patrolLift.BeginAnimation(TranslateTransform.YProperty, settle);
            }
            SchedulePatrol(now);
            restIdle.Reset(now);
            personality.Postpone(now);
            if (save) SaveSettings();
        }

        private bool TryGetPatrolSprite(long now, out BitmapSource source, out string family, out bool mirror)
        {
            source = null;
            family = null;
            mirror = false;
            if (patrolRoute == null || !CanPatrol()) return false;
            long age = patrolRoute.Age(now);
            if (age < PetPatrolRoute.TakeoffLeadMs) return false;
            if (age >= PetPatrolRoute.DurationMs - PetPatrolRoute.LandingMs)
            {
                source = restArt.Get("awake-idle", 0);
                family = "rest:awake-idle";
                mirror = true;
            }
            if (source == null)
            {
                AnimationClip clip = clips["dragging"];
                int frame = clip.FrameIndexAt(age - PetPatrolRoute.TakeoffLeadMs, true);
                source = clip.Frames[frame];
                family = "state:dragging";
                mirror = clip.MirrorHorizontal ^ patrolRoute.FacesRight;
            }
            return true;
        }
    }
}
