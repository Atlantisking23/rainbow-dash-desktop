using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace RainbowDashDesktop
{
    // Dock artwork has its own timeline. It never changes the task playback clock.
    internal sealed class EdgeArt
    {
        private readonly Dictionary<string, BitmapSource[]> frames = new Dictionary<string, BitmapSource[]>();
        public bool Complete { get; private set; }
        public EdgeArt(string assets)
        {
            Complete = true;
            foreach (string folder in new string[] { "side", "top", "bottom" })
            {
                BitmapSource[] images = new BitmapSource[9];
                for (int i = 0; i < images.Length; i++)
                {
                    string path = Path.Combine(assets, "edge", folder, i.ToString("000") + ".png");
                    if (!File.Exists(path)) { Complete = false; continue; }
                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(path, UriKind.Absolute); bitmap.EndInit(); bitmap.Freeze();
                    images[i] = bitmap;
                }
                frames[folder] = images;
            }
        }
        public BitmapSource Get(DockEdge edge, int frame)
        {
            string folder = edge == DockEdge.Top ? "top" : edge == DockEdge.Bottom ? "bottom" : "side";
            BitmapSource[] images = frames[folder];
            return images[Math.Max(0, Math.Min(8, frame))] ?? images[0];
        }
    }

    internal sealed class EdgeExpressionController
    {
        private string state;
        private long stateAt;
        private long nextBlink = 3800;
        private long blinkAt = -1;
        public int Sample(string requested, long now, bool reduceMotion = false, DockEdge edge = DockEdge.None)
        {
            if (requested != state)
            { state = requested; stateAt = now; blinkAt = -1; nextBlink = now + 3600; }
            long stateAge = now - stateAt;
            bool side = edge == DockEdge.Left || edge == DockEdge.Right;
            bool needsAttention = state == "waiting" || state == "error";
            int basis = state == "working" ? 2 : state == "completed" && stateAge < 1900 ? 6 :
                state == "received" && stateAge < 1100 ? 1 :
                (state == "interrupted" && stateAge < 1600 || needsAttention && (side || stateAge < 1600)) ? 7 :
                side ? 1 : 0;
            if (reduceMotion || side && needsAttention) return basis;
            // Closed happy eyes are deliberate; only open-eye expressions blink.
            if (basis == 6) return 6;
            if (blinkAt < 0 && now >= nextBlink) blinkAt = now;
            if (blinkAt >= 0)
            {
                long age = now - blinkAt;
                int halfBlink = side && basis == 2 ? 5 : 3;
                if (age < 45) return halfBlink;
                if (age < 110) return 4;
                if (age < 165) return side ? halfBlink : 5;
                blinkAt = -1; nextBlink = now + 4300 + (now % 1700);
            }
            // The calm rest cel is occasional, so the pet keeps a recognizable face.
            if (basis == 0 && (now - stateAt) % 14000 > 12100) return 8;
            return basis;
        }
    }
}
