using System;
using System.Collections.Generic;
using System.Windows;

namespace RainbowDashDesktop
{
    internal enum DockEdge { None, Left, Right, Top, Bottom }

    // All rectangles and pointer positions supplied to this layer are desktop DIPs.
    // Platform code must convert each monitor's physical pixels before calling it.
    internal sealed class EdgeMonitor
    {
        public readonly string Id;
        public readonly Rect Bounds;
        public readonly Rect WorkArea;
        public EdgeMonitor(string id, Rect bounds, Rect workArea)
        { Id = id ?? ""; Bounds = bounds; WorkArea = workArea; }
    }

    internal sealed class EdgeDockTarget
    {
        public readonly DockEdge Edge;
        public readonly string MonitorId;
        public readonly double Fraction;
        public readonly Rect Bounds;
        public EdgeDockTarget(DockEdge edge, string monitorId, double fraction, Rect bounds)
        { Edge = edge; MonitorId = monitorId ?? ""; Fraction = fraction; Bounds = bounds; }
    }

    internal static class EdgeDockGeometry
    {
        public const double SnapDistance = 20;
        public const double CornerMargin = 64;
        public const double PeekDistance = 12;
        public const double UndockDistance = 64;
        private const double Epsilon = 0.01;

        public static Size CompactSize(DockEdge edge)
        {
            if (edge == DockEdge.Left || edge == DockEdge.Right) return new Size(56, 88);
            if (edge == DockEdge.Top) return new Size(88, 88);
            if (edge == DockEdge.Bottom) return new Size(88, 56);
            return new Size(0, 0);
        }

        // The caller invokes this only after a real drag was released successfully.
        // visibleBounds is the rendered pony's opaque area, never its transparent canvas.
        public static bool TryFindDock(Rect visibleBounds, Point pointer, IList<EdgeMonitor> monitors,
            out EdgeDockTarget target)
        {
            target = null;
            if (!Valid(visibleBounds) || !Finite(pointer.X) || !Finite(pointer.Y) || monitors == null) return false;
            double bestDistance = Double.PositiveInfinity;
            bool bestContainsPointer = false;
            foreach (EdgeMonitor monitor in monitors)
            {
                Rect work;
                if (!TryWorkArea(monitor, out work)) continue;
                Rect near = work; near.Inflate(SnapDistance, SnapDistance);
                if (!near.IntersectsWith(visibleBounds)) continue;
                foreach (DockEdge edge in new DockEdge[] { DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom })
                {
                    double signedDistance = EdgeDistance(edge, visibleBounds, work);
                    // Releasing with the pointer against a screen edge often leaves
                    // half the pony beyond that edge. Crossing it is an eligible
                    // arrival, provided the bounded near-work-area test above holds.
                    if (signedDistance > SnapDistance + Epsilon) continue;
                    double distance = Math.Abs(signedDistance);
                    double parallelStart = IsVertical(edge) ? visibleBounds.Top : visibleBounds.Left;
                    double parallelEnd = IsVertical(edge) ? visibleBounds.Bottom : visibleBounds.Right;
                    double workStart = IsVertical(edge) ? work.Top : work.Left;
                    double workEnd = IsVertical(edge) ? work.Bottom : work.Right;
                    if (parallelEnd < workStart || parallelStart > workEnd) continue;
                    double center = IsVertical(edge) ? visibleBounds.Top + visibleBounds.Height / 2 : visibleBounds.Left + visibleBounds.Width / 2;
                    double fraction = FractionForCenter(edge, work, center);
                    EdgeDockTarget candidate = Make(edge, monitor, work, fraction, false);
                    if (IsSharedBoundary(edge, monitor, work, candidate.Bounds, monitors)) continue;
                    bool containsPointer = monitor.Bounds.Contains(pointer);
                    if (target == null || distance < bestDistance - Epsilon ||
                        (Math.Abs(distance - bestDistance) <= Epsilon && containsPointer && !bestContainsPointer))
                    {
                        target = candidate; bestDistance = distance; bestContainsPointer = containsPointer;
                    }
                }
            }
            return target != null;
        }

        public static EdgeDockTarget Resolve(DockEdge edge, string monitorId, double fraction,
            IList<EdgeMonitor> monitors, bool peek)
        { return ResolveCore(edge, monitorId, fraction, monitors, peek, null); }

        public static EdgeDockTarget Resolve(DockEdge edge, string monitorId, double fraction,
            IList<EdgeMonitor> monitors, bool peek, Point fallbackPosition)
        { return ResolveCore(edge, monitorId, fraction, monitors, peek, fallbackPosition); }

        private static EdgeDockTarget ResolveCore(DockEdge edge, string monitorId, double fraction,
            IList<EdgeMonitor> monitors, bool peek, Point? fallbackPosition)
        {
            if (edge == DockEdge.None || !Enum.IsDefined(typeof(DockEdge), edge) || monitors == null) return null;
            EdgeMonitor chosen = FindMonitor(monitorId, monitors, fallbackPosition);
            Rect work;
            if (chosen == null || !TryWorkArea(chosen, out work)) return null;
            EdgeDockTarget target = Make(edge, chosen, work, Normalize(fraction), peek);
            // A changed monitor layout may turn a previously external edge into a seam.
            // Restoring at that seam would obstruct moving between displays.
            if (IsSharedBoundary(edge, chosen, work, target.Bounds, monitors)) return null;
            return target;
        }

        public static Rect UndockBounds(EdgeDockTarget target, Size normalSize, IList<EdgeMonitor> monitors)
        {
            if (target == null || !Finite(normalSize.Width) || !Finite(normalSize.Height) ||
                normalSize.Width <= 0 || normalSize.Height <= 0) return Rect.Empty;
            Point center = new Point(target.Bounds.Left + target.Bounds.Width / 2,
                target.Bounds.Top + target.Bounds.Height / 2);
            EdgeMonitor monitor = FindMonitor(target.MonitorId, monitors, center);
            Rect work;
            if (monitor == null || !TryWorkArea(monitor, out work)) return Rect.Empty;
            double x = center.X - normalSize.Width / 2, y = center.Y - normalSize.Height / 2;
            if (target.Edge == DockEdge.Left) x = work.Left + PeekDistance;
            else if (target.Edge == DockEdge.Right) x = work.Right - normalSize.Width - PeekDistance;
            else if (target.Edge == DockEdge.Top) y = work.Top + PeekDistance;
            else if (target.Edge == DockEdge.Bottom) y = work.Bottom - normalSize.Height - PeekDistance;
            x = Clamp(x, work.Left, Math.Max(work.Left, work.Right - normalSize.Width));
            y = Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - normalSize.Height));
            return new Rect(x, y, normalSize.Width, normalSize.Height);
        }

        public static double InwardDistance(DockEdge edge, Point press, Point current)
        {
            if (!Finite(press.X) || !Finite(press.Y) || !Finite(current.X) || !Finite(current.Y)) return 0;
            if (edge == DockEdge.Left) return current.X - press.X;
            if (edge == DockEdge.Right) return press.X - current.X;
            if (edge == DockEdge.Top) return current.Y - press.Y;
            if (edge == DockEdge.Bottom) return press.Y - current.Y;
            return 0;
        }

        private static EdgeDockTarget Make(DockEdge edge, EdgeMonitor monitor, Rect work, double fraction, bool peek)
        {
            Size size = CompactSize(edge);
            double length = IsVertical(edge) ? size.Height : size.Width;
            double min, max; AlongRange(edge, work, length, out min, out max);
            double along = min + Normalize(fraction) * (max - min);
            double width = size.Width, height = size.Height;
            if (peek) { if (IsVertical(edge)) width += PeekDistance; else height += PeekDistance; }
            double x = edge == DockEdge.Left ? work.Left : edge == DockEdge.Right ? work.Right - width : along;
            double y = edge == DockEdge.Top ? work.Top : edge == DockEdge.Bottom ? work.Bottom - height : along;
            return new EdgeDockTarget(edge, monitor.Id, Normalize(fraction), new Rect(x, y, width, height));
        }

        private static double FractionForCenter(DockEdge edge, Rect work, double center)
        {
            Size size = CompactSize(edge);
            double length = IsVertical(edge) ? size.Height : size.Width;
            double min, max; AlongRange(edge, work, length, out min, out max);
            if (max - min <= Epsilon) return 0.5;
            return Normalize((center - length / 2 - min) / (max - min));
        }

        private static void AlongRange(DockEdge edge, Rect work, double length, out double min, out double max)
        {
            double start = IsVertical(edge) ? work.Top : work.Left;
            double end = IsVertical(edge) ? work.Bottom : work.Right;
            // Very small screens cannot reserve 64 DIP at both corners. Centering
            // preserves a visible pet instead of producing inverted clamp bounds.
            if (end - start < CornerMargin * 2 + length)
            { min = max = start + Math.Max(0, (end - start - length) / 2); return; }
            min = start + CornerMargin;
            max = end - CornerMargin - length;
        }

        private static bool IsSharedBoundary(DockEdge edge, EdgeMonitor monitor, Rect work,
            Rect compact, IList<EdgeMonitor> monitors)
        {
            // An inset work-area edge belongs to the taskbar and remains a useful
            // docking boundary, even if another monitor adjoins the physical edge.
            double boundary = edge == DockEdge.Left ? work.Left : edge == DockEdge.Right ? work.Right :
                edge == DockEdge.Top ? work.Top : work.Bottom;
            double physical = edge == DockEdge.Left ? monitor.Bounds.Left : edge == DockEdge.Right ? monitor.Bounds.Right :
                edge == DockEdge.Top ? monitor.Bounds.Top : monitor.Bounds.Bottom;
            if (Math.Abs(boundary - physical) > Epsilon) return false;
            foreach (EdgeMonitor other in monitors)
            {
                if (other == null || Object.ReferenceEquals(other, monitor) || !Valid(other.Bounds)) continue;
                bool beyond = edge == DockEdge.Left ? other.Bounds.Left < boundary - Epsilon && other.Bounds.Right >= boundary - Epsilon :
                    edge == DockEdge.Right ? other.Bounds.Right > boundary + Epsilon && other.Bounds.Left <= boundary + Epsilon :
                    edge == DockEdge.Top ? other.Bounds.Top < boundary - Epsilon && other.Bounds.Bottom >= boundary - Epsilon :
                    other.Bounds.Bottom > boundary + Epsilon && other.Bounds.Top <= boundary + Epsilon;
                if (!beyond) continue;
                double overlap = IsVertical(edge) ? Math.Min(compact.Bottom, other.Bounds.Bottom) - Math.Max(compact.Top, other.Bounds.Top) :
                    Math.Min(compact.Right, other.Bounds.Right) - Math.Max(compact.Left, other.Bounds.Left);
                if (overlap > Epsilon) return true;
            }
            return false;
        }

        private static EdgeMonitor FindMonitor(string id, IList<EdgeMonitor> monitors, Point? fallbackPosition)
        {
            if (monitors == null) return null;
            EdgeMonitor first = null, nearest = null;
            double closest = Double.PositiveInfinity;
            foreach (EdgeMonitor monitor in monitors)
            {
                Rect work;
                if (!TryWorkArea(monitor, out work)) continue;
                if (String.Equals(monitor.Id, id, StringComparison.OrdinalIgnoreCase)) return monitor;
                if (first == null) first = monitor;
                if (fallbackPosition.HasValue && Finite(fallbackPosition.Value.X) && Finite(fallbackPosition.Value.Y))
                {
                    double dx = fallbackPosition.Value.X - Clamp(fallbackPosition.Value.X, work.Left, work.Right);
                    double dy = fallbackPosition.Value.Y - Clamp(fallbackPosition.Value.Y, work.Top, work.Bottom);
                    double distance = dx * dx + dy * dy;
                    if (distance < closest) { closest = distance; nearest = monitor; }
                }
            }
            return nearest ?? first;
        }

        private static bool TryWorkArea(EdgeMonitor monitor, out Rect work)
        {
            work = Rect.Empty;
            if (monitor == null || !Valid(monitor.Bounds)) return false;
            work = Valid(monitor.WorkArea) ? Rect.Intersect(monitor.Bounds, monitor.WorkArea) : monitor.Bounds;
            if (!Valid(work)) work = monitor.Bounds;
            return true;
        }

        private static double EdgeDistance(DockEdge edge, Rect visible, Rect work)
        {
            if (edge == DockEdge.Left) return visible.Left - work.Left;
            if (edge == DockEdge.Right) return work.Right - visible.Right;
            if (edge == DockEdge.Top) return visible.Top - work.Top;
            return work.Bottom - visible.Bottom;
        }
        private static bool IsVertical(DockEdge edge) { return edge == DockEdge.Left || edge == DockEdge.Right; }
        private static bool Valid(Rect rect)
        { return !rect.IsEmpty && Finite(rect.X) && Finite(rect.Y) && Finite(rect.Width) && Finite(rect.Height) && rect.Width > 0 && rect.Height > 0; }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static double Normalize(double value) { return Finite(value) ? Clamp(value, 0, 1) : 0.5; }
        private static double Clamp(double value, double min, double max) { return Math.Max(min, Math.Min(max, value)); }
    }

    internal sealed class EdgeDockHoverController
    {
        public const long EnterDelayMs = 250;
        public const long LeaveDelayMs = 800;
        private bool pointerInside, isPeeking, pending;
        private long changedAt, lastNow;
        public bool PointerInside { get { return pointerInside; } }
        public bool IsPeeking { get { return isPeeking; } }
        public void SetPointer(bool inside, long nowMs)
        {
            long now = Time(nowMs);
            if (pointerInside == inside) return;
            pointerInside = inside; changedAt = now; pending = pointerInside != isPeeking;
        }
        public bool Advance(long nowMs)
        {
            long now = Time(nowMs);
            if (!pending || now - changedAt < (pointerInside ? EnterDelayMs : LeaveDelayMs)) return false;
            isPeeking = pointerInside; pending = false; return true;
        }
        public void Reset()
        { pointerInside = false; isPeeking = false; pending = false; changedAt = lastNow = 0; }
        private long Time(long nowMs) { lastNow = Math.Max(lastNow, Math.Max(0, nowMs)); return lastNow; }
    }
}
