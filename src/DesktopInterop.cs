using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RainbowDashDesktop
{
    internal sealed class FullscreenInfo
    {
        public readonly IntPtr WindowHandle;
        public readonly string MonitorId;
        public readonly Rect MonitorBounds, WorkArea, WindowBounds;
        internal FullscreenInfo(IntPtr window, string monitor, Rect bounds, Rect work, Rect windowBounds)
        { WindowHandle = window; MonitorId = monitor; MonitorBounds = bounds; WorkArea = work; WindowBounds = windowBounds; }
    }

    internal static class DesktopInterop
    {
        private const int GwlExStyle = -20, GwlStyle = -16;
        private const long WsExTransparent = 0x20, WsCaption = 0x00C00000, WsThickFrame = 0x00040000;
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
        {
            public int Size; public NativeRect Monitor, Work; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)] private static extern int GetWindowLong32(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)] private static extern IntPtr GetWindowLong64(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)] private static extern int SetWindowLong32(IntPtr window, int index, int value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] private static extern IntPtr SetWindowLong64(IntPtr window, int index, IntPtr value);
        [DllImport("kernel32.dll")] private static extern void SetLastError(uint value);
        private static long Style(IntPtr window, int index)
        { return IntPtr.Size == 8 ? GetWindowLong64(window, index).ToInt64() : (uint)GetWindowLong32(window, index); }
        private static Rect RectOf(NativeRect value)
        { return value.Right <= value.Left || value.Bottom <= value.Top ? Rect.Empty : new Rect(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top); }

        public static bool TryGetCursor(out Point physicalPixels)
        {
            NativePoint point;
            if (!GetCursorPos(out point)) { physicalPixels = new Point(); return false; }
            physicalPixels = new Point(point.X, point.Y); return true;
        }
        // Used only while this pet already owns an ongoing pointer gesture. It
        // prevents a background no-activate window losing an outside mouse-up.
        public static bool IsLeftButtonDown() { return (GetAsyncKeyState(1) & 0x8000) != 0; }
        public static bool TryGetForegroundFullscreen(IntPtr ownWindow, out FullscreenInfo info)
        {
            info = null;
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == ownWindow || !IsWindowVisible(foreground) || IsIconic(foreground)) return false;
            uint process; GetWindowThreadProcessId(foreground, out process);
            if (process == (uint)Process.GetCurrentProcess().Id) return false;
            StringBuilder name = new StringBuilder(256);
            if (GetClassName(foreground, name, name.Capacity) == 0 || IsExcludedClass(name.ToString())) return false;
            NativeRect window;
            if (!GetWindowRect(foreground, out window)) return false;
            IntPtr monitor = MonitorFromWindow(foreground, 2);
            MonitorInfo monitorInfo = new MonitorInfo(); monitorInfo.Size = Marshal.SizeOf(typeof(MonitorInfo));
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo)) return false;
            Rect bounds = RectOf(monitorInfo.Monitor), work = RectOf(monitorInfo.Work), windowBounds = RectOf(window);
            if (!IsFullscreenCandidate(windowBounds, bounds, work, name.ToString(), IsZoomed(foreground), Style(foreground, GwlStyle))) return false;
            info = new FullscreenInfo(foreground, monitorInfo.Device ?? "", bounds, work, windowBounds);
            return true;
        }
        internal static bool IsExcludedClass(string name)
        {
            return String.Equals(name, "Progman", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase);
        }
        internal static bool IsFullscreenCandidate(Rect window, Rect monitor, Rect work, string name, bool zoomed, long style)
        {
            if (window.IsEmpty || monitor.IsEmpty || IsExcludedClass(name)) return false;
            // A normal captioned/resizable maximized window is still a work window,
            // including on systems whose taskbar happens to auto-hide.
            if ((style & (WsCaption | WsThickFrame)) != 0) return false;
            const double tolerance = 2;
            return window.Left <= monitor.Left + tolerance && window.Top <= monitor.Top + tolerance &&
                window.Right >= monitor.Right - tolerance && window.Bottom >= monitor.Bottom - tolerance;
        }
        public static bool SetClickThrough(IntPtr ownWindow, bool transparent)
        {
            uint process;
            if (ownWindow == IntPtr.Zero || !IsWindow(ownWindow) || GetWindowThreadProcessId(ownWindow, out process) == 0 ||
                process != (uint)Process.GetCurrentProcess().Id) return false;
            long current = Style(ownWindow, GwlExStyle);
            long desired = transparent ? current | WsExTransparent : current & ~WsExTransparent;
            if (current == desired) return true;
            SetLastError(0);
            long old = IntPtr.Size == 8 ? SetWindowLong64(ownWindow, GwlExStyle, new IntPtr(desired)).ToInt64() :
                SetWindowLong32(ownWindow, GwlExStyle, unchecked((int)desired));
            return old != 0 || Marshal.GetLastWin32Error() == 0;
        }
        internal static bool IsClickThrough(IntPtr ownWindow)
        { return ownWindow != IntPtr.Zero && (Style(ownWindow, GwlExStyle) & WsExTransparent) != 0; }
    }

    // The window maps the pointer through its current Image transform, including
    // mirroring and Uniform letterboxing, before passing normalized coordinates.
    internal static class AlphaMaskCache
    {
        private sealed class Mask
        {
            public readonly int Width, Height;
            public readonly bool[] Bits;
            public Mask(BitmapSource source)
            {
                Width = source.PixelWidth; Height = source.PixelHeight;
                Bits = new bool[Width * Height];
                FormatConvertedBitmap rgba = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                byte[] pixels = new byte[Width * Height * 4]; rgba.CopyPixels(pixels, Width * 4, 0);
                // A summed-area table computes the two-source-pixel safety halo
                // in linear time, avoiding 25 writes per opaque pixel on the UI thread.
                int integralStride = Width + 1;
                int[] integral = new int[integralStride * (Height + 1)];
                for (int y = 0; y < Height; y++)
                {
                    int row = 0;
                    for (int x = 0; x < Width; x++)
                    {
                        if (pixels[(y * Width + x) * 4 + 3] >= 24) row++;
                        integral[(y + 1) * integralStride + x + 1] = integral[y * integralStride + x + 1] + row;
                    }
                }
                for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                {
                    int left = Math.Max(0, x - 2), right = Math.Min(Width, x + 3);
                    int top = Math.Max(0, y - 2), bottom = Math.Min(Height, y + 3);
                    Bits[y * Width + x] = integral[bottom * integralStride + right] - integral[top * integralStride + right] -
                        integral[bottom * integralStride + left] + integral[top * integralStride + left] > 0;
                }
            }
        }
        private static readonly ConditionalWeakTable<BitmapSource, Mask> Cache = new ConditionalWeakTable<BitmapSource, Mask>();
        public static bool Hit(BitmapSource source, double normalizedX, double normalizedY)
        {
            if (source == null || Double.IsNaN(normalizedX) || Double.IsNaN(normalizedY) || normalizedX < 0 || normalizedY < 0 || normalizedX >= 1 || normalizedY >= 1) return false;
            Mask mask = Cache.GetValue(source, delegate(BitmapSource key) { return new Mask(key); });
            return mask.Bits[(int)(normalizedY * mask.Height) * mask.Width + (int)(normalizedX * mask.Width)];
        }
    }
}
