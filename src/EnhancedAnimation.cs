using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RainbowDashDesktop
{
    internal sealed class EnhancedArt
    {
        internal static readonly string[] Names = { "dock-side", "dock-top", "dock-bottom", "close-book", "interrupt-book", "hair", "preen", "proud", "gaze-idle", "gaze-working" };
        private readonly Dictionary<string, BitmapSource[]> clips = new Dictionary<string, BitmapSource[]>();
        private readonly Dictionary<string, Rect> endpoints = new Dictionary<string, Rect>();
        public bool Complete { get; private set; }
        public EnhancedArt(string assets)
        {
            Complete = true;
            foreach (string name in Names)
            {
                BitmapSource[] frames = new BitmapSource[6];
                for (int i = 0; i < 6; i++)
                {
                    string file = Path.Combine(assets, "enhanced", name, i.ToString("000") + ".png");
                    if (!File.Exists(file)) { Complete = false; continue; }
                    BitmapImage bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(file, UriKind.Absolute); bitmap.EndInit(); bitmap.Freeze(); frames[i] = bitmap;
                }
                clips[name] = frames;
                if (frames[5] != null) endpoints[name] = OpaqueBounds(frames[5]);
            }
        }
        public bool Has(string name)
        { BitmapSource[] value; return clips.TryGetValue(name, out value) && Array.TrueForAll(value, delegate(BitmapSource frame) { return frame != null; }); }
        public BitmapSource Get(string name, int frame)
        { BitmapSource[] value; return clips.TryGetValue(name ?? "", out value) ? value[Math.Max(0, Math.Min(5, frame))] : null; }
        public Rect Endpoint(string name)
        { Rect value; return endpoints.TryGetValue(name, out value) ? value : new Rect(0, 0, 512, 512); }
        public static Rect OpaqueBounds(BitmapSource source)
        {
            FormatConvertedBitmap rgba = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = rgba.PixelWidth, height = rgba.PixelHeight, stride = width * 4;
            byte[] pixels = new byte[stride * height]; rgba.CopyPixels(pixels, stride, 0);
            int x0 = width, y0 = height, x1 = -1, y1 = -1;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                if (pixels[y * stride + x * 4 + 3] > 24)
                { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
            return x1 < x0 ? new Rect(0, 0, width, height) : new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        }
    }

    internal sealed class EnhancedAction
    {
        public string Name { get; private set; }
        public string PendingState { get; private set; }
        public long StartedAt { get; private set; }
        public long Duration { get; private set; }
        public bool Reverse { get; private set; }
        public bool IsBridge { get { return PendingState != null; } }
        public bool Active { get { return Name != null; } }
        public void Start(string name, long now, long duration, string pendingState, bool reverse)
        { Name = name; StartedAt = now; Duration = Math.Max(1, duration); PendingState = pendingState; Reverse = reverse; }
        public int Frame(long now)
        {
            int[] weights = Weights(Name);
            int total = 0; foreach (int weight in weights) total += weight;
            double time = Math.Max(0, now - StartedAt) * (double)total / Duration;
            int frame = 5;
            for (int i = 0; i < 6; i++) { if (time < weights[i]) { frame = i; break; } time -= weights[i]; }
            return Reverse ? 5 - frame : frame;
        }
        private static int[] Weights(string name)
        {
            if (name == "hair") return new int[] { 220, 100, 100, 130, 180, 470 };
            if (name == "proud") return new int[] { 140, 160, 160, 500, 120, 120 };
            if (name == "preen") return new int[] { 180, 230, 300, 300, 260, 230 };
            if (name == "close-book") return new int[] { 80, 110, 180, 180, 150, 140 };
            if (name == "interrupt-book") return new int[] { 45, 55, 80, 110, 85, 45 };
            return new int[] { 100, 90, 100, 110, 110, 90 };
        }
        public bool Finished(long now) { return Active && now - StartedAt >= Duration; }
        public void Cancel() { Name = null; PendingState = null; }
    }

    internal sealed class FocusReturnPosition
    {
        public Rect NormalBounds;
        public EdgeDockTarget Dock;
    }
}
