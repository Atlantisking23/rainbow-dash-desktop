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

internal static class SpritePresentationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static object Field(object value, string name) { return value.GetType().GetField(name, Hidden).GetValue(value); }
    private static void Set(object value, string name, object field) { value.GetType().GetField(name, Hidden).SetValue(value, field); }
    private static object Call(object value, string name, params object[] args)
    { return value.GetType().GetMethods(Hidden).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args); }
    private static BitmapSource Bitmap(params byte[][] pixels)
    {
        byte[] bytes = pixels.SelectMany(p => p).ToArray();
        BitmapSource image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Pbgra32, null, bytes, 8); image.Freeze(); return image;
    }
    private static byte[] Pixels(BitmapSource source)
    { byte[] pixels = new byte[source.PixelWidth * source.PixelHeight * 4]; source.CopyPixels(pixels, source.PixelWidth * 4, 0); return pixels; }
    private static byte[] Pixel(BitmapSource source, int index) { return Pixels(source).Skip(index * 4).Take(4).ToArray(); }
    private static BitmapSource Solid(byte b, byte g, byte r, byte a)
    { byte[] p = { b, g, r, a }; return Bitmap(p, p, p, p); }
    private static void WeightedEdgeTests()
    {
        SpritePresenter presenter = new SpritePresenter(2, 2);
        BitmapSource first = Solid(252, 216, 142, 255), second = Solid(252, 216, 142, 255);
        foreach (int weight in Enumerable.Range(0, 17).Select(i => i * 16))
        {
            presenter.PresentWeighted(first, second, weight, "edge:Top");
            Check(Pixel(presenter.Output, 0).SequenceEqual(new byte[] { 252, 216, 142, 255 }), "The last docking dissolve keeps an identical opaque coat at weight " + weight + ".");
        }
        first = Solid(126, 108, 71, 128); second = Solid(126, 108, 71, 128);
        foreach (int weight in Enumerable.Range(0, 17).Select(i => i * 16))
        {
            presenter.PresentWeighted(first, second, weight, "edge:Top");
            Check(Pixel(presenter.Output, 0).SequenceEqual(new byte[] { 126, 108, 71, 128 }), "The last docking dissolve preserves translucent ink at weight " + weight + ".");
        }
        first = Solid(200, 0, 0, 255); second = Solid(0, 200, 0, 255);
        presenter.PresentWeighted(first, second, 128, "edge:Top");
        Check(Pixel(presenter.Output, 0).SequenceEqual(new byte[] { 100, 100, 0, 255 }), "Docking uses a premultiplied linear midpoint with opaque alpha.");
        presenter.PresentWeighted(first, second, -1, "edge:Top");
        Check(Pixel(presenter.Output, 0)[0] == 200, "Explicit docking weights clamp to the starting endpoint.");
        presenter.PresentWeighted(first, second, 257, "edge:Top");
        Check(Pixel(presenter.Output, 0)[1] == 200, "Explicit docking weights clamp to the ending endpoint.");
        SpriteRasterCache raster = new SpriteRasterCache();
        BitmapSource output = raster.Render(first, new Rect(0, 0, 4, 4), false, 8, 8);
        Check(output.PixelWidth == 8 && output.PixelHeight == 8, "Docking rasterization retains two pixels per DIP.");
        Check(Pixel(output, 0)[3] == 255, "Docking rasterization preserves opaque alpha.");
        for (int i = 0; i < 40; i++) raster.Render(Solid((byte)i, 0, 0, 255), new Rect(0, 0, 4, 4), i % 2 == 0, 8, 8);
        Check(raster.Count <= 12, "Docking rasterization keeps a bounded cache.");
    }
    private static void EdgeGeometryTests(PetWindow window)
    {
        var monitors = (IList<EdgeMonitor>)Call(window, "GetEdgeMonitors");
        Canvas canvas = (Canvas)Field(window, "edgeCanvas");
        Image logical = (Image)Field(window, "edgePony"), visible = (Image)Field(window, "edgeSpriteImage");
        // The invisible legacy canvas has no layout slot of its own. A direct
        // Canvas child reproduces the original Image's real WPF arrangement.
        Image reference = new Image { Stretch = Stretch.Uniform, Opacity = 0, IsHitTestVisible = false,
            RenderTransformOrigin = new Point(.5, .5) };
        canvas.Children.Add(reference);
        Check(((Canvas)Field(window, "legacyEdgeSpriteLayer")).Opacity == 0, "Only the final docking image participates in drawing.");
        foreach (DockEdge edge in new DockEdge[] { DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom })
        {
            EdgeDockTarget target = EdgeDockGeometry.Resolve(edge, "", .5, monitors, false);
            if (target == null) continue;
            Call(window, "EnterDock", target, false);
            bool side = edge == DockEdge.Left || edge == DockEdge.Right;
            double compactWidth = window.Width, compactArtWidth = 0, compactArtLeft = 0;
            foreach (bool hover in new bool[] { false, true })
            {
                EdgeDockHoverController controller = (EdgeDockHoverController)Field(window, "edgeHover");
                controller.Reset();
                if (hover) { controller.SetPointer(true, 0); controller.Advance(250); }
                Call(window, "LayoutEdge");
                if (side && hover)
                {
                    long start = (long)Field(window, "sidePeekStartedAt");
                    Call(window, "AdvanceSidePeek", start + 80L);
                    Call(window, "PresentSprite", start + 80L);
                    window.UpdateLayout();
                    Check(window.Width > compactWidth + 1 && window.Width < compactWidth + 11,
                        edge + " peeks progressively before reaching the full reveal.");
                    Check(Math.Abs(visible.ActualWidth - compactArtWidth) <= 1.1,
                        edge + " artwork keeps its size during the animated reveal.");
                    Call(window, "AdvanceSidePeek", start + 160L);
                    Call(window, "PresentSprite", start + 160L);
                }
                reference.Width = logical.Width; reference.Height = logical.Height; reference.Source = logical.Source;
                reference.RenderTransform = logical.RenderTransform;
                Canvas.SetLeft(reference, Canvas.GetLeft(logical)); Canvas.SetTop(reference, Canvas.GetTop(logical));
                window.UpdateLayout();
                Rect expected = reference.TransformToAncestor(canvas).TransformBounds(new Rect(0, 0, reference.ActualWidth, reference.ActualHeight));
                Rect actual = visible.TransformToAncestor(canvas).TransformBounds(new Rect(0, 0, visible.ActualWidth, visible.ActualHeight));
                string label = edge + (hover ? " hover" : " compact");
                // WPF rounds both the original layout slot and its ink at the
                // monitor DPI; the renderer rounds once to its 2px/DIP grid.
                Check(Math.Abs(actual.X - expected.X) <= 1.1 && Math.Abs(actual.Y - expected.Y) <= 1.1,
                    label + " final image retains the real Uniform image's centered content origin. Expected=" + expected + " actual=" + actual);
                Check(Math.Abs(actual.Width - expected.Width) <= 1.1 && Math.Abs(actual.Height - expected.Height) <= 1.1,
                    label + " final image preserves actual content size within raster and layout rounding. Expected=" + expected + " actual=" + actual);
                if (side && !hover)
                {
                    compactArtWidth = actual.Width;
                    compactArtLeft = window.Left + actual.Left;
                }
                else if (side)
                {
                    Check(Math.Abs(window.Width - compactWidth - 12) <= 1.1 && Math.Abs(actual.Width - compactArtWidth) <= 1.1,
                        label + " reveals 12 more logical pixels without zooming the artwork.");
                    double direction = edge == DockEdge.Left ? 1 : -1;
                    Check(Math.Abs(window.Left + actual.Left - compactArtLeft - direction * 12) <= 1.1,
                        label + " moves the fixed-size artwork toward the screen interior.");
                }
                SpritePresenter presenter = (SpritePresenter)Field(window, "edgeSpritePresenter");
                Check(visible.Source == presenter.Output && visible.Opacity == 1,
                    label + " displays only the composed docking surface.");
                bool body = false, transparent = false;
                for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++)
                {
                    Point point = new Point((x + .5) * window.ActualWidth / 24, (y + .5) * window.ActualHeight / 24);
                    bool hit = (bool)Call(window, "ImageContains", visible, point);
                    if (hit && !body) { Check((bool)Call(window, "IsInteractiveAt", point), label + " visible final alpha remains draggable."); body = true; }
                    if (!hit && !transparent) { Check(!(bool)Call(window, "IsInteractiveAt", point), label + " transparent final alpha passes pointer input through."); transparent = true; }
                }
                Check(body && transparent, label + " samples both visible body and transparent space on the actual compact window.");
            }
            Call(window, "ExpandDock", false); window.UpdateLayout();
        }
        canvas.Children.Remove(reference);
    }
    private static void PureTests()
    {
        byte[] cyan = { 230, 190, 90, 255 }, other = { 220, 180, 80, 255 };
        BitmapSource first = Bitmap(cyan, cyan, cyan, cyan), next = Bitmap(cyan, cyan, cyan, other);
        SpritePresenter presenter = new SpritePresenter(2, 2);
        presenter.Present(first, "awake", 0, false, false); presenter.Present(next, "received", 0, false, false);
        foreach (long time in new long[] { 0, 30, 60, 90, 120 })
        {
            presenter.Present(next, "received", time, false, false);
            Check(Pixel(presenter.Output, 0).SequenceEqual(cyan), "An unchanged opaque coat pixel stays exactly opaque and the same color throughout a dissolve.");
        }
        Check(!presenter.IsTransitioning && Object.ReferenceEquals(presenter.Output, next), "Completed transitions return to the immutable prepared frame.");
        byte[] half = { 115, 95, 45, 128 }, halfOther = { 100, 80, 30, 128 };
        first = Bitmap(half, half, half, half); next = Bitmap(half, half, half, halfOther);
        presenter.Present(first, "sleep", 200, true, false); presenter.Present(next, "wake", 200, false, false);
        presenter.Present(next, "wake", 260, false, false);
        Check(Pixel(presenter.Output, 0).SequenceEqual(half), "A translucent outline remains alpha128 rather than being stacked to alpha192.");
        BitmapSource red = Solid(0, 0, 200, 255), blue = Solid(200, 0, 0, 255), green = Solid(0, 200, 0, 255);
        presenter.Present(red, "red", 400, true, false); presenter.Present(blue, "blue", 400, false, false);
        presenter.Present(blue, "blue", 460, false, false);
        byte[] visible = Pixels(presenter.Output); BitmapSource reusable = presenter.Output;
        presenter.Present(green, "green", 460, false, false);
        Check(Pixels(presenter.Output).SequenceEqual(visible), "A new task begins from the exact currently visible composition.");
        Check(Object.ReferenceEquals(reusable, presenter.Output), "Interrupted transitions reuse one writable output surface.");
        presenter.Present(green, "green", 520, false, false);
        Check(Pixel(presenter.Output, 0).SequenceEqual(new byte[] { 50, 100, 50, 255 }), "Interruption blends from the visible purple midpoint rather than an old red pose.");
        presenter.Present(blue, "drag", 521, true, false);
        Check(!presenter.IsTransitioning && Pixels(presenter.Output).SequenceEqual(Pixels(blue)), "Dragging preempts a transition on the same call.");
        int starts = presenter.TransitionStarts;
        presenter.Present(Solid(200, 0, 0, 255), "same endpoint, different file", 522, false, false);
        Check(presenter.TransitionStarts == starts && !presenter.IsTransitioning, "Byte-identical endpoints do not create a new fade or alpha change.");
        presenter.Present(red, "rest:awake-idle", 600, true, false); starts = presenter.TransitionStarts;
        presenter.Present(green, "rest:awake-idle", 640, false, false);
        Check(presenter.TransitionStarts == starts && Object.ReferenceEquals(presenter.Output, green), "Blink cels change directly, without a double-eye dissolve.");
        presenter.Present(red, "state:working", 700, true, false); starts = presenter.TransitionStarts;
        presenter.Present(blue, "state:working", 740, false, false);
        Check(presenter.TransitionStarts == starts && !presenter.IsTransitioning, "A page-turn cel cannot restart a boundary fade.");
        presenter.Present(red, "rest:fall-asleep", 800, true, false); starts = presenter.TransitionStarts;
        presenter.Present(blue, "rest:fall-asleep", 810, false, true);
        presenter.Present(green, "rest:fall-asleep", 840, false, true);
        Check(presenter.IsTransitioning && presenter.TransitionStarts == starts + 1, "Slow settling cels can soften without repeatedly restarting their bridge.");
        byte[] blank = { 0, 0, 0, 0 };
        BitmapSource left = Bitmap(cyan, blank, cyan, blank), right = Bitmap(blank, cyan, blank, cyan);
        presenter.Present(left, "left", 1000, true, false);
        Check(presenter.Hit(.25, .25) && !presenter.Hit(.75, .25), "Hit testing follows the initially visible body.");
        presenter.Present(right, "right", 1000, false, false); presenter.Present(right, "right", 1060, false, false);
        Check(presenter.Hit(.25, .25) && presenter.Hit(.75, .25), "Both visibly blended silhouettes remain draggable during a transition.");
        presenter.Present(right, "right", 1120, false, false);
        Check(!presenter.Hit(.25, .25) && presenter.Hit(.75, .25) && presenter.OpaqueBounds.X == .5, "Mutable output cannot leave an obsolete alpha mask or old visible bounds.");
        Check(!presenter.Hit(Double.NaN, .5) && !presenter.Hit(-.1, .5) && !presenter.Hit(1, .5), "Invalid or out-of-canvas pointers are transparent.");
        for (int i = 0; i < 40; i++) presenter.Present(Solid((byte)i, 0, 0, 255), "cache", 1200 + i, true, false);
        Check(presenter.CachedFrames <= 8, "Prepared pixel buffers have a bounded cache rather than duplicating every art frame.");
        SpritePresenter allocationProbe = new SpritePresenter(2, 2);
        for (int i = 0; i < 40; i++) allocationProbe.Present(Solid((byte)i, 0, 0, 255), "long clip", i, true, false);
        Check(allocationProbe.PixelBufferAllocations <= 8, "Long clips recycle bounded pixel buffers instead of allocating one large array per frame.");

        AnimationClip quiet = new AnimationClip { PlaybackMode = "quiet", RestFrame = 0, HoldFrame = 0, Duration = 300,
            BlinkFrames = new int[] { 1 }, BlinkDelays = new int[] { 100 }, BlinkMinMs = 10000, BlinkMaxMs = 10000,
            ActionFrames = new int[] { 2 }, ActionDelays = new int[] { 400 }, ActionInitialMinMs = 1500, ActionInitialMaxMs = 1500,
            ActionMinMs = 9000, ActionMaxMs = 9000 };
        quiet.Frames.AddRange(new BitmapSource[] { red, blue, green }); quiet.Delays.AddRange(new int[] { 100, 100, 100 });
        AnimationClip received = new AnimationClip { PlaybackMode = "once", Duration = 300, HoldFrame = 2 };
        received.Frames.AddRange(quiet.Frames); received.Delays.AddRange(quiet.Delays);
        var clips = new Dictionary<string, AnimationClip> { { "idle", quiet }, { "working", quiet }, { "received", received } };
        NaturalPlaybackController playback = new NaturalPlaybackController(clips, new Random(1)); playback.Request("working", 0, false);
        Check(playback.CanStartQuietOverlay(1499), "Gaze may begin during a genuinely still reading hold.");
        Check(!playback.CanStartQuietOverlay(1500) && playback.Sample(1600, false) == 2, "Gaze cannot steal a page turn that has already started.");
        playback.SetQuietOverlay(true, 1600);
        Check(playback.Sample(5000, false) == 0, "A gaze overlay does not advance hidden page-turn cels.");
        playback.SetQuietOverlay(false, 5000);
        Check(playback.Sample(5001, false) == 0 && playback.NextActionMs == 14000, "Gaze exit reveals a settled reading pose and schedules the next full page turn later.");
        playback.SetQuietOverlay(true, 5100); playback.Request("received", 5110, false);
        Check(playback.VisualState == "received" && playback.Sample(5110, false) == 0, "A real task still preempts an overlaid quiet cadence immediately.");
        WeightedEdgeTests();
    }

    [STAThread]
    public static int Main(string[] args)
    {
        try { PureTests(); }
        catch (Exception error) { Console.WriteLine("FAIL pure " + error); return 1; }
        if (args.Length == 0) { Console.WriteLine("PASS SpritePresentationTests " + checks + " checks"); return 0; }
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        string settingsPath = Path.Combine(App.UserDirectory, "settings.json");
        string settingsBefore = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        App app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; int result = 1;
        PetWindow window = new PetWindow(new string[] { "--demo", "--smoke-test", "--preview-state", "idle", "--assets-dir", args[0] }, null);
        window.Loaded += delegate
        {
            try
            {
                Image image = (Image)Field(window, "spriteImage");
                SpritePresenter presenter = (SpritePresenter)Field(window, "spritePresenter");
                Check(((Grid)Field(window, "legacySpriteLayer")).Opacity == 0 && image.Opacity == 1, "Only one final character image participates in actual drawing.");
                Check(image.Source == presenter.Output && image.Source.Width > 0 && image.Source.Height > 0, "Direct initialization and the final renderer use the same output.");
                foreach (string state in new string[] { "received", "working", "completed", "interrupted", "waiting", "error" })
                {
                    Call(window, "ApplyState", state, true);
                    Check(presenter.Sequence == "state:" + state && image.Source == presenter.Output && image.Opacity == 1, state + " updates the final image immediately without leaking old layers.");
                    Check(image.Width == 224 && image.Height == 200 && ((BitmapSource)image.Source).PixelWidth == 448 && ((BitmapSource)image.Source).PixelHeight == 400, state + " uses one physical presentation canvas.");
                }
                Call(window, "ApplyState", "idle", true); Call(window, "StartEnhancedAction", "hair", false);
                Check(presenter.Sequence == "enhanced:hair" && image.Source == presenter.Output, "Starting a personality action presents its own frame rather than an underlying sleeping pose.");
                long now = ((System.Diagnostics.Stopwatch)Field(window, "interactionClock")).ElapsedMilliseconds;
                ((RestIdleTimeline)Field(window, "restIdle")).Reset(now - 4145);
                Call(window, "CancelEnhancedAction", true); Call(window, "AdvanceEnhanced");
                Check(((RestIdleTimeline)Field(window, "restIdle")).Sample(now) == 0, "Returning from an idle overlay resets the blink clock instead of instantly closing the eyes.");
                DragInteractionController drag = (DragInteractionController)Field(window, "drag");
                drag.Begin(new Point(100, 100), new Point(window.Left, window.Top), false); drag.Move(new Point(160, 120), 4, 4);
                Call(window, "AdvanceAnimation");
                Check(presenter.Sequence == "state:dragging" && !presenter.IsTransitioning && image.Opacity == 1, "A drag immediately owns the only visible sprite, including during an interrupted dissolve.");
                Check(image.Width == 224 && image.Height == 200 && ((BitmapSource)image.Source).PixelWidth == 448 && ((BitmapSource)image.Source).PixelHeight == 400, "The shorter sprint source cannot expand the presentation canvas.");
                drag.End(false); Call(window, "ApplyState", "working", true); Call(window, "PresentSprite", now + 1000);
                window.UpdateLayout(); bool body = false;
                for (int y = 0; y < 30 && !body; y++) for (int x = 0; x < 30 && !body; x++)
                {
                    Point local = new Point((x + .5) * image.ActualWidth / 30, (y + .5) * image.ActualHeight / 30);
                    Point point = image.TransformToAncestor(window).Transform(local);
                    if ((bool)Call(window, "ImageContains", image, point))
                    { Check((bool)Call(window, "IsInteractiveAt", point), "Only the actual visible final alpha defines a draggable body."); body = true; }
                }
                Check(body && !(bool)Call(window, "IsInteractiveAt", new Point(1, 1)), "Body interaction and transparent-corner pass-through both survive the single-image renderer.");
                EdgeGeometryTests(window);
                Check(Field(window, "monitor") == null && (File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null) == settingsBefore,
                    "Rendering verification starts no account worker and changes no user settings.");
                Console.WriteLine("PASS SpritePresentationTests " + checks + " checks"); result = 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL UI " + error + "\n" + error.InnerException); }
            finally { app.Shutdown(result); }
        };
        app.Run(window); return result;
    }
}
