using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace RainbowDashDesktop
{
    internal sealed partial class UiSettings
    {
        public bool KeepOnTop = true;
        public bool ReduceMotion;
    }

    internal sealed partial class PetWindow
    {
        private const int VisibilityHotkeyMessage = 0x0312;
        private const int VisibilityHotkeyId = 0x5244;
        private const uint VisibilityHotkeyModifiers = 0x4003; // Ctrl + Alt, without auto-repeat.
        private const uint VisibilityHotkeyKey = 0x79; // F10.
        private const string VisibilityHotkeyDisplay = "Ctrl+Alt+F10";
        private IntPtr visibilityHotkeyWindow;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        private bool ReduceMotionEnabled { get { return settings.ReduceMotion; } }

        private bool ReducedMotionActive
        {
            get
            {
                return ReduceMotionEnabled && EffectiveRestPreview == null && previewState == null &&
                    previewAction == null && previewEdge == null && (manualState == null || manualInteraction);
            }
        }

        private void RegisterVisibilityShortcut(IntPtr window)
        {
            if (demo || smokeTest || window == IntPtr.Zero || visibilityHotkeyWindow != IntPtr.Zero) return;
            if (RegisterHotKey(window, VisibilityHotkeyId, VisibilityHotkeyModifiers, VisibilityHotkeyKey))
                visibilityHotkeyWindow = window;
        }

        private void UnregisterVisibilityShortcut()
        {
            if (visibilityHotkeyWindow == IntPtr.Zero) return;
            UnregisterHotKey(visibilityHotkeyWindow, VisibilityHotkeyId);
            visibilityHotkeyWindow = IntPtr.Zero;
        }

        private bool HandleVisibilityShortcut(int message, IntPtr id)
        {
            if (message != VisibilityHotkeyMessage || id.ToInt64() != VisibilityHotkeyId || visibilityHotkeyWindow == IntPtr.Zero)
                return false;
            if (positioned && !closing && !drag.IsPressed && !edgePressed && !MenuIsOpen())
                Dispatcher.BeginInvoke(new Action(delegate { if (!closing) CyclePetVisibility(); }));
            return true;
        }

        private void CyclePetVisibility()
        {
            if (!IsVisible) { Restore(); return; }
            if (IsDocked && !dockLeaving || !DockPetForControls()) Hide();
        }

        private bool DockPetForControls()
        {
            if (IsDocked && !dockLeaving)
            {
                if (!IsVisible) Show();
                return true;
            }
            if (!edgeArt.Complete) return false;
            EdgeDockTarget target = dockLeaving ? dockTarget : NearestFocusDock();
            if (target == null) return false;
            UserIntervened();
            if (!IsVisible) Show();
            EnterDock(target, true);
            return IsDocked;
        }

        private void SetKeepOnTop(bool enabled)
        {
            settings.KeepOnTop = enabled;
            Topmost = enabled;
            SaveSettings();
        }

        private void SetReduceMotion(bool enabled)
        {
            if (ReduceMotionEnabled == enabled) return;
            settings.ReduceMotion = enabled;
            long now = interactionClock.ElapsedMilliseconds;
            if (enabled)
            {
                CancelPatrol(now);
                CancelPetTouch();
                CancelEnhancedAction(true);
                gazeFrame = -1;
                gaze.Reset(now);
                if (dockLeaving) ExpandDock(dockLeaveSave);
                else if (dockTransition) EnterDock(dockTarget, false);
            }
            else
            {
                restIdle.Reset(now);
                personality.Postpone(now);
                playback.RestartQuietCadence(now);
            }
            AdvanceAnimation();
            AdvanceEdge();
            UpdateRenderCadence();
            SaveSettings();
        }

        private void ApplyReducedMotionPose(ref BitmapSource source, ref string family)
        {
            if (family == "rest:awake-idle" || family == "rest:wake-up")
            {
                source = restArt.Get("awake-idle", 0) ?? source;
                family = "rest:awake-idle";
                return;
            }

            if (family == "rest:fall-asleep") family = "state:idle";
            if (family == null || !family.StartsWith("state:", StringComparison.Ordinal)) return;
            string state = family.Substring("state:".Length);
            AnimationClip clip;
            if (!clips.TryGetValue(state, out clip) || clip.Frames.Count == 0) return;
            int frame = state == "idle" || state == "dragging" ? 0 :
                clip.PlaybackMode == "quiet" ? clip.RestFrame : clip.HoldFrame;
            source = clip.Frames[Math.Max(0, Math.Min(clip.Frames.Count - 1, frame))];
        }

        private void AddPetControlMenuItems(ContextMenu menu)
        {
            MenuItem dock = new MenuItem { Header = "收边" };
            dock.Click += delegate { DockPetForControls(); };
            MenuItem cycle = new MenuItem { Header = "切换显示状态", ToolTip = "依次展开、收边和隐藏" };
            cycle.Click += delegate { CyclePetVisibility(); };
            MenuItem reduce = new MenuItem { Header = "减少动效", IsCheckable = true, IsChecked = ReduceMotionEnabled };
            reduce.Click += delegate { SetReduceMotion(reduce.IsChecked); };
            menu.Opened += delegate
            {
                dock.IsEnabled = !IsDocked || dockLeaving;
                cycle.InputGestureText = visibilityHotkeyWindow != IntPtr.Zero ? VisibilityHotkeyDisplay : "";
                reduce.IsChecked = ReduceMotionEnabled;
            };
            menu.Items.Add(dock);
            menu.Items.Add(cycle);
            menu.Items.Add(reduce);
        }

        private void AddPetControlTrayItems(Forms.ContextMenuStrip menu)
        {
            Forms.ToolStripMenuItem dock = new Forms.ToolStripMenuItem("收边");
            dock.Click += delegate { Dispatcher.BeginInvoke(new Action(delegate { DockPetForControls(); })); };
            Forms.ToolStripMenuItem cycle = new Forms.ToolStripMenuItem("切换显示状态");
            cycle.Click += delegate { Dispatcher.BeginInvoke(new Action(CyclePetVisibility)); };
            Forms.ToolStripMenuItem reduce = new Forms.ToolStripMenuItem("减少动效") { Checked = ReduceMotionEnabled, CheckOnClick = true };
            reduce.Click += delegate
            {
                bool enabled = reduce.Checked;
                Dispatcher.BeginInvoke(new Action(delegate { SetReduceMotion(enabled); }));
            };
            menu.Opening += delegate
            {
                dock.Enabled = !IsDocked || dockLeaving || !IsVisible;
                cycle.ShortcutKeyDisplayString = visibilityHotkeyWindow != IntPtr.Zero ? VisibilityHotkeyDisplay : "";
                reduce.Checked = ReduceMotionEnabled;
            };
            menu.Items.Add(dock);
            menu.Items.Add(cycle);
            menu.Items.Add(reduce);
        }
    }
}
