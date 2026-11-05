using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RainbowDashDesktop;

internal static class QuotaAndCadenceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static object Read(PetWindow window, string name) { return typeof(PetWindow).GetField(name, Private).GetValue(window); }
    private static void Write(PetWindow window, string name, object value) { typeof(PetWindow).GetField(name, Private).SetValue(window, value); }
    private static void Invoke(PetWindow window, string name) { typeof(PetWindow).GetMethod(name, Private).Invoke(window, null); }
    private static void Check(bool pass, string reason)
    {
        if (!pass) throw new InvalidOperationException(reason);
        checks++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        App app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        PetWindow pet = new PetWindow(new[] { "--demo", "--smoke-test", "--preview-state", "idle", "--assets-dir", args[0] }, null);
        pet.Loaded += delegate
        {
            try
            {
                DispatcherTimer quiet = (DispatcherTimer)Read(pet, "quietRenderTimer");
                Check(quiet.IsEnabled && quiet.Interval.TotalMilliseconds == 32 && !(bool)Read(pet, "renderingHooked"),
                    "Quiet standing uses its own timer instead of keeping the compositor running.");
                Write(pet, "manualRestPreview", "sleeping");
                Write(pet, "manualRestUntil", DateTime.UtcNow.AddSeconds(8));
                Invoke(pet, "UpdateRenderCadence");
                Check(quiet.Interval.TotalMilliseconds == 100, "Cloud sleep can render at a slower cadence.");
                pet.Hide();
                Check(!quiet.IsEnabled && !(bool)Read(pet, "renderingHooked"), "Hiding stops both visual clocks.");
                Check(((DispatcherTimer)Read(pet, "pollTimer")).IsEnabled, "Task and quota poll is still running while hidden.");
                Invoke(pet, "OpenQuotaDetails");
                Window details = (Window)Read(pet, "quotaDetailsWindow");
                TextBlock label = (TextBlock)Read(pet, "quotaDetailsText");
                Check(details.IsVisible && !pet.IsVisible && label.Text.Contains("演示数据") &&
                    label.Text.Contains("72%") && label.Text.Contains("86%"), "Details open separately and label both demo periods.");
                Write(pet, "lastSnapshot", new PetSnapshot { WeekRemaining = 0 });
                Invoke(pet, "UpdateQuotaDetails");
                Check(label.Text.Contains("五小时剩余  未知") && label.Text.Contains("一周剩余      0%"),
                    "A missing quota is distinguishable from a real 0% quota.");
                details.Close();
                Check(Read(pet, "quotaDetailsWindow") == null, "Closing the panel clears its reference.");
                pet.Show();
                Check(quiet.IsEnabled, "Showing the pet restores its visual clock.");
                Console.WriteLine("PASS QuotaAndCadenceTests " + checks + " checks");
                app.Shutdown(0);
            }
            catch (Exception error) { Console.Error.WriteLine(error); app.Shutdown(1); }
        };
        return app.Run(pet);
    }
}
