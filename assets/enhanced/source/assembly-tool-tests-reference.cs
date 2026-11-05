using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Web.Script.Serialization;

internal static class EnhancedAssetToolTests
{
    private static int assertions;
    private static void Check(bool condition, string reason)
    { assertions++; if (!condition) throw new Exception(reason); }
    private static Dictionary<string, object> Read(string path)
    { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path)); }
    private static double CenterX(string path)
    {
        using (Bitmap image = new Bitmap(path))
        {
            double weighted = 0, coverage = 0;
            for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
            { byte alpha = image.GetPixel(x, y).A; weighted += x * alpha; coverage += alpha; }
            return weighted / coverage;
        }
    }
    public static int Main()
    {
        string temp = Path.GetFullPath(Path.GetTempPath());
        string root = Path.Combine(temp, "RainbowDashEnhancedAssetTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string sheet = Path.Combine(root, "fixture.png");
            using (Bitmap image = new Bitmap(900, 600, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(image))
                {
                    graphics.Clear(Color.Transparent);
                    for (int index = 0; index < 6; index++)
                    {
                        int x = index % 3 * 300, y = index / 3 * 300;
                        graphics.FillRectangle(Brushes.CornflowerBlue, x + 80 + index, y + 50 + index, 140, 180);
                        graphics.FillRectangle(Brushes.Magenta, x + 10, y + 10, 10, 10);
                        graphics.FillRectangle(Brushes.Black, x + 75 + index, y + 50 + index, 3, 3);
                    }
                    graphics.FillRectangle(Brushes.CornflowerBlue, 210, 110, 105, 20);
                }
                image.Save(sheet, ImageFormat.Png);
            }
            foreach (string anchor in new[] { "center", "top", "right", "bottom" })
            {
                string destination = Path.Combine(root, anchor), report = Path.Combine(root, anchor + ".json");
                Check(EnhancedAssetTool.Main(new[] { "--sheet", sheet, "--kind", "test-motion", "--anchor", anchor,
                    "--out", destination, "--report", report }) == 0, "Assembly failed for " + anchor);
                Check(Directory.GetFiles(destination, "*.png").Length == 6, "Six frames required");
                Dictionary<string, object> metadata = Read(report);
                Dictionary<string, object> grid = (Dictionary<string, object>)metadata["sourceGrid"];
                Check((int)grid["columns"] == 3 && (int)grid["rows"] == 2, "Grid must remain 3 by 2");
                Check((string)metadata["canvasAnchor"] == anchor, "Anchor independent of clip name");
                Check(!(bool)metadata["translationAlignment"], "Default retains authored motion");
                ArrayList frames = (ArrayList)metadata["frames"];
                int sourcePixels = 0;
                foreach (Dictionary<string, object> frame in frames) sourcePixels += (int)frame["sourcePixels"];
                Check(sourcePixels == (int)metadata["sourceVisiblePixels"], "Every input alpha pixel assigned once");
                Dictionary<string, object> first = (Dictionary<string, object>)frames[0];
                Check((int)first["crossBoundaryComponentsPreserved"] == 1, "Cross-grid character extension preserved");
                Dictionary<string, object> registration = (Dictionary<string, object>)metadata["sharedRegistration"];
                double left = Convert.ToDouble(registration["left"]), top = Convert.ToDouble(registration["top"]);
                double width = Convert.ToDouble(registration["width"]), height = Convert.ToDouble(registration["height"]);
                Check(anchor != "top" || Math.Abs(top) < .001, "Top anchored");
                Check(anchor != "bottom" || Math.Abs(top + height - 512) < .001, "Bottom anchored");
                Check(anchor != "right" || Math.Abs(left + width - 512) < .001, "Right anchored");
                for (int index = 0; index < 6; index++)
                using (Bitmap output = new Bitmap(Path.Combine(destination, index.ToString("000") + ".png")))
                {
                    Check(output.Width == 512 && output.Height == 512, "Fixed output canvas");
                    Check(output.GetPixel(0, 511).A == 0, "Transparent canvas remains transparent");
                }
            }
            string filtered = Path.Combine(root, "filtered"), filteredReport = Path.Combine(root, "filtered.json");
            Check(EnhancedAssetTool.Main(new[] { "--sheet", sheet, "--kind", "close-book", "--anchor", "bottom", "--out", filtered,
                "--report", filteredReport, "--island-fraction", "0.01", "--island-gap", "4", "--align-translations", "true" }) == 0,
                "Optional static-reference integer registration");
            Dictionary<string, object> filteredMetadata = Read(filteredReport);
            Dictionary<string, object> filter = (Dictionary<string, object>)filteredMetadata["islandFilter"];
            Check((int)filter["removedComponents"] == 6 && (int)filter["removedPixels"] == 600,
                "Only detached distant noise removed, close disconnected detail retained");
            Check((int)filter["largestRemovedComponentPixels"] == 100, "Removed component size auditable");
            ArrayList filteredFrames = (ArrayList)filteredMetadata["frames"];
            Dictionary<string, object> second = (Dictionary<string, object>)filteredFrames[1];
            Dictionary<string, object> translation = (Dictionary<string, object>)second["translation"];
            Check((int)translation["y"] == -1, "Book/cloud bottom anchor registration uses integer translation");
            string glow = Path.Combine(root, "glow.png");
            using (Bitmap image = new Bitmap(900, 600, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(image))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    using (Brush low = new SolidBrush(Color.FromArgb(35, 15, 140, 220)))
                    for (int frame = 0; frame < 6; frame++)
                    {
                        int x = frame % 3 * 300, y = frame / 3 * 300;
                        graphics.FillRectangle(low, x + 90, y + 80, 170, 200);
                        graphics.FillRectangle(Brushes.CornflowerBlue, x + 100, y + 100, 140, 160);
                    }
                }
                image.Save(glow, ImageFormat.Png);
            }
            string matteDestination = Path.Combine(root, "matte"), matteReport = Path.Combine(root, "matte.json");
            Check(EnhancedAssetTool.Main(new[] { "--sheet", glow, "--kind", "matte-test", "--out", matteDestination,
                "--report", matteReport, "--core-alpha", "200", "--edge-radius", "2", "--anchor", "bottom",
                "--target-box", "88,130,336,354" }) == 0, "Standard high-coverage matte extraction");
            Dictionary<string, object> matteMetadata = Read(matteReport);
            Dictionary<string, object> matte = (Dictionary<string, object>)matteMetadata["matteExtraction"];
            Check((int)matte["removedLowCoveragePixels"] == (170 * 200 - 144 * 164) * 6, "Only coverage beyond two-pixel antialias fringe removed");
            Check((int)matteMetadata["sourceVisiblePixels"] == 144 * 164 * 6, "All core pixels and exact two-pixel edge fringe preserved");
            Dictionary<string, object> target = (Dictionary<string, object>)matteMetadata["targetBox"];
            Check((int)target["left"] == 88 && (int)target["top"] == 130 && (int)target["width"] == 336 && (int)target["height"] == 354,
                "Old reading reference box stays explicit");
            Dictionary<string, object> placement = (Dictionary<string, object>)matteMetadata["sharedRegistration"];
            Check(Convert.ToDouble(placement["left"]) >= 88 && Convert.ToDouble(placement["top"]) >= 130 &&
                Math.Abs(Convert.ToDouble(placement["top"]) + Convert.ToDouble(placement["height"]) - 484) < .001,
                "Registered cloud baseline remains at old reading baseline");
            string mirrorDestination = Path.Combine(root, "mirror"), mirrorReport = Path.Combine(root, "mirror.json");
            Check(EnhancedAssetTool.Main(new[] { "--sheet", sheet, "--kind", "mirror-test", "--out", mirrorDestination,
                "--report", mirrorReport, "--mirror-frames", "2,3" }) == 0, "Selected whole-cel orientation normalization");
            ArrayList mirrorFrames = (ArrayList)Read(mirrorReport)["frames"];
            for (int frame = 0; frame < 6; frame++)
            {
                bool expected = frame == 2 || frame == 3;
                Check((bool)((Dictionary<string, object>)mirrorFrames[frame])["mirrored"] == expected, "Mirror metadata tracks exact authored frame");
                double originalX = CenterX(Path.Combine(root, "center", frame.ToString("000") + ".png"));
                double mirrorX = CenterX(Path.Combine(mirrorDestination, frame.ToString("000") + ".png"));
                Check(expected ? Math.Abs(originalX + mirrorX - 511) < 1 : Math.Abs(originalX - mirrorX) < .001,
                    "Whole-cel reflection preserves coverage and never modifies other cels");
            }
            string opaque = Path.Combine(root, "opaque.png");
            using (Bitmap image = new Bitmap(900, 600))
            { using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.White); image.Save(opaque, ImageFormat.Png); }
            Check(EnhancedAssetTool.Main(new[] { "--sheet", opaque, "--kind", "rejected", "--out", Path.Combine(root, "rejected") }) == 1,
                "Opaque pseudo-transparent sheet rejected");
            Console.WriteLine("Enhanced asset assembly: " + assertions + " checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("RainbowDashEnhancedAssetTests-", StringComparison.Ordinal) && Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }
}
