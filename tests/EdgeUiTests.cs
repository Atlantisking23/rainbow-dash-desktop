using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RainbowDashDesktop;

internal static class EdgeUiTests
{
    private static int checks;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(object obj, string name) { return obj.GetType().GetField(name, Private).GetValue(obj); }
    private static void Set(object obj, string name, object value) { obj.GetType().GetField(name, Private).SetValue(obj, value); }
    private static object Call(object obj, string name, params object[] args) { return obj.GetType().GetMethod(name, Private).Invoke(obj, args); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); checks++; }
    private static void Near(double actual, double expected, string message)
    { Check(Math.Abs(actual - expected) <= 1, message + " expected=" + expected + " actual=" + actual); }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    private static Size NativeSize(Window window)
    {
        NativeRect rect;
        Check(GetWindowRect(new System.Windows.Interop.WindowInteropHelper(window).Handle, out rect),
            "The test reads its own native window rectangle");
        Matrix deviceToDip = PresentationSource.FromVisual(window).CompositionTarget.TransformFromDevice;
        return new Size((rect.Right - rect.Left) * deviceToDip.M11, (rect.Bottom - rect.Top) * deviceToDip.M22);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1) { Console.WriteLine("Pass the assets directory."); return 2; }
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        string settingsFile = Path.Combine(App.UserDirectory, "settings.json");
        string before = File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : null;
        int result = 1;
        App app = new App(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        PetWindow window = new PetWindow(new string[] { "--demo", "--smoke-test", "--assets-dir", args[0] }, null);
        window.Loaded += delegate
        {
            try
            {
                Check(((EdgeArt)Field(window, "edgeArt")).Complete, "All 27 authored edge cels load.");
                Check(Field(window, "monitor") == null, "Render and demo modes start no account worker.");
                IList<EdgeMonitor> monitors = (IList<EdgeMonitor>)Call(window, "GetEdgeMonitors");
                UiSettings settings = (UiSettings)Field(window, "settings");
                Grid root = (Grid)Field(window, "root");
                Canvas edgeCanvas = (Canvas)Field(window, "edgeCanvas");
                StackPanel normal = (StackPanel)Field(window, "normalStack");
                Image image = (Image)Field(window, "edgePony");
                QuotaRing quota = (QuotaRing)Field(window, "quotaRing");
                settings.ReduceMotion = true;
                foreach (DockEdge edge in new DockEdge[] { DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom })
                {
                    EdgeDockTarget target = EdgeDockGeometry.Resolve(edge, "", .5, monitors, false);
                    if (target == null) continue;
                    Call(window, "EnterDock", target, false); window.UpdateLayout();
                    Size compact = EdgeDockGeometry.CompactSize(edge);
                    Near(window.Width, compact.Width, edge + " real native width is compact");
                    Near(window.Height, compact.Height, edge + " real native height is compact");
                    Size native = NativeSize(window);
                    Near(native.Width, compact.Width, edge + " HWND width has no invisible full pony hit area");
                    Near(native.Height, compact.Height, edge + " HWND height is compact in physical pixels");
                    Check(normal.Visibility == Visibility.Collapsed && edgeCanvas.Visibility == Visibility.Visible,
                        edge + " full pony does not occupy a hidden full-size hit area");
                    Check(Object.ReferenceEquals(quota.Root.Parent, edgeCanvas), edge + " quota is reparented safely");
                    Check(quota.Root.Visibility == Visibility.Collapsed, edge + " quota hidden while tucked");
                    Check(image.Source != null, edge + " real pose is displayed");
                    if (edge == DockEdge.Top)
                        Check(image.RenderTransform == Transform.Identity, "Top pose is kept front-facing upside down without another rotation");
                    EdgeDockHoverController hover = (EdgeDockHoverController)Field(window, "edgeHover");
                    hover.Reset(); hover.SetPointer(true, 0); hover.Advance(250); Call(window, "LayoutEdge"); window.UpdateLayout();
                    Near(window.Width, compact.Width + (edge == DockEdge.Left || edge == DockEdge.Right ? 12 : 0),
                        edge + " hover adds only 12 DIP inward without quota space");
                    Near(window.Height, compact.Height + (edge == DockEdge.Top || edge == DockEdge.Bottom ? 12 : 0),
                        edge + " hover is 12 DIP deeper");
                    native = NativeSize(window);
                    Near(native.Width, compact.Width + (edge == DockEdge.Left || edge == DockEdge.Right ? 12 : 0),
                        edge + " hovered HWND width includes only the inward peek");
                    Near(native.Height, compact.Height + (edge == DockEdge.Top || edge == DockEdge.Bottom ? 12 : 0),
                        edge + " hovered HWND height includes only the inward peek");
                    Check(quota.Root.Visibility == Visibility.Collapsed, edge + " quota stays hidden throughout hover");
                    Check(!settings.QuotaCollapsed, edge + " docking does not change the normal quota preference");
                    settings.QuotaCollapsed = true; Call(window, "ApplyScaleAndCollapse");
                    Check(quota.Root.Visibility == Visibility.Collapsed, edge + " explicit quota hide remains honored");
                    settings.QuotaCollapsed = false;
                    Call(window, "ExpandDock", false); window.UpdateLayout();
                    Near(window.Width, 304, edge + " click/menu expansion restores native full width");
                    Near(window.Height, 242, edge + " expansion restores full height");
                    Check(normal.Visibility == Visibility.Visible && edgeCanvas.Visibility == Visibility.Collapsed,
                        edge + " normal animation returns");
                    Check(!Object.ReferenceEquals(quota.Root.Parent, edgeCanvas), edge + " quota returns to normal parent");
                    Check(quota.Root.Visibility == Visibility.Visible, edge + " expansion restores the user's visible quota preference");
                    settings.QuotaCollapsed = true; Call(window, "ApplyScaleAndCollapse");
                    Call(window, "EnterDock", target, false);
                    hover.Reset(); hover.SetPointer(true, 0); hover.Advance(250); Call(window, "LayoutEdge");
                    Check(quota.Root.Visibility == Visibility.Collapsed, edge + " hidden preference remains hidden while peeking");
                    Call(window, "ExpandDock", false); window.UpdateLayout();
                    Check(quota.Root.Visibility == Visibility.Collapsed && settings.QuotaCollapsed,
                        edge + " expansion also preserves the user's hidden quota preference");
                    settings.QuotaCollapsed = false; Call(window, "ApplyScaleAndCollapse");
                }
                settings.Scale = 1.25; Call(window, "ApplyScaleAndCollapse"); window.UpdateLayout();
                Near(window.Width, 380, "Changing scale after an expansion resizes the actual native window");
                Near(window.Height, 302.5, "Full height follows user scale after manual compact sizing");
                EdgeDockTarget scaledSide = EdgeDockGeometry.Resolve(DockEdge.Right, "", .5, monitors, false);
                if (scaledSide != null)
                {
                    Call(window, "EnterDock", scaledSide, false); window.UpdateLayout();
                    Size native = NativeSize(window);
                    Near(native.Width, 105, "Side compact width follows 125% pet scale");
                    Near(native.Height, 140, "Side compact height follows 125% pet scale");
                    Near(image.Width, 120, "Side artwork follows 125% pet scale");
                    Call(window, "ExpandDock", false); window.UpdateLayout();
                }
                settings.ReduceMotion = false;
                settings.Scale = 1; Call(window, "ApplyScaleAndCollapse"); window.UpdateLayout();
                Image pony = (Image)Field(window, "pony");
                foreach (double scale in new double[] { 1.25, 1.5 })
                {
                    settings.Scale = scale; Call(window, "ApplyScaleAndCollapse"); window.UpdateLayout();
                    Rect actual = (Rect)Call(window, "VisiblePetBounds");
                    Rect local = OpaqueLocalBounds(pony);
                    Point first = pony.PointToScreen(local.TopLeft), last = pony.PointToScreen(local.BottomRight);
                    PresentationSource source = PresentationSource.FromVisual(window);
                    first = source.CompositionTarget.TransformFromDevice.Transform(first);
                    last = source.CompositionTarget.TransformFromDevice.Transform(last);
                    Rect viaScreen = new Rect(first, last);
                    Near(actual.Left, viaScreen.Left, scale + " opaque X follows actual screen DIPs");
                    Near(actual.Top, viaScreen.Top, scale + " opaque Y follows actual screen DIPs");
                    Near(actual.Width, viaScreen.Width, scale + " opaque width includes user scale");
                    Near(actual.Height, viaScreen.Height, scale + " opaque height includes user scale");
                }
                settings.Scale = 1; Call(window, "ApplyScaleAndCollapse"); window.UpdateLayout();
                // Exercise the actual release integration using the real rendered pony
                // alpha bounds, including an overshoot caused by holding its center.
                foreach (DockEdge edge in new DockEdge[] { DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom })
                {
                    EdgeDockTarget target = EdgeDockGeometry.Resolve(edge, monitors[0].Id, .5, monitors, false);
                    if (target == null) continue;
                    Rect work = monitors[0].WorkArea;
                    window.Left = work.Left + work.Width / 2 - window.Width / 2;
                    window.Top = work.Top + work.Height / 2 - window.Height / 2;
                    Rect visible = (Rect)Call(window, "VisiblePetBounds");
                    Point release;
                    if (edge == DockEdge.Left)
                    { window.Left += work.Left - visible.Left - 40; release = new Point(work.Left, work.Top + work.Height / 2); }
                    else if (edge == DockEdge.Right)
                    { window.Left += work.Right - visible.Right + 40; release = new Point(work.Right - 1, work.Top + work.Height / 2); }
                    else if (edge == DockEdge.Top)
                    { window.Top += work.Top - visible.Top - 40; release = new Point(work.Left + work.Width / 2, work.Top); }
                    else
                    { window.Top += work.Bottom - visible.Bottom + 40; release = new Point(work.Left + work.Width / 2, work.Bottom - 1); }
                    DragInteractionController drag = (DragInteractionController)Field(window, "drag");
                    drag.Begin(new Point(0, 0), new Point(window.Left, window.Top), false);
                    drag.Move(new Point(20, 20), 4, 4);
                    Set(window, "lastPointerPosition", release);
                    bool isSide = edge == DockEdge.Left || edge == DockEdge.Right;
                    window.UpdateLayout();
                    Rect preDock = (Rect)Call(window, "VisiblePetBounds");
                    Call(window, "FinishPointerGesture", true);
                    Check(Field(window, "dockTarget") != null, edge + " real release docks when held pony crosses the border");
                    Check((bool)Field(window, "dockTransition"), edge + " released sprint enters the 600 ms transition");
                    long start = (long)Field(window, "dockTransitionAt");
                    if (isSide)
                    {
                        Set(window, "dockTransitionAt", start + 250); Call(window, "AdvanceEdge"); window.UpdateLayout();
                        Rect first = (Rect)Call(window, "VisiblePetBounds");
                        Near(first.Left, preDock.Left, edge + " entrance starts at the visible normal pose");
                        Near(first.Width, preDock.Width, edge + " entrance starts without shrinking the normal pose");
                    }
                    Set(window, "dockTransitionAt", start - 300); Call(window, "AdvanceEdge");
                    Check(window.Width >= target.Bounds.Width && window.Width <= 304, edge + " actual native hit area shrinks during entrance");
                    Set(window, "dockTransitionAt", start - 700); Call(window, "AdvanceEdge");
                    Near(window.Width, target.Bounds.Width, edge + " entrance settles at compact width");
                    Check(!(bool)Field(window, "dockTransition"), edge + " transition completes once");
                    if (isSide) CheckSideExit(window, edge);
                    else { Call(window, "ExpandDock", false); window.UpdateLayout(); }
                }
                EdgeExpressionController expression = new EdgeExpressionController();
                Check(expression.Sample("received", 0) == 1 && expression.Sample("received", 1200) == 0, "Receipt reaction is brief");
                Check(expression.Sample("completed", 2000) == 6 && expression.Sample("completed", 4000) == 0, "Proud smile settles naturally");
                Check(expression.Sample("working", 5000) == 2, "Work expression is focused");
                Check(expression.Sample("working", 8600) == 3 && expression.Sample("working", 8650) == 4 &&
                    expression.Sample("working", 8720) == 5 && expression.Sample("working", 8800) == 2, "Blink uses half/closed/open cels then returns to focus");
                EdgeExpressionController side = new EdgeExpressionController();
                Check(side.Sample("idle", 0, false, DockEdge.Left) == 1, "Side idle starts with the curious, open-eyed pose");
                Check(side.Sample("idle", 3600, false, DockEdge.Left) == 3 && side.Sample("idle", 3650, false, DockEdge.Left) == 4 &&
                    side.Sample("idle", 3720, false, DockEdge.Left) == 3 && side.Sample("idle", 3800, false, DockEdge.Left) == 1,
                    "Side idle blink closes and opens on the same gaze");
                bool awakeDuringSideIdle = true;
                for (long time = 3820; time <= 28000; time += 20)
                    awakeDuringSideIdle &= side.Sample("idle", time, false, DockEdge.Left) != 8;
                Check(awakeDuringSideIdle, "Side idle never substitutes the sleep pose");
                Check(side.Sample("working", 29000, false, DockEdge.Right) == 2 &&
                    side.Sample("working", 32600, false, DockEdge.Right) == 5 &&
                    side.Sample("working", 32720, false, DockEdge.Right) == 5,
                    "Side work blink keeps the focused gaze");
                Check(side.Sample("completed", 33000, false, DockEdge.Right) == 6 &&
                    side.Sample("completed", 34900, false, DockEdge.Right) == 1, "Side completion smiles before returning to curious idle");
                Check(side.Sample("waiting", 35000, false, DockEdge.Right) == 7 &&
                    side.Sample("waiting", 40000, false, DockEdge.Right) == 7 &&
                    side.Sample("error", 41000, true, DockEdge.Right) == 7 &&
                    side.Sample("idle", 42000, true, DockEdge.Right) == 1, "Side attention lasts until the task state changes");
                Check((File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : null) == before,
                    "UI test and preview modes leave user settings untouched");
                Console.WriteLine("PASS " + checks + " edge UI checks"); result = 0;
            }
            catch (Exception ex) { Console.WriteLine(ex); }
            finally { window.Close(); app.Shutdown(result); }
        };
        app.Run(window);
        return result;
    }

    private static void CheckSideExit(PetWindow window, DockEdge edge)
    {
        window.UpdateLayout();
        Rect compact = (Rect)Call(window, "VisiblePetBounds");
        Call(window, "ExpandDockAnimated", false); window.UpdateLayout();
        Rect outgoing = (Rect)Call(window, "VisiblePetBounds");
        Check(!compact.IsEmpty && compact.Width > 0 && compact.Height > 0, edge + " compact sprite contains visible art");
        Near(outgoing.X, compact.X, edge + " exit starts at the currently visible side artwork");
        Near(outgoing.Width, compact.Width, edge + " exit starts without resizing the side artwork");
        long start = (long)Field(window, "dockLeavingAt");
        Call(window, "AdvanceDockExit", start + 570L); window.UpdateLayout();
        Rect before = (Rect)Call(window, "VisiblePetBounds");
        Call(window, "AdvanceDockExit", start + 600L); window.UpdateLayout();
        Rect after = (Rect)Call(window, "VisiblePetBounds");
        Check(Field(window, "dockTarget") == null && after.Width > 0, edge + " shows the complete normal sprite when it finishes expanding");
        Check(Math.Abs(before.Left - after.Left) <= 12 && Math.Abs(before.Right - after.Right) <= 12,
            edge + " last exit frames meet normal artwork without clipping or a horizontal jump. Before=" + before + " after=" + after);
    }

    private static Rect OpaqueLocalBounds(Image image)
    {
        System.Windows.Media.Imaging.BitmapSource bitmap = (System.Windows.Media.Imaging.BitmapSource)image.Source;
        System.Windows.Media.Imaging.FormatConvertedBitmap rgba = new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int width = rgba.PixelWidth, height = rgba.PixelHeight, stride = width * 4;
        byte[] pixels = new byte[stride * height]; rgba.CopyPixels(pixels, stride, 0);
        int x0 = width, y0 = height, x1 = -1, y1 = -1;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            if (pixels[y * stride + x * 4 + 3] > 32)
            { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        return new Rect((double)x0 / width * image.ActualWidth, (double)y0 / height * image.ActualHeight,
            (double)(x1 - x0 + 1) / width * image.ActualWidth, (double)(y1 - y0 + 1) / height * image.ActualHeight);
    }
}
