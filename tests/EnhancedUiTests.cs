using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RainbowDashDesktop;

internal static class EnhancedUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static object Field(object value, string name) { return value.GetType().GetField(name, Hidden).GetValue(value); }
    private static void Set(object value, string name, object field) { value.GetType().GetField(name, Hidden).SetValue(value, field); }
    private static object Call(object value, string name, params object[] args)
    {
        MethodInfo method = value.GetType().GetMethods(Hidden).First(m => m.Name == name && m.GetParameters().Length == args.Length);
        return method.Invoke(value, args);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); checks++; }
    private static void Near(double actual, double expected, string message)
    { Check(Math.Abs(actual - expected) <= 1, message + " actual=" + actual + " expected=" + expected); }
    [DllImport("user32.dll", EntryPoint = "GetWindowLong")] private static extern int GetWindowLong(IntPtr window, int index);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1) { Console.WriteLine("Pass assets directory."); return 2; }
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        string settings = Path.Combine(App.UserDirectory, "settings.json");
        string before = File.Exists(settings) ? File.ReadAllText(settings) : null;
        int result = 1;
        App app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs error)
        { Console.WriteLine("DISPATCH " + error.Exception.GetType().Name + " " + error.Exception.Message + "\n" + error.Exception.StackTrace); error.Handled = true; app.Shutdown(1); };
        PetWindow window = new PetWindow(new string[] { "--demo", "--smoke-test", "--assets-dir", args[0] }, null);
        window.Loaded += delegate
        {
            try
            {
                window.UpdateLayout();
                Console.WriteLine("Phase art");
                EnhancedArt art = (EnhancedArt)Field(window, "enhancedArt");
                Check(art.Complete, "All sixty newly authored enhanced cels load.");
                foreach (string name in EnhancedArt.Names)
                {
                    Check(art.Has(name), "Six-frame clip available: " + name);
                    for (int i = 0; i < 6; i++) Check(art.Get(name, i) != null, name + " frame " + i + " available");
                }
                Check(Field(window, "monitor") == null, "UI regression starts no account worker.");
                EnhancedAction action = (EnhancedAction)Field(window, "enhancedAction");
                Console.WriteLine("Phase bridge");
                NaturalPlaybackController playback = (NaturalPlaybackController)Field(window, "playback");
                Call(window, "ApplyState", "working", true);
                Call(window, "ApplyState", "completed", false);
                Check(action.Name == "close-book" && action.PendingState == "completed", "Work completion starts authored book-closing bridge.");
                Check(playback.VisualState == "working", "Completion celebration clock waits for book closing.");
                long started = action.StartedAt;
                Call(window, "ApplyState", "completed", false);
                Check(action.StartedAt == started, "Repeated task polling does not restart closing.");
                action.Start("close-book", -10000, 840, "completed", false);
                Call(window, "AdvanceEnhanced");
                Check(!action.Active && playback.VisualState == "completed", "Bridge ends into a complete celebration from its start.");
                Check(playback.Sample(((System.Diagnostics.Stopwatch)Field(window, "interactionClock")).ElapsedMilliseconds, false) == 0,
                    "The celebration begins at cel zero rather than skipping its first half.");
                Call(window, "ApplyState", "working", true);
                Call(window, "ApplyState", "interrupted", false);
                Check(action.Name == "interrupt-book", "Work interruption first stops the page and turns an ear.");
                Call(window, "ApplyState", "received", false);
                Check(!action.Active && playback.VisualState == "received", "A new task immediately preempts old terminal bridge.");
                Call(window, "ApplyState", "idle", true);
                Console.WriteLine("Phase personality");
                foreach (string name in new string[] { "hair", "preen", "proud" })
                {
                    Call(window, "StartEnhancedAction", name, false);
                    Check(action.Name == name && ((Image)Field(window, "enhancedPony")).Visibility == Visibility.Visible, name + " can be played explicitly.");
                    Call(window, "ApplyState", "working", false);
                    Check(!action.Active, "Task activity preempts " + name);
                    Call(window, "ApplyState", "idle", true);
                }
                Call(window, "StartEnhancedAction", "hair", false);
                Call(window, "CancelEnhancedAction", true); Call(window, "AdvanceEnhanced");
                Check(((Image)Field(window, "enhancedPony")).Visibility == Visibility.Visible, "An outgoing action has a brief authored-pose crossfade.");
                Set(window, "overlayEndingAt", -10000L); Call(window, "AdvanceEnhanced");
                Check(((Image)Field(window, "restPony")).Visibility == Visibility.Visible && ((Image)Field(window, "pony")).Opacity == 0,
                    "Natural standing art is restored after a visual action.");
                EnhancedAction rhythm = new EnhancedAction();
                rhythm.Start("hair", 0, 1200, null, false);
                Check(rhythm.Frame(219) == 0 && rhythm.Frame(220) == 1 && rhythm.Frame(320) == 2 && rhythm.Frame(420) == 3,
                    "Hair flick middle cels move in 100 ms beats after a longer anticipation.");
                rhythm.Start("proud", 0, 1200, null, false);
                Check(rhythm.Frame(460) == 3 && rhythm.Frame(959) == 3 && rhythm.Frame(960) == 4,
                    "Crossed-hoof proud endpoint holds for 500 ms.");
                rhythm.Start("preen", 0, 1500, null, false);
                Check(rhythm.Frame(410) == 2 && rhythm.Frame(709) == 2 && rhythm.Frame(710) == 3,
                    "Feather-grooming gesture holds its slow central 300 ms cel.");

                var monitors = (System.Collections.Generic.IList<EdgeMonitor>)Call(window, "GetEdgeMonitors");
                Image dockImage = (Image)Field(window, "dockActionPony");
                QuotaRing quota = (QuotaRing)Field(window, "quotaRing");
                foreach (DockEdge edge in new DockEdge[] { DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom })
                {
                    Console.WriteLine("Phase dock " + edge);
                    EdgeDockTarget target = EdgeDockGeometry.Resolve(edge, "", .5, monitors, false);
                    if (target == null) continue;
                    Call(window, "EnterDock", target, false); window.UpdateLayout();
                    string name = edge == DockEdge.Top ? "dock-top" : edge == DockEdge.Bottom ? "dock-bottom" : "dock-side";
                    double width = -1, height = -1;
                    for (int frame = 0; frame < 6; frame++)
                    {
                        Call(window, "ShowDockAction", name, frame, edge);
                        if (frame == 0) { width = dockImage.Width; height = dockImage.Height; }
                        Near(dockImage.Width, width, edge + " authored entry keeps one scale for every cel");
                        Near(dockImage.Height, height, edge + " does not fit each new body bbox separately");
                    }
                    Check(quota.Root.Visibility == Visibility.Collapsed, edge + " quota stays hidden throughout authored dock action.");
                    Call(window, "ExpandDockAnimated", false);
                    Check((bool)Field(window, "dockLeaving"), edge + " exit starts authored reverse animation.");
                    Set(window, "dockLeavingAt", -10000L); Call(window, "AdvanceEdge");
                    Check(Field(window, "dockTarget") == null, edge + " exit ends in true current task view.");
                    Near(window.Width, 304, edge + " exit restores normal native window size");
                }

                Call(window, "ApplyState", "working", true); Set(window, "restExitAt", 0L); Call(window, "AdvanceEnhanced"); window.UpdateLayout();
                Console.WriteLine("Phase alpha");
                Check(!(bool)Call(window, "IsInteractiveAt", new Point(1, 1)), "Transparent corner is clickable through.");
                FrameworkElement status = (FrameworkElement)Field(window, "statusPill");
                Point pill = status.TransformToAncestor(window).Transform(new Point(status.ActualWidth / 2, status.ActualHeight / 2));
                Check((bool)Call(window, "IsInteractiveAt", pill), "Status controls remain interactive.");
                Point ring = quota.Root.TransformToAncestor(window).Transform(new Point(18, 18));
                Check((bool)Call(window, "IsInteractiveAt", ring), "Quota refresh control remains interactive.");
                Image pony = (Image)Field(window, "spriteImage");
                bool foundOpaque = false;
                for (int y = 0; y < 20 && !foundOpaque; y++) for (int x = 0; x < 20 && !foundOpaque; x++)
                {
                    Point local = new Point((x + .5) * pony.ActualWidth / 20, (y + .5) * pony.ActualHeight / 20);
                    Point location = pony.TransformToAncestor(window).Transform(local);
                    if ((bool)Call(window, "ImageContains", pony, location))
                    { Check((bool)Call(window, "IsInteractiveAt", location), "Opaque pony pixels remain draggable."); foundOpaque = true; }
                }
                Check(foundOpaque, "Actual source art has hittable opaque pixels.");
                IntPtr handle = new WindowInteropHelper(window).Handle;
                int styles = GetWindowLong(handle, -20);
                Check(DesktopInterop.SetClickThrough(handle, true) && (GetWindowLong(handle, -20) & 0x20) != 0,
                    "Cross-process transparent-area pass through is implemented in native style.");
                Check(DesktopInterop.SetClickThrough(handle, false) && (GetWindowLong(handle, -20) & 0x20) == 0,
                    "Entering the pony restores native mouse interaction.");
                Check((GetWindowLong(handle, -20) & ~0x20) == (styles & ~0x20), "Pass through preserves every unrelated extended style.");

                Rect original = new Rect(window.Left, window.Top, window.Width, window.Height);
                Console.WriteLine("Phase focus");
                Call(window, "SetManualFocus", true);
                Check(((FocusController)Field(window, "focus")).IsActive && Field(window, "dockTarget") != null,
                    "Manual focus quietly tucks the pet to an external edge.");
                Check(quota.Root.Visibility == Visibility.Collapsed, "Focus mode never shows quota ring.");
                Check(Field(window, "focusReturn") != null, "Focus remembers the user's prior placement.");
                Call(window, "SetManualFocus", false);
                Check(Field(window, "dockTarget") == null, "Leaving focus restores a previously expanded pet.");
                Near(window.Left, original.Left, "Focus restores original X"); Near(window.Top, original.Top, "Focus restores original Y");
                EdgeDockTarget right = EdgeDockGeometry.Resolve(DockEdge.Right, "", .4, monitors, false);
                if (right != null)
                {
                    Call(window, "EnterDock", right, false); Call(window, "SetManualFocus", true); Call(window, "SetManualFocus", false);
                    Check(((EdgeDockTarget)Field(window, "dockTarget")).Edge == DockEdge.Right, "A previously docked pet remains docked after focus.");
                    Call(window, "ExpandDock", false);
                }
                Check((File.Exists(settings) ? File.ReadAllText(settings) : null) == before, "Enhanced tests do not alter user settings.");
                Console.WriteLine("PASS " + checks + " enhanced UI assertions"); result = 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL " + error.GetType().Name + " " + error.Message); Console.WriteLine(error.StackTrace); if (error.InnerException != null) Console.WriteLine(error.InnerException.GetType().Name + " " + error.InnerException.Message + "\n" + error.InnerException.StackTrace); }
            finally { window.Close(); app.Shutdown(result); }
        };
        app.Run(window); return result;
    }
}
