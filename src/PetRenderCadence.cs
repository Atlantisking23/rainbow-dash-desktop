using System;
using System.Windows.Media;
using System.Windows.Threading;

namespace RainbowDashDesktop
{
    internal sealed partial class PetWindow
    {
        private bool renderCadenceReady;
        private DispatcherTimer quietRenderTimer;

        private void OnPetVisibilityChanged()
        {
            if (!renderCadenceReady || closing) return;
            if (!IsVisible)
            {
                CancelPatrol(interactionClock.ElapsedMilliseconds);
                CancelPetTouch();
                interactionTimer.Stop();
            }
            else if (!demo && !smokeTest)
                interactionTimer.Start();
            UpdateRenderCadence();
        }

        private void UpdateRenderCadence()
        {
            if (!renderCadenceReady || closing) return;
            if (!IsVisible) { StopRenderCadence(); return; }

            string restPreview = EffectiveRestPreview;
            bool sleeping = restPreview == "sleeping" || restPreview == null && rest.Stage == RestStage.Sleeping;
            bool animated = drag.IsPressed || edgePressed || dockTransition || dockLeaving || PatrolInProgress;
            if (!animated && !ReducedMotionActive && !IsDocked && !sleeping)
            {
                animated = rest.Stage != RestStage.Awake || restPreview == "fall" || restPreview == "wake" ||
                    restPreview == null && (playback.VisualState != "idle" || enhancedAction.Active ||
                    previewAction != null || gazeFrame >= 0 || PetTouchInProgress || spritePresenter.IsTransitioning);
            }

            if (animated)
            {
                if (quietRenderTimer != null) quietRenderTimer.Stop();
                if (!renderingHooked) { CompositionTarget.Rendering += FrameRendering; renderingHooked = true; }
                return;
            }

            if (renderingHooked) { CompositionTarget.Rendering -= FrameRendering; renderingHooked = false; }
            if (quietRenderTimer == null)
            {
                quietRenderTimer = new DispatcherTimer(DispatcherPriority.Background);
                quietRenderTimer.Tick += FrameRendering;
            }
            TimeSpan interval = TimeSpan.FromMilliseconds(sleeping || ReducedMotionActive || IsDocked && focus.IsActive ? 100 : 32);
            if (quietRenderTimer.Interval != interval) quietRenderTimer.Interval = interval;
            if (!quietRenderTimer.IsEnabled) quietRenderTimer.Start();
        }

        private void StopRenderCadence()
        {
            if (renderingHooked) { CompositionTarget.Rendering -= FrameRendering; renderingHooked = false; }
            if (quietRenderTimer != null) quietRenderTimer.Stop();
        }
    }
}
