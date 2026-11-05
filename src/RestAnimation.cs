using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace RainbowDashDesktop
{
    // Natural standing and cloud sleeping share a fixed 512px canvas and scale.
    // Never fit individual body bounds; transition cels add and remove the cloud.
    internal sealed class RestArt
    {
        internal static readonly string[] Names = { "awake-idle", "fall-asleep", "wake-up" };
        private readonly Dictionary<string, BitmapSource[]> clips = new Dictionary<string, BitmapSource[]>();
        public bool Complete { get; private set; }
        public RestArt(string assets)
        {
            Complete = true;
            foreach (string name in Names)
            {
                BitmapSource[] frames = new BitmapSource[6];
                for (int i = 0; i < frames.Length; i++)
                {
                    string file = Path.Combine(assets, "rest", name, i.ToString("000") + ".png");
                    if (!File.Exists(file)) { Complete = false; continue; }
                    BitmapImage bitmap = new BitmapImage(); bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(Path.GetFullPath(file), UriKind.Absolute);
                    bitmap.EndInit(); bitmap.Freeze();
                    if (bitmap.PixelWidth != 512 || bitmap.PixelHeight != 512) Complete = false;
                    frames[i] = bitmap;
                }
                clips[name] = frames;
            }
        }
        public bool Has(string name)
        { BitmapSource[] value; return clips.TryGetValue(name ?? "", out value) && Array.TrueForAll(value, delegate(BitmapSource cel) { return cel != null; }); }
        public BitmapSource Get(string name, int frame)
        { BitmapSource[] value; return clips.TryGetValue(name ?? "", out value) ? value[Math.Max(0, Math.Min(5, frame))] : null; }
    }

    // Clear open eyes at rest; closed eyelids appear only during a brief blink.
    // This visual clock deliberately does not report attention to RestController.
    internal sealed class RestIdleTimeline
    {
        private long startedAt;
        public void Reset(long now) { startedAt = now; }
        public int Sample(long now)
        {
            long tick = Math.Max(0, now - startedAt) % 9800;
            if (tick >= 4100 && tick < 4140) return 2;
            if (tick >= 4140 && tick < 4190) return 3;
            if (tick >= 4190 && tick < 4260) return 4;
            if (tick >= 7900 && tick < 8350) return 5;
            return 0;
        }
    }

    // Compare absolute pointer coordinates, not window-relative positions. A
    // parked pointer, changing art, quota refresh or automatic docking cannot
    // manufacture attention. Small physical moves accumulate to two DIPs.
    internal sealed class RestPointerActivity
    {
        private bool known;
        private double anchorX, anchorY;
        public bool Observe(double x, double y, bool nearby)
        {
            if (!known) { known = true; anchorX = x; anchorY = y; return nearby; }
            double dx = x - anchorX, dy = y - anchorY;
            if (dx * dx + dy * dy < 4) return false;
            anchorX = x; anchorY = y;
            return nearby;
        }
    }
}
