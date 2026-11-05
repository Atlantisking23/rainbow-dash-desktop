using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using RainbowDashDesktop;

internal static class NaturalPlaybackTests
{
    private static int count;
    private static AnimationClip Clip(int frames, int delay, string mode)
    {
        AnimationClip clip = new AnimationClip { PlaybackMode = mode, HoldFrame = frames - 1 };
        for (int i = 0; i < frames; i++) { clip.Frames.Add(null); clip.Delays.Add(delay); clip.Duration += delay; }
        return clip;
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); count++; }
    [STAThread]
    public static int Main()
    {
        try
        {
            Dictionary<string, AnimationClip> clips = new Dictionary<string, AnimationClip>();
            clips["received"] = Clip(24, 70, "once"); clips["completed"] = Clip(24, 60, "once");
            clips["interrupted"] = Clip(24, 75, "once"); clips["working"] = Clip(24, 40, "loop");
            clips["waiting"] = Clip(24, 80, "hold"); clips["idle"] = Clip(24, 140, "idle");
            clips["idle"].BlinkFrames = new int[] { 7, 8, 9, 10 };
            clips["idle"].BlinkDelays = new int[] { 30, 35, 35, 45 };
            NaturalPlaybackController player = new NaturalPlaybackController(clips, new Random(123));
            player.Request("completed", 0, false);
            Check(player.Sample(6000, false) == 23, "Six-second completion metadata cannot repeat the 1.44-second celebration.");
            player.Request("interrupted", 7000, false);
            Check(player.Sample(15000, false) == 23, "Eight-second interruption metadata holds its endpoint after one reaction.");
            player.Request("received", 16000, false);
            Check(player.Sample(18000, false) == 23, "Two-second receipt window cannot start a second partial acknowledgement.");
            Check(player.Sample(17680, true) == 0, "Only explicit manual-loop mode repeats a transient action.");
            player.Request("received", 20000, true); player.Request("working", 20900, false);
            player.Sample(21000, false);
            Check(player.VisualState == "received", "Working waits for acknowledgement endpoint rather than cutting its pose mid-action.");
            Check(player.Sample(21680, false) == 0 && player.VisualState == "working", "Working starts at frame zero once acknowledgement finishes.");
            Check(player.Sample(22640, false) == 0, "Working is allowed a seamless native wing-cycle loop.");
            player.Request("received", 24000, true); player.Request("interrupted", 24100, false);
            Check(player.VisualState == "interrupted" && player.Sample(24100, false) == 0, "Cancellation preempts pending acknowledgement immediately.");
            player.Request("completed", 25000, false); player.Request("completed", 25900, false);
            Check(player.Sample(26200, false) == 20, "Repeated metadata polls do not restart an existing reaction.");
            player.Request("idle", 30000, false);
            long blink = player.NextBlinkMs;
            Check(blink >= 34500 && blink <= 37800 && player.Sample(blink - 1, false) == 0, "Idle settles for 4.5 to 7.8 seconds before a blink.");
            Check(player.Sample(blink, false) == 7 && player.Sample(blink + 31, false) == 8 && player.Sample(blink + 66, false) == 9 && player.Sample(blink + 101, false) == 10, "Blink eyelid cels follow a short asymmetric 145 ms schedule.");
            Check(player.Sample(blink + 146, false) == 0 && player.NextBlinkMs >= blink + 4646, "Blink returns to the exact settled pose and schedules a fresh long hold.");
            player.Request("waiting", 50000, false);
            Check(player.Sample(55000, false) == 23, "Waiting does not keep looping the old aliased flying clip.");
            AnimationClip entry = Clip(6, 100, "loop"); entry.LoopStart = 2; entry.LoopEnd = 5;
            Check(entry.FrameIndexAt(0, true) == 0 && entry.FrameIndexAt(100, true) == 1 && entry.FrameIndexAt(200, true) == 2 && entry.FrameIndexAt(600, true) == 2,
                "Authored takeoff entry plays once before a configured flight-only loop range.");
            AnimationClip reading = Clip(6, 100, "quiet"); reading.RestFrame = 5;
            reading.RestFrames = new int[] { 5, 0, 4 }; reading.BlinkFrames = new int[] { 1, 2, 3 };
            reading.BlinkDelays = new int[] { 35, 45, 65 }; reading.BlinkMinMs = 3000; reading.BlinkMaxMs = 5000;
            clips["reading"] = reading;
            NaturalPlaybackController quiet = new NaturalPlaybackController(clips, new Random(123));
            quiet.Request("reading", 0, false); long readingBlink = quiet.NextBlinkMs;
            Check(readingBlink >= 3000 && readingBlink <= 5000 && quiet.Sample(readingBlink - 1, false) == 5,
                "Six-cel reading holds one settled reading pose for three to five seconds.");
            Check(quiet.Sample(readingBlink, false) == 1 && quiet.Sample(readingBlink + 36, false) == 2 && quiet.Sample(readingBlink + 81, false) == 3,
                "Reading blink uses only the descending/closed/reopening cels for 145 ms.");
            Check(quiet.Sample(readingBlink + 145, false) == 0,
                "Reading advances from its normal hold to its first downward reading hold after the blink.");
            long secondReadingBlink = quiet.NextBlinkMs; quiet.Sample(secondReadingBlink, false);
            Check(quiet.Sample(secondReadingBlink + 145, false) == 4,
                "Reading reaches the lower-line gaze after the next blink rather than looping six cels.");
            AnimationClip sleep = Clip(6, 100, "loop"); sleep.Delays.Clear(); sleep.Duration = 0;
            foreach (int delay in new int[] { 850, 700, 650, 750, 900, 1150 }) { sleep.Delays.Add(delay); sleep.Duration += delay; }
            clips["sleeping"] = sleep;
            NaturalPlaybackController sleeping = new NaturalPlaybackController(clips, new Random(123)); sleeping.Request("sleeping", 0, false);
            Check(sleeping.Sample(0, false) == 0 && sleeping.Sample(850, false) == 1 && sleeping.Sample(5000, false) == 0,
                "Sleeping art loops only its five-second closed-eye micro-breath cycle, without randomized blinking.");
            AnimationClip pages = Clip(12, 100, "quiet"); pages.RestFrame = 5;
            pages.ActionFrames = new int[] { 6, 7, 8, 9, 10, 11 };
            pages.ActionDelays = new int[] { 100, 100, 100, 100, 100, 100 };
            clips["pages"] = pages;
            NaturalPlaybackController pageReader = new NaturalPlaybackController(clips, new Random(123));
            pageReader.Request("pages", 0, false); long firstPage = pageReader.NextActionMs;
            pageReader.Request("pages", 2000, false);
            bool pageOnce = firstPage >= 3500 && firstPage <= 5500 && pageReader.Sample(firstPage - 1, false) == 5 &&
                pageReader.Sample(firstPage, false) == 6 && pageReader.Sample(firstPage + 200, false) == 8;
            pageReader.Request("pages", firstPage + 250, false);
            pageOnce = pageOnce && pageReader.Sample(firstPage + 599, false) == 11 && pageReader.Sample(firstPage + 600, false) == 5;
            Check(pageOnce && pageReader.NextActionMs >= firstPage + 9600 && pageReader.NextActionMs <= firstPage + 14600 &&
                pageReader.Sample(pageReader.NextActionMs - 1, false) == 5,
                "A blink-free reader turns one page within 5.5 seconds despite repeated polls, then reads for 9 to 14 seconds.");
            pages.BlinkFrames = new int[] { 1, 2, 3 }; pages.BlinkDelays = new int[] { 35, 45, 65 };
            pages.BlinkMinMs = pages.BlinkMaxMs = pages.ActionInitialMinMs = pages.ActionInitialMaxMs = 3500;
            NaturalPlaybackController exclusive = new NaturalPlaybackController(clips, new Random(123)); exclusive.Request("pages", 0, false);
            bool exclusiveActions = exclusive.Sample(3500, true) == 6 && exclusive.Sample(3800, true) == 9 &&
                exclusive.Sample(4100, true) == 5 && exclusive.Sample(7599, false) == 5 && exclusive.Sample(7600, false) == 1;
            exclusive.Request("pages", 8000, true); exclusive.Sample(11500, false);
            exclusive.Request("interrupted", 11600, false);
            exclusiveActions = exclusiveActions && exclusive.VisualState == "interrupted" && exclusive.Sample(11600, false) == 0;
            exclusive.Request("pages", 12000, false); exclusive.Sample(15500, false); exclusive.Request("completed", 15600, false);
            Check(exclusiveActions && exclusive.VisualState == "completed" && exclusive.Sample(15600, false) == 0,
                "Page turns and blinks never overlap, quiet previews keep natural timing, and interruption/completion preempt a page immediately.");

            DragInteractionController drag = new DragInteractionController();
            drag.Begin(new Point(100, 100), new Point(400, 300), true);
            Point window = drag.Move(new Point(103, 102), 4, 4);
            Check(!drag.IsDragging && window == new Point(400, 300) && drag.DisplayState("working") == "working" && drag.End(true),
                "A small pointer movement preserves the ordinary pose and one click without shifting the window.");
            drag.Begin(new Point(100, 100), new Point(400, 300), true);
            window = drag.Move(new Point(112, 105), 4, 4);
            Check(drag.IsDragging && drag.FacesRight && window == new Point(412, 305),
                "Crossing the drag threshold preserves the original grab offset and turns the sprint toward a rightward drag.");
            window = drag.Move(new Point(108, 108), 4, 4);
            bool stableRight = drag.FacesRight;
            drag.Move(new Point(103, 108), 4, 4); stableRight = stableRight && drag.FacesRight;
            drag.Move(new Point(102, 108), 4, 4);
            bool turnedLeft = !drag.FacesRight;
            drag.Move(new Point(110, 108), 4, 4);
            bool stableLeft = !drag.FacesRight;
            drag.Move(new Point(112, 108), 4, 4);
            Check(window == new Point(408, 308) && stableRight && turnedLeft && stableLeft && drag.FacesRight,
                "The window follows absolute pointer displacement, while a 10 DIP reversal hysteresis prevents facing jitter.");
            Check(!drag.End(true) && !drag.IsPressed && !drag.IsDragging,
                "Releasing a completed drag clears capture state and never triggers a click acknowledgement.");
            drag.Begin(new Point(100, 100), new Point(400, 300), true);
            window = drag.Move(new Point(101, 111), 4, 4);
            Check(drag.IsDragging && !drag.FacesRight && window == new Point(401, 311),
                "A vertical drag starts a left-facing sprint without a position jump.");
            NaturalPlaybackController taskDuringDrag = new NaturalPlaybackController(clips, new Random(123));
            taskDuringDrag.Request("received", 0, false);
            taskDuringDrag.Request("working", 900, false);
            taskDuringDrag.Sample(2000, false);
            bool workingUnderSprint = taskDuringDrag.VisualState == "working" && drag.DisplayState(taskDuringDrag.VisualState) == "dragging";
            taskDuringDrag.Request("interrupted", 2100, false);
            taskDuringDrag.Sample(2200, false);
            bool interruptedUnderSprint = drag.DisplayState(taskDuringDrag.VisualState) == "dragging";
            Check(workingUnderSprint && interruptedUnderSprint && !drag.End(false) && drag.DisplayState(taskDuringDrag.VisualState) == "interrupted",
                "Task acknowledgement and cancellation continue beneath the sprint, and lost capture immediately restores the latest task.");
            drag.Begin(new Point(0, 0), new Point(0, 0), true); drag.Move(new Point(-15, 0), 4, 4);
            taskDuringDrag.Request("completed", 2300, false);
            taskDuringDrag.Sample(2400, false); drag.End(true);
            Check(drag.DisplayState(taskDuringDrag.VisualState) == "completed" && taskDuringDrag.Sample(4000, false) == 23,
                "Releasing after a completion restores that completion without restarting its task animation clock.");
            drag.Begin(new Point(0, 0), new Point(0, 0), true);
            bool lostClick = drag.End(false);
            drag.Begin(new Point(0, 0), new Point(0, 0), false);
            Check(!lostClick && !drag.End(true) && !drag.End(true),
                "Lost capture and quota-ring presses cannot produce a pet click, and ending twice is harmless.");
            Dictionary<string, object> orientation = new Dictionary<string, object>();
            orientation["idle"] = new Dictionary<string, object> { { "mirror_horizontal", true } };
            orientation["dragging"] = new Dictionary<string, object> { { "mirror_horizontal", false } };
            MethodInfo configure = typeof(PetWindow).GetMethod("ConfigurePlayback", BindingFlags.NonPublic | BindingFlags.Static);
            AnimationClip idleFacing = Clip(6, 100, "idle"), sprintFacing = Clip(6, 70, "loop");
            configure.Invoke(null, new object[] { idleFacing, "idle", orientation });
            configure.Invoke(null, new object[] { sprintFacing, "dragging", orientation });
            Check(idleFacing.MirrorHorizontal && !sprintFacing.MirrorHorizontal && sprintFacing.PlaybackMode == "loop" && !sprintFacing.Grounded,
                "Per-clip manifest orientation is respected without a playback profile, and the sprint defaults to an airborne loop.");
            Check(App.IsFatalRenderFailure(new System.Runtime.InteropServices.COMException("render", unchecked((int)0x88980406))) &&
                App.IsFatalRenderFailure(new InvalidOperationException("wrapped", new System.Runtime.InteropServices.COMException("render", unchecked((int)0x88980406)))) &&
                !App.IsFatalRenderFailure(new System.Runtime.InteropServices.COMException("unrelated", unchecked((int)0x80004005))) &&
                !App.IsFatalRenderFailure(new InvalidOperationException("unrelated")),
                "Only the recorded fatal WPF render error ends the poisoned render loop; unrelated errors are not classified as render failures.");
            Check(PetWindow.ConnectionLabel(new PetSnapshot { CodexRunning = true, ConnectionStatus = "retrying" }).Contains("自动重连") &&
                PetWindow.ConnectionLabel(new PetSnapshot { CodexRunning = true, ConnectionStatus = "connected" }).Contains("已同步") &&
                PetWindow.ConnectionLabel(new PetSnapshot { CodexRunning = false }).Contains("等待"),
                "Connection feedback distinguishes local synchronization, retrying, and waiting without changing task animation.");
            Check(PetWindow.QuotaStatusLabel(new PetSnapshot { QuotaStatus = "refreshing" }).Contains("同步") &&
                PetWindow.QuotaStatusLabel(new PetSnapshot { QuotaStatus = "retrying" }).Contains("保留最近") &&
                PetWindow.QuotaStatusLabel(new PetSnapshot { QuotaStatus = "ok", QuotaSource = "account/rateLimits/read" }).Contains("官方额度接口") &&
                PetWindow.QuotaStatusLabel(new PetSnapshot { QuotaStatus = "expired" }).Contains("已重置"),
                "Quota feedback distinguishes live refresh, retained fallback, and expired windows rather than reporting task connection as quota freshness.");
            Console.WriteLine("Passed " + count + " behavioral checks."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
