// ============================================================================
// AnimationClips.cs - 动画帧时间表、内嵌精灵加载器与探头包围盒对齐
// 核心原理：
//   1. ClipSequence 定义帧播放顺序与每帧停留毫秒数（支持来回呼吸、单峰停顿等节奏）；
//   2. AnimationClip 从程序集嵌入式资源读取 pre-baked 的 PNG 序列；
//   3. EdgePeekTab 扫描透明 PNG 的实际不透明像素范围，将探头边缘严格贴合屏幕边界。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal enum PetAction { None, Stretch, Curious, Celebrate, Thinking }

internal sealed class ClipSequence
{
    internal readonly string Name;
    internal readonly int FrameCount;
    internal readonly int[] FrameOrder;
    internal readonly int[] FrameMillis;
    internal readonly int DurationMillis;

    internal ClipSequence(string name, int count, int[] order, int[] millis)
    {
        if (order.Length != millis.Length) throw new ArgumentException("Animation frame order and timing must match.");
        Name = name;
        FrameCount = count;
        FrameOrder = order;
        FrameMillis = millis;
        for (int i = 0; i < millis.Length; i++)
        {
            if (order[i] < 0 || order[i] >= count || millis[i] <= 0) throw new ArgumentException("Invalid animation frame schedule.");
            DurationMillis += millis[i];
        }
    }

    internal string FileName(int frame) { return "anim-" + Name + "-" + frame; }

    internal int FrameAt(double millis)
    {
        if (millis <= 0) return FrameOrder[0];
        for (int i = 0; i < FrameMillis.Length; i++)
        {
            millis -= FrameMillis[i];
            if (millis < 0) return FrameOrder[i];
        }
        return FrameOrder[FrameOrder.Length - 1];
    }
}

internal sealed class AnimationClip
{
    private readonly BitmapSource[] frames;
    private readonly Rect[] outlines;
    internal readonly ClipSequence Sequence;
    internal double Duration { get { return Sequence.DurationMillis / 1000.0; } }

    internal AnimationClip(ClipSequence sequence, Func<string, BitmapSource> read, bool alignEdge = false)
    {
        Sequence = sequence;
        frames = new BitmapSource[sequence.FrameCount];
        if (alignEdge) outlines = new Rect[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = read(sequence.FileName(i));
            if (alignEdge) outlines[i] = EdgePeekTab.NormalizedBounds(frames[i]);
        }
    }

    internal BitmapSource At(double seconds) { return frames[Sequence.FrameAt(seconds * 1000)]; }
    internal BitmapSource Cel(int index) { return frames[index]; }

    internal Rect Outline(int index) { return outlines[index]; }

}

internal sealed class EdgePeekTab
{
    internal readonly BitmapSource Image;
    internal readonly Rect Bounds;

    internal EdgePeekTab(BitmapSource image)
    {
        Image = image;
        Bounds = NormalizedBounds(image);
    }

    internal Vector Align(Rect frame, Rect visible, DockEdge edge)
    {
        var art = Project(frame, Bounds);
        double x = visible.Left + visible.Width / 2 - art.Left - art.Width / 2;
        double y = visible.Top + visible.Height / 2 - art.Top - art.Height / 2;
        const double overlap = 0.5;
        if (edge == DockEdge.Left) x = visible.Left - art.Left - overlap;
        if (edge == DockEdge.Right) x = visible.Right - art.Right + overlap;
        if (edge == DockEdge.Top) y = visible.Top - art.Top - overlap;
        if (edge == DockEdge.Bottom) y = visible.Bottom - art.Bottom + overlap;
        return new Vector(x, y);
    }

    internal static Rect Project(Rect frame, Rect normalized)
    {
        return new Rect(frame.Left + normalized.Left * frame.Width, frame.Top + normalized.Top * frame.Height,
            normalized.Width * frame.Width, normalized.Height * frame.Height);
    }

    internal static Rect NormalizedBounds(BitmapSource art)
    {
        var bitmap = new FormatConvertedBitmap(art, PixelFormats.Bgra32, null, 0);
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight, stride = width * 4;
        var pixels = new byte[height * stride];
        bitmap.CopyPixels(pixels, stride, 0);
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (pixels[y * stride + x * 4 + 3] < 128) continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }
        return right < left ? Rect.Empty : new Rect(left / (double)width, top / (double)height,
            (right + 1 - left) / (double)width, (bottom + 1 - top) / (double)height);
    }
}

internal sealed class PetAnimations
{
    internal static readonly ClipSequence Wave = new ClipSequence("wave", 11,
        new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 7, 6, 7, 8, 9, 10, 0 },
        new[] { 90, 45, 110, 45, 145, 45, 115, 45, 150, 45, 110, 45, 145, 50, 130, 85 });
    internal static readonly ClipSequence Stretch = new ClipSequence("stretch", 15,
        new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 9, 8, 9, 10, 11, 12, 13, 14 },
        new[] { 125, 50, 150, 50, 165, 50, 185, 55, 290, 55, 240, 55, 220, 55, 210, 55, 180, 50, 165 });
    // Cel 4 is the full tilt; cels 5 and 6 already bring the head back.
    internal static readonly ClipSequence Curious = new ClipSequence("curious", 7,
        new[] { 0, 1, 2, 3, 4, 5, 6, 1, 0 },
        new[] { 180, 120, 320, 160, 1750, 175, 540, 140, 230 });
    internal static readonly ClipSequence Celebrate = new ClipSequence("celebrate", 17,
        new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 },
        new[] { 90, 45, 115, 45, 135, 45, 120, 45, 190, 50, 165, 45, 145, 45, 135, 45, 115 });
    internal static readonly ClipSequence DockLeft = new ClipSequence("dock-left", 7,
        new[] { 0, 1, 2, 3, 4, 5, 6, 5, 4 },
        new[] { 100, 45, 155, 50, 200, 50, 245, 50, 205 });
    internal static readonly ClipSequence DockRight = new ClipSequence("dock-right", 7, DockLeft.FrameOrder, DockLeft.FrameMillis);
    internal static readonly ClipSequence[] All = { Wave, Stretch, Curious, Celebrate, DockLeft, DockRight };

    internal readonly AnimationClip Greeting;
    private readonly AnimationClip stretching, curious, celebration;
    private readonly AnimationClip leftDock, rightDock;
    private readonly EdgePeekTab[] peekTabs;

    internal PetAnimations(Func<string, BitmapSource> read)
    {
        Greeting = new AnimationClip(Wave, read);
        stretching = new AnimationClip(Stretch, read);
        curious = new AnimationClip(Curious, read);
        celebration = new AnimationClip(Celebrate, read);
        leftDock = new AnimationClip(DockLeft, read, true);
        rightDock = new AnimationClip(DockRight, read, true);
        peekTabs = new[] { null, new EdgePeekTab(read("peek-left")), new EdgePeekTab(read("peek-top")),
            new EdgePeekTab(read("peek-right")), new EdgePeekTab(read("peek-bottom")) };
    }

    internal AnimationClip For(PetAction action)
    {
        switch (action)
        {
            case PetAction.Stretch: return stretching;
            case PetAction.Curious: return curious;
            case PetAction.Thinking: return curious;
            case PetAction.Celebrate: return celebration;
            default: return null;
        }
    }

    internal AnimationClip AtEdge(DockEdge edge)
    {
        return edge == DockEdge.Left ? leftDock : edge == DockEdge.Right ? rightDock : null;
    }

    internal EdgePeekTab PeekAt(DockEdge edge) { return peekTabs[(int)edge]; }
}
