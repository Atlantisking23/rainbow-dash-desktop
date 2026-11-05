using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RainbowDashDesktop;

internal static class CompanionPoliciesTests
{
    private static int count;
    private static void Check(bool condition, string message)
    { count++; if (!condition) throw new Exception(message); }
    private static BitmapSource Image(int width, int height, params int[] visible)
    {
        byte[] pixels = new byte[width * height * 4];
        foreach (int index in visible) pixels[index * 4 + 3] = 255;
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze(); return image;
    }
    [STAThread]
    public static int Main()
    {
        try
        {
            FocusController focus = new FocusController();
            Check(!focus.Update(false, true, 0) && !focus.IsActive, "Ordinary desktop starts inactive.");
            Check(!focus.Update(true, true, 100), "Fullscreen entry has a debounce.");
            Check(!focus.Update(true, true, 1099), "A short full-screen candidate cannot dock the pet.");
            Check(focus.Update(true, true, 1100) && focus.AutomaticActive && focus.IsActive, "Stable fullscreen enters once after one second.");
            Check(!focus.Update(true, true, 1200), "Stable fullscreen does not replay focus entry.");
            Check(!focus.Update(false, true, 1300), "Fullscreen exit is delayed.");
            Check(!focus.Update(true, true, 1700) && focus.IsActive, "Transient focus switch does not expand the pet.");
            focus.Update(false, true, 2000);
            Check(!focus.Update(false, true, 3499), "Exit remains debounced until 1.5 seconds.");
            Check(focus.Update(false, true, 3500) && !focus.IsActive, "Leaving fullscreen exits once.");
            focus.SetManual(true);
            Check(focus.IsActive && focus.Update(false, true, 4000), "Manual focus takes effect and produces an entry edge.");
            focus.Update(true, true, 4100); focus.Update(true, true, 5100);
            focus.Update(false, true, 5200);
            Check(!focus.Update(false, true, 6700) && focus.ManualEnabled && focus.IsActive && !focus.AutomaticActive,
                "Automatic exit cannot disable manual focus.");
            focus.SetManual(false);
            Check(focus.Update(false, true, 6800) && !focus.IsActive, "Disabling manual mode exits when automatic mode is off.");
            focus.Update(true, true, 7000); focus.Update(true, true, 8000);
            focus.SuppressAutomaticUntilClear();
            Check(focus.Update(true, true, 8100) && !focus.IsActive, "A deliberate user override releases automatic ownership.");
            Check(!focus.Update(true, true, 12000) && !focus.IsActive, "Same full-screen session cannot repeatedly take control back.");
            focus.Update(false, true, 12100); focus.Update(true, true, 12300);
            Check(!focus.Update(true, true, 14000) && !focus.IsActive, "A brief false probe does not clear override suppression.");
            focus.Update(false, true, 15000); focus.Update(false, true, 16500);
            focus.Update(true, true, 16600);
            Check(focus.Update(true, true, 17600) && focus.IsActive, "New fullscreen session can re-arm after a real exit.");
            Check(focus.Update(true, false, 17700) && !focus.AutomaticActive, "Disabling automatic focus cancels it immediately.");
            focus.SetManual(true); focus.Update(true, false, 18000);
            Check(!focus.Update(false, false, 19000) && focus.IsActive, "Automatic toggle does not erase manual preference.");

            GazeController gaze = new GazeController();
            Check(gaze.Sample(true, 151, -40, 0) == -1, "Distant mouse does not attract attention.");
            Check(gaze.Sample(true, 80, 0, 1) == -1, "Centre dead zone does not force an arbitrary direction.");
            Check(gaze.Sample(true, 80, -50, 10) == 1, "Left approach moves eyes first.");
            Check(gaze.Sample(true, 80, -50, 189) == 1 && gaze.Sample(true, 80, -50, 190) == 2, "Head follows the glance after 180 milliseconds.");
            Check(gaze.Sample(true, 80, 100, 300) == 2, "Crossing the cursor cannot make an active glance swivel constantly.");
            Check(gaze.Sample(true, 80, -50, 2510) == 5, "Attention has a bounded hold and returns naturally.");
            Check(gaze.Sample(true, 80, -50, 2670) == -1, "Return cel yields to normal playback.");
            Check(gaze.Sample(true, 80, 50, 10000) == -1, "Gaze cooldown prevents repeated interruption.");
            Check(gaze.Sample(true, 80, 50, 10670) == 3 && gaze.Sample(true, 80, 50, 10850) == 4, "Right approach uses matching eye-then-head poses after cooldown.");
            Check(gaze.Sample(false, 80, 50, 11000) == -1, "A task, dock, drag or focus condition immediately preempts a glance.");
            Check(gaze.Sample(true, 80, 50, 11001) == -1, "Preemption starts a cooldown rather than immediately reappearing.");
            gaze.Reset(20000);
            Check(gaze.Sample(true, 80, -50, 27999) == -1 && gaze.Sample(true, 80, -50, 28000) == 1, "Explicit reset clears attention and observes full cooldown.");
            Check(gaze.Sample(true, 176, -50, 28100) == 5, "Moving away begins a return instead of tracking from afar.");
            Check(gaze.Sample(true, Double.NaN, -50, 28110) == -1, "Invalid cursor coordinates cancel safely.");
            Check(gaze.Sample(true, 80, Double.PositiveInfinity, 50000) == -1, "Non-finite direction cannot start a pose.");
            GazeController monotonic = new GazeController(); monotonic.Sample(true, 20, 30, 1000);
            Check(monotonic.Sample(true, 20, 30, 500) == 3 && monotonic.Sample(true, 20, 30, 1180) == 4,
                "A clock moving backwards cannot skip or rewind the attention phase.");

            QuietPersonalityScheduler quiet = new QuietPersonalityScheduler(123);
            Check(quiet.Sample(true, 0) == null && quiet.Sample(true, 24999) == null, "Idle personality waits at least 25 seconds.");
            string first = quiet.Sample(true, 40001);
            Check(first == "hair" || first == "preen" || first == "proud", "First idle action occurs within its 40 second upper bound.");
            Check(quiet.Sample(true, 40002) == null && quiet.Sample(true, 85000) == null, "Long cooldown avoids repeatedly cycling poses.");
            string second = quiet.Sample(true, 130002);
            Check(second != null && second != first, "A later eligible action varies rather than immediately repeating.");
            Check(quiet.Sample(false, 200000) == null && quiet.Sample(true, 200001) == null && quiet.Sample(true, 225000) == null,
                "Task/focus/drag interruption restarts a calm settling period.");
            Check(quiet.Sample(true, 240002) != null, "Idle eventually resumes after interruption.");
            quiet.Postpone(300000);
            Check(quiet.Sample(true, 344999) == null && quiet.Sample(true, 390001) != null, "Manual action postpones automatic repeats for 45 to 90 seconds.");

            Rect monitor = new Rect(0, 0, 1920, 1080), work = new Rect(0, 0, 1920, 1040);
            Check(DesktopInterop.IsFullscreenCandidate(monitor, monitor, work, "Chrome_WidgetWin_1", false, 0), "Borderless fullscreen qualifies.");
            Check(DesktopInterop.IsFullscreenCandidate(new Rect(1, 1, 1918, 1078), monitor, work, "GameWindow", false, 0), "Two-pixel geometry tolerance accepts real fullscreen rounding.");
            Check(!DesktopInterop.IsFullscreenCandidate(work, monitor, work, "Chrome_WidgetWin_1", true, 0), "Work-area maximize does not cover the display.");
            Check(!DesktopInterop.IsFullscreenCandidate(monitor, monitor, monitor, "Chrome_WidgetWin_1", true, 0x00C00000), "Captioned maximize is excluded even if the taskbar auto-hides.");
            Check(!DesktopInterop.IsFullscreenCandidate(monitor, monitor, monitor, "AnyWindow", false, 0x00040000), "Decorated resizable work windows are excluded.");
            foreach (string name in new string[] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
                Check(!DesktopInterop.IsFullscreenCandidate(monitor, monitor, work, name, false, 0), "Shell class cannot force automatic focus: " + name);
            Check(!DesktopInterop.IsFullscreenCandidate(Rect.Empty, monitor, work, "Game", false, 0), "Empty foreground rectangle is ignored.");
            Rect negative = new Rect(-1280, -100, 1280, 1024);
            Check(DesktopInterop.IsFullscreenCandidate(negative, negative, negative, "Game", false, 0), "Fullscreen geometry supports negative-coordinate monitors.");

            BitmapSource mask = Image(11, 11, 5 * 11 + 5);
            Check(AlphaMaskCache.Hit(mask, .5, .5), "Opaque image interior remains draggable.");
            Check(AlphaMaskCache.Hit(mask, 3.5 / 11, 3.5 / 11), "Small antialias safety halo retains nearby edge interaction.");
            Check(!AlphaMaskCache.Hit(mask, 2.5 / 11, 5.5 / 11), "Halo does not swallow wider transparent space.");
            Check(!AlphaMaskCache.Hit(mask, 0, 0), "Transparent blank corner is click-through.");
            Check(!AlphaMaskCache.Hit(mask, 1, .5) && !AlphaMaskCache.Hit(mask, -.01, .5) && !AlphaMaskCache.Hit(mask, .5, Double.NaN), "Invalid normalized bounds never address pixel memory.");
            Check(!AlphaMaskCache.Hit(null, .5, .5), "Missing image does not block the desktop.");
            Check(!AlphaMaskCache.Hit(Image(10, 10), .5, .5), "Fully transparent image stays transparent.");
            byte[] lowAlpha = new byte[11 * 11 * 4]; lowAlpha[(5 * 11 + 5) * 4 + 3] = 23;
            BitmapSource faint = BitmapSource.Create(11, 11, 96, 96, PixelFormats.Bgra32, null, lowAlpha, 44); faint.Freeze();
            Check(!AlphaMaskCache.Hit(faint, .5, .5), "Faint alpha noise below threshold does not intercept clicks.");
            Check(AlphaMaskCache.Hit(mask, .5, .5) && !AlphaMaskCache.Hit(mask, .01, .01), "Repeated cached hit checks preserve geometry.");

            // Native mutation is limited to this newly created test HWND; no
            // foreground application is activated, modified, clicked or inspected.
            using (HwndSource own = new HwndSource(new HwndSourceParameters("RainbowDash policy test") { Width = 20, Height = 20, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080, UsesPerPixelOpacity = true }))
            {
                Check(DesktopInterop.SetClickThrough(own.Handle, true) && DesktopInterop.IsClickThrough(own.Handle), "Own layered-window input style can enable pass-through.");
                Check(DesktopInterop.SetClickThrough(own.Handle, true), "Enabling an unchanged flag is idempotent.");
                Check(DesktopInterop.SetClickThrough(own.Handle, false) && !DesktopInterop.IsClickThrough(own.Handle), "Returning over opaque art can restore own input.");
            }
            Check(!DesktopInterop.SetClickThrough(IntPtr.Zero, true), "Null handle cannot mutate a window.");
            Console.WriteLine("PASS " + count + " companion policy/interoperability checks"); return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
}
