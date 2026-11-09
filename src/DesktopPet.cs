using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace RainbowDashDesktop
{
    public class App : Application
    {
        internal static readonly string UserDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainbowDashDesktop");

        [STAThread]
        public static int Main(string[] args)
        {
            Mutex instance = null;
            EventWaitHandle restore = null;
            bool special = args.Any(delegate(string value) { return value == "--smoke-test" || value == "--render-preview" || value == "--preview-rest"; });
            try
            {
                Directory.CreateDirectory(UserDirectory);
                // This small transparent overlay does not need the D3D pipeline.
                // Keep it independent of the render-thread failures recorded on this PC.
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                if (!special)
                {
                    bool created;
                    instance = new Mutex(true, "Local\\RainbowDashDesktop.Pet", out created);
                    if (!created)
                    {
                        try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting("Local\\RainbowDashDesktop.Restore")) signal.Set(); }
                        catch { }
                        instance.Dispose();
                        return 0;
                    }
                    restore = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\RainbowDashDesktop.Restore");
                }
                App app = new App();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                bool renderFailed = false;
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                {
                    e.Handled = true;
                    if (IsFatalRenderFailure(e.Exception))
                    {
                        // A poisoned WPF render partition cannot be healed by swallowing
                        // the exception; doing so left a frozen overlay and repeated logs.
                        if (!renderFailed)
                        { renderFailed = true; Log("Render", e.Exception); app.Shutdown(3); }
                        return;
                    }
                    Log("UI", e.Exception);
                };
                PetWindow window = new PetWindow(args, restore);
                int result = app.Run(window);
                return result;
            }
            catch (Exception ex)
            {
                Log("Startup", ex);
                if (IsFatalRenderFailure(ex)) return 3;
                if (!special) MessageBox.Show("云宝未能启动。诊断记录已保存到：\n" + System.IO.Path.Combine(UserDirectory, "app.log"), "云宝桌宠", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
            finally
            {
                if (restore != null) restore.Dispose();
                if (instance != null) { try { instance.ReleaseMutex(); } catch { } instance.Dispose(); }
            }
        }

        internal static bool IsFatalRenderFailure(Exception error)
        {
            for (Exception current = error; current != null; current = current.InnerException)
            {
                COMException interop = current as COMException;
                if (interop != null && interop.ErrorCode == unchecked((int)0x88980406)) return true;
            }
            return false;
        }

        internal static void Log(string context, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(UserDirectory);
                string path = System.IO.Path.Combine(UserDirectory, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512000) File.WriteAllText(path, "");
                File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + context + " " + ex + Environment.NewLine);
            }
            catch { }
        }
    }

    internal sealed partial class UiSettings
    {
        public UiSettings() { }
        public bool AutonomousPatrol;
        public double Left = Double.NaN;
        public double Top = Double.NaN;
        public double Scale = 1.0;
        public bool QuotaCollapsed;
        public bool EdgeDockingEnabled = true;
        public string DockSide = "None";
        public string DockMonitor = "";
        public double DockFraction = .5;
        public bool ManualFocus;
        public bool AutoFocusWhenFullscreen = true;
        public bool TransparentClickThrough = true;
        public bool NearbyGaze = true;
        public bool PersonalityActions = true;
        public int SleepDelayMinutes = 5;
    }

    internal sealed class AnimationClip
    {
        public readonly List<BitmapSource> Frames = new List<BitmapSource>();
        public readonly List<int> Delays = new List<int>();
        public int Duration;
        public int Width;
        public int Height;
        public bool HighQuality;
        public string PlaybackMode = "once";
        public int RestFrame;
        public int[] RestFrames = new int[0];
        public int HoldFrame = -1;
        public bool Grounded = true;
        public bool MirrorHorizontal;
        public int LoopStart;
        public int LoopEnd = -1;
        public int[] BlinkFrames = new int[0];
        public int[] BlinkDelays = new int[0];
        public int BlinkMinMs = 4500;
        public int BlinkMaxMs = 7800;
        public int[] ActionFrames = new int[0];
        public int[] ActionDelays = new int[0];
        public int ActionInitialMinMs = 3500;
        public int ActionInitialMaxMs = 5500;
        public int ActionMinMs = 9000;
        public int ActionMaxMs = 14000;

        public int FrameIndexAt(long elapsed, bool loop)
        {
            if (Frames.Count == 0) return -1;
            if (!loop && elapsed >= Duration) return Math.Max(0, Math.Min(Frames.Count - 1, HoldFrame < 0 ? Frames.Count - 1 : HoldFrame));
            int start = loop ? Math.Max(0, Math.Min(Frames.Count - 1, LoopStart)) : 0;
            int end = loop && LoopEnd >= 0 ? Math.Max(start, Math.Min(Frames.Count - 1, LoopEnd)) : Frames.Count - 1;
            long duration = 0;
            for (int i = start; i <= end; i++) duration += Delays[i];
            long tick = Math.Max(0, elapsed);
            if (loop)
            {
                long entryDuration = 0;
                for (int i = 0; i < start; i++) entryDuration += Delays[i];
                if (tick < entryDuration)
                {
                    for (int i = 0; i < start; i++) { if (tick < Delays[i]) return i; tick -= Delays[i]; }
                }
                tick = Math.Max(0, elapsed - entryDuration);
                if (duration > 0) tick %= duration;
            }
            for (int i = start; i <= end; i++) { if (tick < Delays[i]) return i; tick -= Delays[i]; }
            return end;
        }
    }

    // Mouse capture and authored sprint playback stay independent of task state.
    // Coordinates are screen DIPs, so moving the window cannot feed back into the drag.
    internal sealed class DragInteractionController
    {
        private Point pressedAt;
        private Point windowAtPress;
        private double directionExtremeX;
        private bool mayClick;
        public bool IsPressed { get; private set; }
        public bool IsDragging { get; private set; }
        public bool FacesRight { get; private set; }
        public const double DirectionHysteresis = 10;

        public void Begin(Point pointer, Point windowPosition, bool allowClick)
        {
            pressedAt = pointer; windowAtPress = windowPosition; directionExtremeX = pointer.X;
            mayClick = allowClick; IsPressed = true; IsDragging = false; FacesRight = false;
        }

        public Point Move(Point pointer, double horizontalThreshold, double verticalThreshold)
        {
            if (!IsPressed) return windowAtPress;
            Vector delta = pointer - pressedAt;
            if (!IsDragging && (Math.Abs(delta.X) >= Math.Max(1, horizontalThreshold) ||
                Math.Abs(delta.Y) >= Math.Max(1, verticalThreshold))) IsDragging = true;
            if (!IsDragging) return windowAtPress;
            if (FacesRight)
            {
                directionExtremeX = Math.Max(directionExtremeX, pointer.X);
                if (directionExtremeX - pointer.X >= DirectionHysteresis)
                { FacesRight = false; directionExtremeX = pointer.X; }
            }
            else
            {
                directionExtremeX = Math.Min(directionExtremeX, pointer.X);
                if (pointer.X - directionExtremeX >= DirectionHysteresis)
                { FacesRight = true; directionExtremeX = pointer.X; }
            }
            return windowAtPress + delta;
        }

        // Losing capture cancels a possible click; a completed drag can never click.
        public bool End(bool allowClick)
        {
            bool clicked = IsPressed && !IsDragging && mayClick && allowClick;
            IsPressed = false; IsDragging = false; mayClick = false;
            return clicked;
        }

        public string DisplayState(string taskState)
        { return IsDragging ? "dragging" : taskState; }
    }

    // Pure timeline controller: display-refresh frequency cannot restart a reaction.
    // Artwork owns pose changes; this class only schedules cels and meaningful holds.
    internal sealed class NaturalPlaybackController
    {
        private readonly IDictionary<string, AnimationClip> clips;
        private readonly Random random;
        private string requestedState;
        private string pendingState;
        private long startedMs;
        private long nextBlinkMs;
        private long blinkStartedMs = -1;
        private long nextActionMs;
        private long actionStartedMs = -1;
        private int restIndex;
        private bool quietOverlay;
        public string VisualState { get; private set; }
        internal long NextBlinkMs { get { return nextBlinkMs; } }
        internal long NextActionMs { get { return nextActionMs; } }

        public NaturalPlaybackController(IDictionary<string, AnimationClip> source, Random rng)
        { clips = source; random = rng ?? new Random(); }

        public void Request(string state, long now, bool restart)
        {
            if (!clips.ContainsKey(state)) state = "idle";
            if (!clips.ContainsKey(state)) return;
            if (requestedState == state && !restart) return;
            requestedState = state;
            AnimationClip current;
            // Let an acknowledgement complete before entering the working loop.
            // Cancellation/completion/error always preempt it immediately.
            if (!restart && VisualState == "received" && state == "working" &&
                clips.TryGetValue(VisualState, out current) && now - startedMs < current.Duration)
            { pendingState = state; return; }
            Begin(state, now);
        }

        private void Begin(string state, long now)
        {
            VisualState = state; pendingState = null; startedMs = now; blinkStartedMs = -1; actionStartedMs = -1; restIndex = 0;
            AnimationClip clip = clips[state];
            nextBlinkMs = now + NextBlinkDelay(clip);
            nextActionMs = clip.ActionFrames.Length == 0 ? Int64.MaxValue : now + NextActionDelay(clip, true);
        }

        private int NextBlinkDelay(AnimationClip clip)
        { return random.Next(Math.Max(1000, clip.BlinkMinMs), Math.Max(clip.BlinkMinMs + 1, clip.BlinkMaxMs + 1)); }

        private int NextActionDelay(AnimationClip clip, bool initial)
        {
            int min = Math.Max(1000, initial ? clip.ActionInitialMinMs : clip.ActionMinMs);
            int max = Math.Max(min, initial ? clip.ActionInitialMaxMs : clip.ActionMaxMs);
            return random.Next(min, max + 1);
        }

        internal bool CanStartQuietOverlay(long now)
        {
            AnimationClip clip;
            if (String.IsNullOrEmpty(VisualState) || !clips.TryGetValue(VisualState, out clip)) return false;
            if (clip.PlaybackMode != "idle" && clip.PlaybackMode != "quiet") return true;
            Sample(now, false);
            return actionStartedMs < 0 && blinkStartedMs < 0;
        }

        internal void SetQuietOverlay(bool active, long now)
        {
            if (quietOverlay == active) return;
            quietOverlay = active;
            if (!active) RestartQuietCadence(now);
        }

        internal void RestartQuietCadence(long now)
        {
            AnimationClip clip;
            if (String.IsNullOrEmpty(VisualState) || !clips.TryGetValue(VisualState, out clip) ||
                clip.PlaybackMode != "idle" && clip.PlaybackMode != "quiet") return;
            blinkStartedMs = actionStartedMs = -1;
            nextBlinkMs = now + NextBlinkDelay(clip);
            nextActionMs = clip.ActionFrames.Length == 0 ? Int64.MaxValue : now + NextActionDelay(clip, false);
        }

        public int Sample(long now, bool manualLoop)
        {
            if (String.IsNullOrEmpty(VisualState)) return -1;
            AnimationClip clip = clips[VisualState];
            if (pendingState != null && now - startedMs >= clip.Duration)
            { string next = pendingState; Begin(next, now); clip = clips[VisualState]; }
            long elapsed = Math.Max(0, now - startedMs);
            // A quiet preview keeps its authored reading cadence even when the
            // repeat-preview option is enabled for one-shot reactions.
            if (manualLoop && clip.PlaybackMode != "idle" && clip.PlaybackMode != "quiet") return clip.FrameIndexAt(elapsed, true);
            if (clip.PlaybackMode == "hold") return Math.Max(0, Math.Min(clip.Frames.Count - 1, clip.HoldFrame < 0 ? clip.Frames.Count - 1 : clip.HoldFrame));
            if (clip.PlaybackMode != "idle" && clip.PlaybackMode != "quiet") return clip.FrameIndexAt(elapsed, clip.PlaybackMode == "loop");
            if (quietOverlay) return RestIndex(clip);

            // Quiet states rest between mutually exclusive blink and authored
            // actions, such as turning one page. A task change preempts either.
            int frame;
            if (actionStartedMs >= 0)
            {
                if (SequenceFrame(clip, clip.ActionFrames, clip.ActionDelays, now - actionStartedMs, 130, out frame)) return frame;
                actionStartedMs = -1;
                nextActionMs = now + NextActionDelay(clip, false);
                nextBlinkMs = now + NextBlinkDelay(clip);
                return RestIndex(clip);
            }
            if (blinkStartedMs >= 0)
            {
                if (SequenceFrame(clip, clip.BlinkFrames, clip.BlinkDelays, now - blinkStartedMs, 35, out frame)) return frame;
                blinkStartedMs = -1; nextBlinkMs = now + NextBlinkDelay(clip); restIndex++;
            }
            if (clip.ActionFrames.Length > 0 && now >= nextActionMs)
            {
                actionStartedMs = now;
                if (SequenceFrame(clip, clip.ActionFrames, clip.ActionDelays, 0, 130, out frame)) return frame;
            }
            if (clip.BlinkFrames.Length > 0 && now >= nextBlinkMs)
            {
                blinkStartedMs = now;
                if (SequenceFrame(clip, clip.BlinkFrames, clip.BlinkDelays, 0, 35, out frame)) return frame;
            }
            return RestIndex(clip);
        }

        private static bool SequenceFrame(AnimationClip clip, int[] frames, int[] delays, long elapsed, int fallbackDelay, out int frame)
        {
            long tick = Math.Max(0, elapsed);
            for (int i = 0; i < frames.Length; i++)
            {
                int delay = i < delays.Length ? Math.Max(10, delays[i]) : fallbackDelay;
                if (tick < delay)
                { frame = Math.Max(0, Math.Min(clip.Frames.Count - 1, frames[i])); return true; }
                tick -= delay;
            }
            frame = -1; return false;
        }

        private int RestIndex(AnimationClip clip)
        {
            int index = clip.RestFrames.Length > 0 ? clip.RestFrames[restIndex % clip.RestFrames.Length] : clip.RestFrame;
            return Math.Max(0, Math.Min(clip.Frames.Count - 1, index));
        }
    }

    internal sealed partial class PetWindow : Window
    {
        private const double BaseWidth = 304;
        private const double StageHeight = 234;
        private const int GwlExStyle = -20;
        private const int WmMouseActivate = 0x0021;
        private const int WmDisplayChange = 0x007E;
        private readonly string[] states = { "idle", "received", "working", "interrupted", "completed", "waiting", "error" };
        private readonly Dictionary<string, AnimationClip> clips = new Dictionary<string, AnimationClip>();
        private readonly Stopwatch interactionClock = Stopwatch.StartNew();
        private readonly Stopwatch transitionClock = new Stopwatch();
        private readonly NaturalPlaybackController playback;
        private readonly DispatcherTimer pollTimer;
        private readonly DispatcherTimer saveTimer;
        private readonly CodexMonitor monitor;
        private readonly EventWaitHandle restoreSignal;
        private readonly bool demo;
        private readonly bool smokeTest;
        private readonly string previewPath;
        private readonly string previewState;
        private readonly string assetDirectoryOverride;
        private readonly UiSettings settings;
        private readonly Grid root;
        private readonly StackPanel normalStack;
        private readonly Canvas edgeCanvas;
        private readonly Image edgePony;
        private readonly Canvas legacyEdgeSpriteLayer;
        private readonly Image edgeSpriteImage;
        private readonly SpriteRasterCache edgeRasterCache = new SpriteRasterCache();
        private SpritePresenter edgeSpritePresenter;
        private int edgeSpriteWidth, edgeSpriteHeight;
        private readonly EdgeArt edgeArt;
        private readonly EnhancedArt enhancedArt;
        private readonly SpriteAppearance spriteAppearance;
        private readonly SpritePresenter spritePresenter = new SpritePresenter();
        private readonly Grid legacySpriteLayer;
        private readonly Image spriteImage;
        private bool batchingSpritePresentation;
        private bool spriteQuietOverlay;
        private readonly RestArt restArt;
        private readonly RestController rest = new RestController();
        private readonly RestIdleTimeline restIdle = new RestIdleTimeline();
        private readonly RestPointerActivity restPointer = new RestPointerActivity();
        private readonly Image restPony;
        private readonly Image restPreviousPony;
        private readonly string previewRest;
        private readonly int previewRestFrame = -1;
        private string manualRestPreview;
        private DateTime manualRestUntil;
        private long manualRestStartedAt;
        private long handledWakeVersion;
        private RestStage lastRestStage = RestStage.Awake;
        private long sleepingStartedAt;
        private long restCelChangedAt;
        private long restExitAt = -1;
        private string paintedRestName;
        private int paintedRestFrame = -1;
        private string wakeVisualHoldState;
        private long wakeVisualHoldUntil;
        private bool pressWokePet;
        private bool dragWakeVisualSuppressed;
        private long sleepPointerGraceUntil;
        private string pendingAwakeAction;
        private readonly EnhancedAction enhancedAction = new EnhancedAction();
        private readonly Dictionary<BitmapSource, Rect> opaqueBoundsCache = new Dictionary<BitmapSource, Rect>();
        private readonly Image enhancedPony;
        private readonly Image dockActionPony;
        private readonly FocusController focus = new FocusController();
        private readonly GazeController gaze = new GazeController();
        private readonly QuietPersonalityScheduler personality = new QuietPersonalityScheduler();
        private readonly DispatcherTimer interactionTimer;
        private FocusReturnPosition focusReturn;
        private bool focusApplied;
        private bool updatingWindowBounds;
        private string lastEnhancedName;
        private long overlayStartedAt;
        private long overlayEndingAt = -1;
        private long lastFocusPoll = -1000;
        private int gazeFrame = -1;
        private MenuItem focusMenu;
        private bool dockLeaving;
        private long dockLeavingAt;
        private Rect dockLeavingFrom, dockLeavingTo;
        private bool dockLeaveSave;
        private readonly string previewAction;
        private readonly int previewActionFrame = -1;
        private readonly bool previewFocus;
        private readonly EdgeExpressionController edgeExpression = new EdgeExpressionController();
        private readonly EdgeDockHoverController edgeHover = new EdgeDockHoverController();
        private EdgeDockTarget dockTarget;
        private bool dockTransition;
        private long dockTransitionAt;
        private Rect dockTransitionFrom;
        private bool edgePressed;
        private Point edgePressPoint;
        private readonly string previewEdge;
        private readonly bool previewEdgeHover;
        private readonly int previewEdgeFrame = -1;
        private bool IsDocked { get { return dockTarget != null; } }
        private readonly Image pony;
        private readonly Image previousPony;
        private readonly Grid petStage;
        private readonly TextBlock missingArt;
        private readonly Border statusPill;
        private readonly TextBlock statusDot;
        private readonly TextBlock statusText;
        private readonly QuotaRing quotaRing;
        private Forms.NotifyIcon tray;
        private Drawing.Icon trayIcon;
        private string shownState = "";
        private string statusColorState;
        private string manualState;
        private DateTime manualUntil;
        private bool manualInteraction;
        private bool manualPreviewLoop;
        private string paintedState;
        private readonly DragInteractionController drag = new DragInteractionController();
        private long dragStartedMs;
        private double hoverStarted = -100;
        private double lastHoverStarted = -100;
        private DateTime hoverTextUntil;
        private int demoIndex = 2;
        private DateTime demoNext = DateTime.UtcNow.AddSeconds(8);
        private PetSnapshot lastSnapshot;
        private bool closing;
        private bool positioned;
        private bool renderingHooked;
        private bool hadReadFailure;
        private int telemetryFailures;
        private DateTime refreshFeedbackUntil;
        private const string SettingsFile = "settings.json";

        public PetWindow(string[] args, EventWaitHandle restore)
        {
            restoreSignal = restore;
            int assetsIndex = Array.IndexOf(args, "--assets-dir");
            if (assetsIndex >= 0 && assetsIndex + 1 < args.Length) assetDirectoryOverride = System.IO.Path.GetFullPath(args[assetsIndex + 1]);
            smokeTest = args.Contains("--smoke-test");
            int previewIndex = Array.IndexOf(args, "--render-preview");
            if (previewIndex >= 0)
            {
                if (previewIndex + 1 >= args.Length) throw new ArgumentException("--render-preview requires a PNG output path.");
                previewPath = System.IO.Path.GetFullPath(args[previewIndex + 1]);
            }
            demo = args.Contains("--demo") || previewPath != null;
            int statePreviewIndex = Array.IndexOf(args, "--preview-state");
            if (statePreviewIndex >= 0 && statePreviewIndex + 1 < args.Length)
            {
                previewState = args[statePreviewIndex + 1].ToLowerInvariant();
                int selectedState = Array.IndexOf(states, previewState);
                if (selectedState < 0 && previewState != "dragging") throw new ArgumentException("Unknown preview state.");
                if (selectedState >= 0) demoIndex = selectedState;
            }
            int edgeIndex = Array.IndexOf(args, "--preview-edge");
            if (edgeIndex >= 0 && edgeIndex + 1 < args.Length)
            {
                previewEdge = args[edgeIndex + 1].ToLowerInvariant();
                DockEdge selected;
                if (!Enum.TryParse<DockEdge>(previewEdge, true, out selected) || selected == DockEdge.None)
                    throw new ArgumentException("Unknown preview edge.");
            }
            previewEdgeHover = args.Contains("--edge-hover");
            int edgeFrameIndex = Array.IndexOf(args, "--edge-frame");
            if (edgeFrameIndex >= 0 && edgeFrameIndex + 1 < args.Length)
            {
                int value;
                if (!Int32.TryParse(args[edgeFrameIndex + 1], out value) || value < 0 || value > 8)
                    throw new ArgumentException("--edge-frame must be between 0 and 8.");
                previewEdgeFrame = value;
            }
            int actionIndex = Array.IndexOf(args, "--preview-action");
            if (actionIndex >= 0 && actionIndex + 1 < args.Length)
            {
                previewAction = args[actionIndex + 1].ToLowerInvariant();
                if (Array.IndexOf(EnhancedArt.Names, previewAction) < 0) throw new ArgumentException("Unknown enhanced action.");
            }
            int actionFrameIndex = Array.IndexOf(args, "--action-frame");
            if (actionFrameIndex >= 0 && actionFrameIndex + 1 < args.Length)
            {
                int value;
                if (!Int32.TryParse(args[actionFrameIndex + 1], out value) || value < 0 || value > 5) throw new ArgumentException("--action-frame must be between 0 and 5.");
                previewActionFrame = value;
            }
            previewFocus = args.Contains("--preview-focus");
            int restIndex = Array.IndexOf(args, "--preview-rest");
            if (restIndex >= 0)
            {
                if (restIndex + 1 >= args.Length) throw new ArgumentException("--preview-rest requires awake, sleeping, fall or wake.");
                previewRest = args[restIndex + 1].ToLowerInvariant();
                if (previewRest != "awake" && previewRest != "sleeping" && previewRest != "fall" && previewRest != "wake")
                    throw new ArgumentException("Unknown rest preview.");
                demo = true;
            }
            int restFrameIndex = Array.IndexOf(args, "--rest-frame");
            if (restFrameIndex >= 0)
            {
                int value;
                if (restFrameIndex + 1 >= args.Length || !Int32.TryParse(args[restFrameIndex + 1], out value) || value < 0 || value > 5)
                    throw new ArgumentException("--rest-frame must be between 0 and 5.");
                previewRestFrame = value;
            }
            settings = LoadSettings();
            if (previewPath != null || smokeTest || previewRest != null) settings = new UiSettings();
            rest.Configure(settings.SleepDelayMinutes * 60000L, interactionClock.ElapsedMilliseconds);

            Title = "云宝 · Codex 桌宠";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = settings.KeepOnTop;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            LoadClips();
            string assets = assetDirectoryOverride ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "assets");
            edgeArt = new EdgeArt(assets);
            enhancedArt = new EnhancedArt(assets);
            spriteAppearance = new SpriteAppearance(assets);
            restArt = new RestArt(assets);
            focus.SetManual(settings.ManualFocus || previewFocus);
            playback = new NaturalPlaybackController(clips, null);
            // Previews are self-contained and must not start account/network workers.
            monitor = demo || smokeTest ? null : new CodexMonitor();

            root = new Grid { Width = BaseWidth, Background = Brushes.Transparent, ClipToBounds = true };
            Content = root;
            StackPanel stack = new StackPanel { Width = BaseWidth, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            normalStack = stack;
            root.Children.Add(stack);

            Grid stage = new Grid { Width = BaseWidth - 24, Height = StageHeight, Margin = new Thickness(12, 2, 12, 6), Background = Brushes.Transparent };
            petStage = stage;
            stage.MouseEnter += PetMouseEnter;
            stage.MouseLeftButtonDown += PetPointerDown;
            stage.MouseMove += PetPointerMove;
            stage.MouseLeftButtonUp += PetPointerUp;
            stage.LostMouseCapture += delegate { FinishPointerGesture(false); };
            stack.Children.Add(stage);

            Ellipse shadow = new Ellipse
            {
                Width = 116, Height = 9, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 29), Fill = Brush("#4208192A")
            };
            shadow.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 9 };
            stage.Children.Add(shadow);
            // Retain timeline outputs for compatibility, but only spriteImage
            // draws the character. Opacity belongs to the final compositor.
            legacySpriteLayer = new Grid { Opacity = 0, IsHitTestVisible = false };
            stage.Children.Add(legacySpriteLayer);

            pony = new Image
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 29), Stretch = Stretch.Fill,
                IsHitTestVisible = false, RenderTransformOrigin = new Point(.5, .5)
            };
            RenderOptions.SetBitmapScalingMode(pony, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(pony, EdgeMode.Aliased);
            legacySpriteLayer.Children.Add(pony);
            previousPony = new Image
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 29), Stretch = Stretch.Fill, IsHitTestVisible = false,
                Opacity = 0, Visibility = Visibility.Collapsed, RenderTransformOrigin = new Point(.5, .5)
            };
            RenderOptions.SetBitmapScalingMode(previousPony, BitmapScalingMode.HighQuality);
            legacySpriteLayer.Children.Add(previousPony);
            enhancedPony = new Image { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Width = 224, Height = 200, Margin = new Thickness(0, 0, 0, 29), Stretch = Stretch.Uniform,
                Visibility = Visibility.Collapsed, IsHitTestVisible = false, RenderTransformOrigin = new Point(.5, .5) };
            RenderOptions.SetBitmapScalingMode(enhancedPony, BitmapScalingMode.HighQuality);
            legacySpriteLayer.Children.Add(enhancedPony);
            restPony = new Image { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Width = 224, Height = 200, Margin = new Thickness(0, 0, 0, 29), Stretch = Stretch.Uniform,
                Visibility = Visibility.Collapsed, IsHitTestVisible = false, RenderTransformOrigin = new Point(.5, .5),
                RenderTransform = new ScaleTransform(-1, 1) };
            restPreviousPony = new Image { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Width = 224, Height = 200, Margin = new Thickness(0, 0, 0, 29), Stretch = Stretch.Uniform,
                Visibility = Visibility.Collapsed, IsHitTestVisible = false, RenderTransformOrigin = new Point(.5, .5),
                RenderTransform = new ScaleTransform(-1, 1) };
            RenderOptions.SetBitmapScalingMode(restPony, BitmapScalingMode.HighQuality);
            RenderOptions.SetBitmapScalingMode(restPreviousPony, BitmapScalingMode.HighQuality);
            legacySpriteLayer.Children.Add(restPony); legacySpriteLayer.Children.Add(restPreviousPony);
            spriteImage = new Image { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Width = 224, Height = 200, Margin = new Thickness(0, 0, 0, 29), Stretch = Stretch.Fill,
                IsHitTestVisible = false, Opacity = 1 };
            RenderOptions.SetBitmapScalingMode(spriteImage, BitmapScalingMode.HighQuality);
            stage.Children.Add(spriteImage);
            missingArt = new TextBlock
            {
                Text = "未找到动画资源", Foreground = Brush("#FFDFE8F4"), FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            stage.Children.Add(missingArt);

            statusPill = new Border
            {
                CornerRadius = new CornerRadius(12), Background = Brush("#F01B2437"), BorderBrush = Brush("#59425671"),
                BorderThickness = new Thickness(1), Padding = new Thickness(11, 5, 12, 5),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3)
            };
            StackPanel pillInner = new StackPanel { Orientation = Orientation.Horizontal };
            statusDot = new TextBlock { Text = "●", FontSize = 9, Margin = new Thickness(0, 1, 6, 0) };
            statusText = new TextBlock { FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brush("#FFE2EBF7") };
            pillInner.Children.Add(statusDot); pillInner.Children.Add(statusText);
            statusPill.Child = pillInner;
            stage.Children.Add(statusPill);

            quotaRing = new QuotaRing();
            quotaRing.Root.HorizontalAlignment = HorizontalAlignment.Right;
            quotaRing.Root.VerticalAlignment = VerticalAlignment.Bottom;
            quotaRing.Root.Margin = new Thickness(0, 0, 16, 0);
            quotaRing.Root.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ClickCount == 2) { FinishPointerGesture(false); RefreshQuota(); e.Handled = true; }
                else if (IsDocked) e.Handled = true;
                else DragSurface(sender, e);
            };
            ToolTipService.SetInitialShowDelay(quotaRing.Root, 250);
            ToolTipService.SetShowDuration(quotaRing.Root, 20000);
            stage.Children.Add(quotaRing.Root);
            quotaRing.Update(new PetSnapshot(), demo, false);

            edgeCanvas = new Canvas { Background = Brushes.Transparent, Visibility = Visibility.Collapsed };
            root.Children.Add(edgeCanvas);
            legacyEdgeSpriteLayer = new Canvas { Opacity = 0, IsHitTestVisible = false };
            edgeCanvas.Children.Add(legacyEdgeSpriteLayer);
            edgePony = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false, RenderTransformOrigin = new Point(.5, .5) };
            RenderOptions.SetBitmapScalingMode(edgePony, BitmapScalingMode.HighQuality);
            legacyEdgeSpriteLayer.Children.Add(edgePony);
            dockActionPony = new Image { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
                RenderTransformOrigin = new Point(.5, .5) };
            RenderOptions.SetBitmapScalingMode(dockActionPony, BitmapScalingMode.HighQuality);
            legacyEdgeSpriteLayer.Children.Add(dockActionPony);
            edgeSpriteImage = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, Opacity = 1 };
            RenderOptions.SetBitmapScalingMode(edgeSpriteImage, BitmapScalingMode.HighQuality);
            edgeCanvas.Children.Add(edgeSpriteImage);
            edgeCanvas.MouseEnter += delegate { edgeHover.SetPointer(true, interactionClock.ElapsedMilliseconds); };
            edgeCanvas.MouseLeave += delegate { edgeHover.SetPointer(false, interactionClock.ElapsedMilliseconds); };
            edgeCanvas.MouseLeftButtonDown += EdgePointerDown;
            edgeCanvas.MouseMove += EdgePointerMove;
            edgeCanvas.MouseLeftButtonUp += EdgePointerUp;
            edgeCanvas.LostMouseCapture += delegate { edgePressed = false; };
            root.ContextMenu = BuildContextMenu();
            ApplyScaleAndCollapse();
            ApplyState(previewRest != null ? "idle" : demo ? (previewState ?? "working") : "idle");

            pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            pollTimer.Tick += delegate { Poll(); };
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            saveTimer.Tick += delegate { saveTimer.Stop(); SaveSettings(); };
            SourceInitialized += WindowSourceInitialized;
            Loaded += WindowLoaded;
            LocationChanged += delegate { if (positioned && !closing && previewPath == null && !smokeTest && !PatrolInProgress) { saveTimer.Stop(); saveTimer.Start(); } };
            interactionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            interactionTimer.Tick += delegate { UpdateInteraction(); };
            IsVisibleChanged += delegate { OnPetVisibilityChanged(); };
            Closing += delegate { closing = true; DisposeResources(); };
        }

        private void WindowLoaded(object sender, RoutedEventArgs e)
        {
            if (!Double.IsNaN(settings.Left) && !Double.IsNaN(settings.Top))
            { Left = settings.Left; Top = settings.Top; }
            else
            {
                Rect area = SystemParameters.WorkArea;
                Left = area.Right - ActualWidth - 22;
                Top = area.Bottom - ActualHeight - 24;
            }
            ClampToScreen();
            if (previewEdge != null)
            {
                DockEdge edge;
                Enum.TryParse<DockEdge>(previewEdge, true, out edge);
                EnterDock(EdgeDockGeometry.Resolve(edge, "", .5, GetEdgeMonitors(), previewEdgeHover, settings.Scale), false);
            }
            else if (settings.EdgeDockingEnabled)
            {
                DockEdge edge;
                if (Enum.TryParse<DockEdge>(settings.DockSide, true, out edge) && edge != DockEdge.None)
                    EnterDock(EdgeDockGeometry.Resolve(edge, settings.DockMonitor, settings.DockFraction, GetEdgeMonitors(), false, new Point(Left, Top), settings.Scale), false);
            }
            positioned = true;
            if (previewAction != null) StartEnhancedAction(previewAction, false);
            if (focus.IsActive) ApplyFocusState(true);
            if (!demo && !smokeTest) interactionTimer.Start();
            if (!smokeTest && previewPath == null) CreateTray();
            Poll();
            AdvanceAnimation();
            renderCadenceReady = true;
            UpdateRenderCadence();
            pollTimer.Start();
            if (previewPath != null)
            {
                DispatcherTimer previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                previewTimer.Tick += delegate
                {
                    previewTimer.Stop();
                    try { SavePreview(previewPath); Application.Current.Shutdown(0); }
                    catch (Exception ex) { App.Log("Preview", ex); Application.Current.Shutdown(2); }
                };
                previewTimer.Start();
            }
            if (smokeTest)
            {
                DispatcherTimer smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                smokeTimer.Tick += delegate
                {
                    smokeTimer.Stop();
                    bool valid = states.Concat(new string[] { "dragging" }).All(delegate(string state) { return clips.ContainsKey(state) && clips[state].Frames.Count > 1; }) &&
                        RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly && edgeArt.Complete && enhancedArt.Complete && restArt.Complete;
                    if (!valid) App.Log("Smoke", new InvalidOperationException("Every animation, edge cel, enhanced action and rest cel must load, and software rendering must be active."));
                    Application.Current.Shutdown(valid && !hadReadFailure ? 0 : 2);
                };
                smokeTimer.Start();
            }
        }

        private void WindowSourceInitialized(object sender, EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | 0x00000080 | 0x08000000);
            HwndSource source = HwndSource.FromHwnd(handle);
            if (source != null)
            {
                source.AddHook(WindowMessage);
                RegisterVisibilityShortcut(handle);
            }
        }

        private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (HandleVisibilityShortcut(msg, wParam)) { handled = true; return IntPtr.Zero; }
            if (msg == WmMouseActivate) { handled = true; return new IntPtr(3); }
            if (msg == 0x0084 && !closing && settings.TransparentClickThrough && !drag.IsPressed && !edgePressed && !MenuIsOpen())
            {
                long packed = lParam.ToInt64();
                Point physical = new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
                if (!IsInteractiveAt(PointFromScreen(physical))) { handled = true; return new IntPtr(-1); }
            }
            if (msg == WmDisplayChange || msg == 0x02E0 || (msg == 0x001A && wParam.ToInt64() == 0x002F))
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    CancelPatrol(interactionClock.ElapsedMilliseconds, false);
                    if (IsDocked) RefreshDockGeometry(); else ClampToScreen();
                }));
            return IntPtr.Zero;
        }

        private void Poll()
        {
            try
            {
                if (restoreSignal != null && restoreSignal.WaitOne(0)) Restore();
                PetSnapshot snapshot;
                if (demo || smokeTest)
                {
                    if (previewPath == null && previewState == null && previewRest == null && DateTime.UtcNow >= demoNext)
                    { demoIndex = (demoIndex + 1) % states.Length; demoNext = DateTime.UtcNow.AddSeconds(states[demoIndex] == "working" ? 8 : 4); }
                    snapshot = new PetSnapshot
                    {
                        State = previewRest != null ? "idle" : previewState ?? states[demoIndex], CodexRunning = true,
                        ActiveTasks = previewRest != null || previewState == "idle" ? 0 : 1,
                        FiveHourRemaining = 72, WeekRemaining = 86,
                        FiveHourResetUtc = DateTime.UtcNow.AddHours(3).AddMinutes(21),
                        WeekResetUtc = DateTime.UtcNow.AddDays(4).AddHours(12),
                        QuotaUpdatedUtc = DateTime.UtcNow, Source = "演示数据", Detail = "动画与额度显示演示；数值不是账户真实额度。"
                    };
                }
                else snapshot = monitor.Read() ?? new PetSnapshot();
                telemetryFailures = 0;
                lastSnapshot = snapshot;
                quotaRing.Update(snapshot, demo, DateTime.UtcNow < refreshFeedbackUntil);
                UpdateQuotaDetails();
                string state = String.IsNullOrEmpty(snapshot.State) ? "idle" : snapshot.State.ToLowerInvariant();
                if (!clips.ContainsKey(state)) state = "idle";
                ObserveRestTask(state, snapshot.ActiveTasks, interactionClock.ElapsedMilliseconds);
                if (manualState != null && DateTime.UtcNow < manualUntil) state = manualState;
                else { manualState = null; manualInteraction = false; }
                ApplyState(state);
                string detail = CleanText(snapshot.Detail, 180);
                statusPill.ToolTip = String.IsNullOrEmpty(detail) ? StateLabel(state) : StateLabel(state) + "\n" + detail;
                if (!demo)
                {
                    string health = ConnectionLabel(snapshot);
                    if (snapshot.SessionCount > 0) health += " · " + snapshot.SessionCount.ToString(CultureInfo.InvariantCulture) + " 个会话";
                    if (!String.IsNullOrEmpty(snapshot.LastReadFailure) && snapshot.ConnectionStatus == "retrying") health += "\n" + snapshot.LastReadFailure;
                    statusPill.ToolTip = statusPill.ToolTip + "\n" + health;
                }
                if (manualState != null) statusPill.ToolTip = (manualInteraction ? "轻点互动" : "动画预览") + " · 稍后自动恢复\n" + StateLabel(state);
                UpdateStatusText(snapshot);
                UpdateRenderCadence();
            }
            catch (Exception ex)
            {
                if (App.IsFatalRenderFailure(ex)) throw;
                hadReadFailure = true;
                telemetryFailures++;
                if (telemetryFailures == 1 || telemetryFailures % 60 == 0) App.Log("Telemetry", ex);
                if (lastSnapshot == null) quotaRing.Update(new PetSnapshot(), demo, false);
                quotaRing.SetFeedback("本机状态暂不可用；稍后自动重试。");
            }
        }

        internal static string ConnectionLabel(PetSnapshot snapshot)
        {
            if (snapshot.ConnectionStatus == "retrying") return "本机状态读取暂不可用 · 自动重连中";
            if (snapshot.ConnectionStatus == "connected") return "本机 ChatGPT / Codex 状态已同步";
            return snapshot.CodexRunning ? "正在同步本机任务状态…" : "等待本机 ChatGPT / Codex 启动";
        }

        internal static string QuotaStatusLabel(PetSnapshot snapshot)
        {
            if (snapshot.QuotaStatus == "refreshing") return "正在同步额度…";
            if (snapshot.QuotaStatus == "expired") return "额度窗口已重置，正在获取新额度…";
            if (snapshot.QuotaStatus == "retrying")
            {
                if (snapshot.QuotaLastFailure == "auth-required") return "额度需要登录 · 请检查 ChatGPT / Codex 登录状态";
                return "额度刷新暂时失败 · 保留最近记录，自动重试中";
            }
            if (snapshot.QuotaStatus == "unavailable") return "暂未取得额度 · 将自动重试";
            return snapshot.QuotaSource == "account/rateLimits/read" ? "官方额度接口 · 每分钟自动刷新" : "本地额度快照 · 等待官方接口同步";
        }

        private static string SyncLabel(DateTime? updated)
        {
            if (!updated.HasValue) return "未同步";
            TimeSpan age = DateTime.UtcNow - ToUtc(updated.Value);
            if (age.TotalSeconds < 60) return "刚刚同步";
            if (age.TotalMinutes < 60) return Math.Max(1, (int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " 分钟前";
            if (age.TotalHours < 24) return ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + " 小时前";
            return "上次 " + updated.Value.ToLocalTime().ToString("MM-dd HH:mm");
        }

        private void ApplyState(string state)
        { ApplyState(state, false); }

        private void ApplyState(string state, bool restart)
        {
            if (shownState == state && !restart) return;
            if (state != "idle") CancelPatrol(interactionClock.ElapsedMilliseconds);
            CancelPetTouch();
            string previousState = shownState;
            shownState = state;
            if (state == "idle" && previousState != "idle") restIdle.Reset(interactionClock.ElapsedMilliseconds);
            gaze.Reset(interactionClock.ElapsedMilliseconds); gazeFrame = -1;
            enhancedAction.Cancel();
            statusText.Text = StateLabel(state);
            long now = interactionClock.ElapsedMilliseconds;
            // Task metadata updates immediately. Only its presentation waits for
            // waking; a newer poll replaces the pending target, never the wake clock.
            if (rest.Stage != RestStage.Awake || state == "idle" && wakeVisualHoldState != null && now < wakeVisualHoldUntil)
            { AdvanceAnimation(); UpdateStatusText(lastSnapshot); return; }
            if (state != "idle" && state != wakeVisualHoldState) { wakeVisualHoldState = null; wakeVisualHoldUntil = 0; }
            string bridge = !ReducedMotionActive && !restart && previousState == "working" && playback.VisualState == "working" && !IsDocked && !drag.IsPressed && !focus.IsActive ?
                state == "completed" ? "close-book" : state == "interrupted" ? "interrupt-book" : null : null;
            if (bridge != null && enhancedArt.Has(bridge))
                enhancedAction.Start(bridge, interactionClock.ElapsedMilliseconds, bridge == "close-book" ? 840 : 420, state, false);
            else playback.Request(state, interactionClock.ElapsedMilliseconds, restart);
            AdvanceAnimation();
            UpdateStatusText(lastSnapshot);
        }

        private void PaintState(string state)
        {
            BitmapSource previous = pony.Source as BitmapSource;
            previousPony.Source = previous;
            previousPony.Width = pony.ActualWidth;
            previousPony.Height = pony.ActualHeight;
            previousPony.RenderTransform = pony.RenderTransform == null ? Transform.Identity : pony.RenderTransform.CloneCurrentValue();
            AnimationClip outgoing, incoming;
            bool fade = previous != null && !String.IsNullOrEmpty(paintedState) &&
                clips.TryGetValue(paintedState, out outgoing) && outgoing.Grounded &&
                clips.TryGetValue(state, out incoming) && incoming.Grounded;
            previousPony.Visibility = fade ? Visibility.Visible : Visibility.Collapsed;
            previousPony.Opacity = fade ? 1 : 0;
            if (fade) transitionClock.Restart(); else transitionClock.Reset();
            paintedState = state;
            AnimationClip clip;
            if (clips.TryGetValue(state, out clip) && clip.Frames.Count > 0)
            {
                pony.Visibility = Visibility.Visible; missingArt.Visibility = Visibility.Collapsed;
                double factor = clip.HighQuality ? Math.Min(224.0 / clip.Width, 200.0 / clip.Height) : clip.Height > 106 ? 1.6 : 2.0;
                pony.Width = Math.Round(clip.Width * factor); pony.Height = Math.Round(clip.Height * factor);
                RenderOptions.SetBitmapScalingMode(pony, clip.HighQuality ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
                RenderOptions.SetEdgeMode(pony, clip.HighQuality ? EdgeMode.Unspecified : EdgeMode.Aliased);
                pony.Source = clip.Frames[0];
                SetPonyFacing(state, clip);
            }
            else
            { pony.Visibility = Visibility.Collapsed; missingArt.Visibility = Visibility.Visible; }
        }

        private void AdvanceAnimation()
        {
            long now = interactionClock.ElapsedMilliseconds;
            AdvanceRestState(now);
            // Advance the real task even while the pointer owns the visual pose.
            int index = playback.Sample(now, manualState != null && !manualInteraction && manualPreviewLoop);
            // Keep the released sprint cel as the outgoing pose during the short
            // edge entrance, while the independent task timeline continues to run.
            if (dockTransition || dockLeaving) return;
            string visualState = drag.DisplayState(playback.VisualState);
            if (drag.IsDragging && !clips.ContainsKey("dragging")) visualState = playback.VisualState;
            if (visualState != paintedState && !String.IsNullOrEmpty(visualState)) PaintState(visualState);
            AnimationClip clip;
            if (clips.TryGetValue(visualState ?? "", out clip))
            {
                if (drag.IsDragging && visualState == "dragging") index = clip.FrameIndexAt(now - dragStartedMs, true);
                if (index >= 0 && index < clip.Frames.Count)
                {
                    BitmapSource frame = clip.Frames[index];
                    if (!Object.ReferenceEquals(pony.Source, frame)) pony.Source = frame;
                }
                SetPonyFacing(visualState, clip);
            }
            if (transitionClock.IsRunning)
            {
                // A short dissolve bridges tiny source-pose differences without
                // resizing, sliding, banking or adding motion to planted hooves.
                double progress = Math.Min(1, transitionClock.Elapsed.TotalMilliseconds / 120.0);
                previousPony.Opacity = 1 - progress * progress * (3 - 2 * progress);
                if (progress >= 1) { previousPony.Visibility = Visibility.Collapsed; previousPony.Source = null; transitionClock.Stop(); }
            }
            AdvanceRestVisual(now);
        }

        private void SetPonyFacing(string state, AnimationClip clip)
        {
            bool mirror = clip.MirrorHorizontal ^ (state == "dragging" && drag.IsDragging && drag.FacesRight);
            double scaleX = mirror ? -1 : 1;
            ScaleTransform current = pony.RenderTransform as ScaleTransform;
            if (current == null || current.ScaleX != scaleX) pony.RenderTransform = new ScaleTransform(scaleX, 1);
        }

        private string EffectiveRestPreview
        {
            get
            {
                if (previewRest != null) return previewRest;
                return manualRestPreview != null && DateTime.UtcNow < manualRestUntil ? manualRestPreview : null;
            }
        }

        private void ObserveRestTask(string state, int activeTasks, long now)
        {
            string before = rest.RequestedTaskState;
            rest.ObserveTask(state, activeTasks, now);
            if (state != "idle" && state != before)
            {
                manualRestPreview = null; pendingAwakeAction = null;
                manualState = null; manualInteraction = false;
                wakeVisualHoldState = null; wakeVisualHoldUntil = 0;
            }
            AdvanceRestState(now);
        }

        private void NotifyRestInteraction(RestAttentionKind kind)
        {
            long now = interactionClock.ElapsedMilliseconds;
            rest.NotifyInteraction(kind, now);
            if (kind == RestAttentionKind.Drag && rest.Stage == RestStage.Waking) dragWakeVisualSuppressed = true;
            if (kind != RestAttentionKind.Pointer)
            { manualRestPreview = null; wakeVisualHoldState = null; wakeVisualHoldUntil = 0; }
            if (kind == RestAttentionKind.Drag) pendingAwakeAction = null;
            AdvanceRestState(now);
        }

        private void AdvanceRestState(long now)
        {
            rest.Advance(now);
            if (rest.Stage != lastRestStage)
            {
                if (rest.Stage != RestStage.Awake)
                {
                    CancelEnhancedAction(false); gaze.Reset(now); gazeFrame = -1;
                    pendingAwakeAction = null;
                }
                if (rest.Stage == RestStage.Sleeping) sleepingStartedAt = now;
                if (rest.Stage == RestStage.Awake)
                { dragWakeVisualSuppressed = false; restIdle.Reset(now); personality.Postpone(now); gaze.Reset(now); gazeFrame = -1; }
                lastRestStage = rest.Stage;
            }
            if (rest.WakeCompletedVersion != handledWakeVersion)
            {
                handledWakeVersion = rest.WakeCompletedVersion;
                string target = rest.LastWakeTarget;
                if (String.IsNullOrEmpty(target) || !clips.ContainsKey(target)) target = "idle";
                playback.Request(target, now, true);
                // A task can finish and return to idle during the wake. Give that
                // terminal reaction its entire authored duration exactly once.
                if (target == "completed" || target == "interrupted" || target == "error")
                { wakeVisualHoldState = target; wakeVisualHoldUntil = now + Math.Max(1, clips[target].Duration); }
                if (pendingAwakeAction != null && rest.RequestedTaskState == "idle")
                { string action = pendingAwakeAction; pendingAwakeAction = null; StartEnhancedAction(action, false); }
            }
            if (wakeVisualHoldState != null && now >= wakeVisualHoldUntil)
            {
                wakeVisualHoldState = null; wakeVisualHoldUntil = 0;
                if (shownState == "idle") playback.Request("idle", now, false);
            }
        }

        private void AdvanceRestVisual(long now)
        {
            try
            {
            AdvanceRestState(now);
            if (IsDocked && !(dockLeaving && sideMotion != null))
            { restPony.Visibility = Visibility.Collapsed; restPreviousPony.Visibility = Visibility.Collapsed; return; }
            string selected = EffectiveRestPreview;
            bool preview = selected != null;
            if (!preview)
            {
                if (rest.Stage == RestStage.Falling) selected = "fall";
                else if (rest.Stage == RestStage.Sleeping) selected = "sleeping";
                else if (rest.Stage == RestStage.Waking) selected = "wake";
                else if (playback.VisualState == "idle" && !enhancedAction.Active && gazeFrame < 0 && previewAction == null) selected = "awake";
            }
            if (!preview && rest.Stage == RestStage.Waking && dragWakeVisualSuppressed) selected = "awake";
            if (drag.IsDragging)
            {
                restPony.Visibility = Visibility.Collapsed; restPreviousPony.Visibility = Visibility.Collapsed;
                restExitAt = -1; paintedRestName = null; paintedRestFrame = -1;
                pony.Opacity = 1; enhancedPony.Visibility = Visibility.Collapsed;
                return;
            }
            if (selected == null)
            {
                if (restPony.Visibility != Visibility.Visible) return;
                if (restExitAt < 0) restExitAt = now;
                double amount = Math.Max(0, Math.Min(1, (now - restExitAt) / 120.0));
                restPony.Opacity = 1 - amount; restPreviousPony.Visibility = Visibility.Collapsed;
                if (!enhancedAction.Active && gazeFrame < 0 && lastEnhancedName == null) pony.Opacity = amount;
                previousPony.Visibility = Visibility.Collapsed;
                if (amount >= 1) { restPony.Visibility = Visibility.Collapsed; restExitAt = -1; paintedRestName = null; paintedRestFrame = -1; }
                return;
            }
            int frame;
            string name;
            BitmapSource source;
            long elapsed = previewRest != null ? now : now - manualRestStartedAt;
            if (selected == "awake")
            { name = "awake-idle"; frame = preview && previewRestFrame >= 0 ? previewRestFrame : restIdle.Sample(now); source = restArt.Get(name, frame); }
            else if (selected == "sleeping")
            {
                name = "sleeping"; AnimationClip sleep;
                source = null; frame = 0;
                if (clips.TryGetValue("idle", out sleep))
                { frame = preview && previewRestFrame >= 0 ? previewRestFrame : sleep.FrameIndexAt(now - sleepingStartedAt, true); source = sleep.Frames[Math.Max(0, Math.Min(sleep.Frames.Count - 1, frame))]; }
            }
            else
            {
                name = selected == "fall" ? "fall-asleep" : "wake-up";
                frame = preview ? previewRestFrame >= 0 ? previewRestFrame : Math.Min(5, (int)(Math.Max(0, elapsed) / (selected == "fall" ? 1200.0 : 1000.0) * 6)) : rest.PhaseFrame(now, 6);
                if (!preview && selected == "wake" && rest.WakeFromFalling)
                {
                    name = "fall-asleep";
                    int first = Math.Min(5, (int)(rest.WakeFromFallProgress * 6));
                    double progress = Math.Min(1, Math.Max(0, now - rest.PhaseStartedAt) / (double)Math.Max(1, rest.PhaseDuration));
                    frame = Math.Max(0, first - (int)(progress * (first + 1)));
                }
                source = restArt.Get(name, frame);
            }
            if (source == null)
            { restPony.Visibility = Visibility.Collapsed; restPreviousPony.Visibility = Visibility.Collapsed; pony.Opacity = 1; return; }
            bool wasVisible = restPony.Visibility == Visibility.Visible;
            if (!Object.ReferenceEquals(restPony.Source, source))
            {
                bool blend = wasVisible && restPony.Source != null &&
                    (selected == "fall" || selected == "wake" || paintedRestName != name);
                restPreviousPony.Source = blend ? restPony.Source : null;
                restPreviousPony.Visibility = blend ? Visibility.Visible : Visibility.Collapsed;
                restCelChangedAt = now;
                restPony.Source = source;
            }
            paintedRestName = name; paintedRestFrame = frame; restExitAt = -1;
            restPony.Visibility = Visibility.Visible; restPony.Opacity = 1;
            if (restPreviousPony.Visibility == Visibility.Visible)
            {
                double blend = Math.Max(0, Math.Min(1, (now - restCelChangedAt) / 90.0));
                restPreviousPony.Opacity = 1 - blend;
                if (blend >= 1) { restPreviousPony.Visibility = Visibility.Collapsed; restPreviousPony.Source = null; }
            }
            pony.Opacity = 0; previousPony.Visibility = Visibility.Collapsed;
            if (rest.Stage != RestStage.Awake || preview)
            { enhancedPony.Visibility = Visibility.Collapsed; enhancedPony.Opacity = 0; }
            missingArt.Visibility = Visibility.Collapsed;
            }
            finally { PresentSprite(now); }
        }

        private void PresentSprite(long now)
        {
            if (batchingSpritePresentation || spriteImage == null || spriteAppearance == null) return;
            if (IsDocked || dockLeaving)
            { spriteImage.Visibility = Visibility.Collapsed; PresentEdgeSprite(); return; }

            PresentNormalSprite(now);
        }

        private void PresentNormalSprite(long now)
        {
            string enhancedName = enhancedAction.Active ? enhancedAction.Name : gazeFrame >= 0 ? shownState == "working" ? "gaze-working" : "gaze-idle" : null;
            int enhancedFrame = enhancedAction.Active ? enhancedAction.Frame(now) : gazeFrame;
            if (previewAction != null) { enhancedName = previewAction; enhancedFrame = previewActionFrame >= 0 ? previewActionFrame : enhancedFrame; }
            bool overlay = !ReducedMotionActive && !drag.IsDragging && rest.Stage == RestStage.Awake && EffectiveRestPreview == null && enhancedName != null && enhancedArt.Has(enhancedName);
            BitmapSource touchFrame;
            string touchFamily;
            bool touchMirror;
            bool touching = TryGetPetTouchFrame(now, out touchFrame, out touchFamily, out touchMirror);
            if (spriteQuietOverlay && !overlay && !touching && shownState == "idle") restIdle.Reset(now);
            spriteQuietOverlay = overlay || touching; playback.SetQuietOverlay(spriteQuietOverlay, now);
            int index = playback.Sample(now, manualState != null && !manualInteraction && manualPreviewLoop);
            BitmapSource source = null;
            string family = null;
            bool mirror = false, softenCels = false;
            AnimationClip clip;
            if (drag.IsDragging && clips.TryGetValue("dragging", out clip))
            {
                int frame = clip.FrameIndexAt(now - dragStartedMs, true);
                source = clip.Frames[frame]; family = "state:dragging";
                mirror = clip.MirrorHorizontal ^ drag.FacesRight;
            }
            else if (TryGetPatrolSprite(now, out source, out family, out mirror)) { }
            else if (touching) { source = touchFrame; family = touchFamily; mirror = touchMirror; }
            else if (EffectiveRestPreview != null || rest.Stage != RestStage.Awake || playback.VisualState == "idle" && !overlay)
            {
                string name = paintedRestName;
                if (name == "awake-idle" || String.IsNullOrEmpty(name))
                { name = "awake-idle"; source = EffectiveRestPreview != null && previewRestFrame >= 0 ? restPony.Source as BitmapSource : restArt.Get(name, restIdle.Sample(now)); }
                else source = restPony.Source as BitmapSource;
                family = name == "sleeping" ? "state:idle" : "rest:" + name;
                mirror = true;
                softenCels = name == "fall-asleep" || name == "wake-up";
            }
            else if (overlay)
            {
                source = enhancedArt.Get(enhancedName, Math.Max(0, enhancedFrame));
                family = "enhanced:" + enhancedName;
            }
            else if (clips.TryGetValue(playback.VisualState ?? "", out clip) && index >= 0 && index < clip.Frames.Count)
            { source = clip.Frames[index]; family = "state:" + playback.VisualState; mirror = clip.MirrorHorizontal; }

            if (ReducedMotionActive) ApplyReducedMotionPose(ref source, ref family);
            if (source == null) { spriteImage.Visibility = Visibility.Collapsed; return; }
            BitmapSource prepared = spriteAppearance.Prepare(source, family, mirror);
            spritePresenter.Present(prepared, family, now, drag.IsDragging || ReducedMotionActive, softenCels);
            spriteImage.Source = spritePresenter.Output;
            spriteImage.Opacity = 1; spriteImage.Visibility = Visibility.Visible;
            missingArt.Visibility = Visibility.Collapsed;
            RememberPetTouchPose(family, mirror);
        }

        private static Rect SpriteRectangle(Image image)
        {
            BitmapSource source = image.Source as BitmapSource;
            if (source == null || image.Width <= 0 || image.Height <= 0) return Rect.Empty;
            double width = image.Width, height = image.Height;
            if (image.Stretch == Stretch.Uniform)
            {
                double factor = Math.Min(width / source.PixelWidth, height / source.PixelHeight);
                width = source.PixelWidth * factor; height = source.PixelHeight * factor;
            }
            double left = Canvas.GetLeft(image), top = Canvas.GetTop(image);
            left = Double.IsNaN(left) ? 0 : left; top = Double.IsNaN(top) ? 0 : top;
            if (image.Stretch == Stretch.Uniform)
            { left += (image.Width - width) / 2; top += (image.Height - height) / 2; }
            return new Rect(left, top, width, height);
        }

        private void PresentEdgeSprite()
        {
            if (batchingSpritePresentation || edgeSpriteImage == null || !IsDocked) return;
            if (sideMotion != null) { PresentSideMotion(); return; }
            bool dock = dockActionPony.Visibility == Visibility.Visible && dockActionPony.Opacity > 0 && dockActionPony.Source != null;
            bool edge = edgePony.Visibility == Visibility.Visible && edgePony.Opacity > 0 && edgePony.Source != null;
            if (dock && !dockTransition) edge = false;
            if (!dock && !edge) { edgeSpriteImage.Visibility = Visibility.Collapsed; return; }
            Image first = dock ? dockActionPony : edgePony;
            Image second = dock && edge ? edgePony : first;
            Rect firstRect = SpriteRectangle(first), secondRect = SpriteRectangle(second);
            if (firstRect.IsEmpty || secondRect.IsEmpty) return;
            ScaleTransform firstFacing = first.RenderTransform as ScaleTransform, secondFacing = second.RenderTransform as ScaleTransform;
            int weight = Object.ReferenceEquals(first, second) ? 0 : (int)Math.Round(second.Opacity / (first.Opacity + second.Opacity) * 256);
            PaintEdgeSprites((BitmapSource)first.Source, firstRect, firstFacing != null && firstFacing.ScaleX < 0,
                (BitmapSource)second.Source, secondRect, secondFacing != null && secondFacing.ScaleX < 0, weight);
        }

        private void PaintEdgeSprites(BitmapSource first, Rect firstRect, bool mirrorFirst,
            BitmapSource second, Rect secondRect, bool mirrorSecond, int weight)
        {
            if (weight == 0) { second = first; secondRect = firstRect; mirrorSecond = mirrorFirst; }
            else if (weight == 256) { first = second; firstRect = secondRect; mirrorFirst = mirrorSecond; }
            Rect union = Rect.Union(firstRect, secondRect);
            int width = Math.Max(1, (int)Math.Ceiling(Math.Round(union.Width * 2, 6)));
            int height = Math.Max(1, (int)Math.Ceiling(Math.Round(union.Height * 2, 6)));
            if (edgeSpritePresenter == null || edgeSpriteWidth != width || edgeSpriteHeight != height)
            { edgeSpriteWidth = width; edgeSpriteHeight = height; edgeSpritePresenter = new SpritePresenter(width, height); }
            firstRect.Offset(-union.X, -union.Y);
            secondRect.Offset(-union.X, -union.Y);
            firstRect.Location = new Point(Math.Round(firstRect.X, 6), Math.Round(firstRect.Y, 6));
            secondRect.Location = new Point(Math.Round(secondRect.X, 6), Math.Round(secondRect.Y, 6));
            BitmapSource preparedFirst = edgeRasterCache.Render(first, firstRect, mirrorFirst, width, height);
            BitmapSource preparedSecond = Object.ReferenceEquals(first, second) && firstRect == secondRect && mirrorFirst == mirrorSecond
                ? preparedFirst : edgeRasterCache.Render(second, secondRect, mirrorSecond, width, height);
            edgeSpritePresenter.PresentWeighted(preparedFirst, preparedSecond, weight, "edge:" + dockTarget.Edge.ToString());
            edgeSpriteImage.Source = edgeSpritePresenter.Output;
            edgeSpriteImage.Width = width / 2.0; edgeSpriteImage.Height = height / 2.0;
            Canvas.SetLeft(edgeSpriteImage, union.X); Canvas.SetTop(edgeSpriteImage, union.Y);
            edgeSpriteImage.Visibility = Visibility.Visible; edgeSpriteImage.Opacity = 1;
        }

        private void PreviewRest(string kind)
        {
            NotifyRestInteraction(RestAttentionKind.Menu);
            manualState = null; manualInteraction = false;
            CancelEnhancedAction(false); gazeFrame = -1;
            manualRestPreview = kind; manualRestUntil = DateTime.UtcNow.AddSeconds(8);
            manualRestStartedAt = interactionClock.ElapsedMilliseconds;
            AdvanceAnimation(); AdvanceRestVisual(manualRestStartedAt); UpdateStatusText(lastSnapshot);
        }

        private void LetPetSleep()
        {
            long now = interactionClock.ElapsedMilliseconds;
            manualState = null; manualInteraction = false; manualUntil = DateTime.MinValue;
            manualRestPreview = null; manualRestUntil = DateTime.MinValue;
            pendingAwakeAction = null; wakeVisualHoldState = null; wakeVisualHoldUntil = 0;
            dragWakeVisualSuppressed = false; pressWokePet = false;
            CancelEnhancedAction(false); gaze.Reset(now); gazeFrame = -1;
            lastEnhancedName = null; overlayEndingAt = -1;
            enhancedPony.Visibility = Visibility.Collapsed; previousPony.Visibility = Visibility.Collapsed;
            // Menu dismissal can leave the pointer moving over the pet. Keep
            // tracking its anchor, but allow the deliberate sleep to settle first.
            sleepPointerGraceUntil = now + 1500;
            rest.RequestSleep(now);
            AdvanceRestState(now); AdvanceAnimation(); AdvanceEdge(); AdvanceRestVisual(now); UpdateStatusText(lastSnapshot);
        }

        private void ObserveRestPointer(Point screenPoint, bool nearby, long now)
        {
            bool moved = restPointer.Observe(screenPoint.X, screenPoint.Y, nearby);
            if (moved && now >= sleepPointerGraceUntil) NotifyRestInteraction(RestAttentionKind.Pointer);
        }

        private void FrameRendering(object sender, EventArgs e)
        {
            if (IsVisible && !closing && !updatingWindowBounds)
            {
                long now = interactionClock.ElapsedMilliseconds;
                batchingSpritePresentation = true;
                try
                {
                AdvanceAnimation();
                AdvancePatrol(now);
                AdvanceEdge();
                AdvanceEnhanced();
                AdvanceRestVisual(now);
                // Hover feedback expires smoothly between the one-second metadata polls.
                if (lastSnapshot != null) UpdateStatusText(lastSnapshot);
                }
                finally { batchingSpritePresentation = false; PresentSprite(now); UpdateRenderCadence(); }
            }
        }

        private void UpdateStatusText(PetSnapshot snapshot)
        {
            string label = StateLabel(shownState);
            if (drag.IsDragging) label = StateLabel("dragging");
            else if (EffectiveRestPreview == "sleeping" || rest.Stage == RestStage.Sleeping) label = "云上小憩";
            else if (EffectiveRestPreview == "fall" || rest.Stage == RestStage.Falling) label = "困了，歇一会儿";
            else if (EffectiveRestPreview == "wake" || rest.Stage == RestStage.Waking) label = "云宝醒来啦";
            else if (manualState != null && manualInteraction) label = "云宝来啦";
            else if (EffectiveRestPreview == "awake") label = "清醒待机";
            else if (rest.Stage == RestStage.Awake && EffectiveRestPreview == null && !drag.IsDragging && manualState == null && shownState == "idle")
                label = DateTime.UtcNow < hoverTextUntil ? "云宝在这儿" : "清醒待机";
            else if (rest.Stage == RestStage.Awake && EffectiveRestPreview == null && !drag.IsDragging && manualState == null && shownState == "working" && snapshot != null && snapshot.ActiveTasks > 1)
                label = "正在处理 · " + snapshot.ActiveTasks.ToString(CultureInfo.InvariantCulture) + " 个任务";
            if (statusText.Text != label) statusText.Text = label;
            string colorState = drag.IsDragging ? "dragging" : shownState;
            if (statusColorState != colorState)
            { statusDot.Foreground = Brush(StateColor(colorState)); statusColorState = colorState; }
        }

        // All pose motion is authored in the cels; grounded hooves stay planted.

        private void PetMouseEnter(object sender, MouseEventArgs e)
        {
            double now = interactionClock.Elapsed.TotalSeconds;
            if (drag.IsPressed || now - lastHoverStarted < 6) return;
            hoverStarted = now; lastHoverStarted = now;
            hoverTextUntil = DateTime.UtcNow.AddSeconds(1.8);
            UpdateStatusText(lastSnapshot);
        }

        private void PetPointerDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            RememberPetTouch(e.GetPosition(this));
            BeginPointerGesture(e, true);
            e.Handled = true;
        }

        private void PetPointerMove(object sender, MouseEventArgs e)
        {
            if (!drag.IsPressed) return;
            if (e.LeftButton != MouseButtonState.Pressed) { FinishPointerGesture(false); return; }
            bool wasDragging = drag.IsDragging;
            Point position = drag.Move(PointerScreenPosition(e), SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance);
            if (!drag.IsDragging) return;
            if (!wasDragging)
            {
                dragStartedMs = interactionClock.ElapsedMilliseconds;
                NotifyRestInteraction(RestAttentionKind.Drag);
                // Dragging dismisses an old preview, and never creates a task override.
                manualState = null; manualInteraction = false;
                Poll();
            }
            Left = position.X; Top = position.Y;
            AdvanceAnimation(); UpdateStatusText(lastSnapshot);
            e.Handled = true;
        }

        private void PetPointerUp(object sender, MouseButtonEventArgs e)
        {
            lastPointerPosition = PointerScreenPosition(e);
            bool hadPress = drag.IsPressed;
            bool clicked = FinishPointerGesture(true);
            if (hadPress) e.Handled = true;
            if (!clicked) return;
            if (pressWokePet || rest.Stage != RestStage.Awake)
            { pressWokePet = false; NotifyRestInteraction(RestAttentionKind.Click); e.Handled = true; return; }
            if (TryPlayPetTouch()) { e.Handled = true; return; }
            AnimationClip receipt;
            if (clips.TryGetValue("received", out receipt))
            {
                manualState = "received"; manualInteraction = true;
                double duration = Math.Max(.9, Math.Min(2.0, receipt.Duration / 1000.0));
                manualUntil = DateTime.UtcNow.AddSeconds(duration);
                ApplyState("received", true); UpdateStatusText(lastSnapshot);
                statusPill.ToolTip = "轻点互动 · 稍后恢复真实任务状态";
            }
            e.Handled = true;
        }

        private Point lastPointerPosition;

        private Point PointerScreenPosition(MouseEventArgs e)
        {
            Point devicePoint = PointToScreen(e.GetPosition(this));
            PresentationSource source = PresentationSource.FromVisual(this);
            lastPointerPosition = source != null && source.CompositionTarget != null ?
                source.CompositionTarget.TransformFromDevice.Transform(devicePoint) : devicePoint;
            return lastPointerPosition;
        }

        private void BeginPointerGesture(MouseButtonEventArgs e, bool allowClick)
        {
            CancelPatrol(interactionClock.ElapsedMilliseconds);
            CancelPetTouch();
            pressWokePet = rest.Stage != RestStage.Awake;
            NotifyRestInteraction(RestAttentionKind.Click);
            UserIntervened();
            CancelEnhancedAction(true); gazeFrame = -1;
            if (drag.IsPressed) FinishPointerGesture(false);
            drag.Begin(PointerScreenPosition(e), new Point(Left, Top), allowClick);
            if (!petStage.CaptureMouse()) drag.End(false);
        }

        private bool FinishPointerGesture(bool allowClick)
        {
            if (!drag.IsPressed) return false;
            bool wasDragging = drag.IsDragging;
            bool clicked = drag.End(allowClick);
            // End first: ReleaseMouseCapture raises LostMouseCapture synchronously.
            if (petStage.IsMouseCaptured) petStage.ReleaseMouseCapture();
            if (wasDragging)
            {
                NotifyRestInteraction(RestAttentionKind.Drag);
                bool docked = false;
                if (!closing && allowClick && settings.EdgeDockingEnabled && edgeArt.Complete)
                {
                    EdgeDockTarget target;
                    if (EdgeDockGeometry.TryFindDock(VisiblePetBounds(), lastPointerPosition, GetEdgeMonitors(), out target, settings.Scale))
                    { EnterDock(target, true); docked = true; }
                }
                if (!docked) ClampToScreen();
                SaveSettings();
                if (!closing) Poll();
            }
            if (!closing) { AdvanceAnimation(); UpdateStatusText(lastSnapshot); }
            return clicked;
        }

        private static string DockActionName(DockEdge edge)
        { return edge == DockEdge.Top ? "dock-top" : edge == DockEdge.Bottom ? "dock-bottom" : "dock-side"; }

        private void StartEnhancedAction(string name, bool reverse)
        {
            if (!enhancedArt.Has(name)) return;
            if (rest.Stage != RestStage.Awake)
            { pendingAwakeAction = name; return; }
            CancelEnhancedAction(true);
            gaze.Reset(interactionClock.ElapsedMilliseconds); gazeFrame = -1;
            long duration = name.StartsWith("dock-", StringComparison.Ordinal) ? 600 : name == "preen" ? 1500 : 1200;
            enhancedAction.Start(name, interactionClock.ElapsedMilliseconds, duration, null, reverse);
            AdvanceEnhanced();
        }

        private void CancelEnhancedAction(bool commitPending)
        {
            string pending = enhancedAction.PendingState;
            enhancedAction.Cancel();
            if (commitPending && pending != null && pending == shownState)
                playback.Request(pending, interactionClock.ElapsedMilliseconds, false);
        }

        private void AdvanceEnhanced()
        {
            long now = interactionClock.ElapsedMilliseconds;
            try
            {
            if (rest.Stage != RestStage.Awake || EffectiveRestPreview != null)
            { enhancedPony.Visibility = Visibility.Collapsed; return; }
            if (IsDocked)
            {
                enhancedPony.Visibility = Visibility.Collapsed;
                if (previewAction != null && previewAction.StartsWith("dock-", StringComparison.Ordinal) && enhancedArt.Has(previewAction))
                {
                    ShowDockAction(previewAction, previewActionFrame >= 0 ? previewActionFrame : 5, dockTarget.Edge);
                    edgePony.Opacity = 0;
                }
                return;
            }
            if (enhancedAction.Active && previewAction == null && enhancedAction.Finished(now))
            {
                string pending = enhancedAction.PendingState;
                enhancedAction.Cancel();
                if (pending != null && shownState == pending) playback.Request(pending, now, false);
                AdvanceAnimation();
            }
            string name = enhancedAction.Active ? enhancedAction.Name : gazeFrame >= 0 ? shownState == "working" ? "gaze-working" : "gaze-idle" : null;
            int frame = enhancedAction.Active ? enhancedAction.Frame(now) : gazeFrame;
            if (previewAction != null) { name = previewAction; frame = previewActionFrame >= 0 ? previewActionFrame : frame; }
            if (name == null || !enhancedArt.Has(name))
            {
                if (lastEnhancedName != null)
                { lastEnhancedName = null; overlayEndingAt = now; }
                double leaving = overlayEndingAt < 0 ? 1 : Math.Min(1, (now - overlayEndingAt) / 120.0);
                enhancedPony.Opacity = 1 - leaving; pony.Opacity = leaving;
                if (leaving >= 1) { enhancedPony.Visibility = Visibility.Collapsed; overlayEndingAt = -1; }
                return;
            }
            if (name != lastEnhancedName)
            { lastEnhancedName = name; overlayStartedAt = now; overlayEndingAt = -1; }
            enhancedPony.Source = enhancedArt.Get(name, Math.Max(0, frame));
            enhancedPony.RenderTransform = Transform.Identity;
            enhancedPony.Visibility = Visibility.Visible;
            double entering = previewAction != null ? 1 : Math.Min(1, (now - overlayStartedAt) / 120.0);
            enhancedPony.Opacity = entering; pony.Opacity = 1 - entering; previousPony.Visibility = Visibility.Collapsed;
            }
            finally { AdvanceRestVisual(now); }
        }

        // A single scale/attachment is computed from the endpoint cel. Every authored
        // entrance/exit cel uses it, so clipping reveals a body rather than shrinking it.
        private void ShowDockAction(string name, int frame, DockEdge edge)
        {
            BitmapSource bitmap = enhancedArt.Get(name, frame);
            if (bitmap == null) return;
            bitmap = spriteAppearance.Colorize(bitmap, "enhanced:" + name);
            Rect endpoint = enhancedArt.Endpoint(name);
            Size compact = EdgeDockGeometry.CompactSize(edge);
            double scale = Math.Min(compact.Width / endpoint.Width, compact.Height / endpoint.Height);
            double imageWidth = bitmap.PixelWidth * scale, imageHeight = bitmap.PixelHeight * scale;
            double x, y;
            bool mirror = edge == DockEdge.Left;
            if (edge == DockEdge.Left || edge == DockEdge.Right)
            {
                double anchor = mirror ? bitmap.PixelWidth - endpoint.Right : endpoint.Right;
                x = mirror ? -anchor * scale : root.Width - anchor * scale;
                y = (root.Height - endpoint.Height * scale) / 2 - endpoint.Top * scale;
            }
            else
            {
                x = (root.Width - endpoint.Width * scale) / 2 - endpoint.Left * scale;
                y = edge == DockEdge.Top ? -endpoint.Top * scale : root.Height - endpoint.Bottom * scale;
            }
            dockActionPony.Width = imageWidth; dockActionPony.Height = imageHeight;
            dockActionPony.Source = bitmap;
            dockActionPony.RenderTransform = mirror ? new ScaleTransform(-1, 1) : Transform.Identity;
            Canvas.SetLeft(dockActionPony, x); Canvas.SetTop(dockActionPony, y);
            dockActionPony.Visibility = Visibility.Visible; dockActionPony.Opacity = 1;
            PresentEdgeSprite();
        }

        private void ExpandDockAnimated(bool save)
        {
            if (!IsDocked || dockLeaving) return;
            bool side = EdgeDockGeometry.IsSide(dockTarget.Edge);
            string name = DockActionName(dockTarget.Edge);
            if (ReducedMotionActive || (side ? dockTransition : !enhancedArt.Has(name))) { ExpandDock(save); return; }
            dockTransition = false;
            dockLeaving = true; dockLeaveSave = save;
            dockLeavingAt = interactionClock.ElapsedMilliseconds;
            dockLeavingFrom = new Rect(Left, Top, Width, Height);
            dockLeavingTo = EdgeDockGeometry.UndockBounds(dockTarget,
                new Size(BaseWidth * settings.Scale, (StageHeight + 8) * settings.Scale), GetEdgeMonitors());
            normalStack.Visibility = Visibility.Collapsed;
            if (side && !dockLeavingTo.IsEmpty)
            {
                sideMotion = new SideDockMotion(dockLeavingTo, dockLeavingFrom, NormalSpriteBounds(dockLeavingTo), null);
                sideMotion.DockedAmount = 1;
                AdvanceRestVisual(dockLeavingAt);
            }
            else edgePony.Opacity = 0;
            AdvanceDockExit(dockLeavingAt);
        }

        private void AdvanceDockExit(long now)
        {
            if (!IsDocked || !dockLeaving) return;
            double t = Math.Max(0, Math.Min(1, (now - dockLeavingAt) / 600.0));
            double smooth = t * t * (3 - 2 * t);
            Rect end = dockLeavingTo;
            if (end.IsEmpty) { ExpandDock(dockLeaveSave); return; }
            if (sideMotion != null)
            {
                if (t >= 1) ExpandDock(dockLeaveSave);
                else { AdvanceSideMotion(1 - smooth); PresentEdgeSprite(); }
                return;
            }
            SetWindowBounds(new Rect(dockLeavingFrom.X + (end.X - dockLeavingFrom.X) * smooth,
                dockLeavingFrom.Y + (end.Y - dockLeavingFrom.Y) * smooth,
                dockLeavingFrom.Width + (end.Width - dockLeavingFrom.Width) * smooth,
                dockLeavingFrom.Height + (end.Height - dockLeavingFrom.Height) * smooth));
            ShowDockAction(DockActionName(dockTarget.Edge), 5 - Math.Min(5, (int)(t * 6)), dockTarget.Edge);
            quotaRing.Root.Visibility = Visibility.Collapsed;
            if (t >= 1) ExpandDock(dockLeaveSave);
        }

        private bool MenuIsOpen()
        { return root.ContextMenu != null && root.ContextMenu.IsOpen || tray != null && tray.ContextMenuStrip != null && tray.ContextMenuStrip.Visible; }

        private bool ElementContains(FrameworkElement element, Point windowPoint)
        {
            if (element == null || element.Visibility != Visibility.Visible || element.Opacity < .05 || !element.IsVisible) return false;
            try
            {
                GeneralTransform inverse = element.TransformToAncestor(this).Inverse;
                if (inverse == null) return false;
                Point local = inverse.Transform(windowPoint);
                return local.X >= 0 && local.Y >= 0 && local.X < element.ActualWidth && local.Y < element.ActualHeight;
            }
            catch (InvalidOperationException) { return false; }
        }

        private bool ImageContains(Image image, Point windowPoint)
        {
            if (!ElementContains(image, windowPoint)) return false;
            BitmapSource bitmap = image.Source as BitmapSource;
            if (bitmap == null || image.ActualWidth < 1 || image.ActualHeight < 1) return false;
            Point local = image.TransformToAncestor(this).Inverse.Transform(windowPoint);
            double width = image.ActualWidth, height = image.ActualHeight, x = 0, y = 0;
            if (image.Stretch == Stretch.Uniform)
            {
                double scale = Math.Min(width / bitmap.PixelWidth, height / bitmap.PixelHeight);
                double contentWidth = bitmap.PixelWidth * scale, contentHeight = bitmap.PixelHeight * scale;
                x = (width - contentWidth) / 2; y = (height - contentHeight) / 2;
                width = contentWidth; height = contentHeight;
            }
            double normalizedX = (local.X - x) / width, normalizedY = (local.Y - y) / height;
            if (Object.ReferenceEquals(image, spriteImage)) return spritePresenter.Hit(normalizedX, normalizedY);
            if (Object.ReferenceEquals(image, edgeSpriteImage)) return edgeSpritePresenter != null && edgeSpritePresenter.Hit(normalizedX, normalizedY);
            return AlphaMaskCache.Hit(bitmap, normalizedX, normalizedY);
        }

        private bool IsInteractiveAt(Point windowPoint)
        {
            if (windowPoint.X < 0 || windowPoint.Y < 0 || windowPoint.X >= ActualWidth || windowPoint.Y >= ActualHeight) return false;
            if (drag.IsPressed || edgePressed || MenuIsOpen()) return true;
            if (IsDocked) return ImageContains(edgeSpriteImage, windowPoint);
            return ElementContains(quotaRing.Root, windowPoint) || ElementContains(statusPill, windowPoint) || ImageContains(spriteImage, windowPoint);
        }

        private void UpdateInteraction()
        {
            if (closing || !IsVisible) return;
            long now = interactionClock.ElapsedMilliseconds;
            Point physical;
            bool cursorKnown = DesktopInterop.TryGetCursor(out physical);
            if (!demo && !drag.IsPressed && !edgePressed && !MenuIsOpen() && now - lastFocusPoll >= 500)
            {
                lastFocusPoll = now;
                FullscreenInfo fullscreen;
                bool isFullscreen = DesktopInterop.TryGetForegroundFullscreen(new WindowInteropHelper(this).Handle, out fullscreen);
                UpdateFocusMode(isFullscreen);
            }
            if (cursorKnown)
            {
                Point point = PointFromScreen(physical);
                PresentationSource source = PresentationSource.FromVisual(this);
                Point screenPoint = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice.Transform(physical) : physical;
                if (edgePressed)
                {
                    if (!DesktopInterop.IsLeftButtonDown())
                    { edgePressed = false; if (edgeCanvas.IsMouseCaptured) edgeCanvas.ReleaseMouseCapture(); }
                    else TryPullFromEdge(screenPoint);
                }
                else if (drag.IsPressed && drag.IsDragging)
                {
                    lastPointerPosition = screenPoint;
                    if (!DesktopInterop.IsLeftButtonDown()) FinishPointerGesture(true);
                    else { Point moved = drag.Move(screenPoint, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance); Left = moved.X; Top = moved.Y; NotifyRestInteraction(RestAttentionKind.Drag); }
                }
                bool transparent = settings.TransparentClickThrough && !drag.IsPressed && !edgePressed && !MenuIsOpen() && !IsInteractiveAt(point);
                DesktopInterop.SetClickThrough(new WindowInteropHelper(this).Handle, transparent);
                Rect area = VisiblePetBounds();
                Point global = new Point(Left + point.X, Top + point.Y);
                double dx = Math.Max(area.Left - global.X, Math.Max(0, global.X - area.Right));
                double dy = Math.Max(area.Top - global.Y, Math.Max(0, global.Y - area.Bottom));
                double distance = Math.Sqrt(dx * dx + dy * dy);
                ObserveRestPointer(screenPoint, distance <= 140, now);
                bool eligible = !ReducedMotionActive && !PatrolInProgress && !PetTouchInProgress && rest.Stage == RestStage.Awake && wakeVisualHoldState == null && playback.VisualState == shownState && EffectiveRestPreview == null && settings.NearbyGaze && !IsDocked && !focus.IsActive && !drag.IsPressed && !enhancedAction.Active &&
                    manualState == null && (shownState == "idle" || shownState == "working");
                if (eligible && shownState == "working" && gazeFrame < 0) eligible = playback.CanStartQuietOverlay(now);
                gazeFrame = gaze.Sample(eligible, distance, global.X - (area.Left + area.Width / 2), now);
            }
            AdvanceRestState(now);
            ObservePetReturn(now);
            bool quietIdle = !ReducedMotionActive && !PatrolInProgress && !PetTouchInProgress && rest.CanRunIdleExtras && wakeVisualHoldState == null && playback.VisualState == "idle" && EffectiveRestPreview == null && settings.PersonalityActions && !IsDocked && !focus.IsActive && !drag.IsPressed && manualState == null && shownState == "idle";
            string action = personality.Sample(quietIdle, now);
            if (action != null && !enhancedAction.Active && gazeFrame < 0) StartEnhancedAction(action, false);
            AdvanceEnhanced();
            UpdateRenderCadence();
        }

        private void SetManualFocus(bool enabled)
        {
            settings.ManualFocus = enabled; focus.SetManual(enabled);
            if (focusMenu != null) focusMenu.IsChecked = enabled;
            UpdateFocusMode(false); SaveSettings();
        }

        private void UpdateFocusMode(bool foregroundFullscreen)
        {
            focus.Update(foregroundFullscreen, settings.AutoFocusWhenFullscreen, interactionClock.ElapsedMilliseconds);
            if (focusApplied != focus.IsActive) ApplyFocusState(focus.IsActive);
        }

        private void ApplyFocusState(bool active)
        {
            if (active) { CancelPatrol(interactionClock.ElapsedMilliseconds); CancelPetTouch(); }
            focusApplied = active;
            CancelEnhancedAction(true); gazeFrame = -1; gaze.Reset(interactionClock.ElapsedMilliseconds);
            if (active)
            {
                if (focusReturn == null)
                    focusReturn = new FocusReturnPosition { NormalBounds = new Rect(Left, Top, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)), Dock = dockTarget };
                if (!IsDocked)
                {
                    EdgeDockTarget target = NearestFocusDock();
                    if (target != null) EnterDock(target, false);
                }
            }
            else
            {
                FocusReturnPosition previous = focusReturn; focusReturn = null;
                if (previous != null)
                {
                    if (previous.Dock != null)
                    {
                        EdgeDockTarget target = EdgeDockGeometry.Resolve(previous.Dock.Edge, previous.Dock.MonitorId,
                            previous.Dock.Fraction, GetEdgeMonitors(), false, new Point(previous.NormalBounds.X, previous.NormalBounds.Y), settings.Scale);
                        if (target != null) EnterDock(target, false); else ExpandDock(false);
                    }
                    else
                    {
                        ExpandDock(false); Left = previous.NormalBounds.Left; Top = previous.NormalBounds.Top; ClampToScreen();
                    }
                }
            }
            SaveSettings(); AdvanceEnhanced();
        }

        private EdgeDockTarget NearestFocusDock()
        {
            IList<EdgeMonitor> monitors = GetEdgeMonitors();
            Point center = new Point(Left + ActualWidth / 2, Top + ActualHeight / 2);
            EdgeDockTarget best = null; double bestDistance = Double.PositiveInfinity;
            foreach (EdgeMonitor screen in monitors)
            {
                if (!screen.Bounds.Contains(center)) continue;
                foreach (DockEdge edge in new DockEdge[] { DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom })
                {
                    bool vertical = edge == DockEdge.Left || edge == DockEdge.Right;
                    Size size = EdgeDockGeometry.CompactSize(edge, settings.Scale);
                    double start = vertical ? screen.WorkArea.Top : screen.WorkArea.Left;
                    double length = vertical ? screen.WorkArea.Height : screen.WorkArea.Width;
                    double extent = vertical ? size.Height : size.Width;
                    double denominator = length - EdgeDockGeometry.CornerMargin * 2 - extent;
                    double fraction = denominator <= 0 ? .5 : ((vertical ? center.Y : center.X) - start - EdgeDockGeometry.CornerMargin - extent / 2) / denominator;
                    EdgeDockTarget target = EdgeDockGeometry.Resolve(edge, screen.Id, fraction, monitors, false, settings.Scale);
                    if (target == null) continue;
                    double distance = edge == DockEdge.Left ? center.X - screen.WorkArea.Left : edge == DockEdge.Right ? screen.WorkArea.Right - center.X :
                        edge == DockEdge.Top ? center.Y - screen.WorkArea.Top : screen.WorkArea.Bottom - center.Y;
                    if (distance < bestDistance) { bestDistance = distance; best = target; }
                }
            }
            return best;
        }

        private void UserIntervened()
        {
            // Also cancels an automatic entry still in its one-second debounce;
            // a pending fullscreen transition must never steal an active drag.
            focusReturn = null;
            focus.SuppressAutomaticUntilClear();
            UpdateFocusMode(false);
        }

        private static string StateLabel(string state)
        {
            switch (state)
            {
                case "received": return "收到任务";
                case "working": return "正在处理";
                case "interrupted": return "任务中断";
                case "completed": return "任务完成";
                case "waiting": return "等待操作";
                case "error": return "需要留意";
                case "dragging": return "云宝冲刺";
                default: return "待机中";
            }
        }

        private static string StateColor(string state)
        {
            switch (state)
            {
                case "received": return "#FFFFD374";
                case "working": return "#FF6DD6FF";
                case "interrupted": return "#FFFFAB7D";
                case "completed": return "#FF7FE2B9";
                case "waiting": return "#FFC0AEFF";
                case "error": return "#FFFF8D9C";
                case "dragging": return "#FF7BDFFF";
                default: return "#FFACBED9";
            }
        }

        private void RefreshQuota()
        {
            try
            {
                if (!demo) monitor.RefreshQuota();
                refreshFeedbackUntil = DateTime.UtcNow.AddSeconds(2);
                quotaRing.SetFeedback(demo ? "演示模式 · 非真实额度" : "正在向官方客户端刷新额度…");
            }
            catch (Exception ex) { if (App.IsFatalRenderFailure(ex)) throw; App.Log("Refresh", ex); quotaRing.SetFeedback("刷新失败，稍后会自动重试。"); }
        }

        private void ToggleQuota()
        {
            settings.QuotaCollapsed = !settings.QuotaCollapsed;
            ApplyScaleAndCollapse();
            Dispatcher.BeginInvoke(new Action(delegate { ClampToScreen(); SaveSettings(); }));
        }

        private void ApplyScaleAndCollapse()
        {
            normalStack.LayoutTransform = new ScaleTransform(settings.Scale, settings.Scale);
            if (IsDocked)
            {
                if (EdgeDockGeometry.CompactSize(dockTarget.Edge, settings.Scale) != dockTarget.Bounds.Size) RefreshDockGeometry();
                else LayoutEdge();
                return;
            }
            root.LayoutTransform = Transform.Identity;
            root.Width = BaseWidth * settings.Scale;
            root.Height = (StageHeight + 8) * settings.Scale;
            Width = root.Width; Height = root.Height;
            quotaRing.Root.Visibility = settings.QuotaCollapsed ? Visibility.Collapsed : Visibility.Visible;
        }

        private ContextMenu BuildContextMenu()
        {
            ContextMenu menu = new ContextMenu { FontFamily = FontFamily, FontSize = 12 };
            menu.Opened += delegate { NotifyRestInteraction(RestAttentionKind.Menu); };
            menu.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler(delegate
            {
                long now = interactionClock.ElapsedMilliseconds;
                rest.NotifyInteraction(RestAttentionKind.Menu, now); AdvanceRestState(now);
            }));
            MenuItem title = new MenuItem { Header = "云宝 · Rainbow Dash", IsEnabled = false };
            menu.Items.Add(title);
            MenuItem refresh = new MenuItem { Header = "刷新额度" };
            refresh.Click += delegate { RefreshQuota(); }; menu.Items.Add(refresh);
            MenuItem quotaDetails = new MenuItem { Header = "查看额度详情" };
            quotaDetails.Click += delegate { OpenQuotaDetails(); }; menu.Items.Add(quotaDetails);
            MenuItem collapse = new MenuItem { Header = "显示 / 隐藏额度圆环" };
            collapse.Click += delegate { ToggleQuota(); }; menu.Items.Add(collapse);
            MenuItem expand = new MenuItem { Header = "展开云宝" };
            expand.Click += delegate { UserIntervened(); ExpandDockAnimated(true); }; menu.Items.Add(expand);
            MenuItem sleep = new MenuItem { Header = "让云宝休息睡觉", ToolTip = "立即入睡，任务变化或互动时醒来；任务和额度继续同步" };
            sleep.Click += delegate(object sender, RoutedEventArgs e) { LetPetSleep(); e.Handled = true; };
            menu.Items.Add(sleep);
            focusMenu = new MenuItem { Header = "专注模式", IsCheckable = true, IsChecked = settings.ManualFocus };
            focusMenu.Click += delegate { SetManualFocus(focusMenu.IsChecked); }; menu.Items.Add(focusMenu);
            MenuItem autoFocus = new MenuItem { Header = "全屏时自动收边", IsCheckable = true, IsChecked = settings.AutoFocusWhenFullscreen };
            autoFocus.Click += delegate { settings.AutoFocusWhenFullscreen = autoFocus.IsChecked; UpdateFocusMode(false); SaveSettings(); }; menu.Items.Add(autoFocus);
            MenuItem clickThrough = new MenuItem { Header = "透明空白可以点穿", IsCheckable = true, IsChecked = settings.TransparentClickThrough };
            clickThrough.Click += delegate { settings.TransparentClickThrough = clickThrough.IsChecked; UpdateInteraction(); SaveSettings(); }; menu.Items.Add(clickThrough);
            MenuItem nearby = new MenuItem { Header = "靠近时看向鼠标", IsCheckable = true, IsChecked = settings.NearbyGaze };
            nearby.Click += delegate { settings.NearbyGaze = nearby.IsChecked; gaze.Reset(interactionClock.ElapsedMilliseconds); SaveSettings(); }; menu.Items.Add(nearby);
            MenuItem personalityMenu = new MenuItem { Header = "云宝的小动作" };
            foreach (string item in new string[] { "hair", "preen", "proud" })
            {
                string name = item;
                MenuItem entry = new MenuItem { Header = name == "hair" ? "甩一下刘海" : name == "preen" ? "整理羽毛" : "抱臂得意" };
                entry.Click += delegate { UserIntervened(); if (IsDocked) ExpandDock(false); StartEnhancedAction(name, false); personality.Postpone(interactionClock.ElapsedMilliseconds); };
                personalityMenu.Items.Add(entry);
            }
            MenuItem lowFrequency = new MenuItem { Header = "待机时偶尔做小动作", IsCheckable = true, IsChecked = settings.PersonalityActions };
            lowFrequency.Click += delegate { settings.PersonalityActions = lowFrequency.IsChecked; SaveSettings(); };
            personalityMenu.Items.Add(new Separator()); personalityMenu.Items.Add(lowFrequency); menu.Items.Add(personalityMenu);
            menu.Items.Add(BuildPatrolMenu());
            AddPetControlMenuItems(menu);
            MenuItem sleepDelay = new MenuItem { Header = "多久后睡觉" };
            foreach (int item in new int[] { 3, 5, 10, 0 })
            {
                int minutes = item;
                MenuItem entry = new MenuItem { Header = minutes == 0 ? "不自动睡觉" : minutes.ToString(CultureInfo.InvariantCulture) + " 分钟没互动后",
                    IsCheckable = true, IsChecked = settings.SleepDelayMinutes == minutes, Tag = minutes };
                entry.Click += delegate
                {
                    settings.SleepDelayMinutes = minutes;
                    rest.Configure(minutes * 60000L, interactionClock.ElapsedMilliseconds);
                    foreach (MenuItem other in sleepDelay.Items) other.IsChecked = (int)other.Tag == minutes;
                    SaveSettings(); AdvanceAnimation();
                };
                sleepDelay.Items.Add(entry);
            }
            menu.Items.Add(sleepDelay);
            MenuItem dockEnabled = new MenuItem { Header = "拖到边缘时躲藏", IsCheckable = true, IsChecked = settings.EdgeDockingEnabled };
            dockEnabled.Click += delegate
            {
                settings.EdgeDockingEnabled = dockEnabled.IsChecked;
                if (!settings.EdgeDockingEnabled) ExpandDock(true);
                SaveSettings();
            };
            menu.Items.Add(dockEnabled);
            MenuItem preview = new MenuItem { Header = "预览专属动画" };
            foreach (string item in states.Where(delegate(string state) { return state != "idle"; }))
            {
                string state = item;
                MenuItem entry = new MenuItem { Header = StateLabel(state), Tag = state };
                entry.Click += delegate { manualRestPreview = null; manualState = state; manualInteraction = false; manualUntil = DateTime.UtcNow.AddSeconds(8); ApplyState(state, true); };
                preview.Items.Add(entry);
            }
            preview.Items.Add(new Separator());
            foreach (string item in new string[] { "awake", "sleeping", "fall", "wake" })
            {
                string kind = item;
                MenuItem entry = new MenuItem { Header = kind == "awake" ? "清醒待机 · 自然站立" : kind == "sleeping" ? "云上小憩 · 蜷卧呼吸" : kind == "fall" ? "慢慢趴下入睡" : "醒来伸展", Tag = kind };
                entry.Click += delegate { PreviewRest(kind); };
                preview.Items.Add(entry);
            }
            MenuItem automatic = new MenuItem { Header = "恢复任务状态" };
            automatic.Click += delegate { manualRestPreview = null; manualState = null; manualInteraction = false; Poll(); };
            preview.Items.Add(new Separator()); preview.Items.Add(automatic); menu.Items.Add(preview);
            MenuItem repeatPreview = new MenuItem { Header = "手动预览时循环动作", IsCheckable = true, IsChecked = false };
            repeatPreview.Click += delegate { manualPreviewLoop = repeatPreview.IsChecked; };
            preview.Items.Add(repeatPreview);
            MenuItem scale = new MenuItem { Header = "桌宠大小" };
            foreach (double item in new double[] { .8, 1.0, 1.25, 1.5 })
            {
                double factor = item;
                MenuItem entry = new MenuItem { Header = (factor * 100).ToString("0", CultureInfo.InvariantCulture) + "%", IsCheckable = true, IsChecked = Math.Abs(settings.Scale - factor) < .01, Tag = factor };
                entry.Click += delegate
                {
                    settings.Scale = factor;
                    CancelPatrol(interactionClock.ElapsedMilliseconds, false);
                    foreach (object obj in scale.Items) { MenuItem other = obj as MenuItem; if (other != null) other.IsChecked = Math.Abs((double)other.Tag - factor) < .01; }
                    ApplyScaleAndCollapse();
                    Dispatcher.BeginInvoke(new Action(delegate { ClampToScreen(); SaveSettings(); }));
                };
                scale.Items.Add(entry);
            }
            menu.Items.Add(scale);
            MenuItem top = new MenuItem { Header = "保持置顶", IsCheckable = true, IsChecked = settings.KeepOnTop };
            top.Click += delegate { SetKeepOnTop(top.IsChecked); }; menu.Items.Add(top);
            menu.Items.Add(new Separator());
            MenuItem hide = new MenuItem { Header = "隐藏到托盘" };
            hide.Click += delegate { Hide(); }; menu.Items.Add(hide);
            MenuItem exit = new MenuItem { Header = "退出云宝" };
            exit.Click += delegate { Application.Current.Shutdown(); }; menu.Items.Add(exit);
            return menu;
        }

        private void CreateTray()
        {
            try
            {
                tray = new Forms.NotifyIcon { Text = "云宝 · Codex 任务与额度", Visible = false };
                AnimationClip idle;
                if (clips.TryGetValue("idle", out idle) && idle.Frames.Count > 0)
                {
                    PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(idle.Frames[0]));
                    using (MemoryStream stream = new MemoryStream())
                    {
                        encoder.Save(stream); stream.Position = 0;
                        using (Drawing.Bitmap source = new Drawing.Bitmap(stream))
                        using (Drawing.Bitmap iconBitmap = new Drawing.Bitmap(32, 32))
                        {
                            using (Drawing.Graphics graphics = Drawing.Graphics.FromImage(iconBitmap))
                            {
                                graphics.Clear(Drawing.Color.Transparent);
                                graphics.InterpolationMode = Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                                graphics.DrawImage(source, new Drawing.Rectangle(0, 2, 32, 28));
                            }
                            IntPtr handle = iconBitmap.GetHicon();
                            using (Drawing.Icon temporary = Drawing.Icon.FromHandle(handle)) trayIcon = (Drawing.Icon)temporary.Clone();
                            DestroyIcon(handle);
                        }
                    }
                }
                tray.Icon = trayIcon ?? Drawing.SystemIcons.Application;
                Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
                menu.Items.Add("显示云宝", null, delegate { Dispatcher.BeginInvoke(new Action(Restore)); });
                menu.Items.Add("展开云宝", null, delegate { Dispatcher.BeginInvoke(new Action(Restore)); });
                menu.Items.Add("查看额度详情", null, delegate { Dispatcher.BeginInvoke(new Action(OpenQuotaDetails)); });
                menu.Items.Add("刷新额度", null, delegate { Dispatcher.BeginInvoke(new Action(RefreshQuota)); });
                Forms.ToolStripMenuItem focusItem = new Forms.ToolStripMenuItem("专注模式") { Checked = settings.ManualFocus, CheckOnClick = true };
                focusItem.Click += delegate { Dispatcher.BeginInvoke(new Action(delegate { SetManualFocus(focusItem.Checked); })); };
                menu.Opening += delegate { NotifyRestInteraction(RestAttentionKind.Menu); focusItem.Checked = settings.ManualFocus; };
                menu.ItemClicked += delegate { long now = interactionClock.ElapsedMilliseconds; rest.NotifyInteraction(RestAttentionKind.Menu, now); AdvanceRestState(now); };
                menu.Items.Add(focusItem);
                AddPetControlTrayItems(menu);
                menu.Items.Add("隐藏云宝", null, delegate { Dispatcher.BeginInvoke(new Action(Hide)); });
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("退出", null, delegate { Dispatcher.BeginInvoke(new Action(delegate { Application.Current.Shutdown(); })); });
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(Restore)); };
                tray.Visible = true;
            }
            catch (Exception ex) { if (App.IsFatalRenderFailure(ex)) throw; App.Log("Tray", ex); }
        }

        private void Restore()
        {
            Show();
            NotifyRestInteraction(RestAttentionKind.Menu);
            UserIntervened();
            WindowState = WindowState.Normal;
            if (IsDocked) ExpandDockAnimated(true);
            else ClampToScreen();
            if (Topmost) { Topmost = false; Topmost = true; }
        }

        private IList<EdgeMonitor> GetEdgeMonitors()
        {
            double sx = 1, sy = 1;
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
            { sx = source.CompositionTarget.TransformToDevice.M11; sy = source.CompositionTarget.TransformToDevice.M22; }
            List<EdgeMonitor> result = new List<EdgeMonitor>();
            foreach (Forms.Screen screen in Forms.Screen.AllScreens)
            {
                Drawing.Rectangle b = screen.Bounds, w = screen.WorkingArea;
                result.Add(new EdgeMonitor(screen.DeviceName,
                    new Rect(b.Left / sx, b.Top / sy, b.Width / sx, b.Height / sy),
                    new Rect(w.Left / sx, w.Top / sy, w.Width / sx, w.Height / sy)));
            }
            return result;
        }

        private Rect VisiblePetBounds()
        {
            Image visible = IsDocked ? edgeSpriteImage : spriteImage;
            if (visible.Source == null || visible.ActualWidth < 1 || visible.ActualHeight < 1) return new Rect(Left, Top, ActualWidth, ActualHeight);
            BitmapSource source = visible.Source as BitmapSource;
            Rect opaque = new Rect(0, 0, 1, 1);
            if (!IsDocked || edgeSpritePresenter != null)
            {
                opaque = IsDocked ? edgeSpritePresenter.OpaqueBounds : spritePresenter.OpaqueBounds;
                if (opaque.IsEmpty) return new Rect(Left, Top, 0, 0);
            }
            else if (source != null)
            {
                Rect cached;
                if (!opaqueBoundsCache.TryGetValue(source, out cached))
                {
                    Rect pixels = EnhancedArt.OpaqueBounds(source);
                    cached = new Rect(pixels.X / source.PixelWidth, pixels.Y / source.PixelHeight, pixels.Width / source.PixelWidth, pixels.Height / source.PixelHeight);
                    if (opaqueBoundsCache.Count >= 64) opaqueBoundsCache.Clear();
                    opaqueBoundsCache[source] = cached;
                }
                opaque = cached;
            }
            double contentWidth = visible.ActualWidth, contentHeight = visible.ActualHeight;
            double offsetX = 0, offsetY = 0;
            if (source != null && visible.Stretch == Stretch.Uniform)
            {
                double factor = Math.Min(contentWidth / source.PixelWidth, contentHeight / source.PixelHeight);
                double fittedWidth = source.PixelWidth * factor, fittedHeight = source.PixelHeight * factor;
                offsetX = (contentWidth - fittedWidth) / 2; offsetY = (contentHeight - fittedHeight) / 2;
                contentWidth = fittedWidth; contentHeight = fittedHeight;
            }
            Rect local = new Rect(offsetX + opaque.X * contentWidth, offsetY + opaque.Y * contentHeight,
                opaque.Width * contentWidth, opaque.Height * contentHeight);
            // TransformToAncestor includes the current left/right facing and the user scale.
            Rect inside = visible.TransformToAncestor(root).TransformBounds(local);
            if (IsDocked) inside.Intersect(new Rect(0, 0, root.Width, root.Height));
            if (inside.IsEmpty) return new Rect(Left, Top, 0, 0);
            return new Rect(Left + inside.X, Top + inside.Y, inside.Width, inside.Height);
        }

        private void EnterDock(EdgeDockTarget target, bool animate)
        {
            animate = animate && !ReducedMotionActive && !(dockLeaving && sideMotion != null);
            if (target == null || !edgeArt.Complete) return;
            BitmapSource entrySource = spriteImage.Source as BitmapSource;
            CancelEnhancedAction(true); gazeFrame = -1; dockLeaving = false;
            lastEnhancedName = null; overlayEndingAt = -1; enhancedPony.Visibility = Visibility.Collapsed; pony.Opacity = 1;
            dockTransitionFrom = new Rect(Left, Top, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
            dockTarget = EdgeDockGeometry.Resolve(target.Edge, target.MonitorId, target.Fraction, GetEdgeMonitors(), false, settings.Scale);
            if (dockTarget == null) return;
            sideMotion = null;
            ResetSidePeek();
            if (animate && EdgeDockGeometry.IsSide(dockTarget.Edge))
            {
                animate = entrySource != null;
                if (animate)
                {
                    if (!entrySource.IsFrozen)
                    {
                        entrySource = new WriteableBitmap(entrySource);
                        entrySource.Freeze();
                    }
                    sideMotion = new SideDockMotion(dockTransitionFrom, dockTarget.Bounds, NormalSpriteBounds(dockTransitionFrom), entrySource);
                }
            }
            CancelPatrol(interactionClock.ElapsedMilliseconds, false);
            CancelPetTouch();
            edgeHover.Reset();
            edgeCanvas.Visibility = Visibility.Visible;
            petStage.Children.Remove(quotaRing.Root);
            if (!edgeCanvas.Children.Contains(quotaRing.Root)) edgeCanvas.Children.Add(quotaRing.Root);
            quotaRing.Root.Margin = new Thickness(0);
            quotaRing.Root.Visibility = Visibility.Collapsed;
            edgeCanvas.ToolTip = "点击展开 · 向屏幕内拖动可拉出云宝";
            SizeToContent = SizeToContent.Manual;
            dockTransition = animate;
            dockTransitionAt = interactionClock.ElapsedMilliseconds;
            normalStack.Visibility = animate && !enhancedArt.Has(DockActionName(dockTarget.Edge)) ? Visibility.Visible : Visibility.Collapsed;
            LayoutEdge(); AdvanceEdge();
            if (!animate) SaveSettings();
        }

        private Rect EdgeWindowBounds(bool peek)
        {
            EdgeDockTarget resolved = EdgeDockGeometry.Resolve(dockTarget.Edge, dockTarget.MonitorId,
                dockTarget.Fraction, GetEdgeMonitors(), peek, settings.Scale);
            if (resolved == null) return dockTarget.Bounds;
            return resolved.Bounds;
        }

        private void LayoutEdge()
        {
            if (!IsDocked) return;
            bool peek = previewEdgeHover || edgeHover.IsPeeking;
            quotaRing.Root.Visibility = Visibility.Collapsed;
            if (EdgeDockGeometry.IsSide(dockTarget.Edge))
            {
                LayoutSideEdge(peek);
                PresentEdgeSprite();
                return;
            }
            Rect bounds = EdgeWindowBounds(peek);
            Size face = EdgeDockGeometry.CompactSize(dockTarget.Edge);
            double factor = peek ? (face.Height + 12) / face.Height : 1;
            double imageWidth = face.Width * factor, imageHeight = face.Height * factor;
            double imageX = (face.Width - imageWidth) / 2;
            edgePony.Width = imageWidth; edgePony.Height = imageHeight;
            edgePony.RenderTransform = Transform.Identity;
            Canvas.SetLeft(edgePony, imageX); Canvas.SetTop(edgePony, 0);
            if (!dockTransition) SetWindowBounds(bounds);
            PresentEdgeSprite();
        }

        private void SetWindowBounds(Rect bounds)
        {
            if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return;
            bool previous = updatingWindowBounds; updatingWindowBounds = true;
            try
            {
                root.LayoutTransform = Transform.Identity;
                root.Width = bounds.Width; root.Height = bounds.Height;
                Width = bounds.Width; Height = bounds.Height;
                Left = bounds.Left; Top = bounds.Top;
            }
            finally { updatingWindowBounds = previous; }
        }

        private void AdvanceEdge()
        {
            try
            {
            if (!IsDocked) return;
            long now = interactionClock.ElapsedMilliseconds;
            if (dockLeaving) { AdvanceDockExit(now); return; }
            if (!edgePressed && (!dockTransition || sideMotion == null) && edgeHover.Advance(now)) LayoutEdge();
            else if (SidePeekMoving) AdvanceSidePeek(now);
            int frame = previewEdgeFrame >= 0 ? previewEdgeFrame : edgeExpression.Sample(shownState, now, ReducedMotionActive, dockTarget.Edge);
            BitmapSource edgeFrame = edgeArt.Get(dockTarget.Edge, focus.IsActive || rest.IsSleepingLike || EffectiveRestPreview == "sleeping" || EffectiveRestPreview == "fall" ? 8 : frame);
            edgePony.Source = edgeFrame == null ? null : spriteAppearance.Colorize(edgeFrame, "edge:" + dockTarget.Edge.ToString().ToLowerInvariant());
            if (dockTransition)
            {
                double t = Math.Max(0, Math.Min(1, (now - dockTransitionAt) / 600.0));
                double smooth = t * t * (3 - 2 * t);
                if (sideMotion != null)
                {
                    AdvanceSideMotion(smooth);
                    if (t >= 1) FinishDockEntry();
                    return;
                }
                Rect end = dockTarget.Bounds;
                SetWindowBounds(new Rect(dockTransitionFrom.X + (end.X - dockTransitionFrom.X) * smooth,
                    dockTransitionFrom.Y + (end.Y - dockTransitionFrom.Y) * smooth,
                    dockTransitionFrom.Width + (end.Width - dockTransitionFrom.Width) * smooth,
                    dockTransitionFrom.Height + (end.Height - dockTransitionFrom.Height) * smooth));
                normalStack.Opacity = Math.Max(0, 1 - t * 2);
                string dockName = DockActionName(dockTarget.Edge);
                if (enhancedArt.Has(dockName))
                {
                    int actionFrame = Math.Min(5, (int)(t * 6));
                    ShowDockAction(dockName, actionFrame, dockTarget.Edge);
                    dockActionPony.Opacity = t < .866666667 ? 1 : (1 - t) / .133333333;
                    edgePony.Opacity = t < .866666667 ? 0 : (t - .866666667) / .133333333;
                }
                else edgePony.Opacity = Math.Min(1, Math.Max(0, (t - .22) / .58));
                if (t >= 1) FinishDockEntry();
            }
            }
            finally { PresentSprite(interactionClock.ElapsedMilliseconds); }
        }

        private void FinishDockEntry()
        {
            sideMotion = null;
            dockTransition = false; normalStack.Visibility = Visibility.Collapsed;
            normalStack.Opacity = 1; edgePony.Opacity = 1; dockActionPony.Visibility = Visibility.Collapsed;
            LayoutEdge(); SaveSettings();
        }

        private void RefreshDockGeometry()
        {
            if (!IsDocked) return;
            EdgeDockTarget resolved = EdgeDockGeometry.Resolve(dockTarget.Edge, dockTarget.MonitorId,
                dockTarget.Fraction, GetEdgeMonitors(), false, new Point(Left, Top), settings.Scale);
            if (resolved == null) { ExpandDock(true); return; }
            dockTarget = resolved; dockTransition = false; dockLeaving = false;
            sideMotion = null;
            dockActionPony.Visibility = Visibility.Collapsed;
            normalStack.Visibility = Visibility.Collapsed; normalStack.Opacity = 1; edgePony.Opacity = 1;
            LayoutEdge(); SaveSettings();
        }

        private void ExpandDock(bool save)
        {
            if (!IsDocked) return;
            Rect normal = EdgeDockGeometry.UndockBounds(dockTarget,
                new Size(BaseWidth * settings.Scale, (StageHeight + 8) * settings.Scale), GetEdgeMonitors());
            dockTarget = null; dockTransition = false; dockLeaving = false; edgePressed = false; edgeHover.Reset();
            sideMotion = null;
            dockActionPony.Visibility = Visibility.Collapsed;
            if (edgeCanvas.IsMouseCaptured) edgeCanvas.ReleaseMouseCapture();
            edgeCanvas.Children.Remove(quotaRing.Root);
            if (!petStage.Children.Contains(quotaRing.Root)) petStage.Children.Add(quotaRing.Root);
            Canvas.SetLeft(quotaRing.Root, Double.NaN); Canvas.SetTop(quotaRing.Root, Double.NaN);
            quotaRing.Root.Margin = new Thickness(0, 0, 16, 0);
            edgeCanvas.Visibility = Visibility.Collapsed;
            edgeSpriteImage.Visibility = Visibility.Collapsed;
            normalStack.Visibility = Visibility.Visible; normalStack.Opacity = 1;
            ApplyScaleAndCollapse();
            Width = root.Width; Height = root.Height;
            if (!normal.IsEmpty) { Left = normal.Left; Top = normal.Top; }
            ClampToScreen(); AdvanceAnimation();
            if (save) SaveSettings();
        }

        private void EdgePointerDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsDocked || e.LeftButton != MouseButtonState.Pressed) return;
            NotifyRestInteraction(RestAttentionKind.Click);
            UserIntervened();
            edgePressPoint = PointerScreenPosition(e); edgePressed = edgeCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void EdgePointerMove(object sender, MouseEventArgs e)
        {
            if (!IsDocked || !edgePressed) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            { edgePressed = false; if (edgeCanvas.IsMouseCaptured) edgeCanvas.ReleaseMouseCapture(); return; }
            Point point = PointerScreenPosition(e);
            if (TryPullFromEdge(point)) e.Handled = true;
        }

        private bool TryPullFromEdge(Point point)
        {
            if (!IsDocked || !edgePressed || dockLeaving) return false;
            if (EdgeDockGeometry.InwardDistance(dockTarget.Edge, edgePressPoint, point) < EdgeDockGeometry.UndockDistance) return false;
            Point press = edgePressPoint;
            DockEdge pulledEdge = dockTarget.Edge;
            NotifyRestInteraction(RestAttentionKind.Drag);
            ExpandDock(false);
            if (enhancedArt.Has(DockActionName(pulledEdge))) StartEnhancedAction(DockActionName(pulledEdge), true);
            Vector delta = point - press;
            drag.Begin(press, new Point(Left - delta.X, Top - delta.Y), false);
            drag.Move(point, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance);
            dragStartedMs = interactionClock.ElapsedMilliseconds;
            manualState = null; manualInteraction = false;
            if (!petStage.CaptureMouse())
            {
                drag.End(false); ClampToScreen(); SaveSettings();
                Poll(); AdvanceAnimation(); UpdateStatusText(lastSnapshot); return true;
            }
            Poll(); AdvanceAnimation(); UpdateStatusText(lastSnapshot);
            return true;
        }

        private void EdgePointerUp(object sender, MouseButtonEventArgs e)
        {
            if (!edgePressed) return;
            Point point = PointerScreenPosition(e);
            bool clicked = (point - edgePressPoint).Length < Math.Max(SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance);
            edgePressed = false;
            if (edgeCanvas.IsMouseCaptured) edgeCanvas.ReleaseMouseCapture();
            if (clicked) ExpandDockAnimated(true);
            e.Handled = true;
        }

        private void DragSurface(object sender, MouseButtonEventArgs e)
        {
            DependencyObject node = e.OriginalSource as DependencyObject;
            while (node != null)
            {
                if (node is Button || node is MenuItem) return;
                if (node == sender) break;
                node = VisualTreeHelper.GetParent(node);
            }
            if (e.LeftButton != MouseButtonState.Pressed) return;
            BeginPointerGesture(e, false);
            e.Handled = true;
        }

        private void ClampToScreen()
        {
            if (IsDocked) { RefreshDockGeometry(); return; }
            if (Double.IsNaN(Left) || Double.IsNaN(Top)) return;
            double sx = 1, sy = 1;
            HwndSource source = PresentationSource.FromVisual(this) as HwndSource;
            if (source != null && source.CompositionTarget != null)
            { sx = source.CompositionTarget.TransformToDevice.M11; sy = source.CompositionTarget.TransformToDevice.M22; }
            Drawing.Point center = new Drawing.Point((int)((Left + ActualWidth / 2) * sx), (int)((Top + ActualHeight / 2) * sy));
            Drawing.Rectangle work = Forms.Screen.FromPoint(center).WorkingArea;
            double left = work.Left / sx, top = work.Top / sy, right = work.Right / sx, bottom = work.Bottom / sy;
            Left = Math.Max(left, Math.Min(Left, Math.Max(left, right - ActualWidth)));
            Top = Math.Max(top, Math.Min(Top, Math.Max(top, bottom - ActualHeight)));
        }

        private void SavePreview(string path)
        {
            UpdateLayout();
            double scale = 2;
            RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * scale), (int)Math.Ceiling(ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string directory = System.IO.Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (FileStream stream = new FileStream(path, FileMode.Create)) encoder.Save(stream);
        }

        private UiSettings LoadSettings()
        {
            try
            {
                string path = System.IO.Path.Combine(App.UserDirectory, SettingsFile);
                if (File.Exists(path))
                {
                    UiSettings loaded = new JavaScriptSerializer().Deserialize<UiSettings>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        if (Double.IsNaN(loaded.Scale) || loaded.Scale < .65 || loaded.Scale > 1.75) loaded.Scale = 1;
                        if (Double.IsInfinity(loaded.Left)) loaded.Left = Double.NaN;
                        if (Double.IsInfinity(loaded.Top)) loaded.Top = Double.NaN;
                        if (loaded.SleepDelayMinutes != 0 && loaded.SleepDelayMinutes != 3 && loaded.SleepDelayMinutes != 5 && loaded.SleepDelayMinutes != 10)
                            loaded.SleepDelayMinutes = 5;
                        return loaded;
                    }
                }
            }
            catch (Exception ex) { App.Log("Load settings", ex); }
            return new UiSettings();
        }

        private void SaveSettings()
        {
            if (previewPath != null || previewRest != null || smokeTest || !positioned) return;
            try
            {
                if (focusReturn != null)
                {
                    settings.DockSide = focusReturn.Dock == null ? "None" : focusReturn.Dock.Edge.ToString();
                    settings.DockMonitor = focusReturn.Dock == null ? "" : focusReturn.Dock.MonitorId;
                    settings.DockFraction = focusReturn.Dock == null ? .5 : focusReturn.Dock.Fraction;
                    settings.Left = focusReturn.NormalBounds.Left; settings.Top = focusReturn.NormalBounds.Top;
                }
                else if (IsDocked)
                {
                    settings.DockSide = dockTarget.Edge.ToString(); settings.DockMonitor = dockTarget.MonitorId;
                    settings.DockFraction = dockTarget.Fraction;
                    settings.Left = dockTarget.Bounds.X; settings.Top = dockTarget.Bounds.Y;
                }
                else { settings.Left = Left; settings.Top = Top; settings.DockSide = "None"; }
                Directory.CreateDirectory(App.UserDirectory);
                File.WriteAllText(System.IO.Path.Combine(App.UserDirectory, SettingsFile), new JavaScriptSerializer().Serialize(settings));
            }
            catch (Exception ex) { App.Log("Save settings", ex); }
        }

        private void DisposeResources()
        {
            CancelPatrol(interactionClock.ElapsedMilliseconds, false);
            SaveSettings();
            StopRenderCadence();
            UnregisterVisibilityShortcut();
            CloseQuotaDetails();
            if (pollTimer != null) pollTimer.Stop();
            if (interactionTimer != null) interactionTimer.Stop();
            try { DesktopInterop.SetClickThrough(new WindowInteropHelper(this).Handle, false); } catch { }
            if (saveTimer != null) saveTimer.Stop();
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            if (trayIcon != null) { trayIcon.Dispose(); trayIcon = null; }
            if (monitor != null) monitor.Dispose();
        }

        private void LoadClips()
        {
            string assets = assetDirectoryOverride ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "assets");
            IDictionary<string, object> manifest = null;
            try
            {
                string manifestPath = System.IO.Path.Combine(assets, "manifest.json");
                if (File.Exists(manifestPath)) manifest = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(manifestPath)) as IDictionary<string, object>;
            }
            catch (Exception ex) { App.Log("Asset manifest", ex); }
            foreach (string state in states.Concat(new string[] { "dragging" }))
            {
                try
                {
                    bool? quality = null;
                    object stateObject;
                    if (manifest != null && manifest.TryGetValue(state, out stateObject)) quality = QualityHint(stateObject as IDictionary<string, object>);
                    if (!quality.HasValue) quality = QualityHint(manifest);
                    AnimationClip clip = LoadPngFrames(System.IO.Path.Combine(assets, "frames", state), quality);
                    if (clip == null || clip.Frames.Count == 0) clip = LoadGif(System.IO.Path.Combine(assets, state + ".gif"), quality);
                    if (clip != null && clip.Frames.Count > 0)
                    { ConfigurePlayback(clip, state, manifest); clips[state] = clip; }
                }
                catch (Exception ex) { if (App.IsFatalRenderFailure(ex)) throw; App.Log("Animation " + state, ex); }
            }
        }

        private static void ConfigurePlayback(AnimationClip clip, string state, IDictionary<string, object> manifest)
        {
            clip.PlaybackMode = state == "idle" ? "idle" : state == "working" || state == "dragging" ? "loop" : state == "waiting" ? "hold" : "once";
            clip.Grounded = state != "working" && state != "dragging";
            clip.HoldFrame = clip.Frames.Count - 1;
            // Compatibility indices for the currently installed canonical-v2 idle sheet.
            // New artwork must declare its actual eyelid cels in natural_playback metadata.
            if (state == "idle" && clip.Frames.Count == 24)
            { clip.BlinkFrames = new int[] { 7, 8, 9, 10 }; clip.BlinkDelays = new int[] { 30, 35, 35, 45 }; }
            object stateValue, value;
            IDictionary<string, object> entry = null, profile = null;
            if (manifest != null && manifest.TryGetValue(state, out stateValue)) entry = stateValue as IDictionary<string, object>;
            if (entry != null && entry.TryGetValue("mirror_horizontal", out value) && value is bool) clip.MirrorHorizontal = (bool)value;
            if (entry != null && entry.TryGetValue("natural_playback", out value)) profile = value as IDictionary<string, object>;
            if (profile == null) return;
            if (profile.TryGetValue("mode", out value) && value is string)
            {
                string mode = ((string)value).ToLowerInvariant();
                if (mode == "idle" || mode == "quiet" || mode == "loop" || mode == "once" || mode == "hold") clip.PlaybackMode = mode;
            }
            if (profile.TryGetValue("grounded", out value) && value is bool) clip.Grounded = (bool)value;
            clip.RestFrame = ProfileInt(profile, "rest_frame", clip.RestFrame, 0, clip.Frames.Count - 1);
            if (profile.TryGetValue("rest_frames", out value)) clip.RestFrames = ProfileArray(value, 0, clip.Frames.Count - 1);
            clip.HoldFrame = ProfileInt(profile, "hold_frame", clip.HoldFrame, 0, clip.Frames.Count - 1);
            clip.LoopStart = ProfileInt(profile, "loop_start_frame", 0, 0, clip.Frames.Count - 1);
            clip.LoopEnd = ProfileInt(profile, "loop_end_frame", clip.Frames.Count - 1, clip.LoopStart, clip.Frames.Count - 1);
            clip.BlinkMinMs = ProfileInt(profile, "blink_min_ms", 4500, 2000, 30000);
            clip.BlinkMaxMs = ProfileInt(profile, "blink_max_ms", 7800, clip.BlinkMinMs, 45000);
            if (profile.TryGetValue("blink_frames", out value)) clip.BlinkFrames = ProfileArray(value, 0, clip.Frames.Count - 1);
            if (profile.TryGetValue("blink_delays_ms", out value)) clip.BlinkDelays = ProfileArray(value, 10, 250);
            if (profile.TryGetValue("action_frames", out value)) clip.ActionFrames = ProfileArray(value, 0, clip.Frames.Count - 1);
            if (profile.TryGetValue("action_delays_ms", out value)) clip.ActionDelays = ProfileArray(value, 10, 2000);
            clip.ActionInitialMinMs = ProfileInt(profile, "action_initial_min_ms", 3500, 1000, 60000);
            clip.ActionInitialMaxMs = ProfileInt(profile, "action_initial_max_ms", 5500, clip.ActionInitialMinMs, 120000);
            clip.ActionMinMs = ProfileInt(profile, "action_min_ms", 9000, 1000, 120000);
            clip.ActionMaxMs = ProfileInt(profile, "action_max_ms", 14000, clip.ActionMinMs, 180000);
            if (profile.TryGetValue("frame_delays_ms", out value))
            {
                int[] delays = ProfileArray(value, 10, 10000);
                if (delays.Length == clip.Frames.Count)
                { clip.Delays.Clear(); clip.Delays.AddRange(delays); clip.Duration = delays.Sum(); }
            }
        }

        private static int ProfileInt(IDictionary<string, object> profile, string key, int fallback, int min, int max)
        {
            object value;
            if (!profile.TryGetValue(key, out value)) return fallback;
            try { return Math.Max(min, Math.Min(max, Convert.ToInt32(value, CultureInfo.InvariantCulture))); }
            catch { return fallback; }
        }

        private static int[] ProfileArray(object value, int min, int max)
        {
            object[] array = value as object[];
            if (array == null) return new int[0];
            List<int> result = new List<int>();
            foreach (object item in array)
            { try { result.Add(Math.Max(min, Math.Min(max, Convert.ToInt32(item, CultureInfo.InvariantCulture)))); } catch { } }
            return result.ToArray();
        }

        private static bool? QualityHint(IDictionary<string, object> entry)
        {
            if (entry == null) return null;
            object value;
            if (entry.TryGetValue("pixel_art", out value) && value is bool) return !(bool)value;
            if (entry.TryGetValue("high_resolution", out value) && value is bool) return (bool)value;
            foreach (string key in new string[] { "rendering", "style", "quality" })
            {
                if (!entry.TryGetValue(key, out value) || !(value is string)) continue;
                string label = ((string)value).ToLowerInvariant();
                if (label.Contains("pixel") || label.Contains("像素") || label == "nearest") return false;
                if (label.Contains("smooth") || label.Contains("show") || label.Contains("high") || label.Contains("vector") || label.Contains("cartoon") || label.Contains("高清") || label.Contains("动画")) return true;
            }
            return null;
        }

        private static int DecodeWidth(AnimationClip clip)
        {
            if (!clip.HighQuality) return 0;
            // Keep two physical pixels per displayed logical pixel, without retaining
            // 224 full-resolution PNG canvases in memory. Never upscale during decoding.
            double factor = Math.Min(1.0, Math.Min(448.0 / clip.Width, 400.0 / clip.Height));
            return Math.Max(1, (int)Math.Round(clip.Width * factor));
        }

        private static void ReadImageSize(string path, out int width, out int height)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
                width = decoder.Frames[0].PixelWidth; height = decoder.Frames[0].PixelHeight;
            }
        }

        private static AnimationClip LoadPngFrames(string directory, bool? highQuality)
        {
            if (!Directory.Exists(directory)) return null;
            string[] files = Directory.GetFiles(directory, "*.png");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            if (files.Length == 0) return null;
            object[] delays = null;
            string timingFile = System.IO.Path.Combine(directory, "durations.json");
            if (File.Exists(timingFile))
            {
                try { delays = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(timingFile)) as object[]; }
                catch (Exception ex) { App.Log("Frame timing", ex); }
            }
            AnimationClip clip = new AnimationClip();
            ReadImageSize(files[0], out clip.Width, out clip.Height);
            clip.HighQuality = highQuality ?? (clip.Width > 256 || clip.Height > 256);
            int decodeWidth = DecodeWidth(clip);
            for (int i = 0; i < files.Length; i++)
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
                bitmap.UriSource = new Uri(files[i], UriKind.Absolute); bitmap.EndInit(); bitmap.Freeze();
                int delay = 100;
                if (delays != null && i < delays.Length) { try { delay = Convert.ToInt32(delays[i], CultureInfo.InvariantCulture); } catch { } }
                delay = Math.Max(clip.HighQuality ? 10 : 20, Math.Min(5000, delay));
                clip.Frames.Add(bitmap); clip.Delays.Add(delay); clip.Duration += delay;
            }
            return clip;
        }

        private static AnimationClip LoadGif(string path, bool? highQuality)
        {
            if (!File.Exists(path)) return null;
            AnimationClip clip = new AnimationClip();
            using (Drawing.Image image = Drawing.Image.FromFile(path))
            {
                Drawing.Imaging.FrameDimension dimension = new Drawing.Imaging.FrameDimension(image.FrameDimensionsList[0]);
                int count = image.GetFrameCount(dimension);
                byte[] timing = null;
                try { timing = image.GetPropertyItem(0x5100).Value; } catch { }
                clip.Width = image.Width; clip.Height = image.Height;
                clip.HighQuality = highQuality ?? (clip.Width > 256 || clip.Height > 256);
                int decodeWidth = DecodeWidth(clip);
                for (int i = 0; i < count; i++)
                {
                    image.SelectActiveFrame(dimension, i);
                    using (Drawing.Bitmap frame = new Drawing.Bitmap(image.Width, image.Height, Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (Drawing.Graphics graphics = Drawing.Graphics.FromImage(frame))
                        { graphics.Clear(Drawing.Color.Transparent); graphics.DrawImage(image, 0, 0, image.Width, image.Height); }
                        // PNG preserves the GIF's transparency; HBITMAP conversion loses its alpha channel.
                        using (MemoryStream stream = new MemoryStream())
                        {
                            frame.Save(stream, Drawing.Imaging.ImageFormat.Png); stream.Position = 0;
                            BitmapImage bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
                            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); clip.Frames.Add(bitmap);
                        }
                    }
                    int delay = timing != null && timing.Length >= (i + 1) * 4 ? BitConverter.ToInt32(timing, i * 4) * 10 : 100;
                    delay = Math.Max(clip.HighQuality ? 10 : 20, Math.Min(5000, delay));
                    clip.Delays.Add(delay); clip.Duration += delay;
                }
            }
            return clip;
        }

        private static Button SmallButton(string text, string tooltip)
        {
            Button button = new Button
            {
                Content = text, Width = 24, Height = 24, Padding = new Thickness(0),
                Foreground = Brush("#FF91A6C3"), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand, ToolTip = tooltip, Focusable = false,
                FontFamily = new FontFamily("Segoe UI Symbol"), VerticalAlignment = VerticalAlignment.Center
            };
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ButtonBorder";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            ControlTemplate template = new ControlTemplate(typeof(Button)); template.VisualTree = border;
            Trigger hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#FF2C3B53"), "ButtonBorder"));
            hover.Setters.Add(new Setter(Button.ForegroundProperty, Brush("#FFD1E9FF")));
            template.Triggers.Add(hover); button.Template = template;
            return button;
        }

        internal static SolidColorBrush Brush(string color)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush;
        }

        private static LinearGradientBrush RainbowBrush()
        {
            LinearGradientBrush brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            string[] colors = { "#FFF37388", "#FFFFAE6F", "#FFFFE27B", "#FF84D5A5", "#FF71CCF5", "#FFB29BFF" };
            for (int i = 0; i < colors.Length; i++) brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colors[i]), (double)i / (colors.Length - 1)));
            brush.Freeze(); return brush;
        }

        private static string CleanText(string value, int length)
        {
            if (String.IsNullOrEmpty(value)) return "";
            value = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            return value.Length <= length ? value : value.Substring(0, length) + "…";
        }

        internal static DateTime ToUtc(DateTime value)
        { return value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime(); }

        internal static string FormatLocal(DateTime value)
        { return ToUtc(value).ToLocalTime().ToString("MM月dd日 HH:mm", CultureInfo.InvariantCulture); }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    }

    internal sealed class QuotaRing
    {
        public readonly Grid Root;
        private readonly RingArc fiveHour;
        private readonly RingArc week;
        private readonly TextBlock number;
        private readonly TextBlock demoBadge;
        private readonly TextBlock details;
        private string detailText = "额度未同步";

        public QuotaRing()
        {
            Root = new Grid { Width = 36, Height = 36, Background = Brushes.Transparent };
            Root.Children.Add(new Ellipse { Fill = PetWindow.Brush("#DD1B2437"), IsHitTestVisible = false });
            fiveHour = new RingArc(Root, 14.5, "#FF79DBF3");
            week = new RingArc(Root, 9.5, "#FFC4ACFF");
            number = new TextBlock
            {
                Text = "—", FontFamily = new FontFamily("Segoe UI"), FontSize = 9,
                FontWeight = FontWeights.SemiBold, Foreground = PetWindow.Brush("#FFE7F0FF"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            Root.Children.Add(number);
            demoBadge = new TextBlock
            {
                Text = "演示", FontSize = 8, Foreground = PetWindow.Brush("#FFFFD485"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -12, 0, 0), IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Root.Children.Add(demoBadge);
            details = new TextBlock { FontSize = 11, LineHeight = 18, Foreground = PetWindow.Brush("#FFE2EBF7") };
            Root.ToolTip = new ToolTip
            {
                Content = details, Background = PetWindow.Brush("#F51B2437"),
                BorderBrush = PetWindow.Brush("#FF41516A"), BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 10, 7)
            };
        }

        public void Update(PetSnapshot snapshot, bool demo, bool refreshing)
        {
            double? hourly = Normalize(snapshot.FiveHourRemaining);
            double? weekly = Normalize(snapshot.WeekRemaining);
            fiveHour.Update(hourly); week.Update(weekly);
            number.Text = hourly.HasValue ? hourly.Value.ToString("0", CultureInfo.InvariantCulture) : "—";
            number.FontSize = hourly.HasValue && hourly.Value >= 99.5 ? 8 : 9;
            number.Foreground = PetWindow.Brush(hourly.HasValue && hourly.Value <= 10 ? "#FFFFAD98" : "#FFE7F0FF");
            demoBadge.Visibility = demo ? Visibility.Visible : Visibility.Collapsed;
            string sync = snapshot.QuotaUpdatedUtc.HasValue ? PetWindow.FormatLocal(snapshot.QuotaUpdatedUtc.Value) : "未同步";
            detailText = (demo ? "演示数据 · 非真实额度\n" : "") +
                "外圈 · 五小时剩余  " + ValueLabel(hourly) + "\n" +
                "内圈 · 一周剩余      " + ValueLabel(weekly) + "\n" +
                (snapshot.FiveHourResetUtc.HasValue ? "五小时重置  " + PetWindow.FormatLocal(snapshot.FiveHourResetUtc.Value) + "\n" : "") +
                (snapshot.WeekResetUtc.HasValue ? "一周重置      " + PetWindow.FormatLocal(snapshot.WeekResetUtc.Value) + "\n" : "") +
                "最后同步      " + sync + "\n" +
                (demo ? "" : PetWindow.QuotaStatusLabel(snapshot) + "\n" + PetWindow.ConnectionLabel(snapshot) + "\n") +
                "双击刷新 · 拖动移动 · 右键设置";
            details.Text = refreshing ? "正在重新读取本机记录…\n\n" + detailText : detailText;
            System.Windows.Automation.AutomationProperties.SetName(Root,
                "Codex剩余额度：五小时" + ValueLabel(hourly) + "，一周" + ValueLabel(weekly));
        }

        public void SetFeedback(string message)
        { details.Text = message + "\n\n" + detailText; }

        internal static double? Normalize(double? value)
        { return value.HasValue && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value) ? (double?)Math.Max(0, Math.Min(100, value.Value)) : null; }

        internal static string ValueLabel(double? value)
        { return value.HasValue ? value.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "未知"; }

        private sealed class RingArc
        {
            private readonly Ellipse track;
            private readonly System.Windows.Shapes.Path progress;
            private readonly double radius;
            private readonly Brush color;
            private double? lastValue;
            private bool initialized;

            public RingArc(Grid host, double r, string stroke)
            {
                radius = r; color = PetWindow.Brush(stroke);
                track = new Ellipse
                {
                    Width = r * 2 + 2.5, Height = r * 2 + 2.5, Stroke = PetWindow.Brush("#FF435066"), StrokeThickness = 2.5,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                };
                host.Children.Add(track);
                progress = new System.Windows.Shapes.Path
                {
                    Width = 36, Height = 36, Stroke = color, StrokeThickness = 2.5,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false
                };
                host.Children.Add(progress);
            }

            public void Update(double? value)
            {
                if (initialized && value == lastValue) return;
                initialized = true; lastValue = value;
                track.StrokeDashArray = value.HasValue ? null : new DoubleCollection { 1, 2 };
                progress.Stroke = value.HasValue && value.Value <= 10 ? PetWindow.Brush("#FFED947F") : color;
                if (!value.HasValue || value.Value <= 0) { progress.Data = Geometry.Empty; return; }
                Geometry geometry;
                if (value.Value >= 100) geometry = new EllipseGeometry(new Point(18, 18), radius, radius);
                else
                {
                    double angle = value.Value / 100 * Math.PI * 2;
                    StreamGeometry arc = new StreamGeometry();
                    using (StreamGeometryContext drawing = arc.Open())
                    {
                        drawing.BeginFigure(new Point(18, 18 - radius), false, false);
                        drawing.ArcTo(new Point(18 + Math.Sin(angle) * radius, 18 - Math.Cos(angle) * radius),
                            new Size(radius, radius), 0, value.Value > 50, SweepDirection.Clockwise, true, false);
                    }
                    geometry = arc;
                }
                geometry.Freeze(); progress.Data = geometry;
            }
        }
    }
}
