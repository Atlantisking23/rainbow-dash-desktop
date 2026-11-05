using System;
using System.Collections.Generic;
using System.Windows;
using RainbowDashDesktop;

internal static class EdgeDockingTests
{
    private static int count;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
    private static bool Close(double a, double b) { return Math.Abs(a - b) < 0.001; }
    private static bool At(Rect r, double x, double y, double width, double height)
    { return Close(r.X, x) && Close(r.Y, y) && Close(r.Width, width) && Close(r.Height, height); }
    private static EdgeMonitor Monitor(string id, double x, double y, double w, double h)
    { Rect r = new Rect(x, y, w, h); return new EdgeMonitor(id, r, r); }
    private static bool Dock(Rect visible, IList<EdgeMonitor> monitors, out EdgeDockTarget target)
    { return EdgeDockGeometry.TryFindDock(visible, new Point(visible.Left + visible.Width / 2, visible.Top + visible.Height / 2), monitors, out target); }

    public static int Main()
    {
        try
        {
            EdgeMonitor main = new EdgeMonitor("main", new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040));
            List<EdgeMonitor> one = new List<EdgeMonitor> { main };
            EdgeDockTarget target;
            Check(Dock(new Rect(10, 400, 240, 180), one, out target) && target.Edge == DockEdge.Left,
                "A released pony ten DIP from the left work edge docks left.");
            Check(At(target.Bounds, 0, 446, 56, 88), "Left dock owns only a 56 by 88 DIP hit window.");
            Check(Dock(new Rect(1660, 400, 240, 180), one, out target) && target.Edge == DockEdge.Right && At(target.Bounds, 1864, 446, 56, 88),
                "Right snap includes the exact twenty DIP threshold and stays inside work area.");
            Check(Dock(new Rect(700, 8, 240, 180), one, out target) && target.Edge == DockEdge.Top && At(target.Bounds, 776, 0, 88, 88),
                "Top docking keeps a horizontal 88 by 88 DIP viewport for the entire inverted head.");
            Check(Dock(new Rect(700, 850, 240, 180), one, out target) && target.Edge == DockEdge.Bottom && At(target.Bounds, 776, 984, 88, 56),
                "Bottom docking uses the taskbar work edge rather than the physical screen bottom.");
            Check(!Dock(new Rect(20.1, 400, 240, 180), one, out target), "A pony farther than twenty DIP is not magnetically pulled to the edge.");
            Check(!Dock(new Rect(500, 400, 240, 180), one, out target), "Ordinary interior positions remain undocked.");
            Check(Dock(new Rect(-8, 400, 240, 180), one, out target) && target.Edge == DockEdge.Left,
                "A small release overshoot is recovered to the visible edge.");
            Check(Dock(new Rect(-200, 400, 240, 180), one, out target) && target.Edge == DockEdge.Left,
                "A deep crossing still attaches while part of the pony remains on its screen.");
            Check(EdgeDockGeometry.TryFindDock(new Rect(-80, 400, 240, 180), new Point(0, 450), one, out target) && target.Edge == DockEdge.Left,
                "Dragging the grabbed head to the left pointer boundary accepts an eighty DIP visual overshoot.");
            Check(EdgeDockGeometry.TryFindDock(new Rect(1840, 400, 240, 180), new Point(1920, 450), one, out target) && target.Edge == DockEdge.Right,
                "A pointer release at the right boundary also accepts an eighty DIP visual overshoot.");
            Check(EdgeDockGeometry.TryFindDock(new Rect(700, -80, 240, 180), new Point(800, 0), one, out target) && target.Edge == DockEdge.Top,
                "A pointer release at the top boundary accepts a partially offscreen head.");
            Check(EdgeDockGeometry.TryFindDock(new Rect(700, 960, 240, 180), new Point(800, 1040), one, out target) && target.Edge == DockEdge.Bottom,
                "A pointer release at the bottom work edge accepts a partially crossed pony.");
            Check(!Dock(new Rect(-300, 400, 240, 180), one, out target), "A pony wholly beyond the left screen buffer is not assigned a remote dock.");
            Check(!Dock(new Rect(1950, 400, 240, 180), one, out target), "A pony wholly beyond the right screen buffer remains undocked.");
            Check(!Dock(new Rect(700, -210, 240, 180), one, out target), "A pony wholly above the screen buffer remains undocked.");
            Check(!Dock(new Rect(700, 1070, 240, 180), one, out target), "A pony wholly below the work-area buffer remains undocked.");

            Check(Dock(new Rect(8, 4, 240, 180), one, out target) && target.Edge == DockEdge.Top,
                "A corner release chooses only the nearer of its two edges.");
            Check(Close(target.Bounds.Left, 84), "The along-edge position follows the opaque pony center.");
            Check(Dock(new Rect(2, 2, 50, 50), one, out target) && target.Edge == DockEdge.Left && Close(target.Bounds.Top, 64),
                "An exact corner tie has a stable single edge and reserves sixty-four DIP at the corner.");
            Check(Dock(new Rect(2, 1000, 30, 30), one, out target) && target.Edge == DockEdge.Left && Close(target.Bounds.Bottom, 976),
                "The far side corner also retains a sixty-four DIP gap.");
            Check(Dock(new Rect(1888, 2, 30, 30), one, out target) && target.Edge == DockEdge.Right && Close(target.Bounds.Top, 64),
                "The opposite upper corner remains reachable without occupying its corner.");
            EdgeDockTarget left = EdgeDockGeometry.Resolve(DockEdge.Left, "main", 0.25, one, false);
            EdgeDockTarget leftPeek = EdgeDockGeometry.Resolve(DockEdge.Left, "main", 0.25, one, true);
            Check(Close(leftPeek.Bounds.Left, left.Bounds.Left) && Close(leftPeek.Bounds.Top, left.Bounds.Top) &&
                Close(leftPeek.Bounds.Width - left.Bounds.Width, 12), "Left hover reveals twelve DIP inward without shifting its edge anchor.");
            EdgeDockTarget right = EdgeDockGeometry.Resolve(DockEdge.Right, "main", 0.5, one, false);
            EdgeDockTarget rightPeek = EdgeDockGeometry.Resolve(DockEdge.Right, "main", 0.5, one, true);
            Check(Close(rightPeek.Bounds.Right, right.Bounds.Right) && Close(right.Bounds.Left - rightPeek.Bounds.Left, 12),
                "Right hover grows toward the screen interior.");
            EdgeDockTarget top = EdgeDockGeometry.Resolve(DockEdge.Top, "main", 0.5, one, false);
            EdgeDockTarget topPeek = EdgeDockGeometry.Resolve(DockEdge.Top, "main", 0.5, one, true);
            Check(Close(topPeek.Bounds.Top, 0) && At(topPeek.Bounds, 916, 0, 88, 100),
                "Top hover grows downward while preserving horizontal alignment.");
            EdgeDockTarget bottom = EdgeDockGeometry.Resolve(DockEdge.Bottom, "main", 0.5, one, false);
            EdgeDockTarget bottomPeek = EdgeDockGeometry.Resolve(DockEdge.Bottom, "main", 0.5, one, true);
            Check(Close(bottomPeek.Bounds.Bottom, 1040) && Close(bottomPeek.Bounds.Height - bottom.Bounds.Height, 12),
                "Bottom hover grows upward and leaves the taskbar uncovered.");

            List<EdgeMonitor> joined = new List<EdgeMonitor> { main, Monitor("second", 1920, 0, 1920, 1080) };
            Check(!Dock(new Rect(1680, 400, 240, 180), joined, out target), "The right monitor seam is not a docking wall.");
            Check(!Dock(new Rect(1920, 400, 240, 180), joined, out target), "The same seam is suppressed from its other monitor.");
            Check(!EdgeDockGeometry.TryFindDock(new Rect(1800, 400, 240, 180), new Point(1920, 450), joined, out target),
                "A pony crossing a monitor seam cannot dock on either side of that seam.");
            Check(Dock(new Rect(3600, 400, 240, 180), joined, out target) && target.MonitorId == "second" && target.Edge == DockEdge.Right,
                "An external edge on a secondary monitor still docks normally.");
            Check(EdgeDockGeometry.TryFindDock(new Rect(3760, 400, 240, 180), new Point(3840, 450), joined, out target) &&
                target.MonitorId == "second" && target.Edge == DockEdge.Right,
                "A crossed external secondary-monitor edge docks despite the same overshoot that is rejected at its seam.");
            List<EdgeMonitor> partial = new List<EdgeMonitor> { Monitor("main", 0, 0, 1920, 1080), Monitor("upper-right", 1920, 0, 1280, 600) };
            Check(!Dock(new Rect(1680, 300, 240, 160), partial, out target), "A partial seam suppresses the overlapping section.");
            Check(Dock(new Rect(1680, 800, 240, 160), partial, out target) && target.Edge == DockEdge.Right,
                "The exposed section below a partial seam remains available.");
            List<EdgeMonitor> stacked = new List<EdgeMonitor> { Monitor("lower", 0, 0, 1920, 1080), Monitor("upper", 0, -1080, 1920, 1080) };
            Check(!Dock(new Rect(700, 0, 240, 180), stacked, out target), "Vertical monitor seams are suppressed as well.");
            List<EdgeMonitor> taskbarSide = new List<EdgeMonitor> {
                new EdgeMonitor("left", new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1880, 1080)), Monitor("right", 1920, 0, 1920, 1080) };
            Check(Dock(new Rect(1640, 400, 240, 180), taskbarSide, out target) && Close(target.Bounds.Right, 1880),
                "An inset taskbar boundary takes priority over a physical neighboring monitor.");
            Check(EdgeDockGeometry.Resolve(DockEdge.Right, "main", 0.5, joined, false) == null,
                "Restoring an old external edge after it becomes a seam safely rejects docking.");

            EdgeDockTarget saved = EdgeDockGeometry.Resolve(DockEdge.Top, "main", 0.75, one, false);
            List<EdgeMonitor> resized = new List<EdgeMonitor> { Monitor("main", -2560, -1440, 2560, 1440) };
            EdgeDockTarget restored = EdgeDockGeometry.Resolve(saved.Edge, saved.MonitorId, saved.Fraction, resized, false);
            Check(restored.MonitorId == "main" && Close(restored.Fraction, 0.75) && resized[0].Bounds.Contains(restored.Bounds),
                "Saved relative placement survives resolution, DPI-coordinate, and negative-origin changes.");
            List<EdgeMonitor> remaining = new List<EdgeMonitor> { Monitor("far", -1920, 0, 1920, 1080), Monitor("near", 0, 0, 1920, 1080) };
            restored = EdgeDockGeometry.Resolve(DockEdge.Bottom, "removed", 0.5, remaining, false, new Point(1500, 600));
            Check(restored.MonitorId == "near" && remaining[1].Bounds.Contains(restored.Bounds),
                "A removed monitor restores onto the nearest surviving screen instead of remaining offscreen.");
            Check(EdgeDockGeometry.Resolve(DockEdge.Left, "MAIN", -5, one, false).Fraction == 0 &&
                EdgeDockGeometry.Resolve(DockEdge.Left, "main", 5, one, false).Fraction == 1,
                "Persisted fractions are bounded and monitor identity is case insensitive.");
            Check(Close(EdgeDockGeometry.Resolve(DockEdge.Top, "main", Double.NaN, one, false).Fraction, 0.5),
                "A corrupt fraction restores at the safe center.");
            List<EdgeMonitor> tiny = new List<EdgeMonitor> { Monitor("tiny", 0, 0, 160, 160) };
            restored = EdgeDockGeometry.Resolve(DockEdge.Top, "tiny", 1, tiny, false);
            Check(At(restored.Bounds, 36, 0, 88, 88), "A tiny work area centers the pet instead of generating inverted corner limits.");
            Check(EdgeDockGeometry.Resolve(DockEdge.None, "main", 0, one, false) == null &&
                EdgeDockGeometry.Resolve((DockEdge)99, "main", 0, one, false) == null,
                "Invalid persisted edges do not create a dock window.");
            Check(EdgeDockGeometry.Resolve(DockEdge.Top, "main", 0.5, new List<EdgeMonitor>(), false) == null &&
                !Dock(Rect.Empty, one, out target), "Unavailable monitors and missing opaque bounds fail safely.");

            Rect expanded = EdgeDockGeometry.UndockBounds(left, new Size(304, 304), one);
            Check(At(expanded, 12, left.Bounds.Top + 44 - 152, 304, 304), "Clicking a left dock unfolds a full pet inside its work area.");
            expanded = EdgeDockGeometry.UndockBounds(top, new Size(304, 304), one);
            Check(Close(expanded.Top, 12) && main.WorkArea.Contains(expanded), "A top dock unfolds downward onto the screen.");
            expanded = EdgeDockGeometry.UndockBounds(bottom, new Size(304, 304), one);
            Check(Close(expanded.Bottom, 1028) && main.WorkArea.Contains(expanded), "A bottom dock unfolds above the taskbar.");
            expanded = EdgeDockGeometry.UndockBounds(right, new Size(304, 304), one);
            Check(Close(expanded.Right, 1908) && main.WorkArea.Contains(expanded), "A right dock unfolds leftward onto the screen.");
            Check(EdgeDockGeometry.UndockBounds(null, new Size(304, 304), one).IsEmpty,
                "An absent dock target cannot generate an offscreen expansion.");
            Check(EdgeDockGeometry.InwardDistance(DockEdge.Left, new Point(0, 0), new Point(64, 100)) == 64 &&
                EdgeDockGeometry.InwardDistance(DockEdge.Right, new Point(100, 0), new Point(36, 100)) == 64,
                "Horizontal inward dragging uses the signed edge normal and its sixty-four DIP threshold.");
            Check(EdgeDockGeometry.InwardDistance(DockEdge.Top, new Point(0, 0), new Point(100, 64)) == 64 &&
                EdgeDockGeometry.InwardDistance(DockEdge.Bottom, new Point(0, 100), new Point(100, 36)) == 64,
                "Vertical inward dragging also follows the edge normal.");
            Check(EdgeDockGeometry.InwardDistance(DockEdge.Left, new Point(0, 0), new Point(-100, 200)) < 0 &&
                EdgeDockGeometry.InwardDistance(DockEdge.Top, new Point(0, 0), new Point(200, 0)) == 0,
                "Dragging outward or only along an edge cannot be mistaken for an inward pull.");

            EdgeDockHoverController hover = new EdgeDockHoverController();
            hover.SetPointer(true, 1000);
            Check(!hover.Advance(1249) && !hover.IsPeeking, "A pass of less than 250 ms does not start a peek.");
            Check(hover.Advance(1250) && hover.IsPeeking && hover.PointerInside, "A stable 250 ms hover reveals the pet once.");
            Check(!hover.Advance(1500), "Continuing to hover does not restart the reveal animation.");
            hover.SetPointer(false, 2000);
            Check(!hover.Advance(2799) && hover.IsPeeking, "Leaving preserves the peek during the 800 ms grace period.");
            Check(hover.Advance(2800) && !hover.IsPeeking, "The pet retreats exactly after an 800 ms absence.");
            hover.SetPointer(true, 3000); hover.SetPointer(false, 3100);
            Check(!hover.Advance(4000) && !hover.IsPeeking, "Brief boundary crossings cancel a pending peek.");
            hover.SetPointer(true, 5000); hover.Advance(5250); hover.SetPointer(false, 5300); hover.SetPointer(true, 5600);
            Check(!hover.Advance(7000) && hover.IsPeeking, "Reentering during the leave grace period cancels the retreat.");
            hover.Reset(); hover.SetPointer(true, 10000); hover.SetPointer(true, 10100);
            Check(hover.Advance(10250) && hover.IsPeeking, "Repeated enter notifications do not postpone a genuine hover.");
            hover.Reset(); hover.SetPointer(true, 1000); hover.Advance(900);
            Check(!hover.IsPeeking && hover.Advance(1250), "A backwards clock sample cannot trigger an early reveal.");
            hover.Reset(); Check(!hover.IsPeeking && !hover.PointerInside && !hover.Advance(50000),
                "Undocking can reset every pending hover transition.");
            Console.WriteLine("Edge docking checks passed: " + count);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
