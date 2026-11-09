using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RainbowDashDesktop
{
    internal sealed partial class PetWindow
    {
        private const int SidePeekDurationMs = 160;

        private sealed class SideDockMotion
        {
            public readonly Rect NormalWindow, SideWindow, NormalSprite;
            public readonly BitmapSource EntrySource;
            public double DockedAmount;

            public SideDockMotion(Rect normalWindow, Rect sideWindow, Rect normalSprite, BitmapSource entrySource)
            {
                NormalWindow = normalWindow;
                SideWindow = sideWindow;
                NormalSprite = normalSprite;
                EntrySource = entrySource;
            }
        }

        private SideDockMotion sideMotion;
        private double sidePeekFrom, sidePeekAmount, sidePeekGoal;
        private long sidePeekStartedAt;

        private bool SidePeekMoving
        {
            get { return IsDocked && EdgeDockGeometry.IsSide(dockTarget.Edge) && !dockTransition && !dockLeaving && sidePeekAmount != sidePeekGoal; }
        }

        private void ResetSidePeek()
        {
            sidePeekFrom = sidePeekAmount = sidePeekGoal = 0;
        }

        private void LayoutSideEdge(bool peek)
        {
            long now = interactionClock.ElapsedMilliseconds;
            double target = peek ? 1 : 0;
            if (sidePeekGoal != target)
            {
                sidePeekFrom = sidePeekAmount;
                sidePeekGoal = target;
                sidePeekStartedAt = now;
            }
            if (previewEdgeHover || !positioned) sidePeekAmount = target;
            AdvanceSidePeek(now);
        }

        private void AdvanceSidePeek(long now)
        {
            if (dockTransition || dockLeaving) return;
            if (ReducedMotionActive) sidePeekAmount = sidePeekGoal;
            else if (SidePeekMoving)
            {
                double amount = Math.Max(0, Math.Min(1, (now - sidePeekStartedAt) / (double)SidePeekDurationMs));
                sidePeekAmount = Mix(sidePeekFrom, sidePeekGoal, Ease(amount));
            }
            Rect compact = dockTarget.Bounds;
            Rect bounds = sidePeekAmount == 0 ? compact : MixBounds(compact, EdgeWindowBounds(true), sidePeekAmount);
            Rect sprite = SideSpriteBounds(bounds);
            edgePony.Width = sprite.Width;
            edgePony.Height = sprite.Height;
            edgePony.RenderTransform = dockTarget.Edge == DockEdge.Left ? new ScaleTransform(-1, 1) : Transform.Identity;
            Canvas.SetLeft(edgePony, sprite.Left - bounds.Left);
            Canvas.SetTop(edgePony, sprite.Top - bounds.Top);
            SetWindowBounds(bounds);
        }

        private Rect SideSpriteBounds(Rect window)
        {
            double size = EdgeDockGeometry.SidePoseSize(settings.Scale);
            double left = dockTarget.Edge == DockEdge.Left ? window.Right - size : window.Left;
            return new Rect(left, window.Top + (window.Height - size) / 2, size, size);
        }

        private Rect NormalSpriteBounds(Rect window)
        {
            double scale = settings.Scale;
            return new Rect(window.Left + (BaseWidth - spriteImage.Width) * scale / 2,
                window.Top + (2 + StageHeight - spriteImage.Height - spriteImage.Margin.Bottom) * scale,
                spriteImage.Width * scale, spriteImage.Height * scale);
        }

        private void AdvanceSideMotion(double dockedAmount)
        {
            sideMotion.DockedAmount = dockedAmount;
            SetWindowBounds(MixBounds(sideMotion.NormalWindow, sideMotion.SideWindow, dockedAmount));
        }

        private void PresentSideMotion()
        {
            if (dockLeaving) PresentNormalSprite(interactionClock.ElapsedMilliseconds);
            BitmapSource normal = sideMotion.EntrySource ?? spriteImage.Source as BitmapSource;
            BitmapSource side = edgePony.Source as BitmapSource;
            if (normal == null || side == null) return;
            Rect normalRect = sideMotion.NormalSprite;
            Rect sideRect = SideSpriteBounds(sideMotion.SideWindow);
            double amount = sideMotion.DockedAmount;
            bool mirrorSide = dockTarget.Edge == DockEdge.Left;
            Point normalAnchor = new Point(normalRect.Left + normalRect.Width * .32, normalRect.Top + normalRect.Height * .40);
            Point sideAnchor = new Point(sideRect.Left + sideRect.Width * (mirrorSide ? .30 : .70), sideRect.Top + sideRect.Height * .53);
            Point anchor = new Point(Mix(normalAnchor.X, sideAnchor.X, amount), Mix(normalAnchor.Y, sideAnchor.Y, amount));
            normalRect.Offset(anchor.X - normalAnchor.X - Left, anchor.Y - normalAnchor.Y - Top);
            sideRect.Offset(anchor.X - sideAnchor.X - Left, anchor.Y - sideAnchor.Y - Top);
            double blend = Ease(Math.Max(0, Math.Min(1, (amount - .38) / .24)));
            PaintEdgeSprites(normal, normalRect, false, side, sideRect, mirrorSide, (int)Math.Round(blend * 256));
        }

        private static double Ease(double value) { return value * value * (3 - 2 * value); }
        private static double Mix(double start, double end, double amount) { return start + (end - start) * amount; }
        private static Rect MixBounds(Rect start, Rect end, double amount)
        {
            return new Rect(Mix(start.X, end.X, amount), Mix(start.Y, end.Y, amount),
                Mix(start.Width, end.Width, amount), Mix(start.Height, end.Height, amount));
        }
    }
}
