using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RainbowDashDesktop
{
    internal sealed class AppearanceProfile
    {
        public double Scale = 1;
        public double AnchorX = 256, AnchorY = 480;
        public double OffsetX, OffsetY;
    }

    // A display calibration, not an asset rewrite. Original PNGs and alpha stay
    // intact. Every pose uses the same virtual source-pixel ruler; cloud, tail
    // and wing bounds never determine a new fit on individual animation frames.
    internal sealed class SpriteAppearance
    {
        public const int CanvasWidth = 448, CanvasHeight = 400;
        private const double PixelScale = 400.0 / 512;
        private readonly Dictionary<string, AppearanceProfile> profiles = new Dictionary<string, AppearanceProfile>(StringComparer.Ordinal);
        private readonly Dictionary<PreparedKey, BitmapSource> prepared = new Dictionary<PreparedKey, BitmapSource>();
        private readonly Dictionary<PreparedKey, BitmapSource> colored = new Dictionary<PreparedKey, BitmapSource>();
        private readonly Queue<PreparedKey> colorOrder = new Queue<PreparedKey>();
        private readonly int[] referenceCoat = { 142, 216, 252 };

        public SpriteAppearance(string assets)
        {
            string file = Path.Combine(assets ?? "", "presentation.json");
            if (!File.Exists(file)) return;
            Dictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("Invalid presentation calibration.");
            object value;
            if (root.TryGetValue("referenceCoat", out value) && value is string)
            {
                string rgb = ((string)value).TrimStart('#'); int packed;
                if (rgb.Length != 6 || !Int32.TryParse(rgb, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out packed))
                    throw new InvalidDataException("Invalid coat reference color.");
                referenceCoat[0] = (packed >> 16) & 255; referenceCoat[1] = (packed >> 8) & 255; referenceCoat[2] = packed & 255;
            }
            Dictionary<string, object> entries = root.TryGetValue("profiles", out value) ? value as Dictionary<string, object> : null;
            if (entries == null) return;
            foreach (KeyValuePair<string, object> pair in entries)
            {
                Dictionary<string, object> entry = pair.Value as Dictionary<string, object>;
                if (entry == null) continue;
                AppearanceProfile p = new AppearanceProfile();
                p.Scale = Number(entry, "scale", 1, .75, 1.25);
                p.AnchorX = Number(entry, "anchorX", 256, 0, 512); p.AnchorY = Number(entry, "anchorY", 480, 0, 512);
                p.OffsetX = Number(entry, "offsetX", 0, -80, 80); p.OffsetY = Number(entry, "offsetY", 0, -80, 80);
                profiles[pair.Key] = p;
            }
        }

        private static double Number(Dictionary<string, object> entry, string name, double fallback, double min, double max)
        {
            object value; if (!entry.TryGetValue(name, out value)) return fallback;
            double n = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Double.IsNaN(n) || Double.IsInfinity(n) || n < min || n > max)
                throw new InvalidDataException("Presentation value outside safe bounds: " + name);
            return n;
        }

        internal AppearanceProfile Profile(string family)
        {
            AppearanceProfile p;
            if (profiles.TryGetValue(family ?? "", out p)) return p;
            return new AppearanceProfile();
        }

        internal BitmapSource Colorize(BitmapSource source, string family)
        {
            if (source == null) return null;
            PreparedKey key = new PreparedKey(source, family ?? "", false);
            BitmapSource value;
            if (colored.TryGetValue(key, out value)) return value;
            value = ColorizeCore(source, family);
            // Edge and docking clips need source-sized images. Keep this cache
            // bounded; prepared normal poses have their own immutable cache.
            colored[key] = value; colorOrder.Enqueue(key);
            while (colorOrder.Count > 18) colored.Remove(colorOrder.Dequeue());
            return value;
        }

        internal BitmapSource Prepare(BitmapSource source, string family, bool mirror)
        {
            if (source == null) return null;
            PreparedKey key = new PreparedKey(source, family ?? "", mirror);
            BitmapSource value;
            if (prepared.TryGetValue(key, out value)) return value;
            AppearanceProfile p = Profile(family);
            BitmapSource display = ColorizeCore(source, family);
            double virtualHeight = 512.0 * source.PixelHeight / source.PixelWidth;
            double virtualTop = 512 - virtualHeight;
            double sx = (mirror ? -1 : 1) * p.Scale * PixelScale;
            double tx = 24 + (p.AnchorX * (1 - p.Scale) + p.OffsetX + (mirror ? 512 * p.Scale : 0)) * PixelScale;
            double ty = (p.AnchorY * (1 - p.Scale) + p.OffsetY) * PixelScale;
            DrawingVisual visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushTransform(new MatrixTransform(new Matrix(sx, 0, 0, p.Scale * PixelScale, tx, ty)));
                dc.DrawImage(display, new Rect(0, virtualTop, 512, virtualHeight));
                dc.Pop();
            }
            RenderTargetBitmap frame = new RenderTargetBitmap(CanvasWidth, CanvasHeight, 96, 96, PixelFormats.Pbgra32);
            frame.Render(visual); frame.Freeze(); prepared[key] = frame;
            return frame;
        }

        private BitmapSource ColorizeCore(BitmapSource source, string family)
        {
            FormatConvertedBitmap bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
            byte[] pixels = new byte[stride * height]; bgra.CopyPixels(pixels, stride, 0);
            bool[] cloud = CloudProtection(pixels, width, height, family);
            Dictionary<int, int> histogram = new Dictionary<int, int>();
            int mode = 0, largest = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (pixels[i + 3] < 230 || cloud != null && cloud[i / 4] || !TrainingCoat(r, g, b)) continue;
                int key = r << 16 | g << 8 | b, count;
                histogram.TryGetValue(key, out count); histogram[key] = ++count;
                if (count > largest) { largest = count; mode = key; }
            }
            if (largest > 0)
            {
                int dr = referenceCoat[0] - ((mode >> 16) & 255), dg = referenceCoat[1] - ((mode >> 8) & 255), db = referenceCoat[2] - (mode & 255);
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i + 3] == 0 || cloud != null && cloud[i / 4]) continue;
                    int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                    if (!DisplayCoat(r, g, b)) continue;
                    // Feather only the color-range boundary; alpha is never
                    // changed. White clouds, dark outlines, eyes and rainbow
                    // colors stay outside the body-fill calibration range.
                    double weight = 1;
                    if (r < 55) weight *= (r - 40) / 15.0;
                    if (r > 190) weight *= (205 - r) / 15.0;
                    if (g < 145) weight *= (g - 130) / 15.0;
                    if (b < 175) weight *= (b - 160) / 15.0;
                    if (b - r < 35) weight *= (b - r - 20) / 15.0;
                    if (g - r < 25) weight *= (g - r - 15) / 10.0;
                    if (b - g > 50) weight *= (65 - (b - g)) / 15.0;
                    weight = Math.Max(0, Math.Min(1, weight));
                    pixels[i] = Clamp(b + db * weight); pixels[i + 1] = Clamp(g + dg * weight); pixels[i + 2] = Clamp(r + dr * weight);
                }
            }
            BitmapSource result = BitmapSource.Create(width, height, PositiveDpi(source.DpiX), PositiveDpi(source.DpiY), PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze(); return result;
        }

        private static bool TrainingCoat(int r, int g, int b)
        { return r >= 55 && r <= 190 && g >= 145 && g <= 240 && b >= 175 && b > r + 25 && g > r + 20 && b >= g - 3 && b - g < 65; }
        private static bool DisplayCoat(int r, int g, int b)
        { return r > 40 && r < 205 && g > 130 && g <= 245 && b > 160 && b > r + 20 && g > r + 15 && b >= g - 3 && b - g < 65; }

        private static bool[] CloudProtection(byte[] pixels, int width, int height, string family)
        {
            bool hasCloud = family == "state:working" || family == "state:waiting" || family == "state:idle" ||
                family == "rest:fall-asleep" || family == "rest:wake-up" || family == "enhanced:gaze-working" ||
                family == "enhanced:close-book" || family == "enhanced:interrupt-book";
            if (!hasCloud) return null;
            int count = width * height, minimum = Math.Max(32, count / 300);
            bool[] seen = new bool[count], cloud = new bool[count]; int[] queue = new int[count];
            for (int seed = 0; seed < count; seed++)
            {
                if (seen[seed] || !WhiteCore(pixels, seed)) continue;
                int take = 0, put = 0; long sumY = 0;
                queue[put++] = seed; seen[seed] = true;
                while (take < put)
                {
                    int index = queue[take++], x = index % width, y = index / width; sumY += y;
                    if (x > 0) CloudVisit(pixels, index - 1, seen, queue, ref put);
                    if (x + 1 < width) CloudVisit(pixels, index + 1, seen, queue, ref put);
                    if (y > 0) CloudVisit(pixels, index - width, seen, queue, ref put);
                    if (y + 1 < height) CloudVisit(pixels, index + width, seen, queue, ref put);
                }
                if (put >= minimum && sumY / (double)put >= height * .64)
                    for (int i = 0; i < put; i++) cloud[queue[i]] = true;
            }
            // The surrounding blue ink belongs to the cloud as well. A small
            // source-resolution halo protects that ink without including the
            // character's separate head/torso fill or any frame-dependent fit.
            int span = width + 1; int[] integral = new int[span * (height + 1)];
            for (int y = 0; y < height; y++)
            {
                int row = 0;
                for (int x = 0; x < width; x++)
                { if (cloud[y * width + x]) row++; integral[(y + 1) * span + x + 1] = integral[y * span + x + 1] + row; }
            }
            int radius = Math.Max(2, (int)Math.Round(width * 5.0 / 512));
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int left = Math.Max(0, x - radius), right = Math.Min(width, x + radius + 1);
                int top = Math.Max(0, y - radius), bottom = Math.Min(height, y + radius + 1);
                cloud[y * width + x] = integral[bottom * span + right] - integral[top * span + right] -
                    integral[bottom * span + left] + integral[top * span + left] > 0;
            }
            return cloud;
        }
        private static bool WhiteCore(byte[] pixels, int index)
        { int p = index * 4; return pixels[p + 3] >= 224 && pixels[p] >= 224 && pixels[p + 1] >= 224 && pixels[p + 2] >= 224; }
        private static void CloudVisit(byte[] pixels, int index, bool[] seen, int[] queue, ref int put)
        { if (!seen[index] && WhiteCore(pixels, index)) { seen[index] = true; queue[put++] = index; } }
        private static byte Clamp(double n) { return (byte)Math.Max(0, Math.Min(255, Math.Round(n))); }
        private static double PositiveDpi(double dpi) { return Double.IsNaN(dpi) || dpi <= 0 ? 96 : dpi; }

        private struct PreparedKey : IEquatable<PreparedKey>
        {
            private readonly BitmapSource source; private readonly string family; private readonly bool mirror;
            public PreparedKey(BitmapSource s, string f, bool m) { source = s; family = f; mirror = m; }
            public bool Equals(PreparedKey other) { return Object.ReferenceEquals(source, other.source) && family == other.family && mirror == other.mirror; }
            public override bool Equals(object obj) { return obj is PreparedKey && Equals((PreparedKey)obj); }
            public override int GetHashCode() { return RuntimeHelpers.GetHashCode(source) ^ family.GetHashCode() ^ (mirror ? 0x421 : 0); }
        }
    }
}
