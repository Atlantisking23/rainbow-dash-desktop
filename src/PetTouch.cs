using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RainbowDashDesktop
{
    internal enum PetTouchPart { General, Head, Wing, Changing }

    internal static class PetTouchRegions
    {
        // Deliberately cover the face/ear and the visible feather area, leaving
        // the body, tail, book and cloud to the ordinary click behavior.
        // These points are on the authored 512-square cels before presentation.
        private static readonly Point[] StandingHead = {
            new Point(261, 139), new Point(310, 136), new Point(366, 94), new Point(443, 100),
            new Point(469, 200), new Point(430, 213), new Point(437, 251), new Point(379, 274),
            new Point(323, 272), new Point(269, 220)
        };
        private static readonly Point[] StandingWing = {
            new Point(249, 290), new Point(272, 290), new Point(290, 302), new Point(298, 323),
            new Point(308, 342), new Point(295, 356), new Point(272, 352), new Point(256, 335), new Point(246, 312)
        };
        private static readonly Point[] ReadingHead = {
            new Point(95, 152), new Point(143, 141), new Point(191, 126), new Point(252, 129),
            new Point(280, 190), new Point(274, 259), new Point(247, 288), new Point(205, 302),
            new Point(172, 300), new Point(162, 276), new Point(138, 265), new Point(115, 273), new Point(96, 228)
        };
        private static readonly Point[] GazeHead = {
            new Point(54, 225), new Point(80, 154), new Point(88, 132), new Point(160, 124),
            new Point(207, 162), new Point(230, 158), new Point(241, 193), new Point(226, 242),
            new Point(204, 267), new Point(167, 292), new Point(112, 289), new Point(88, 269), new Point(88, 236)
        };
        private static readonly Point[] GazeWing = {
            new Point(215, 312), new Point(231, 308), new Point(258, 311), new Point(266, 329),
            new Point(254, 352), new Point(236, 368), new Point(214, 366), new Point(210, 348)
        };
        private static readonly Point[] ReadingGazeHead = {
            new Point(214, 149), new Point(253, 130), new Point(303, 131), new Point(358, 164),
            new Point(386, 155), new Point(403, 184), new Point(401, 222), new Point(376, 252),
            new Point(370, 277), new Point(344, 275), new Point(332, 297), new Point(293, 310),
            new Point(250, 283), new Point(230, 246), new Point(220, 256)
        };

        public static PetTouchPart Classify(string family, AppearanceProfile profile, bool mirror, Point normalized)
        {
            const double pixelsPerSourceUnit = SpriteAppearance.CanvasHeight / 512.0;
            const double sideMargin = (SpriteAppearance.CanvasWidth - SpriteAppearance.CanvasHeight) / 2.0;
            double x = (normalized.X * SpriteAppearance.CanvasWidth - sideMargin) / pixelsPerSourceUnit;
            double y = normalized.Y * SpriteAppearance.CanvasHeight / pixelsPerSourceUnit;
            x = (x - profile.AnchorX * (1 - profile.Scale) - profile.OffsetX) / profile.Scale;
            y = (y - profile.AnchorY * (1 - profile.Scale) - profile.OffsetY) / profile.Scale;
            Point authored = new Point(mirror ? 512 - x : x, y);
            switch (family)
            {
                case "rest:awake-idle":
                    if (Contains(StandingHead, authored)) return PetTouchPart.Head;
                    if (Contains(StandingWing, authored)) return PetTouchPart.Wing;
                    break;
                case "enhanced:gaze-idle":
                    if (Contains(GazeHead, authored)) return PetTouchPart.Head;
                    if (Contains(GazeWing, authored)) return PetTouchPart.Wing;
                    break;
                case "state:working":
                    if (Contains(ReadingHead, authored)) return PetTouchPart.Head;
                    break;
                case "enhanced:gaze-working":
                    if (Contains(ReadingGazeHead, authored)) return PetTouchPart.Head;
                    break;
            }
            return PetTouchPart.General;
        }

        private static bool Contains(Point[] polygon, Point point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                Point a = polygon[i], b = polygon[j];
                if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < a.X + (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y))
                    inside = !inside;
            }
            return inside;
        }
    }

    internal sealed partial class PetWindow
    {
        private enum PetTouchReaction { None, AwakeBlink, ReadingBlink, Feathers, Greeting }
        private static readonly int[] FeatherCelEnds = { 110, 215, 360, 520, 660, 860 };
        private const long PetAwayThresholdMs = 5 * 60 * 1000;
        private const long PetGreetingCooldownMs = 30 * 60 * 1000;
        private PetTouchPart pressedPetPart;
        private string pressedPetState, petTouchPoseFamily, petTouchState;
        private bool petTouchPoseMirror, petHasInputSample;
        private uint petLastInputTick;
        private PetTouchReaction petTouchReaction;
        private long petTouchStarted, nextPetIdleCheck, petGreetingExpires, nextPetGreeting;

        private bool PetTouchInProgress { get { return petTouchReaction != PetTouchReaction.None; } }

        private void RememberPetTouchPose(string family, bool mirror)
        {
            petTouchPoseFamily = family;
            petTouchPoseMirror = mirror;
        }

        private void RememberPetTouch(Point windowPoint)
        {
            pressedPetState = shownState;
            pressedPetPart = PetTouchPart.General;
            if (petTouchPoseFamily == null || !ImageContains(spriteImage, windowPoint)) return;
            if (spritePresenter.IsTransitioning)
            {
                // Neither pose's authored zones fully describe a blended cel.
                pressedPetPart = PetTouchPart.Changing;
                return;
            }
            GeneralTransform inverse = spriteImage.TransformToAncestor(this).Inverse;
            if (inverse == null) return;
            Point local = inverse.Transform(windowPoint);
            Point normalized = new Point(local.X / spriteImage.ActualWidth, local.Y / spriteImage.ActualHeight);
            pressedPetPart = PetTouchRegions.Classify(petTouchPoseFamily, spriteAppearance.Profile(petTouchPoseFamily), petTouchPoseMirror, normalized);
        }

        private bool CanAnimatePetTouch()
        {
            return !ReduceMotionEnabled && !focus.IsActive && !IsDocked && !dockLeaving && !drag.IsPressed &&
                !PatrolInProgress && EffectiveRestPreview == null && previewAction == null && manualState == null;
        }

        private bool TryPlayPetTouch()
        {
            if (ReduceMotionEnabled || shownState != pressedPetState || pressedPetPart == PetTouchPart.Changing) return true;
            if (pressedPetPart == PetTouchPart.General) return false;
            if (!CanAnimatePetTouch() || wakeVisualHoldState != null || playback.VisualState != shownState) return true;
            long now = interactionClock.ElapsedMilliseconds;
            if (pressedPetPart == PetTouchPart.Wing && shownState == "idle" && enhancedArt.Has("preen"))
                BeginPetTouch(PetTouchReaction.Feathers, now);
            else if (pressedPetPart == PetTouchPart.Head && shownState == "idle" && restArt.Has("awake-idle"))
                BeginPetTouch(PetTouchReaction.AwakeBlink, now);
            else if (pressedPetPart == PetTouchPart.Head && shownState == "working" && playback.VisualState == "working" && playback.CanStartQuietOverlay(now))
                BeginPetTouch(PetTouchReaction.ReadingBlink, now);
            return true;
        }

        private void BeginPetTouch(PetTouchReaction reaction, long now)
        {
            petTouchReaction = reaction;
            petTouchState = shownState;
            petTouchStarted = now;
            personality.Postpone(now);
            gaze.Reset(now);
            gazeFrame = -1;
            PresentSprite(now);
        }

        private void CancelPetTouch()
        {
            petTouchReaction = PetTouchReaction.None;
            petGreetingExpires = 0;
        }

        private bool TryGetPetTouchFrame(long now, out BitmapSource source, out string family, out bool mirror)
        {
            source = null;
            family = null;
            mirror = false;
            if (!PetTouchInProgress) return false;
            if (!CanAnimatePetTouch() || rest.Stage != RestStage.Awake || enhancedAction.Active || shownState != petTouchState)
            {
                CancelPetTouch();
                return false;
            }

            long age = Math.Max(0, now - petTouchStarted);
            if (petTouchReaction == PetTouchReaction.AwakeBlink && age < 290)
            {
                int frame = age < 50 ? 2 : age < 120 ? 3 : age < 195 ? 4 : 5;
                source = restArt.Get("awake-idle", frame);
                family = "rest:awake-idle";
                mirror = true;
            }
            else if (petTouchReaction == PetTouchReaction.Feathers && age < FeatherCelEnds[FeatherCelEnds.Length - 1])
            {
                int frame = 0;
                while (age >= FeatherCelEnds[frame]) frame++;
                source = enhancedArt.Get("preen", frame);
                family = "enhanced:preen";
            }
            else if (petTouchReaction == PetTouchReaction.ReadingBlink && age < 240)
            {
                AnimationClip reading;
                if (clips.TryGetValue("working", out reading) && reading.BlinkFrames.Length >= 3)
                {
                    int frame = reading.BlinkFrames[age < 55 ? 0 : age < 145 ? 1 : 2];
                    if (frame >= 0 && frame < reading.Frames.Count) source = reading.Frames[frame];
                    family = "state:working";
                    mirror = reading.MirrorHorizontal;
                }
            }
            else if (petTouchReaction == PetTouchReaction.Greeting)
            {
                AnimationClip greeting;
                if (clips.TryGetValue("received", out greeting) && age < greeting.Duration)
                {
                    int frame = greeting.FrameIndexAt(age, false);
                    if (frame >= 0 && frame < greeting.Frames.Count) source = greeting.Frames[frame];
                    family = "state:received";
                    mirror = greeting.MirrorHorizontal;
                }
            }

            if (source != null) return true;
            CancelPetTouch();
            return false;
        }

        private void ObservePetReturn(long now)
        {
            if (demo || smokeTest) return;
            if (now >= nextPetIdleCheck)
            {
                nextPetIdleCheck = now + 1000;
                uint inputTick, idleMs;
                if (TryGetPetInputIdle(out inputTick, out idleMs))
                {
                    // Compare last-input timestamps so a suspended process can
                    // detect the pause even if it missed the idle threshold.
                    uint inputGap = unchecked(inputTick - petLastInputTick);
                    bool returned = petHasInputSample && idleMs < 5000 && inputGap >= PetAwayThresholdMs && inputGap <= Int32.MaxValue;
                    petLastInputTick = inputTick;
                    petHasInputSample = true;
                    if (returned && now >= nextPetGreeting && CanGreetPetReturn())
                    {
                        petGreetingExpires = now + 2800;
                        // Actual Windows input resumed. An explicitly requested
                        // nap is left to the existing direct wake interactions.
                        NotifyRestInteraction(RestAttentionKind.Pointer);
                    }
                }
            }

            if (petGreetingExpires == 0) return;
            if (now >= petGreetingExpires || !CanGreetPetReturn())
            {
                petGreetingExpires = 0;
                return;
            }
            if (rest.Stage != RestStage.Awake || wakeVisualHoldState != null || playback.VisualState != "idle") return;
            petGreetingExpires = 0;
            nextPetGreeting = now + PetGreetingCooldownMs;
            BeginPetTouch(PetTouchReaction.Greeting, now);
        }

        private bool CanGreetPetReturn()
        {
            return IsVisible && settings.PersonalityActions && CanAnimatePetTouch() && shownState == "idle" &&
                rest.IsQuiet && !rest.ManualSleepRequested && !enhancedAction.Active && !MenuIsOpen();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PetLastInputInfo
        {
            public uint Size;
            public uint InputTick;
        }

        [DllImport("user32.dll", EntryPoint = "GetLastInputInfo")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPetLastInputInfo(ref PetLastInputInfo info);

        private static bool TryGetPetInputIdle(out uint inputTick, out uint idleMs)
        {
            PetLastInputInfo info = new PetLastInputInfo { Size = (uint)Marshal.SizeOf(typeof(PetLastInputInfo)) };
            bool available = GetPetLastInputInfo(ref info);
            inputTick = info.InputTick;
            // Input times can race the tick sample and wrap at 2^32 ms.
            idleMs = unchecked((uint)Environment.TickCount - inputTick);
            return available && idleMs <= Int32.MaxValue;
        }
    }
}
