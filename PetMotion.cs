// ============================================================================
// PetMotion.cs - 实时像素级眼球追踪、临界阻尼弹簧物理与 Win32 多屏贴边互操作
// 核心原理：
//   1. EyeMotion：在 WriteableBitmap 上直接重映射左右眼小矩形区域的像素：
//      - 椭圆径向二次衰减权重 Weight = (1 - dx^2 - dy^2)^2，使眼眶/眼白保持静止、
//        仅虹膜瞳孔随鼠标方向双线性偏移，消除贴图撕裂；
//      - 闭眼时基于 LidWeight 与 smoothstep 曲线自上而下扫掠混合睁眼图与闭眼底图。
//   2. GazeBehavior：静止 0.9s 后视线柔化，配合随机微瞥（glance）与偶发双眨眼。
//   3. MotionSpring：采用解析解指数衰减临界阻尼弹簧，帧率波动下也绝对零过冲。
//   4. PetMotion：封装 Win32 P/Invoke（MonitorFromPoint / SetWindowPos / SetWindowRgn）
//      实现无 DPI 模糊的精确跨屏定位、磁吸预览与屏幕边线硬件区域裁剪。
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed class EyeMotion
{
    private readonly byte[] original;
    private readonly byte[] closed;
    private readonly int stride;
    private readonly Eye[] eyes = { new Eye(198, 228, 32, 25), new Eye(276, 219, 32, 25) };
    private double lastX = Double.NaN;
    private double lastY = Double.NaN;
    private double lastLid = Double.NaN;
    private double lastFocus = Double.NaN;
    internal readonly WriteableBitmap Frame;
    internal readonly Rect OpaqueBounds;

    internal EyeMotion(BitmapSource source, BitmapSource blink)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        Frame = new WriteableBitmap(bitmap);
        stride = bitmap.PixelWidth * 4;
        original = new byte[stride * bitmap.PixelHeight];
        closed = new byte[original.Length];
        bitmap.CopyPixels(original, stride, 0);
        new FormatConvertedBitmap(blink, PixelFormats.Pbgra32, null, 0).CopyPixels(closed, stride, 0);
        OpaqueBounds = FindOpaqueBounds(bitmap.PixelWidth, bitmap.PixelHeight);
    }

    internal void SetDirection(double x, double y, double lid = 0, double focus = 0)
    {
        x = Math.Round(x * 24) / 24;
        y = Math.Round(y * 24) / 24;
        lid = Math.Round(lid * 12) / 12;
        focus = Math.Round(focus * 8) / 8;
        if (x == lastX && y == lastY && lid == lastLid && focus == lastFocus) return;
        lastX = x;
        lastY = y;
        lastLid = lid;
        lastFocus = focus;

        for (int i = 0; i < eyes.Length; i++)
        {
            var eye = eyes[i];
            var area = eye.Area;
            double gazeX = Math.Max(-1, Math.Min(1, x + (i == 0 ? 0.13 : -0.13) * focus));
            for (int py = 0; py < area.Height; py++)
            for (int px = 0; px < area.Width; px++)
            {
                int pixel = py * area.Width + px;
                double amount = eye.Weight[pixel] * (1 - lid * 0.8);
                double sx = area.X + px - gazeX * 6.6 * amount;
                double sy = area.Y + py - y * 4.8 * amount;
                int ix = (int)sx, iy = (int)sy;
                double fx = sx - ix, fy = sy - iy;
                int src = iy * stride + ix * 4;
                int lidSource = (area.Y + py) * stride + (area.X + px) * 4;
                int dst = pixel * 4;
                double row = Math.Max(0, Math.Min(1, (py - area.Height / 2.0 + 16) / 35));
                double wipe = Math.Max(0, Math.Min(1, (lid - row * 0.47) / 0.43));
                double mix = wipe * wipe * (3 - 2 * wipe) * eye.LidWeight[pixel];
                for (int color = 0; color < 4; color++)
                {
                    double a = original[src + color] * (1 - fx) + original[src + color + 4] * fx;
                    double b = original[src + stride + color] * (1 - fx) + original[src + stride + color + 4] * fx;
                    eye.Pixels[dst + color] = (byte)Math.Round((a * (1 - fy) + b * fy) * (1 - mix) + closed[lidSource + color] * mix);
                }
            }
            Frame.WritePixels(area, eye.Pixels, area.Width * 4, 0);
        }
    }

    private Rect FindOpaqueBounds(int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (original[y * stride + x * 4 + 3] < 128) continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }
        return right < left ? new Rect(0, 0, width, height) : new Rect(left, top, right - left + 1, bottom - top + 1);
    }

    private sealed class Eye
    {
        internal readonly Int32Rect Area;
        internal readonly double[] Weight;
        internal readonly double[] LidWeight;
        internal readonly byte[] Pixels;

        internal Eye(int x, int y, int rx, int ry)
        {
            Area = new Int32Rect(x - rx, y - ry, rx * 2 + 1, ry * 2 + 1);
            Weight = new double[Area.Width * Area.Height];
            LidWeight = new double[Weight.Length];
            Pixels = new byte[Weight.Length * 4];
            for (int py = 0; py < Area.Height; py++)
            for (int px = 0; px < Area.Width; px++)
            {
                // Keep the iris rim and sclera fixed. Only the interior follows the cursor.
                double dx = (px - rx) / 14.0, dy = (py - ry) / 14.5;
                double falloff = Math.Max(0, 1 - dx * dx - dy * dy);
                Weight[py * Area.Width + px] = falloff * falloff;
                double lx = Math.Abs(px - rx) / (double)rx;
                double ly = Math.Abs(py - ry) / (double)ry;
                double blend = Math.Max(0, Math.Min(1, (1 - Math.Max(lx, ly)) / 0.27));
                LidWeight[py * Area.Width + px] = blend * blend * (3 - 2 * blend);
            }
        }
    }
}

internal sealed class GazeBehavior
{
    private readonly Random random;
    private Point lastPointer;
    private bool hasPointer;
    private double movedAt;
    private double nextGlance = 2.7;
    private double glanceAt = -10;
    private Vector glance;
    private double nextBlink = 2.1;
    private double secondBlink = -10;
    private double blinkRequestedAt = -10;
    internal const double BlinkDuration = 0.235;

    internal GazeBehavior(Random random) { this.random = random; }

    internal void Focused(double time)
    {
        if (time - blinkRequestedAt < 5 || nextBlink - time < 0.35) return;
        blinkRequestedAt = time;
        nextBlink = time + 0.31;
    }

    internal Vector Aim(Vector displacement, Point pointer, double height, double time)
    {
        if (!hasPointer || (pointer - lastPointer).LengthSquared > 12)
        {
            hasPointer = true;
            lastPointer = pointer;
            movedAt = time;
            glanceAt = -10;
        }
        double still = time - movedAt;
        double radius = Math.Max(height * 0.56, displacement.Length);
        double soften = 1 - 0.25 * Math.Max(0, Math.Min(1, (still - 0.9) / 1.5));
        var target = displacement * (soften / radius);
        if (still > 0.65 && time >= nextGlance)
        {
            glanceAt = time;
            glance = new Vector((random.NextDouble() - 0.5) * 0.44, (random.NextDouble() - 0.5) * 0.31);
            nextGlance = time + 1.4 + random.NextDouble() * 2.7;
        }
        double glanceTime = (time - glanceAt) / 0.48;
        if (glanceTime >= 0 && glanceTime < 1) target += glance * Math.Sin(glanceTime * Math.PI);
        return target;
    }

    internal double Lids(double time)
    {
        if (time > nextBlink + BlinkDuration)
        {
            if (time - nextBlink < 0.4 && random.Next(4) == 0) secondBlink = time + 0.1;
            nextBlink = time + 2.6 + random.NextDouble() * 3.8;
        }
        return Math.Max(Closure(time - nextBlink), Closure(time - secondBlink));
    }

    internal static double Closure(double age)
    {
        if (age <= 0 || age >= BlinkDuration) return 0;
        if (age < 0.075) return Ease(age / 0.075);
        if (age < 0.115) return 1;
        return Ease((BlinkDuration - age) / (BlinkDuration - 0.115));
    }

    private static double Ease(double t) { return t * t * (3 - 2 * t); }
}

internal enum DockEdge { None, Left, Top, Right, Bottom }

internal sealed class MotionSpring
{
    internal double Value { get; private set; }
    private double velocity;

    internal void Advance(double target, double seconds)
    {
        const double response = 19;
        double error = Value - target;
        double slope = velocity + response * error;
        double decay = Math.Exp(-response * seconds);
        Value = target + (error + slope * seconds) * decay;
        velocity = (velocity - response * slope * seconds) * decay;
    }
}

internal static class PetMotion
{
    internal const double SnapDistance = 38;
    internal const double UndockDistance = 54;
    internal const double EdgePadding = 3;
    internal const double DockHoldSeconds = 1.25;
    internal const double DockHoverDwellSeconds = 0.20;
    internal const double BodySlideSeconds = 0.64;
    internal const double PeekSlideSeconds = 0.40;
    internal const double DockMotionSeconds = BodySlideSeconds + PeekSlideSeconds;
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { internal int Size; internal NativeRect Monitor, Work; internal int Flags; }
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect area);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr region);

    internal static Point CursorPosition()
    {
        NativePoint point;
        return GetCursorPos(out point) ? new Point(point.X, point.Y) : new Point();
    }

    internal static Rect? MonitorArea(IntPtr window, Point? pointer, bool workArea)
    {
        const uint nearest = 2;
        var monitor = pointer.HasValue
            ? MonitorFromPoint(new NativePoint { X = (int)Math.Round(pointer.Value.X), Y = (int)Math.Round(pointer.Value.Y) }, nearest)
            : MonitorFromWindow(window, nearest);
        if (monitor == IntPtr.Zero) return null;
        var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        NativeRect bounds = workArea ? info.Work : info.Monitor;
        return new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }

    internal static Point? NativeWindowOrigin(IntPtr handle)
    {
        NativeRect bounds;
        return GetWindowRect(handle, out bounds) ? (Point?)new Point(bounds.Left, bounds.Top) : null;
    }

    internal static bool MoveWindow(IntPtr handle, Point position)
    {
        const uint noSize = 0x0001, noZOrder = 0x0004, noActivate = 0x0010, noOwnerZOrder = 0x0200;
        int x = (int)Math.Round(position.X), y = (int)Math.Round(position.Y);
        var current = NativeWindowOrigin(handle);
        return (current.HasValue && current.Value.X == x && current.Value.Y == y) ||
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, noSize | noZOrder | noActivate | noOwnerZOrder);
    }

    internal static bool ClipWindow(IntPtr handle, Rect? bounds)
    {
        if (!bounds.HasValue) return SetWindowRgn(handle, IntPtr.Zero, true) != 0;
        var rect = bounds.Value;
        var region = CreateRectRgn((int)rect.Left, (int)rect.Top, (int)rect.Right, (int)rect.Bottom);
        if (region == IntPtr.Zero) return false;
        if (SetWindowRgn(handle, region, true) != 0) return true;
        DeleteObject(region);
        return false;
    }

    internal static DockEdge NearestEdge(Rect area, Rect silhouette, Point? pointer, DockEdge preferred)
    {
        var distances = new[] { silhouette.Left - area.Left, silhouette.Top - area.Top, area.Right - silhouette.Right, area.Bottom - silhouette.Bottom };
        var grip = pointer ?? new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
        var pointerDistances = new[] { grip.X - area.Left, grip.Y - area.Top, area.Right - grip.X, area.Bottom - grip.Y };
        var edges = new[] { DockEdge.Left, DockEdge.Top, DockEdge.Right, DockEdge.Bottom };
        var scores = new double[edges.Length];
        for (int i = 0; i < edges.Length; i++)
        {
            double score = Math.Max(0, distances[i]);
            if (pointer.HasValue) score = Math.Min(score, Math.Max(0, pointerDistances[i]) + 11);
            if (edges[i] == preferred && score <= UndockDistance) return preferred;
            scores[i] = score;
        }
        var edge = DockEdge.None;
        double nearest = SnapDistance;
        for (int i = 0; i < distances.Length; i++)
        {
            if (scores[i] > nearest) continue;
            nearest = scores[i];
            edge = edges[i];
        }
        return edge;
    }

    internal static Point Preview(Rect area, Rect window, Rect silhouette, DockEdge edge)
    {
        if (edge == DockEdge.None) return window.Location;
        var aligned = Place(area, window, silhouette, edge);
        bool horizontal = edge == DockEdge.Left || edge == DockEdge.Right;
        double from = horizontal ? window.Left : window.Top;
        double to = horizontal ? aligned.X : aligned.Y;
        double remaining = edge == DockEdge.Left || edge == DockEdge.Top ? from - to : to - from;
        if (remaining <= 9) return aligned;
        double strength = Math.Max(0, (SnapDistance - remaining) / SnapDistance);
        double attracted = from + (to - from) * strength * 0.68;
        return horizontal ? new Point(attracted, aligned.Y) : new Point(aligned.X, attracted);
    }

    internal static Point Place(Rect area, Rect window, Rect silhouette, DockEdge edge)
    {
        double left = Math.Max(area.Left, Math.Min(window.Left, area.Right - window.Width));
        double top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.Height));
        if (edge == DockEdge.Left) left = area.Left + EdgePadding - silhouette.Left;
        if (edge == DockEdge.Right) left = area.Right - EdgePadding - silhouette.Right;
        if (edge == DockEdge.Top) top = area.Top + EdgePadding - silhouette.Top;
        if (edge == DockEdge.Bottom) top = area.Bottom - EdgePadding - silhouette.Bottom;
        return new Point(left, top);
    }

    internal static Rect WorkAreaInWindow(Size size, Point origin, Rect workArea)
    {
        workArea.Offset(-origin.X, -origin.Y);
        return Rect.Intersect(new Rect(size), workArea);
    }

    internal static Rect PeekPlacement(Rect screen, Rect art, DockEdge edge)
    {
        bool side = edge == DockEdge.Left || edge == DockEdge.Right;
        double width = art.Width * (side ? 0.60 : 0.90) + 6;
        double height = art.Height * (side ? 0.66 : 0.42) + 6;
        double x = art.Left + (art.Width - width) / 2;
        double y = art.Top + art.Height * 0.13;
        switch (edge)
        {
            case DockEdge.Left: x = screen.Left; break;
            case DockEdge.Right: x = screen.Right - width; break;
            case DockEdge.Top: y = screen.Top; break;
            case DockEdge.Bottom: y = screen.Bottom - height; break;
        }
        return Rect.Intersect(screen, new Rect(x, y, width, height));
    }

    internal static bool IsPeekPhase(double seconds) { return seconds >= BodySlideSeconds; }

    internal static Vector EdgeSlide(Rect art, Rect screen, DockEdge edge, double seconds)
    {
        // Both drawings are beyond the same screen line at the changeover.
        bool peek = IsPeekPhase(seconds);
        double t = peek ? (seconds - BodySlideSeconds) / PeekSlideSeconds : seconds / BodySlideSeconds;
        t = Math.Max(0, Math.Min(1, t));
        double eased = t * t * t * (t * (6 * t - 15) + 10);
        double amount = peek ? 1 - eased : eased;
        const double clearance = 2;
        switch (edge)
        {
            case DockEdge.Left: return new Vector((screen.Left - art.Right - clearance) * amount, 0);
            case DockEdge.Right: return new Vector((screen.Right - art.Left + clearance) * amount, 0);
            case DockEdge.Top: return new Vector(0, (screen.Top - art.Bottom - clearance) * amount);
            case DockEdge.Bottom: return new Vector(0, (screen.Bottom - art.Top + clearance) * amount);
            default: return new Vector();
        }
    }
}
