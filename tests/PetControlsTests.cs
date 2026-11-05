using System;
using System.Diagnostics;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RainbowDashDesktop;

internal static class PetControlsTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static int checks;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }

    private static T Field<T>(PetWindow window, string name)
    {
        return (T)typeof(PetWindow).GetField(name, Hidden).GetValue(window);
    }

    private static bool Property(PetWindow window, string name)
    {
        return (bool)typeof(PetWindow).GetProperty(name, Hidden).GetValue(window, null);
    }

    private static void Call(PetWindow window, string name, params object[] args)
    {
        Type[] types = Array.ConvertAll(args, argument => argument.GetType());
        typeof(PetWindow).GetMethod(name, Hidden, null, types, null).Invoke(window, args);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        App app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        PetWindow window = new PetWindow(new[] { "--demo", "--smoke-test", "--assets-dir", args[0] }, null);
        int result = 1;
        window.Loaded += delegate
        {
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                UiSettings old = serializer.Deserialize<UiSettings>("{\"QuotaCollapsed\":true}");
                Check(old.KeepOnTop && !old.ReduceMotion && old.QuotaCollapsed, "Older settings keep their original defaults and preferences.");
                Check(Field<IntPtr>(window, "visibilityHotkeyWindow") == IntPtr.Zero, "Test and preview windows leave the system shortcut free.");

                Call(window, "SetKeepOnTop", false);
                Call(window, "SetReduceMotion", true);
                UiSettings saved = serializer.Deserialize<UiSettings>(serializer.Serialize(Field<UiSettings>(window, "settings")));
                Check(!window.Topmost && !saved.KeepOnTop && saved.ReduceMotion, "Topmost and reduced motion round-trip through the normal settings representation.");

                Call(window, "CyclePetVisibility");
                Check(window.IsVisible && Property(window, "IsDocked") && !Field<bool>(window, "dockTransition"), "The shortcut cycle starts with an immediate reduced-motion dock.");
                Call(window, "CyclePetVisibility");
                Check(!window.IsVisible, "The next cycle hides the whole pet.");
                Call(window, "CyclePetVisibility");
                Check(window.IsVisible && !Property(window, "IsDocked") && !window.Topmost, "The hidden pet can return without restoring the old topmost preference.");

                long now = Field<Stopwatch>(window, "interactionClock").ElapsedMilliseconds;
                SpritePresenter presenter = Field<SpritePresenter>(window, "spritePresenter");
                Call(window, "ApplyState", "working", true);
                BitmapSource hold = presenter.Output;
                Call(window, "PresentSprite", now + 6000);
                Call(window, "PresentSprite", now + 18000);
                Check(presenter.Sequence == "state:working" && Object.ReferenceEquals(hold, presenter.Output) && !presenter.IsTransitioning,
                    "Reading stays on the same cel through the usual blink and page-turn windows.");
                Call(window, "ApplyState", "completed", false);
                Check(presenter.Sequence == "state:completed" && !presenter.IsTransitioning,
                    "Completion stays visible and skips the book-closing bridge in reduced motion.");

                EdgeExpressionController edge = new EdgeExpressionController();
                edge.Sample("idle", 0, true);
                Check(edge.Sample("idle", 3650, true) == 0 && edge.Sample("working", 4000, true) == 2,
                    "Reduced motion leaves the edge expression still while showing actual task changes.");
                Call(window, "SetReduceMotion", false);
                Check(!Property(window, "ReducedMotionActive"), "The normal animation mode can be restored.");

                Console.WriteLine("PASS PetControlsTests " + checks + " checks");
                result = 0;
            }
            catch (Exception error) { Console.WriteLine(error); }
            finally { app.Shutdown(result); }
        };
        app.Run(window);
        return result;
    }
}
