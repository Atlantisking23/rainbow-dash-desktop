using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

// Deterministic assembly only. This utility never draws character features.
// All nine frames share one crop, one scale, and one canvas registration.
internal static class EdgeAssetTool
{
    private sealed class Component
    {
        public int Id, Count, Left, Top, Right, Bottom, Frame;
        public bool Retained = true;
        public long SumX, SumY;
    }
    private sealed class FrameInfo
    {
        public int Index, Components, Pixels, CrossBoundaryComponents;
        public int TranslationX, TranslationY, SubstantialComponents;
        public int Left = Int32.MaxValue, Top = Int32.MaxValue;
        public int Right = Int32.MinValue, Bottom = Int32.MinValue;
        public int Width { get { return Right - Left + 1; } }
        public int Height { get { return Bottom - Top + 1; } }
    }

    public static int Main(string[] args)
    {
        try
        {
            Dictionary<string, string> options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !args[i].StartsWith("--")) throw new ArgumentException("Expected --name value arguments.");
                options.Add(args[i].Substring(2), args[i + 1]);
            }
            string sheet = Required(options, "sheet"), kind = Required(options, "kind").ToLowerInvariant();
            string destination = Path.GetFullPath(Required(options, "out"));
            string reportPath = options.ContainsKey("report") ? Path.GetFullPath(options["report"]) : Path.Combine(destination, "asset-report.json");
            if (kind != "top" && kind != "side" && kind != "bottom") throw new ArgumentException("kind must be top, side, or bottom.");
            int padding = options.ContainsKey("padding") ? Int32.Parse(options["padding"]) : 0;
            if (padding < 0 || padding > 64) throw new ArgumentException("padding must be between 0 and 64.");
            double islandFraction = options.ContainsKey("island-fraction") ? Double.Parse(options["island-fraction"], System.Globalization.CultureInfo.InvariantCulture) : 0;
            int islandGap = options.ContainsKey("island-gap") ? Int32.Parse(options["island-gap"]) : 4;
            bool alignTranslations = options.ContainsKey("align-translations") && Boolean.Parse(options["align-translations"]);
            if (islandFraction < 0 || islandFraction > 0.01 || islandGap < 0 || islandGap > 16) throw new ArgumentException("Invalid island filtering limits.");
            using (Bitmap original = new Bitmap(sheet))
            using (Bitmap source = new Bitmap(original.Width, original.Height, PixelFormat.Format32bppArgb))
            {
                source.Tag = Path.GetFullPath(sheet);
                using (Graphics graphics = Graphics.FromImage(source))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.DrawImageUnscaled(original, 0, 0);
                }
                Assemble(source, kind, destination, reportPath, padding, islandFraction, islandGap, alignTranslations);
            }
            Console.WriteLine("Validated and assembled nine " + kind + " frames: " + destination);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }

    private static string Required(Dictionary<string, string> options, string key)
    {
        string value;
        if (!options.TryGetValue(key, out value) || String.IsNullOrWhiteSpace(value)) throw new ArgumentException("Missing --" + key + ".");
        return value;
    }

    private static int[] ReadPixels(Bitmap bitmap)
    {
        Rectangle rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int[] pixels = new int[bitmap.Width * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * bitmap.Width, bitmap.Width);
            return pixels;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static void WritePixels(Bitmap bitmap, int[] pixels)
    {
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(pixels, y * bitmap.Width, IntPtr.Add(data.Scan0, y * data.Stride), bitmap.Width);
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static void Assemble(Bitmap source, string kind, string destination, string reportPath, int padding, double islandFraction, int islandGap, bool alignTranslations)
    {
        int width = source.Width, height = source.Height;
        if (width < 300 || height < 300) throw new InvalidDataException("Sprite sheet is too small for nine high quality frames.");
        int[] pixels = ReadPixels(source), labels = new int[pixels.Length], queue = new int[pixels.Length];
        List<Component> components = new List<Component>();
        int transparent = 0, opaque = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((uint)pixels[i] >> 24 == 0) transparent++;
            else opaque++;
        }
        if (transparent < pixels.Length / 100) throw new InvalidDataException("The source sheet must have a transparent background.");
        for (int seed = 0; seed < pixels.Length; seed++)
        {
            if (labels[seed] != 0 || ((uint)pixels[seed] >> 24) == 0) continue;
            Component component = new Component { Id = components.Count + 1, Left = width, Top = height, Right = -1, Bottom = -1 };
            int head = 0, tail = 0;
            queue[tail++] = seed;
            labels[seed] = component.Id;
            while (head < tail)
            {
                int index = queue[head++], x = index % width, y = index / width;
                component.Count++; component.SumX += x; component.SumY += y;
                component.Left = Math.Min(component.Left, x); component.Top = Math.Min(component.Top, y);
                component.Right = Math.Max(component.Right, x); component.Bottom = Math.Max(component.Bottom, y);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int neighbour = ny * width + nx;
                    if (labels[neighbour] == 0 && ((uint)pixels[neighbour] >> 24) != 0)
                    {
                        labels[neighbour] = component.Id;
                        queue[tail++] = neighbour;
                    }
                }
            }
            int column = Math.Min(2, (int)(component.SumX * 3 / component.Count / width));
            int row = Math.Min(2, (int)(component.SumY * 3 / component.Count / height));
            component.Frame = row * 3 + column;
            components.Add(component);
        }
        int removedPixels = 0, removedComponents = 0, largestRemovedComponent = 0;
        int[] largest = new int[9];
        foreach (Component component in components) largest[component.Frame] = Math.Max(largest[component.Frame], component.Count);
        if (islandFraction > 0)
        {
            foreach (Component component in components)
            {
                int limit = (int)(largest[component.Frame] * islandFraction);
                if (component.Count > limit) continue;
                bool nearSubstantialComponent = false;
                for (int y = component.Top; y <= component.Bottom && !nearSubstantialComponent; y++)
                for (int x = component.Left; x <= component.Right && !nearSubstantialComponent; x++)
                {
                    if (labels[y * width + x] != component.Id) continue;
                    for (int dy = -islandGap; dy <= islandGap && !nearSubstantialComponent; dy++)
                    for (int dx = -islandGap; dx <= islandGap && !nearSubstantialComponent; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        int neighbourId = labels[ny * width + nx];
                        if (neighbourId == 0) continue;
                        Component neighbour = components[neighbourId - 1];
                        if (neighbour.Frame == component.Frame && neighbour.Count > limit) nearSubstantialComponent = true;
                    }
                }
                if (!nearSubstantialComponent)
                {
                    component.Retained = false;
                    removedPixels += component.Count;
                    removedComponents++;
                    largestRemovedComponent = Math.Max(largestRemovedComponent, component.Count);
                }
            }
        }
        FrameInfo[] frames = new FrameInfo[9];
        for (int i = 0; i < frames.Length; i++) frames[i] = new FrameInfo { Index = i };
        if (alignTranslations)
        {
            Rectangle[] guides = new Rectangle[9];
            bool[] assigned = new bool[9];
            foreach (Component component in components)
            {
                if (!component.Retained || component.Count <= largest[component.Frame] * 0.01) continue;
                int cellLeft = (component.Frame % 3) * width / 3, cellTop = (component.Frame / 3) * height / 3;
                Rectangle bounds = new Rectangle(component.Left - cellLeft, component.Top - cellTop, component.Right - component.Left + 1, component.Bottom - component.Top + 1);
                guides[component.Frame] = assigned[component.Frame] ? Rectangle.Union(guides[component.Frame], bounds) : bounds;
                assigned[component.Frame] = true;
                frames[component.Frame].SubstantialComponents++;
            }
            for (int i = 0; i < 9; i++)
            {
                if (!assigned[i]) throw new InvalidDataException("No substantial character components in frame " + i);
                if (kind == "side") frames[i].TranslationX = guides[0].Right - guides[i].Right;
                else frames[i].TranslationX = (int)Math.Round((guides[0].Left + guides[0].Right - guides[i].Left - guides[i].Right) / 2.0, MidpointRounding.AwayFromZero);
                frames[i].TranslationY = kind == "top" ? guides[0].Top - guides[i].Top : guides[0].Bottom - guides[i].Bottom;
            }
        }
        int unionLeft = Int32.MaxValue, unionTop = Int32.MaxValue, unionRight = Int32.MinValue, unionBottom = Int32.MinValue;
        foreach (Component component in components)
        {
            if (!component.Retained) continue;
            FrameInfo frame = frames[component.Frame];
            int cellLeft = (frame.Index % 3) * width / 3, cellTop = (frame.Index / 3) * height / 3;
            int cellRight = ((frame.Index % 3) + 1) * width / 3 - 1, cellBottom = ((frame.Index / 3) + 1) * height / 3 - 1;
            frame.Components++; frame.Pixels += component.Count;
            frame.Left = Math.Min(frame.Left, component.Left - cellLeft); frame.Top = Math.Min(frame.Top, component.Top - cellTop);
            frame.Right = Math.Max(frame.Right, component.Right - cellLeft); frame.Bottom = Math.Max(frame.Bottom, component.Bottom - cellTop);
            if (component.Left < cellLeft || component.Top < cellTop || component.Right > cellRight || component.Bottom > cellBottom) frame.CrossBoundaryComponents++;
        }
        foreach (FrameInfo frame in frames)
        {
            if (frame.Pixels < 100) throw new InvalidDataException("Frame " + frame.Index + " is absent or too small. Check the source grid.");
            unionLeft = Math.Min(unionLeft, frame.Left + frame.TranslationX); unionTop = Math.Min(unionTop, frame.Top + frame.TranslationY);
            unionRight = Math.Max(unionRight, frame.Right + frame.TranslationX); unionBottom = Math.Max(unionBottom, frame.Bottom + frame.TranslationY);
        }
        int cropWidth = unionRight - unionLeft + 1, cropHeight = unionBottom - unionTop + 1;
        if (cropWidth > width * 0.6 || cropHeight > height * 0.6) throw new InvalidDataException("Characters appear connected across multiple cells; independent frames cannot be safely reconstructed.");
        float scale = Math.Min((512f - padding * 2) / cropWidth, (512f - padding * 2) / cropHeight);
        float drawWidth = cropWidth * scale, drawHeight = cropHeight * scale;
        float left = (512f - drawWidth) / 2, top = (512f - drawHeight) / 2;
        if (kind == "side") left = 512f - padding - drawWidth;
        if (kind == "top") top = padding;
        if (kind == "bottom") top = 512f - padding - drawHeight;
        Directory.CreateDirectory(destination);
        string stage = Path.Combine(destination, ".assembling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        List<object> outputReports = new List<object>();
        try
        {
            for (int frameIndex = 0; frameIndex < 9; frameIndex++)
            {
                int cellLeft = (frameIndex % 3) * width / 3, cellTop = (frameIndex / 3) * height / 3;
                int[] framePixels = new int[cropWidth * cropHeight];
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int originalIndex = y * width + x, label = labels[originalIndex];
                    if (label == 0 || !components[label - 1].Retained || components[label - 1].Frame != frameIndex) continue;
                    int tx = x - cellLeft + frames[frameIndex].TranslationX - unionLeft, ty = y - cellTop + frames[frameIndex].TranslationY - unionTop;
                    framePixels[ty * cropWidth + tx] = pixels[originalIndex];
                }
                string filename = frameIndex.ToString("000") + ".png";
                using (Bitmap crop = new Bitmap(cropWidth, cropHeight, PixelFormat.Format32bppArgb))
                using (Bitmap output = new Bitmap(512, 512, PixelFormat.Format32bppArgb))
                {
                    WritePixels(crop, framePixels);
                    using (Graphics graphics = Graphics.FromImage(output))
                    {
                        graphics.Clear(Color.Transparent);
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.DrawImage(crop, new RectangleF(left, top, drawWidth, drawHeight), new RectangleF(0, 0, cropWidth, cropHeight), GraphicsUnit.Pixel);
                    }
                    output.Save(Path.Combine(stage, filename), ImageFormat.Png);
                    int[] saved = ReadPixels(output);
                    int visible = 0, alphaClear = 0, outLeft = 512, outTop = 512, outRight = -1, outBottom = -1;
                    for (int index = 0; index < saved.Length; index++)
                    {
                        if (((uint)saved[index] >> 24) == 0) { alphaClear++; continue; }
                        visible++;
                        int x = index % 512, y = index / 512;
                        outLeft = Math.Min(outLeft, x); outTop = Math.Min(outTop, y); outRight = Math.Max(outRight, x); outBottom = Math.Max(outBottom, y);
                    }
                    if (visible < 100 || alphaClear < 100) throw new InvalidDataException("Output failed alpha validation: " + filename);
                    outputReports.Add(new { index = frameIndex, file = filename, sourceComponents = frames[frameIndex].Components,
                        sourcePixels = frames[frameIndex].Pixels, crossBoundaryComponentsPreserved = frames[frameIndex].CrossBoundaryComponents,
                        sourceLargestComponent = largest[frameIndex], substantialComponents = frames[frameIndex].SubstantialComponents,
                        translation = new { x = frames[frameIndex].TranslationX, y = frames[frameIndex].TranslationY },
                        sourceLocalBounds = new { left = frames[frameIndex].Left, top = frames[frameIndex].Top, width = frames[frameIndex].Width, height = frames[frameIndex].Height },
                        outputBounds = new { left = outLeft, top = outTop, width = outRight - outLeft + 1, height = outBottom - outTop + 1 },
                        visiblePixels = visible, transparentPixels = alphaClear });
                }
            }
            foreach (string staged in Directory.GetFiles(stage, "*.png"))
            {
                string target = Path.Combine(destination, Path.GetFileName(staged));
                if (File.Exists(target)) File.Delete(target);
                File.Move(staged, target);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
            File.WriteAllText(reportPath, new JavaScriptSerializer().Serialize(new {
                source = Path.GetFullPath(source.Tag as string ?? ""), kind = kind, sourceWidth = width, sourceHeight = height,
                sourceTransparentPixels = transparent, sourceVisiblePixels = opaque, sourceConnectedComponents = components.Count,
                islandFilter = new { maximumFractionOfLargest = islandFraction, minimumGapPixels = islandGap, removedComponents = removedComponents, largestRemovedComponentPixels = largestRemovedComponent, removedPixels = removedPixels, retainedPixels = opaque - removedPixels },
                translationAlignment = alignTranslations,
                outputWidth = 512, outputHeight = 512, sharedCrop = new { left = unionLeft, top = unionTop, width = cropWidth, height = cropHeight },
                sharedScale = scale, sharedRegistration = new { left = left, top = top, width = drawWidth, height = drawHeight, padding = padding },
                semantics = new[] { "neutral", "attention", "work", "halfblink", "closedblink", "reopen", "completedsmile", "interrupt", "sleep" }, frames = outputReports
            }));
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                foreach (string remaining in Directory.GetFiles(stage)) File.Delete(remaining);
                Directory.Delete(stage, false);
            }
        }
    }
}
