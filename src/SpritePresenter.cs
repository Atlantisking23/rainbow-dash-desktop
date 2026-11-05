using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RainbowDashDesktop
{
    // A single final surface owns both drawing and hit testing. Mixing premultiplied
    // pixels avoids source-over opacity holes and does not deepen translucent edges.
    internal sealed class SpritePresenter
    {
        private sealed class Prepared
        {
            public BitmapSource Source;
            public byte[] Pixels;
            public Rect Bounds;
            public LinkedListNode<BitmapSource> Node;
        }
        private readonly int width, height, stride;
        private readonly Dictionary<BitmapSource, Prepared> cache = new Dictionary<BitmapSource, Prepared>();
        private readonly LinkedList<BitmapSource> cacheOrder = new LinkedList<BitmapSource>();
        private readonly byte[] outgoing, blended;
        private readonly WriteableBitmap transitionBitmap;
        private Prepared target;
        private byte[] current;
        private Rect bounds;
        private long startedAt;
        private int duration;
        public BitmapSource Output { get; private set; }
        public string Sequence { get; private set; }
        public bool IsTransitioning { get; private set; }
        public long Version { get; private set; }
        public int TransitionStarts { get; private set; }
        public int CachedFrames { get { return cache.Count; } }
        internal int PixelBufferAllocations { get; private set; }
        public Rect OpaqueBounds { get { return bounds; } }

        public SpritePresenter() : this(448, 400) { }
        internal SpritePresenter(int pixelWidth, int pixelHeight)
        {
            if (pixelWidth < 1 || pixelHeight < 1) throw new ArgumentOutOfRangeException("pixelWidth");
            width = pixelWidth; height = pixelHeight; stride = width * 4;
            outgoing = new byte[stride * height]; blended = new byte[stride * height];
            transitionBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        }

        public void Present(BitmapSource source, string sequence, long now, bool immediate, bool softenCels)
        {
            if (source == null) return;
            Prepared next = Prepare(source);
            Advance(now);
            if (target == null || immediate) { Stable(next, sequence); return; }
            bool sequenceChanged = !String.Equals(Sequence, sequence, StringComparison.Ordinal);
            if (!sequenceChanged && Object.ReferenceEquals(target, next)) return;
            if (sequenceChanged)
            {
                if (Equal(current, next.Pixels)) { Stable(next, sequence); return; }
                Begin(next, sequence, now, 120); return;
            }
            if (IsTransitioning)
            {
                // The incoming task clock continues during a boundary dissolve.
                // A blink or page turn updates its frame without restarting it.
                target = next; Advance(now); return;
            }
            if (softenCels && !Equal(current, next.Pixels)) Begin(next, sequence, now, 70);
            else Stable(next, sequence);
        }

        private void Begin(Prepared next, string sequence, long now, int milliseconds)
        {
            Buffer.BlockCopy(current, 0, outgoing, 0, outgoing.Length);
            target = next; Sequence = sequence; startedAt = now; duration = milliseconds;
            IsTransitioning = true; TransitionStarts++; Advance(now);
        }
        private void Stable(Prepared next, string sequence)
        {
            bool changed = !Object.ReferenceEquals(Output, next.Source) || IsTransitioning;
            target = next; Sequence = sequence; current = next.Pixels; bounds = next.Bounds;
            Output = next.Source; IsTransitioning = false;
            if (changed) Version++;
        }
        private void Advance(long now)
        {
            if (!IsTransitioning) return;
            long age = Math.Max(0, now - startedAt);
            if (age >= duration) { Stable(target, Sequence); return; }
            int weight = (int)(age * 256 / duration);
            Blend(outgoing, target.Pixels, weight);
        }
        internal void PresentWeighted(BitmapSource first, BitmapSource second, int weight, string sequence)
        {
            Prepared from = Prepare(first), to = Prepare(second);
            weight = Math.Max(0, Math.Min(256, weight));
            if (weight == 0) { Stable(from, sequence); return; }
            if (weight == 256 || Equal(from.Pixels, to.Pixels)) { Stable(to, sequence); return; }
            IsTransitioning = false; target = to; Sequence = sequence;
            Blend(from.Pixels, to.Pixels, weight);
        }
        private void Blend(byte[] first, byte[] second, int weight)
        {
            int inverse = 256 - weight;
            int x0 = width, y0 = height, x1 = -1, y1 = -1;
            for (int offset = 0; offset < blended.Length; offset += 4)
            {
                for (int channel = 0; channel < 4; channel++)
                    blended[offset + channel] = (byte)((first[offset + channel] * inverse + second[offset + channel] * weight + 128) >> 8);
                if (blended[offset + 3] > 24)
                {
                    int pixel = offset / 4, x = pixel % width, y = pixel / width;
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                }
            }
            bounds = Bounds(x0, y0, x1, y1);
            current = blended;
            transitionBitmap.WritePixels(new Int32Rect(0, 0, width, height), blended, stride, 0);
            Output = transitionBitmap; Version++;
        }
        public bool Hit(double normalizedX, double normalizedY)
        {
            if (current == null || Double.IsNaN(normalizedX) || Double.IsNaN(normalizedY) || normalizedX < 0 || normalizedY < 0 || normalizedX >= 1 || normalizedY >= 1) return false;
            int x = Math.Min(width - 1, (int)(normalizedX * width));
            int y = Math.Min(height - 1, (int)(normalizedY * height));
            return current[y * stride + x * 4 + 3] > 24;
        }
        private Prepared Prepare(BitmapSource source)
        {
            Prepared prepared;
            if (cache.TryGetValue(source, out prepared))
            { cacheOrder.Remove(prepared.Node); cacheOrder.AddLast(prepared.Node); return prepared; }
            if (source.PixelWidth != width || source.PixelHeight != height)
                throw new ArgumentException("Prepared sprites must share the presentation canvas.", "source");
            BitmapSource bitmap = source;
            if (bitmap.Format != PixelFormats.Pbgra32)
            { bitmap = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0); bitmap.Freeze(); }
            byte[] pixels = null;
            if (cache.Count >= 8)
            {
                BitmapSource oldest = cacheOrder.First.Value;
                Prepared evicted = cache[oldest]; cache.Remove(oldest); cacheOrder.RemoveFirst();
                if (!Object.ReferenceEquals(evicted.Pixels, current) && !Object.ReferenceEquals(evicted, target)) pixels = evicted.Pixels;
            }
            if (pixels == null) { pixels = new byte[stride * height]; PixelBufferAllocations++; }
            bitmap.CopyPixels(pixels, stride, 0);
            int x0 = width, y0 = height, x1 = -1, y1 = -1;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                if (pixels[y * stride + x * 4 + 3] > 24)
                { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
            prepared = new Prepared { Source = bitmap, Pixels = pixels, Bounds = Bounds(x0, y0, x1, y1) };
            prepared.Node = cacheOrder.AddLast(source); cache[source] = prepared;
            return prepared;
        }
        private Rect Bounds(int x0, int y0, int x1, int y1)
        { return x1 < x0 ? Rect.Empty : new Rect(x0 / (double)width, y0 / (double)height, (x1 - x0 + 1) / (double)width, (y1 - y0 + 1) / (double)height); }
        private static bool Equal(byte[] first, byte[] second)
        {
            if (Object.ReferenceEquals(first, second)) return true;
            if (first == null || second == null || first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false;
            return true;
        }
    }

    // Dock poses retain their actual Canvas geometry. Only the small union of
    // their current image rectangles is rasterized; the window still clips it.
    internal sealed class SpriteRasterCache
    {
        private struct Key : IEquatable<Key>
        {
            public BitmapSource Source; public Rect Rect; public bool Mirror; public int Width, Height;
            public bool Equals(Key other)
            { return Object.ReferenceEquals(Source, other.Source) && Rect == other.Rect && Mirror == other.Mirror && Width == other.Width && Height == other.Height; }
            public override bool Equals(object other) { return other is Key && Equals((Key)other); }
            public override int GetHashCode() { return RuntimeHelpers.GetHashCode(Source) ^ Rect.GetHashCode() ^ Width ^ (Height << 8) ^ Mirror.GetHashCode(); }
        }
        private readonly Dictionary<Key, BitmapSource> cache = new Dictionary<Key, BitmapSource>();
        private readonly Queue<Key> order = new Queue<Key>();
        internal int Count { get { return cache.Count; } }
        public BitmapSource Render(BitmapSource source, Rect rectangle, bool mirror, int width, int height)
        {
            Key key = new Key { Source = source, Rect = rectangle, Mirror = mirror, Width = width, Height = height };
            BitmapSource found;
            if (cache.TryGetValue(key, out found)) return found;
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.PushTransform(new ScaleTransform(2, 2));
                if (mirror) drawing.PushTransform(new ScaleTransform(-1, 1, rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2));
                drawing.DrawImage(source, rectangle);
                if (mirror) drawing.Pop(); drawing.Pop();
            }
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual); bitmap.Freeze();
            if (cache.Count >= 12) cache.Remove(order.Dequeue());
            cache[key] = bitmap; order.Enqueue(key); return bitmap;
        }
    }
}
