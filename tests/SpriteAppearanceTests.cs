using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RainbowDashDesktop;

internal static class SpriteAppearanceTests
{
    private static int checks;
    private static readonly byte[] Target = { 142, 216, 252 };
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new Exception(message); }

    private static byte[] Pixels(BitmapSource image)
    {
        BitmapSource straight = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        straight.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }
    private static BitmapSource Bitmap(int width, int height, byte[] pixels)
    {
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze(); return image;
    }
    private static void Set(byte[] pixels, int width, int x, int y, byte r, byte g, byte b, byte a)
    { int offset = (y * width + x) * 4; pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r; pixels[offset + 3] = a; }

    // Large flat skin patches model a cel's base fill. Separate color swatches
    // stand for cloud, mane, iris, outline and book; they are not skin samples.
    private static BitmapSource Swatches(byte r, byte g, byte b)
    {
        const int width = 32, height = 8;
        byte[] pixels = new byte[width * height * 4];
        byte[][] protectedColors = {
            new byte[] { 255, 255, 255 }, new byte[] { 235, 55, 40 },
            new byte[] { 250, 220, 35 }, new byte[] { 45, 180, 65 },
            new byte[] { 155, 60, 185 }, new byte[] { 16, 20, 29 },
            new byte[] { 116, 62, 54 }, new byte[] { 25, 95, 235 }
        };
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            if (x < 24) Set(pixels, width, x, y, r, g, b, 255);
            else { byte[] color = protectedColors[x - 24]; Set(pixels, width, x, y, color[0], color[1], color[2], (byte)(y == 1 ? 128 : y == 2 ? 1 : 255)); }
        }
        foreach (byte a in new byte[] { 0, 1, 64, 128, 229, 230, 254, 255 })
            Set(pixels, width, 4, a == 0 ? 0 : a == 1 ? 1 : a == 64 ? 2 : a == 128 ? 3 : a == 229 ? 4 : a == 230 ? 5 : a == 254 ? 6 : 7, r, g, b, a);
        return Bitmap(width, height, pixels);
    }

    private static BitmapSource Marker(int height, bool cloud)
    {
        const int width = 512;
        byte[] pixels = new byte[width * height * 4];
        if (cloud)
        {
            // A wider cloud and extended green wing alter the whole silhouette,
            // while the same body patch stays in the same source-pixel position.
            for (int y = height - 32; y < height - 16; y++) for (int x = 16; x < 496; x++)
                Set(pixels, width, x, y, 255, 255, 255, 255);
            for (int y = 24; y < 64; y++) for (int x = 24; x < 260; x++)
                Set(pixels, width, x, y, 45, 180, 65, 255);
        }
        for (int y = height - 144; y < height - 80; y++) for (int x = 160; x < 224; x++)
            Set(pixels, width, x, y, Target[0], Target[1], Target[2], 255);
        return Bitmap(width, height, pixels);
    }

    private static Rect BodyPatch(BitmapSource image)
    {
        byte[] pixels = Pixels(image); int x0 = image.PixelWidth, y0 = image.PixelHeight, x1 = -1, y1 = -1;
        for (int y = 0; y < image.PixelHeight; y++) for (int x = 0; x < image.PixelWidth; x++)
        {
            int offset = (y * image.PixelWidth + x) * 4;
            if (pixels[offset + 3] < 230 || Math.Abs(pixels[offset + 2] - Target[0]) > 4 ||
                Math.Abs(pixels[offset + 1] - Target[1]) > 4 || Math.Abs(pixels[offset] - Target[2]) > 4) continue;
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
        return x1 < x0 ? Rect.Empty : new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    // The audit's broad opaque cyan fill filter, not an outline, cloud or pose
    // bbox. Comparing the most frequent fill tests the visible coat consistency.
    private static bool Coat(int r, int g, int b)
    { return r >= 55 && r <= 190 && g >= 145 && g <= 240 && b >= 175 && b > r + 25 && g > r + 20 && b >= g - 3; }
    private static int Mode(byte[] pixels)
    {
        Dictionary<int, int> colors = new Dictionary<int, int>(); int mode = -1, largest = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            if (pixels[i + 3] < 230 || !Coat(r, g, b)) continue;
            int key = r << 16 | g << 8 | b, count;
            colors.TryGetValue(key, out count); colors[key] = ++count;
            if (count > largest) { largest = count; mode = key; }
        }
        return mode;
    }
    private static bool IsProtected(int r, int g, int b)
    {
        return r >= 232 && g >= 232 && b >= 232 ||
            r >= 170 && g < 150 && b < 170 || r >= 180 && g >= 150 && b < 140 ||
            g >= 120 && g > r + 20 && g > b + 20 || r >= 70 && b >= 100 && r > g + 25 && b > g + 25 ||
            r < 70 && g < 70 && b < 70 || r >= 70 && r < 160 && g < 105 && b < 95;
    }
    private static bool SameAlpha(byte[] before, byte[] after)
    { for (int i = 3; i < before.Length; i += 4) if (before[i] != after[i]) return false; return true; }
    private static bool SameProtected(byte[] before, byte[] after, out int count)
    {
        count = 0;
        for (int i = 0; i < before.Length; i += 4)
        {
            if (before[i + 3] < 230 || !IsProtected(before[i + 2], before[i + 1], before[i])) continue;
            count++;
            for (int channel = 0; channel < 4; channel++) if (before[i + channel] != after[i + channel]) return false;
        }
        return true;
    }
    private static BitmapSource Load(string file)
    {
        BitmapImage bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(Path.GetFullPath(file), UriKind.Absolute); bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    private static string Hash(string file)
    { using (SHA256 hash = SHA256.Create()) using (FileStream input = File.OpenRead(file)) return Convert.ToBase64String(hash.ComputeHash(input)); }
    private static string Family(string relative)
    {
        string[] parts = relative.Replace('\\', '/').Split('/');
        return (parts[0] == "frames" ? "state" : parts[0]) + ":" + parts[1];
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1) { Console.WriteLine("Pass the runtime assets directory."); return 2; }
        string fixture = Path.Combine(Path.GetTempPath(), "RainbowDashAppearanceTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            Directory.CreateDirectory(fixture);
            File.WriteAllText(Path.Combine(fixture, "presentation.json"),
                "{\"version\":1,\"referenceCoat\":\"#8ED8FC\",\"profiles\":{" +
                "\"state:shifted\":{\"scale\":1,\"anchorX\":256,\"anchorY\":480,\"offsetX\":32,\"offsetY\":-16}," +
                "\"state:scaled\":{\"scale\":1.25,\"anchorX\":256,\"anchorY\":480,\"offsetX\":0,\"offsetY\":0}," +
                "\"state:other-anchor\":{\"scale\":1.25,\"anchorX\":128,\"anchorY\":256,\"offsetX\":0,\"offsetY\":0}}}");
            SpriteAppearance appearance = new SpriteAppearance(fixture);
            Check(appearance.Colorize(null, "state:received") == null && appearance.Prepare(null, "state:received", false) == null,
                "Missing art remains missing without creating an opaque placeholder.");
            BitmapSource first = Swatches(123, 207, 249), second = Swatches(159, 223, 251);
            byte[] original = Pixels(first), originalSecond = Pixels(second);
            BitmapSource coloredFirst = appearance.Colorize(first, "rest:awake-idle");
            BitmapSource coloredSecond = appearance.Colorize(second, "state:working");
            byte[] calibrated = Pixels(coloredFirst), calibratedSecond = Pixels(coloredSecond);
            int target = Target[0] << 16 | Target[1] << 8 | Target[2];
            Check(Mode(calibrated) == target && Mode(calibratedSecond) == target,
                "Different source coat fills display the same natural cyan base.");
            Check(coloredFirst.PixelWidth == first.PixelWidth && coloredFirst.PixelHeight == first.PixelHeight &&
                coloredSecond.PixelWidth == second.PixelWidth && coloredSecond.PixelHeight == second.PixelHeight,
                "Color calibration preserves the source canvas dimensions.");
            Check(SameAlpha(original, calibrated) && SameAlpha(originalSecond, calibratedSecond),
                "Calibration preserves every alpha value, including translucent antialias and fully transparent pixels.");
            bool swatchesUnchanged = true;
            for (int y = 0; y < 8; y++) for (int x = 24; x < 32; x++) for (int channel = 0; channel < 4; channel++)
            { int offset = (y * 32 + x) * 4 + channel; if (original[offset] != calibrated[offset]) swatchesUnchanged = false; }
            Check(swatchesUnchanged, "Cloud whites, rainbow colors, black eyes and brown books retain their exact source channels at all tested opacities.");
            Check(original.SequenceEqual(Pixels(first)) && originalSecond.SequenceEqual(Pixels(second)),
                "Calibrating a display frame never mutates its original bitmap.");
            Check(coloredFirst.IsFrozen && Object.ReferenceEquals(coloredFirst, appearance.Colorize(first, "rest:awake-idle")),
                "Repeated display calibration reuses an immutable result.");

            byte[] boundaryPixels = Pixels(first);
            Set(boundaryPixels, 32, 31, 3, 90, 175, 240, 255);
            Set(boundaryPixels, 32, 31, 4, 90, 175, 241, 255);
            byte[] boundaryResult = Pixels(appearance.Colorize(Bitmap(32, 8, boundaryPixels), "rest:awake-idle"));
            foreach (int y in new int[] { 3, 4 })
            {
                int offset = (y * 32 + 31) * 4;
                Check(Enumerable.Range(0, 4).All(delegate(int channel) { return boundaryPixels[offset + channel] == boundaryResult[offset + channel]; }),
                    "Saturated-blue swatches at and beyond the B-G=65 boundary retain all original channels.");
            }

            BitmapSource marker = Marker(512, false), withCloud = Marker(512, true), flight = Marker(384, false);
            BitmapSource prepared = appearance.Prepare(marker, "state:unprofiled", false);
            BitmapSource mirrored = appearance.Prepare(marker, "state:unprofiled", true);
            BitmapSource cloudy = appearance.Prepare(withCloud, "state:unprofiled", false);
            BitmapSource flying = appearance.Prepare(flight, "state:unprofiled", false);
            Check(prepared.PixelWidth == 448 && prepared.PixelHeight == 400 && prepared.Format == PixelFormats.Pbgra32,
                "Every prepared normal frame uses the shared 448 by 400 premultiplied presentation canvas.");
            Check(prepared.IsFrozen && Object.ReferenceEquals(prepared, appearance.Prepare(marker, "state:unprofiled", false)),
                "A repeated prepared frame reuses its frozen cache entry.");
            Check(mirrored.IsFrozen && !Object.ReferenceEquals(prepared, mirrored) &&
                Object.ReferenceEquals(mirrored, appearance.Prepare(marker, "state:unprofiled", true)),
                "Opposite facing has its own stable immutable display frame.");
            Rect body = BodyPatch(prepared), reversedBody = BodyPatch(mirrored), cloudBody = BodyPatch(cloudy), flightBody = BodyPatch(flying);
            Check(!body.IsEmpty && body.Width >= 48 && body.Width <= 52 && body.Height >= 48 && body.Height <= 52,
                "A 64-source-pixel body feature displays at the common 400/512 ruler, without fitting its small bbox.");
            Check(cloudBody == body, "Adding a cloud and an extended wing cannot resize or slide the unchanged body feature.");
            Check(flying.PixelWidth == 448 && flying.PixelHeight == 400 && flightBody == body,
                "A 512 by 384 flying source keeps the same feature size and bottom registration instead of receiving an extra 12-percent enlargement.");
            Check(body.X + body.Width / 2 < 224 && reversedBody.X + reversedBody.Width / 2 > 224 &&
                reversedBody.Width == body.Width && reversedBody.Height == body.Height && reversedBody.Y == body.Y,
                "Changing facing visibly moves an asymmetric feature to the other side while preserving its size and vertical support.");
            AppearanceProfile shiftedProfile = appearance.Profile("state:shifted"), fallback = appearance.Profile("state:missing");
            Check(shiftedProfile.Scale == 1 && shiftedProfile.AnchorX == 256 && shiftedProfile.AnchorY == 480 &&
                shiftedProfile.OffsetX == 32 && shiftedProfile.OffsetY == -16 && fallback.Scale == 1 && fallback.AnchorY == 480,
                "JSON profiles expose source-coordinate calibration and unknown families keep the common default ruler.");
            BitmapSource shifted = appearance.Prepare(marker, "state:shifted", false);
            Rect shiftedBody = BodyPatch(shifted), scaledBody = BodyPatch(appearance.Prepare(marker, "state:scaled", false));
            Check(!Object.ReferenceEquals(prepared, shifted) && Math.Abs(shiftedBody.X - body.X - 25) <= 1 &&
                Math.Abs(shiftedBody.Y - body.Y + 12.5) <= 1,
                "Family offsets move the visible feature in source-pixel units and cannot reuse an incompatible cache entry.");
            Check(scaledBody.Width >= body.Width * 1.20 && scaledBody.Width <= body.Width * 1.30 && scaledBody.Y < body.Y,
                "A declared whole-group scale changes the body feature around the support anchor.");
            Rect otherAnchor = BodyPatch(appearance.Prepare(marker, "state:other-anchor", false));
            Check(otherAnchor.Y > scaledBody.Y + 30 && otherAnchor.Width == scaledBody.Width,
                "Changing the calibration anchor changes placement rather than refitting the feature to a new pose bbox.");

            string assets = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar);
            string[] runtime = Directory.GetFiles(assets, "*.png", SearchOption.AllDirectories)
                .Where(delegate(string file) { string stem = Path.GetFileNameWithoutExtension(file); return stem.Length == 3 && stem.All(Char.IsDigit); })
                .OrderBy(delegate(string file) { return file; }, StringComparer.Ordinal).ToArray();
            Check(runtime.Length == 169, "The complete installed art inventory contains 169 runtime cels.");
            SpriteAppearance actual = new SpriteAppearance(assets);
            string[] guarded = { "frames/received/000.png", "frames/working/005.png", "frames/idle/000.png",
                "rest/awake-idle/000.png", "enhanced/hair/000.png", "edge/top/000.png" };
            Dictionary<string, string> fileHashes = guarded.ToDictionary(delegate(string relative) { return relative; },
                delegate(string relative) { return Hash(Path.Combine(assets, relative)); });
            int protectedPixels = 0, groups = 0; string lastFamily = null;
            foreach (string file in runtime)
            {
                string relative = file.Substring(assets.Length + 1), family = Family(relative);
                if (family != lastFamily) { groups++; lastFamily = family; }
                BitmapSource source = Load(file); byte[] before = Pixels(source);
                BitmapSource result = actual.Colorize(source, family); byte[] after = Pixels(result);
                int mode = Mode(after), protectedCount;
                Check(mode >= 0 && Math.Abs(((mode >> 16) & 255) - Target[0]) <= 3 &&
                    Math.Abs(((mode >> 8) & 255) - Target[1]) <= 3 && Math.Abs((mode & 255) - Target[2]) <= 3,
                    "Visible body-fill mode stays within three channels of the shared base: " + relative);
                Check(result.IsFrozen && result.PixelWidth == source.PixelWidth && result.PixelHeight == source.PixelHeight && SameAlpha(before, after),
                    "Source-sized art and all original alpha values are retained: " + relative);
                Check(SameProtected(before, after, out protectedCount), "Cloud/eye/mane/book color channels remain original: " + relative);
                protectedPixels += protectedCount;
                Check(before.SequenceEqual(Pixels(source)), "Display calibration leaves the decoded source untouched: " + relative);
            }
            Check(groups == 24 && protectedPixels > 100000,
                "Real-art verification covers all 24 pose groups and a substantial protected-color population.");
            // These cyan cloud-outline samples sit inside the general coat
            // color range. White-only protection would miss them and recolor
            // the cloud while correcting the pony's actual skin fill.
            string[] cloudFiles = { "frames/working/000.png", "frames/idle/000.png", "rest/fall-asleep/003.png" };
            int[,] cloudSamples = { { 133, 388, 166, 207, 230, 230 }, { 414, 361, 149, 222, 244, 240 }, { 421, 351, 138, 210, 243, 252 } };
            for (int sample = 0; sample < cloudFiles.Length; sample++)
            {
                BitmapSource cloudSource = Load(Path.Combine(assets, cloudFiles[sample]));
                byte[] cloudBefore = Pixels(cloudSource), cloudAfter = Pixels(actual.Colorize(cloudSource, Family(cloudFiles[sample])));
                int offset = (cloudSamples[sample, 1] * cloudSource.PixelWidth + cloudSamples[sample, 0]) * 4;
                Check(cloudSource.PixelWidth == 512 && cloudSource.PixelHeight == 512 &&
                    cloudBefore[offset + 2] == cloudSamples[sample, 2] && cloudBefore[offset + 1] == cloudSamples[sample, 3] &&
                    cloudBefore[offset] == cloudSamples[sample, 4] && cloudBefore[offset + 3] == cloudSamples[sample, 5],
                    "Cloud-outline fixture coordinates still identify the audited source color: " + cloudFiles[sample]);
                Check(Enumerable.Range(0, 4).All(delegate(int channel) { return cloudBefore[offset + channel] == cloudAfter[offset + channel]; }),
                    "Cyan cloud-outline ink retains all original channels during skin calibration: " + cloudFiles[sample]);
            }
            foreach (KeyValuePair<string, string> originalHash in fileHashes)
                Check(Hash(Path.Combine(assets, originalHash.Key)) == originalHash.Value, "Display calibration does not rewrite the original PNG: " + originalHash.Key);
            Console.WriteLine("PASS " + checks + " sprite appearance checks; 169 cels, 24 groups, " + protectedPixels + " protected-color pixels");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
        finally
        {
            // Only the uniquely named fixture created above is removed.
            string resolved = Path.GetFullPath(fixture), temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("RainbowDashAppearanceTests-", StringComparison.Ordinal) && Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }
}
