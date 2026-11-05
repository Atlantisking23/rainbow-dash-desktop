using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RainbowDashDesktop;

internal static class RestUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static object Field(object owner, string name) { return owner.GetType().GetField(name, Hidden).GetValue(owner); }
    private static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Hidden).SetValue(owner, value); }
    private static object Call(object owner, string name, params object[] args)
    { return owner.GetType().GetMethods(Hidden).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(owner, args); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Reset(PetWindow window)
    {
        Set(window, "rest", new RestController()); Set(window, "lastRestStage", RestStage.Awake);
        Set(window, "handledWakeVersion", 0L); Set(window, "manualState", null); Set(window, "manualRestPreview", null);
        Set(window, "wakeVisualHoldState", null); Set(window, "pendingAwakeAction", null);
        Set(window, "dragWakeVisualSuppressed", false);
        Set(window, "sleepPointerGraceUntil", 0L);
        Set(window, "gazeFrame", -1); ((RestIdleTimeline)Field(window, "restIdle")).Reset(0);
        ((EnhancedAction)Field(window, "enhancedAction")).Cancel();
        Call(window, "ApplyState", "idle", true);
    }
    private static string RenderHash(PetWindow window)
    {
        window.UpdateLayout();
        RenderTargetBitmap image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render((Visual)Field(window, "root"));
        byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        using (var hash = System.Security.Cryptography.SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(pixels));
    }
    private static void Task(PetWindow window, string state, int count, long now)
    { Call(window, "ObserveRestTask", state, count, now); Call(window, "ApplyState", state, false); }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        string settingsPath = Path.Combine(App.UserDirectory, "settings.json");
        string settingsBefore = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        App app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        PetWindow window = new PetWindow(new string[] { "--demo", "--smoke-test", "--assets-dir", args[0] }, null);
        window.Loaded += delegate
        {
            try
            {
                RestArt art = (RestArt)Field(window, "restArt");
                Check(art.Complete, "All eighteen rest cels are complete.");
                foreach (string name in RestArt.Names)
                {
                    Check(art.Has(name), "Rest group exists: " + name);
                    for (int i = 0; i < 6; i++)
                    { BitmapSource cel = art.Get(name, i); Check(cel != null && cel.PixelWidth == 512 && cel.PixelHeight == 512, name + " fixed 512 canvas " + i); }
                }
                Check(Field(window, "monitor") == null, "Regression starts no account or network worker.");
                RestIdleTimeline rhythm = new RestIdleTimeline();
                Check(rhythm.Sample(0) == 0 && rhythm.Sample(3000) == 0, "Awake rests with naturally open eyes.");
                Check(rhythm.Sample(4100) == 2 && rhythm.Sample(4140) == 3 && rhythm.Sample(4190) == 4 && rhythm.Sample(4260) == 0,
                    "Blink eyelid cels have brief separate closing, closed and reopening beats.");
                Check(rhythm.Sample(6500) == 0 && rhythm.Sample(6640) == 0, "Eyes stay open throughout the normal resting hold.");
                RestPointerActivity pointer = new RestPointerActivity();
                Check(!pointer.Observe(0, 0, false), "Far pointer is not attention.");
                Check(!pointer.Observe(0, 0, true), "Moving the pet under a parked pointer is not attention.");
                Check(!pointer.Observe(1, 0, true) && pointer.Observe(2, 0, true), "Real two-DIP near motion counts.");
                for (int i = 0; i < 200; i++) Check(!pointer.Observe(2, 0, true), "A parked pointer cannot keep the pet awake.");

                Reset(window); Call(window, "AdvanceRestVisual", 0L); window.UpdateLayout();
                Image restImage = (Image)Field(window, "restPony");
                Image oldImage = (Image)Field(window, "pony");
                Check(restImage.Visibility == Visibility.Visible && Object.ReferenceEquals(restImage.Source, art.Get("awake-idle", 0)), "Startup idle is natural standing with open eyes.");
                Check(oldImage.Opacity == 0, "Old sleeping cels do not show under awake idle.");
                Check(((ScaleTransform)restImage.RenderTransform).ScaleX == -1, "Raw right-facing rest art faces left at runtime.");
                string awakeHash = RenderHash(window);
                RestController rest = (RestController)Field(window, "rest");
                Call(window, "AdvanceRestVisual", 299999L);
                Check(rest.Stage == RestStage.Awake, "Default idle remains awake before five minutes.");
                Call(window, "AdvanceRestVisual", 300000L);
                Check(rest.Stage == RestStage.Falling && Object.ReferenceEquals(restImage.Source, art.Get("fall-asleep", 0)), "Five minutes starts the entire authored settling bridge.");
                for (int i = 0; i < 6; i++)
                {
                    Call(window, "AdvanceRestVisual", 300000L + i * 200L);
                    Check(Object.ReferenceEquals(restImage.Source, art.Get("fall-asleep", i)), "Settling uses cel " + i);
                    Check(restImage.Width == 224 && restImage.Height == 200, "Natural standing and settling retain one fixed scale.");
                }
                Call(window, "AdvanceRestVisual", 301200L);
                var clips = (Dictionary<string, AnimationClip>)Field(window, "clips");
                Check(rest.Stage == RestStage.Sleeping && Object.ReferenceEquals(restImage.Source, clips["idle"].Frames[0]), "Sleeping reuses original cloud sleeping art.");
                Call(window, "AdvanceRestVisual", 301400L);
                Check(RenderHash(window) != awakeHash, "True WPF renders of awake and sleeping are distinct.");
                Check(!rest.CanRunIdleExtras, "Sleeping disables automatic standing personality actions.");
                Set(window, "gazeFrame", 4); Call(window, "AdvanceEnhanced");
                Check(((Image)Field(window, "enhancedPony")).Visibility == Visibility.Collapsed, "Sleeping cannot display a standing gaze overlay.");
                Set(window, "gazeFrame", -1);
                Call(window, "StartEnhancedAction", "hair", false);
                Check(!((EnhancedAction)Field(window, "enhancedAction")).Active, "A sleeping pet never runs a standing action before waking.");
                Set(window, "pendingAwakeAction", null);

                Task(window, "received", 1, 301500L);
                Check(rest.Stage == RestStage.Waking && (string)Field(window, "shownState") == "received", "Task metadata changes immediately while its visual waits for waking.");
                Call(window, "AdvanceRestVisual", 301500L);
                Check(Object.ReferenceEquals(restImage.Source, art.Get("wake-up", 0)), "A fully asleep pet starts wake-up at the sleeping endpoint.");
                long wakeAt = rest.PhaseStartedAt;
                Task(window, "working", 1, 301600L); Task(window, "completed", 0, 301700L); Task(window, "idle", 0, 301800L);
                Check(rest.PhaseStartedAt == wakeAt && rest.PendingTaskState == "completed", "Short tasks update pending state without restarting the wake and keep their terminal reaction.");
                Call(window, "AdvanceRestVisual", 302499L);
                Check(Object.ReferenceEquals(restImage.Source, art.Get("wake-up", 5)), "Wake reaches the awake endpoint before the task reaction.");
                Call(window, "AdvanceRestState", 302500L);
                NaturalPlaybackController playback = (NaturalPlaybackController)Field(window, "playback");
                Check(rest.Stage == RestStage.Awake && playback.VisualState == "completed" && playback.Sample(302500, false) == 0,
                    "The final task reaction begins at cel zero after one complete wake.");
                Check((string)Field(window, "shownState") == "idle", "Preserving a short terminal visual does not falsify live task metadata.");
                Task(window, "idle", 0, 302600L);
                Check(playback.VisualState == "completed", "Repeated idle polling cannot truncate the terminal reaction.");
                long holdUntil = (long)Field(window, "wakeVisualHoldUntil");
                Call(window, "AdvanceRestState", holdUntil - 1);
                Check(playback.VisualState == "completed", "The terminal reaction gets its entire authored duration.");
                Call(window, "AdvanceRestState", holdUntil);
                Check(playback.VisualState == "idle" && rest.WakeCompletedVersion == 1, "After one reaction, awake idle returns without another wake commit.");

                Reset(window); rest = (RestController)Field(window, "rest");
                Call(window, "AdvanceRestVisual", 300000L); Call(window, "AdvanceRestVisual", 301200L);
                Task(window, "working", 1, 301300L);
                Task(window, "completed", 0, 302300L);
                Check(rest.Stage == RestStage.Awake && playback.VisualState == "completed" && playback.Sample(302300, false) == 0,
                    "Completion arriving on the exact wake boundary commits its terminal cel once.");
                Check(!((EnhancedAction)Field(window, "enhancedAction")).Active && Field(window, "wakeVisualHoldState") != null,
                    "A same-poll wake completion cannot open a book-closing bridge for a book that was never shown.");

                Reset(window); rest = (RestController)Field(window, "rest");
                Call(window, "AdvanceRestVisual", 300000L); Call(window, "AdvanceRestVisual", 300600L);
                Check(Object.ReferenceEquals(restImage.Source, art.Get("fall-asleep", 3)), "Half-settled pose is cel three.");
                Task(window, "received", 1, 300600L); Call(window, "AdvanceRestVisual", 300600L);
                Check(rest.WakeFromFalling && Object.ReferenceEquals(restImage.Source, art.Get("fall-asleep", 3)), "Interrupting a half-settle preserves the current pose rather than jumping fully asleep.");
                Call(window, "AdvanceRestVisual", 300899L);
                Check(Object.ReferenceEquals(restImage.Source, art.Get("fall-asleep", 0)), "Partial settling reverses to the true awake endpoint.");
                Call(window, "AdvanceRestState", 300900L);
                Check(playback.VisualState == "received" && playback.Sample(300900, false) == 0, "Task receipt begins once after the short return bridge.");

                Reset(window); rest = (RestController)Field(window, "rest");
                Call(window, "AdvanceRestVisual", 300000L); Call(window, "AdvanceRestVisual", 301200L);
                DragInteractionController drag = (DragInteractionController)Field(window, "drag");
                rest.NotifyInteraction(RestAttentionKind.Drag, 301210);
                Call(window, "NotifyRestInteraction", RestAttentionKind.Drag);
                drag.Begin(new Point(100, 100), new Point(window.Left, window.Top), false);
                Point moved = drag.Move(new Point(150, 120), 4, 4);
                Check(drag.IsDragging && moved.X == window.Left + 50 && moved.Y == window.Top + 20, "Sleeping never delays physical drag movement.");
                Call(window, "AdvanceAnimation");
                Check(rest.Stage == RestStage.Waking && (string)Field(window, "paintedState") == "dragging" && restImage.Visibility == Visibility.Collapsed && oldImage.Opacity == 1,
                    "Sprint owns the visual immediately while the fast wake advances in the background.");
                drag.End(false);
                Call(window, "AdvanceAnimation");
                Check(rest.Stage == RestStage.Waking && (string)Field(window, "paintedRestName") == "awake-idle",
                    "A very short sleepy drag cannot flash back to the fully asleep wake-start cel after release.");

                Reset(window); rest = (RestController)Field(window, "rest");
                Set(window, "manualState", "completed"); Set(window, "manualInteraction", false);
                Set(window, "manualRestPreview", "sleeping");
                Task(window, "working", 1, 100L);
                Check(Field(window, "manualState") == null && Field(window, "manualRestPreview") == null && playback.VisualState == "working", "New real tasks preempt both task and rest menu previews.");
                Call(window, "AdvanceRestState", 900000L);
                Check(rest.Stage == RestStage.Awake, "Active work never enters automatic sleep.");

                Reset(window); rest = (RestController)Field(window, "rest");
                Call(window, "AdvanceRestVisual", 300000L); Call(window, "AdvanceRestVisual", 301200L);
                var monitors = (IList<EdgeMonitor>)Call(window, "GetEdgeMonitors");
                EdgeArt edgeArt = (EdgeArt)Field(window, "edgeArt");
                foreach (DockEdge edge in new DockEdge[] { DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom })
                {
                    EdgeDockTarget target = EdgeDockGeometry.Resolve(edge, "", .5, monitors, false);
                    if (target == null) continue;
                    Call(window, "EnterDock", target, false); Call(window, "AdvanceEdge");
                    Check(rest.Stage == RestStage.Sleeping && Field(window, "dockTarget") != null, edge + " logical sleep stays docked.");
                    Check(Object.ReferenceEquals(((Image)Field(window, "edgePony")).Source,
                        ((SpriteAppearance)Field(window, "spriteAppearance")).Colorize(edgeArt.Get(edge, 8), "edge:" + edge.ToString().ToLowerInvariant())),
                        edge + " sleeping uses the color-normalized closed-eye edge cel.");
                    Check(((QuotaRing)Field(window, "quotaRing")).Root.Visibility == Visibility.Collapsed, edge + " sleeping keeps quota hidden.");
                    Call(window, "ExpandDock", false);
                }
                ContextMenu menu = ((FrameworkElement)Field(window, "root")).ContextMenu;
                MenuItem delayMenu = menu.Items.OfType<MenuItem>().First(m => (string)m.Header == "多久后睡觉");
                Check(delayMenu.Items.OfType<MenuItem>().Select(m => (int)m.Tag).SequenceEqual(new int[] { 3, 5, 10, 0 }), "The sleep setting offers three, five, ten minutes and never.");

                Check(!menu.Items.OfType<MenuItem>().Any(m => (string)m.Header == "预览休息状态"), "The separate rest preview menu has been removed.");
                MenuItem animationMenu = menu.Items.OfType<MenuItem>().Single(m => (string)m.Header == "预览专属动画");
                string[] previewTags = animationMenu.Items.OfType<MenuItem>().Where(m => m.Tag is string).Select(m => (string)m.Tag).ToArray();
                Check(previewTags.SequenceEqual(new string[] { "received", "working", "interrupted", "completed", "waiting", "error", "awake", "sleeping", "fall", "wake" }),
                    "One preview menu contains six task reactions followed by four distinct rest previews.");
                Check(!animationMenu.Items.OfType<MenuItem>().Any(m => (string)m.Header == "待机中") && previewTags.Distinct().Count() == 10,
                    "The merged previews contain no duplicate generic idle entry.");
                Check(animationMenu.Items.OfType<MenuItem>().Any(m => (string)m.Header == "恢复任务状态") && animationMenu.Items.OfType<MenuItem>().Any(m => (string)m.Header == "手动预览时循环动作"),
                    "The merged preview menu keeps restore and loop controls.");

                Reset(window); rest = (RestController)Field(window, "rest");
                UiSettings uiSettings = (UiSettings)Field(window, "settings");
                int oldSleepDelay = uiSettings.SleepDelayMinutes; uiSettings.SleepDelayMinutes = 0;
                long now = ((System.Diagnostics.Stopwatch)Field(window, "interactionClock")).ElapsedMilliseconds;
                rest.Configure(0, now); Task(window, "working", 1, now);
                MenuItem sleepCommand = menu.Items.OfType<MenuItem>().Single(m => (string)m.Header == "让云宝休息睡觉");
                Check(sleepCommand.ToolTip != null && sleepCommand.ToolTip.ToString().Contains("任务和额度继续同步"), "The true sleep command explains that live work and quotas continue.");
                menu.IsOpen = true;
                Set(window, "manualState", "completed"); Set(window, "manualRestPreview", "wake"); Set(window, "pendingAwakeAction", "hair");
                Set(window, "wakeVisualHoldState", "completed"); Set(window, "wakeVisualHoldUntil", now + 20000);
                ((EnhancedAction)Field(window, "enhancedAction")).Start("hair", now, 1200, null, false); Set(window, "gazeFrame", 2);
                PetSnapshot liveSnapshot = (PetSnapshot)Field(window, "lastSnapshot");
                string liveState = liveSnapshot.State; double? liveQuota = liveSnapshot.FiveHourRemaining;
                bool routedToMenu = false;
                RoutedEventHandler sleepRouteProbe = delegate(object sender, RoutedEventArgs e)
                { if (e.OriginalSource == sleepCommand) routedToMenu = e.Handled; };
                menu.AddHandler(MenuItem.ClickEvent, sleepRouteProbe, true);
                RoutedEventArgs sleepClick = new RoutedEventArgs(MenuItem.ClickEvent, sleepCommand);
                sleepCommand.RaiseEvent(sleepClick);
                menu.RemoveHandler(MenuItem.ClickEvent, sleepRouteProbe); menu.IsOpen = false;
                Check(routedToMenu && sleepClick.Handled, "The actual sleep item click bubbles to the context menu as handled, so its general attention handler cannot wake it.");
                Check(rest.ManualSleepRequested && rest.Stage == RestStage.Falling, "The real sleep menu command starts a sustained sleep, rather than an eight-second preview.");
                Check((string)Field(window, "shownState") == "working" && liveSnapshot.State == liveState && liveSnapshot.FiveHourRemaining == liveQuota,
                    "Manual sleep preserves true task metadata and the live quota snapshot.");
                Check(Field(window, "manualState") == null && Field(window, "manualRestPreview") == null && Field(window, "pendingAwakeAction") == null && Field(window, "wakeVisualHoldState") == null &&
                    !((EnhancedAction)Field(window, "enhancedAction")).Active && (int)Field(window, "gazeFrame") == -1, "Manual sleep clears all old preview, reaction and personality overlays.");
                Check(uiSettings.SleepDelayMinutes == 0 && rest.SleepDelayMs == 0, "Never-auto-sleep still permits manual sleep without changing the setting.");
                long sleepAt = rest.PhaseStartedAt;
                Task(window, "working", 1, sleepAt + 1);
                Check(rest.ManualSleepRequested && rest.Stage == RestStage.Falling, "A same-state same-count busy poll immediately after the routed click cannot wake the pet.");
                long graceUntil = (long)Field(window, "sleepPointerGraceUntil");
                Call(window, "ObserveRestPointer", new Point(800, 500), true, graceUntil - 1000);
                Call(window, "ObserveRestPointer", new Point(820, 500), true, graceUntil - 1);
                Check(rest.ManualSleepRequested && rest.Stage == RestStage.Falling, "Menu-dismissal pointer movement receives a short settling grace period.");
                Call(window, "AdvanceRestVisual", sleepAt + 1200);
                Call(window, "AdvanceRestVisual", sleepAt + 9001);
                Check(rest.ManualSleepRequested && rest.Stage == RestStage.Sleeping && Field(window, "manualRestPreview") == null,
                    "Real manual sleep remains asleep after the former eight-second preview timeout.");
                Call(window, "ObserveRestPointer", new Point(820, 500), true, graceUntil + 1);
                Check(rest.Stage == RestStage.Sleeping, "Pointer anchors continue sampling during the grace period, so parked menu-dismissal movement cannot wake afterward.");
                Call(window, "ObserveRestPointer", new Point(824, 500), true, graceUntil + 2);
                Check(!rest.ManualSleepRequested && rest.Stage == RestStage.Waking, "Fresh nearby pointer movement wakes manual sleep after the grace period.");

                Reset(window); rest = (RestController)Field(window, "rest");
                now = ((System.Diagnostics.Stopwatch)Field(window, "interactionClock")).ElapsedMilliseconds;
                Task(window, "working", 1, now); Call(window, "LetPetSleep"); sleepAt = rest.PhaseStartedAt;
                Task(window, "received", 1, sleepAt + 100);
                Check(!rest.ManualSleepRequested && rest.Stage == RestStage.Waking && (string)Field(window, "shownState") == "received", "A genuinely new task state immediately wakes manual sleep and updates live state.");
                Reset(window); rest = (RestController)Field(window, "rest");
                now = ((System.Diagnostics.Stopwatch)Field(window, "interactionClock")).ElapsedMilliseconds;
                Task(window, "working", 1, now); Call(window, "LetPetSleep"); sleepAt = rest.PhaseStartedAt;
                Task(window, "working", 2, sleepAt + 100);
                Check(!rest.ManualSleepRequested && rest.Stage == RestStage.Waking, "An additional active task wakes manual sleep even if the working label stays the same.");
                Reset(window); rest = (RestController)Field(window, "rest");
                now = ((System.Diagnostics.Stopwatch)Field(window, "interactionClock")).ElapsedMilliseconds;
                Task(window, "working", 2, now); Call(window, "LetPetSleep"); sleepAt = rest.PhaseStartedAt;
                Call(window, "AdvanceRestState", sleepAt + 1200); Call(window, "UpdateStatusText", new PetSnapshot { State = "working", ActiveTasks = 2 });
                Check(((TextBlock)Field(window, "statusText")).Text == "云上小憩", "Manual sleep keeps its sleep label while multiple real tasks continue.");
                Reset(window); rest = (RestController)Field(window, "rest");
                Set(window, "hoverTextUntil", DateTime.UtcNow.AddMinutes(1)); Call(window, "LetPetSleep"); sleepAt = rest.PhaseStartedAt;
                Call(window, "UpdateStatusText", new PetSnapshot { State = "idle" });
                Check(((TextBlock)Field(window, "statusText")).Text == "困了，歇一会儿", "Recent hover feedback cannot cover the settling label.");
                Call(window, "AdvanceRestState", sleepAt + 1200); Call(window, "UpdateStatusText", new PetSnapshot { State = "idle" });
                Check(((TextBlock)Field(window, "statusText")).Text == "云上小憩", "Recent hover feedback cannot cover the sleeping label.");
                Reset(window); now = ((System.Diagnostics.Stopwatch)Field(window, "interactionClock")).ElapsedMilliseconds;
                Task(window, "working", 2, now);
                drag.Begin(new Point(100, 100), new Point(window.Left, window.Top), false); drag.Move(new Point(150, 120), 4, 4);
                Call(window, "UpdateStatusText", new PetSnapshot { State = "working", ActiveTasks = 2 });
                Check(((TextBlock)Field(window, "statusText")).Text == "云宝冲刺", "Multiple-task counts cannot cover the drag sprint label.");
                drag.End(false); Set(window, "hoverTextUntil", DateTime.MinValue);
                foreach (RestAttentionKind kind in new RestAttentionKind[] { RestAttentionKind.Click, RestAttentionKind.Drag, RestAttentionKind.Menu })
                {
                    Reset(window); rest = (RestController)Field(window, "rest"); Call(window, "LetPetSleep");
                    Call(window, "AdvanceRestState", rest.PhaseStartedAt + 1200);
                    Call(window, "NotifyRestInteraction", kind);
                    Check(!rest.ManualSleepRequested && rest.Stage == RestStage.Waking, "Deliberate " + kind + " interaction wakes manual sleep even during pointer grace.");
                }
                uiSettings.SleepDelayMinutes = oldSleepDelay;
                Reset(window);
                foreach (string kind in new string[] { "awake", "sleeping", "fall", "wake" })
                {
                    MenuItem entry = animationMenu.Items.OfType<MenuItem>().Single(m => (string)m.Tag == kind);
                    entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, entry));
                    Check((string)Field(window, "manualRestPreview") == kind && !((RestController)Field(window, "rest")).ManualSleepRequested,
                        "Merged " + kind + " item remains a temporary preview rather than a real sleep command.");
                }

                foreach (string kind in new string[] { "awake", "sleeping", "fall", "wake" })
                {
                    PetWindow preview = new PetWindow(new string[] { "--preview-rest", kind, "--rest-frame", "3", "--assets-dir", args[0] }, null);
                    Check(Field(preview, "monitor") == null && (bool)Field(preview, "demo"), kind + " standalone preview starts no live worker.");
                    Check((string)Field(preview, "previewRest") == kind && (int)Field(preview, "previewRestFrame") == 3, kind + " preview parses the exact authored cel.");
                    Call(preview, "SaveSettings"); preview.Close();
                }
                Check((File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null) == settingsBefore, "UI and preview verification leave the user's settings unchanged.");
                Console.WriteLine("PASS RestUiTests " + checks + " checks"); result = 0;
            }
            catch (Exception error)
            { Console.WriteLine("FAIL " + error + "\n" + (error.InnerException == null ? "" : error.InnerException.ToString())); }
            finally { app.Shutdown(result); }
        };
        app.Run(window);
        return result;
    }
}
