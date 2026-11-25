// ============================================================================
// PrepareSprites.cs - 构建期纯 C# 图像处理、对齐与双向光流补帧管线
// （仅在 Build.ps1 打包阶段运行一次，不进入最终桌宠 EXE，运行期零开销）
// 核心原理：
//   1. 白底泛洪抠图（KeyOutWhiteBackground）：检测四角近白像素后 BFS 泛洪剥离背景，
//      再取最大连通域清除孤立噪点，并对半透明杂边做色度自适应净化；
//   2. 色调对齐（MatchBrightness）：先对齐高光白点（L >= 180）保住深色线稿对比度，
//      再用 Shoulder 中间调曲线匹配 idle.png 整体亮度，消除跨图集闪白或偏灰；
//   3. 1:1 基准锁脚（WriteClip）：以首帧脚底与中心为基准锁定全部动作帧尺度；
//   4. 纯 C# 双向光流补帧（InterpolateFrame）：1/4 下采样块匹配估计前后向运动场 ->
//      两次 3×3 高斯平滑 -> 在预乘 Alpha 空间双线性双向扭曲融合生成平滑中间帧；
//   5. SaveIcon：直接生成含 64×64 PNG 帧的标准 Windows .ico 托盘图标。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

internal static class PrepareSprites
{
    private static readonly string[] Names = { "idle", "blink", "wave", "sleep" };
    private const int FrameWidth = 480, FrameHeight = 600;

    private struct ToneReference
    {
        internal double Mean;
        internal double Bright;
    }

    private static void Main(string[] args)
    {
        Directory.CreateDirectory(args[1]);
        var reference = new ToneReference();
        var idleBounds = new Rectangle(28, 17, 423, 575);
        using (var sheet = new Bitmap(args[0]))
        {
            for (int index = 0; index < Names.Length; index++)
            {
                int x = index % 2 * sheet.Width / 2;
                int y = index / 2 * sheet.Height / 2;
                using (var tile = sheet.Clone(new Rectangle(x, y, sheet.Width / 2, sheet.Height / 2), PixelFormat.Format32bppArgb))
                using (var frame = MakeFrame(tile))
                {
                    frame.Save(Path.Combine(args[1], Names[index] + ".png"), ImageFormat.Png);
                    if (index == 0)
                    {
                        var idlePixels = ReadPixels(frame);
                        reference = MeasureTone(idlePixels);
                        idleBounds = MeasureOpaqueBounds(idlePixels, FrameWidth, FrameHeight);
                        SaveIcon(frame, Path.Combine(args[1], "pet.ico"));
                    }
                }
            }
        }
        string directory = Path.GetDirectoryName(args[0]);
        WriteClip(Path.Combine(directory, "WaveSheet.png"), args[1], "wave", 3, 2, 0, 6, reference, idleBounds);
        string stretch8 = Path.Combine(directory, "StretchSheet_8f.jpg");
        if (File.Exists(stretch8)) WriteClip(stretch8, args[1], "stretch", 4, 2, 0, 8, reference, idleBounds);
        else WriteClip(Path.Combine(directory, "IdleActionsSheet.png"), args[1], "stretch", 4, 2, 0, 4, reference, idleBounds);
        WriteClip(Path.Combine(directory, "IdleActionsSheet.png"), args[1], "curious", 4, 2, 4, 4, reference, idleBounds);
        string celebrate9 = Path.Combine(directory, "CelebrateSheet_9f.jpg");
        if (File.Exists(celebrate9)) WriteClip(celebrate9, args[1], "celebrate", 3, 3, 0, 9, reference, idleBounds);
        else WriteClip(Path.Combine(directory, "CelebrateSheet.png"), args[1], "celebrate", 3, 2, 0, 6, reference, idleBounds);
        WriteClip(Path.Combine(directory, "DockSheet.png"), args[1], "dock-left", 4, 2, 0, 4, reference, idleBounds);
        WriteClip(Path.Combine(directory, "DockSheet.png"), args[1], "dock-right", 4, 2, 4, 4, reference, idleBounds);
        string peek243 = Path.Combine(directory, "PeekTabsSheet_243.jpg");
        WritePeekTabs(File.Exists(peek243) ? peek243 : Path.Combine(directory, "PeekTabsSheet.png"), args[1], reference);
    }

    private static Bitmap MakeFrame(Bitmap tile)
    {
        const int frameWidth = 480, frameHeight = 600;
        Rectangle bounds;
        using (var art = Isolate(tile, out bounds))
        {
            var frame = new Bitmap(frameWidth, frameHeight, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(frame))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                int x = (frameWidth - bounds.Width) / 2 - bounds.Left;
                int y = frameHeight - 9 - bounds.Bottom;
                graphics.DrawImageUnscaled(art, x, y);
            }
            return frame;
        }
    }

    private static void WriteClip(string atlas, string output, string name, int columns, int rows, int first, int count, ToneReference reference, Rectangle idleBounds)
    {
        var frames = new List<Bitmap>();
        var bounds = new List<Rectangle>();
        try
        {
            using (var sheet = new Bitmap(atlas))
            {
                for (int index = first; index < first + count; index++)
                {
                    int column = index % columns, row = index / columns;
                    var region = Rectangle.FromLTRB(column * sheet.Width / columns, row * sheet.Height / rows,
                        (column + 1) * sheet.Width / columns, (row + 1) * sheet.Height / rows);
                    using (var tile = sheet.Clone(region, PixelFormat.Format32bppArgb))
                    {
                        Rectangle box;
                        frames.Add(Isolate(tile, out box, true));
                        bounds.Add(box);
                    }
                }
            }

            // Anchor Frame 0 strictly to idle.png's height, foot baseline, and horizontal center to eliminate layer-swap jumps.
            var b0 = bounds[0];
            double idleCenterX = idleBounds.Left + idleBounds.Width / 2.0;
            double baseScale = (double)idleBounds.Height / Math.Max(1, b0.Height);
            int topAll = bounds.Min(box => box.Top);
            int bottomAll = bounds.Max(box => box.Bottom);
            double maxFitHeightScale = (FrameHeight - 10.0) / Math.Max(1, bottomAll - topAll);
            double scaleY = Math.Min(baseScale, maxFitHeightScale);
            // Keep baseline locked to idleBounds.Bottom unless a higher jump cel needs a few pixels of headroom.
            double topOffset = idleBounds.Bottom - b0.Bottom * scaleY;
            if (topAll * scaleY + topOffset < 4) topOffset = 4 - topAll * scaleY;
            if (bottomAll * scaleY + topOffset > FrameHeight - 4) topOffset = FrameHeight - 4 - bottomAll * scaleY;

            double center0 = b0.Left + b0.Width / 2.0;
            var keyPixels = new List<byte[]>();
            for (int i = 0; i < frames.Count; i++)
            {
                var box = bounds[i];
                double span = Math.Max(center0 - box.Left, box.Right - center0);
                double maxHalfWidth = Math.Min(idleCenterX - 5, FrameWidth - idleCenterX - 5);
                double scaleX = Math.Min(scaleY, maxHalfWidth / Math.Max(1.0, span));
                var source = new Rectangle(0, 0, frames[i].Width, frames[i].Height);
                var target = new RectangleF((float)(idleCenterX - center0 * scaleX), (float)topOffset,
                    (float)(source.Width * scaleX), (float)(source.Height * scaleY));
                keyPixels.Add(RenderScaledPixels(frames[i], source, target, reference));
            }

            // Emit original keyframes interleaved with bidirectional optical-flow intermediate frames (2*N - 1 total frames).
            int outIndex = 0;
            for (int i = 0; i < keyPixels.Count; i++)
            {
                SavePixelBuffer(keyPixels[i], FrameWidth, FrameHeight, Path.Combine(output, "anim-" + name + "-" + outIndex + ".png"));
                outIndex++;
                if (i + 1 < keyPixels.Count)
                {
                    var mid = InterpolateFrame(keyPixels[i], keyPixels[i + 1], FrameWidth, FrameHeight);
                    SavePixelBuffer(mid, FrameWidth, FrameHeight, Path.Combine(output, "anim-" + name + "-" + outIndex + ".png"));
                    outIndex++;
                }
            }
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static void WritePeekTabs(string atlas, string output, ToneReference reference)
    {
        var edges = new[] { "left", "right", "top", "bottom" };
        using (var sheet = new Bitmap(atlas))
        {
            for (int i = 0; i < edges.Length; i++)
            {
                int column = i % 2, row = i / 2;
                var part = Rectangle.FromLTRB(column * sheet.Width / 2, row * sheet.Height / 2,
                    (column + 1) * sheet.Width / 2, (row + 1) * sheet.Height / 2);
                using (var tile = sheet.Clone(part, PixelFormat.Format32bppArgb))
                {
                    Rectangle bounds;
                    using (var art = Isolate(tile, out bounds, true, i))
                    {
                        bool side = i < 2;
                        double scale = Math.Min((side ? 236.0 : 356.0) / bounds.Width, (side ? 344.0 : 216.0) / bounds.Height);
                        float tabWidth = (float)(bounds.Width * scale), tabHeight = (float)(bounds.Height * scale);
                        var target = new RectangleF((FrameWidth - tabWidth) / 2, (FrameHeight - tabHeight) / 2, tabWidth, tabHeight);
                        var pixels = RenderScaledPixels(art, bounds, target, reference);
                        SculptPeekTabEdges(pixels, FrameWidth, FrameHeight, i);
                        SavePixelBuffer(pixels, FrameWidth, FrameHeight, Path.Combine(output, "peek-" + edges[i] + ".png"));
                    }
                }
            }
        }
    }

    private static void SculptPeekTabEdges(byte[] pixels, int w, int h, int edgeIndex)
    {
        var box = MeasureOpaqueBounds(pixels, w, h);
        if (box.Width < 12 || box.Height < 12) return;
        // 1. Strip the artificial drawn dark border line right on the screen-contact lip so the monitor edge itself forms the boundary.
        const int lipTrim = 4;
        for (int y = box.Top; y < box.Bottom; y++)
        for (int x = box.Left; x < box.Right; x++)
        {
            bool onLip = (edgeIndex == 0 && x < box.Left + lipTrim) ||
                         (edgeIndex == 1 && x >= box.Right - lipTrim) ||
                         (edgeIndex == 2 && y < box.Top + lipTrim) ||
                         (edgeIndex == 3 && y >= box.Bottom - lipTrim);
            if (!onLip) continue;
            int idx = (y * w + x) * 4;
            int a = pixels[idx + 3];
            if (a == 0) continue;
            double luma = 0.2126 * pixels[idx + 2] + 0.7152 * pixels[idx + 1] + 0.0722 * pixels[idx];
            if (luma < 155) pixels[idx + 3] = 0;
        }

        // 2. For Left/Right side peeks, replace the flat horizontal bottom cut with an organic curved fade so it never looks like a square card.
        if (edgeIndex == 0 || edgeIndex == 1)
        {
            box = MeasureOpaqueBounds(pixels, w, h);
            double fadeHeight = Math.Max(28.0, box.Height * 0.24);
            for (int y = box.Bottom - (int)Math.Ceiling(fadeHeight) - 10; y < box.Bottom; y++)
            {
                if (y < 0 || y >= h) continue;
                for (int x = box.Left; x < box.Right; x++)
                {
                    int idx = (y * w + x) * 4;
                    int a = pixels[idx + 3];
                    if (a == 0) continue;
                    // Curve rises slightly away from the screen edge so the silhouette rounds off naturally like flowing hair.
                    double awayFromEdge = edgeIndex == 0
                        ? (double)(x - box.Left) / Math.Max(1, box.Width)
                        : (double)(box.Right - 1 - x) / Math.Max(1, box.Width);
                    double localBottom = box.Bottom - 2 - Math.Pow(Math.Max(0, awayFromEdge), 1.5) * (fadeHeight * 0.55);
                    double distToBottom = localBottom - y;
                    if (distToBottom <= 0)
                    {
                        pixels[idx + 3] = 0;
                    }
                    else if (distToBottom < fadeHeight * 0.62)
                    {
                        double t = distToBottom / (fadeHeight * 0.62);
                        double smooth = t * t * (3.0 - 2.0 * t);
                        pixels[idx + 3] = (byte)Math.Round(a * smooth);
                    }
                }
            }
        }
    }

    private static byte[] InterpolateFrame(byte[] a, byte[] b, int width, int height)
    {
        // Multi-scale block-matched bidirectional optical flow (quarter resolution -> smooth -> full-res premultiplied warp).
        const int ds = 4;
        int sw = width / ds, sh = height / ds;
        var lumA = BuildDownsampledFeature(a, width, height, ds);
        var lumB = BuildDownsampledFeature(b, width, height, ds);

        var flowABx = new float[sw * sh];
        var flowABy = new float[sw * sh];
        var flowBAx = new float[sw * sh];
        var flowBAy = new float[sw * sh];
        EstimateFlow(lumA, lumB, sw, sh, flowABx, flowABy);
        EstimateFlow(lumB, lumA, sw, sh, flowBAx, flowBAy);

        // Smooth flow fields twice for organic, tear-free motion.
        SmoothField(flowABx, sw, sh); SmoothField(flowABy, sw, sh);
        SmoothField(flowABx, sw, sh); SmoothField(flowABy, sw, sh);
        SmoothField(flowBAx, sw, sh); SmoothField(flowBAy, sw, sh);
        SmoothField(flowBAx, sw, sh); SmoothField(flowBAy, sw, sh);

        var result = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            float fy = (float)y / ds;
            int sy0 = Math.Min(sh - 1, (int)fy), sy1 = Math.Min(sh - 1, sy0 + 1);
            float wy = fy - sy0;
            for (int x = 0; x < width; x++)
            {
                float fx = (float)x / ds;
                int sx0 = Math.Min(sw - 1, (int)fx), sx1 = Math.Min(sw - 1, sx0 + 1);
                float wx = fx - sx0;

                float vxAB = SampleField(flowABx, sw, sx0, sy0, sx1, sy1, wx, wy) * ds;
                float vyAB = SampleField(flowABy, sw, sx0, sy0, sx1, sy1, wx, wy) * ds;
                float vxBA = SampleField(flowBAx, sw, sx0, sy0, sx1, sy1, wx, wy) * ds;
                float vyBA = SampleField(flowBAy, sw, sx0, sy0, sx1, sy1, wx, wy) * ds;

                // Blend forward-warped A and backward-warped B at t = 0.5 in premultiplied RGBA space.
                double paB, paG, paR, paA;
                double pbB, pbG, pbR, pbA;
                SamplePremul(a, width, height, x - 0.5f * vxAB, y - 0.5f * vyAB, out paB, out paG, out paR, out paA);
                SamplePremul(b, width, height, x - 0.5f * vxBA, y - 0.5f * vyBA, out pbB, out pbG, out pbR, out pbA);

                double outA = 0.5 * (paA + pbA);
                int dst = (y * width + x) * 4;
                if (outA < 2.0)
                {
                    result[dst] = result[dst + 1] = result[dst + 2] = result[dst + 3] = 0;
                }
                else
                {
                    double inv = 255.0 / outA;
                    result[dst] = ClampByte(0.5 * (paB + pbB) * inv);
                    result[dst + 1] = ClampByte(0.5 * (paG + pbG) * inv);
                    result[dst + 2] = ClampByte(0.5 * (paR + pbR) * inv);
                    result[dst + 3] = ClampByte(outA);
                }
            }
        }
        StabilizeAlpha(result, width, height);
        return result;
    }

    private static float[] BuildDownsampledFeature(byte[] pixels, int w, int h, int ds)
    {
        int sw = w / ds, sh = h / ds;
        var feat = new float[sw * sh * 2]; // [luma, alpha]
        for (int sy = 0; sy < sh; sy++)
        for (int sx = 0; sx < sw; sx++)
        {
            double lSum = 0, aSum = 0;
            for (int dy = 0; dy < ds; dy++)
            for (int dx = 0; dx < ds; dx++)
            {
                int i = ((sy * ds + dy) * w + (sx * ds + dx)) * 4;
                double a = pixels[i + 3] / 255.0;
                double l = (0.2126 * pixels[i + 2] + 0.7152 * pixels[i + 1] + 0.0722 * pixels[i]) * a;
                lSum += l;
                aSum += a * 255.0;
            }
            int o = (sy * sw + sx) * 2;
            double inv = 1.0 / (ds * ds);
            feat[o] = (float)(lSum * inv);
            feat[o + 1] = (float)(aSum * inv);
        }
        return feat;
    }

    private static void EstimateFlow(float[] src, float[] dst, int w, int h, float[] fxOut, float[] fyOut)
    {
        const int radius = 3;
        const int search = 6; // up to 24px full-res displacement per half-step
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int center = (y * w + x) * 2;
            if (src[center + 1] < 6f && dst[center + 1] < 6f) continue;

            // Zero-displacement cost with small regularization bias to keep static body rock-solid.
            double zeroCost = PatchCost(src, dst, w, h, x, y, 0, 0, radius);
            if (zeroCost < 12.0) continue;

            double bestCost = zeroCost;
            int bestDx = 0, bestDy = 0;
            for (int dy = -search; dy <= search; dy++)
            for (int dx = -search; dx <= search; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                double reg = (dx * dx + dy * dy) * 0.45;
                if (reg >= bestCost) continue;
                double cost = PatchCost(src, dst, w, h, x, y, dx, dy, radius) + reg;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestDx = dx;
                    bestDy = dy;
                }
            }
            fxOut[y * w + x] = bestDx;
            fyOut[y * w + x] = bestDy;
        }
    }

    private static double PatchCost(float[] src, float[] dst, int w, int h, int cx, int cy, int dx, int dy, int r)
    {
        double sum = 0;
        int count = 0;
        for (int oy = -r; oy <= r; oy += 2)
        {
            int sy = cy + oy, ty = sy + dy;
            if (sy < 0 || sy >= h || ty < 0 || ty >= h) continue;
            for (int ox = -r; ox <= r; ox += 2)
            {
                int sx = cx + ox, tx = sx + dx;
                if (sx < 0 || sx >= w || tx < 0 || tx >= w) continue;
                int si = (sy * w + sx) * 2;
                int ti = (ty * w + tx) * 2;
                double dl = Math.Abs(src[si] - dst[ti]);
                double da = Math.Abs(src[si + 1] - dst[ti + 1]);
                sum += dl + da * 1.4;
                count++;
            }
        }
        return count > 0 ? sum / count : 9999.0;
    }

    private static void SmoothField(float[] field, int w, int h)
    {
        var copy = (float[])field.Clone();
        for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            int p = y * w + x;
            field[p] = (copy[p - w - 1] + 2 * copy[p - w] + copy[p - w + 1] +
                        2 * copy[p - 1] + 4 * copy[p] + 2 * copy[p + 1] +
                        copy[p + w - 1] + 2 * copy[p + w] + copy[p + w + 1]) * (1f / 16f);
        }
    }

    private static float SampleField(float[] f, int w, int x0, int y0, int x1, int y1, float wx, float wy)
    {
        float top = f[y0 * w + x0] * (1 - wx) + f[y0 * w + x1] * wx;
        float bot = f[y1 * w + x0] * (1 - wx) + f[y1 * w + x1] * wx;
        return top * (1 - wy) + bot * wy;
    }

    private static void SamplePremul(byte[] pixels, int w, int h, float fx, float fy, out double pB, out double pG, out double pR, out double pA)
    {
        if (fx < 0 || fy < 0 || fx >= w - 1 || fy >= h - 1)
        {
            pB = pG = pR = pA = 0;
            return;
        }
        int x0 = (int)fx, y0 = (int)fy;
        double wx = fx - x0, wy = fy - y0;
        double w00 = (1 - wx) * (1 - wy), w10 = wx * (1 - wy), w01 = (1 - wx) * wy, w11 = wx * wy;
        int i00 = (y0 * w + x0) * 4, i10 = i00 + 4, i01 = i00 + w * 4, i11 = i01 + 4;

        double a00 = pixels[i00 + 3], a10 = pixels[i10 + 3], a01 = pixels[i01 + 3], a11 = pixels[i11 + 3];
        pA = a00 * w00 + a10 * w10 + a01 * w01 + a11 * w11;
        pB = (pixels[i00] * a00 * w00 + pixels[i10] * a10 * w10 + pixels[i01] * a01 * w01 + pixels[i11] * a11 * w11) / 255.0;
        pG = (pixels[i00 + 1] * a00 * w00 + pixels[i10 + 1] * a10 * w10 + pixels[i01 + 1] * a01 * w01 + pixels[i11 + 1] * a11 * w11) / 255.0;
        pR = (pixels[i00 + 2] * a00 * w00 + pixels[i10 + 2] * a10 * w10 + pixels[i01 + 2] * a01 * w01 + pixels[i11 + 2] * a11 * w11) / 255.0;
    }

    private static byte ClampByte(double v)
    {
        return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)Math.Round(v);
    }

    private static Rectangle MeasureOpaqueBounds(byte[] pixels, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (pixels[(y * width + x) * 4 + 3] < 128) continue;
            if (x < left) left = x;
            if (x > right) right = x;
            if (y < top) top = y;
            if (y > bottom) bottom = y;
        }
        return right < left ? new Rectangle(0, 0, width, height) : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static bool IsNearWhite(Color c, int minChannel, int maxChroma)
    {
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        return min >= minChannel && (max - min) <= maxChroma;
    }

    private static void KeyOutWhiteBackground(Color[] pixels, int w, int h)
    {
        int cornersWhite = 0;
        var samplePoints = new[] { 0, w - 1, (h - 1) * w, h * w - 1, w / 2, (h - 1) * w + w / 2 };
        foreach (int idx in samplePoints)
            if (pixels[idx].A >= 245 && IsNearWhite(pixels[idx], 235, 22)) cornersWhite++;
        if (cornersWhite < 3) return;

        var bg = new bool[pixels.Length];
        var queue = new Queue<int>();
        Action<int> trySeed = p =>
        {
            if (bg[p]) return;
            if (IsNearWhite(pixels[p], 228, 26))
            {
                bg[p] = true;
                queue.Enqueue(p);
            }
        };
        for (int x = 0; x < w; x++) { trySeed(x); trySeed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { trySeed(y * w); trySeed(y * w + (w - 1)); }

        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            int x = p % w, y = p / w;
            if (x > 0) TrySpread(p - 1, pixels, bg, queue);
            if (x < w - 1) TrySpread(p + 1, pixels, bg, queue);
            if (y > 0) TrySpread(p - w, pixels, bg, queue);
            if (y < h - 1) TrySpread(p + w, pixels, bg, queue);
        }

        for (int i = 0; i < pixels.Length; i++)
            if (bg[i]) pixels[i] = Color.FromArgb(0, 0, 0, 0);

        // Softly decontaminate 1px anti-aliased fringe touching removed white background.
        for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            int p = y * w + x;
            if (bg[p]) continue;
            if (!bg[p - 1] && !bg[p + 1] && !bg[p - w] && !bg[p + w]) continue;
            var c = pixels[p];
            int min = Math.Min(c.R, Math.Min(c.G, c.B));
            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            double luma = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
            if (luma >= 210 && (max - min) <= 28)
            {
                double keep = Math.Max(0.0, Math.Min(1.0, (245.0 - luma) / 48.0));
                int a = (int)Math.Round(255.0 * keep);
                if (a < 32) pixels[p] = Color.FromArgb(0, 0, 0, 0);
                else
                {
                    // Estimate underlying plum outline color before white-matte blending.
                    double inv = 1.0 / Math.Max(0.25, keep);
                    int r = Math.Max(0, Math.Min(255, (int)Math.Round(255.0 - (255.0 - c.R) * inv)));
                    int g = Math.Max(0, Math.Min(255, (int)Math.Round(255.0 - (255.0 - c.G) * inv)));
                    int b = Math.Max(0, Math.Min(255, (int)Math.Round(255.0 - (255.0 - c.B) * inv)));
                    pixels[p] = Color.FromArgb(a, r, g, b);
                }
            }
        }
    }

    private static void TrySpread(int p, Color[] pixels, bool[] bg, Queue<int> queue)
    {
        if (bg[p]) return;
        var c = pixels[p];
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int sum = c.R + c.G + c.B;
        if (min >= 220 && (max - min) <= 28 && sum >= 685)
        {
            bg[p] = true;
            queue.Enqueue(p);
        }
    }

    private static Bitmap Isolate(Bitmap tile, out Rectangle bounds, bool cleanEdge = false, int trimThinHorizontalEdge = -1)
    {
        int w = tile.Width;
        int h = tile.Height;
        var pixels = new Color[w * h];
        var seen = new bool[pixels.Length];
        var largest = new List<int>();
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            pixels[y * w + x] = tile.GetPixel(x, y);

        KeyOutWhiteBackground(pixels, w, h);

        // Trim overhanging thin decorative horizontal ledge line on top/bottom peek tabs if present.
        if (trimThinHorizontalEdge >= 2)
        {
            for (int x = 0; x < w; x++)
            {
                int opaqueCol = 0;
                for (int y = 0; y < h; y++)
                    if (pixels[y * w + x].A >= 140) opaqueCol++;
                if (opaqueCol > 0 && opaqueCol <= 12)
                {
                    for (int y = 0; y < h; y++) pixels[y * w + x] = Color.FromArgb(0, 0, 0, 0);
                }
            }
        }

        for (int i = 0; i < pixels.Length; i++)
        {
            if (seen[i] || pixels[i].A < 160) continue;
            var component = new List<int> { i };
            seen[i] = true;
            for (int j = 0; j < component.Count; j++)
            {
                int p = component[j];
                if (p % w > 0) Visit(p - 1, pixels, seen, component);
                if (p % w < w - 1) Visit(p + 1, pixels, seen, component);
                if (p >= w) Visit(p - w, pixels, seen, component);
                if (p < pixels.Length - w) Visit(p + w, pixels, seen, component);
            }
            if (component.Count > largest.Count) largest = component;
        }
        if (largest.Count < 10000) throw new InvalidDataException("Could not find a sprite in the tile.");

        int left = w, top = h, right = 0, bottom = 0;
        foreach (int p in largest)
        {
            left = Math.Min(left, p % w);
            right = Math.Max(right, p % w);
            top = Math.Min(top, p / w);
            bottom = Math.Max(bottom, p / w);
        }
        bounds = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        var frame = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var core = cleanEdge ? new bool[pixels.Length] : null;
        if (core != null) foreach (int p in largest) core[p] = true;
        foreach (int p in largest)
        {
            int x = p % w;
            int y = p / w;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int srcX = x + dx, srcY = y + dy;
                if (srcX < 0 || srcY < 0 || srcX >= w || srcY >= h) continue;
                int index = srcY * w + srcX;
                var color = pixels[index];
                if (cleanEdge && !core[index]) color = CleanNeighbor(color, Math.Abs(dx) + Math.Abs(dy) == 2);
                if (color.A >= 28) frame.SetPixel(srcX, srcY, color);
            }
        }
        return frame;
    }

    private static Color CleanNeighbor(Color color, bool diagonal)
    {
        int chroma = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
        bool grayOrDark = chroma < 24 || Math.Max(color.R, Math.Max(color.G, color.B)) < 115;
        int threshold = grayOrDark ? 67 : 42;
        if (diagonal) threshold += 13;
        if (color.A <= threshold) return Color.Transparent;
        int alpha = (int)Math.Round(color.A * Math.Min(1, (color.A - threshold) / 64.0));
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static byte[] RenderScaledPixels(Bitmap art, Rectangle source, RectangleF target, ToneReference reference)
    {
        using (var premultiplied = new Bitmap(art.Width, art.Height, PixelFormat.Format32bppPArgb))
        using (var scaled = new Bitmap(FrameWidth, FrameHeight, PixelFormat.Format32bppPArgb))
        using (var output = new Bitmap(FrameWidth, FrameHeight, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(premultiplied))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(art, 0, 0);
            }
            using (var graphics = Graphics.FromImage(scaled))
            using (var wrap = new ImageAttributes())
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                wrap.SetWrapMode(WrapMode.TileFlipXY);
                var corners = new[] { target.Location, new PointF(target.Right, target.Top), new PointF(target.Left, target.Bottom) };
                graphics.DrawImage(premultiplied, corners, source, GraphicsUnit.Pixel, wrap);
            }
            using (var graphics = Graphics.FromImage(output))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(scaled, 0, 0);
            }
            var pixels = ReadPixels(output);
            StabilizeAlpha(pixels, FrameWidth, FrameHeight);
            MatchBrightness(pixels, reference);
            return pixels;
        }
    }

    private static void SavePixelBuffer(byte[] pixels, int width, int height, string path)
    {
        using (var output = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        {
            TransferPixels(output, pixels, true);
            output.Save(path, ImageFormat.Png);
        }
    }

    private static void StabilizeAlpha(byte[] pixels, int width, int height)
    {
        var alpha = new byte[width * height];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = pixels[i * 4 + 3];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int p = y * width + x;
            int a = alpha[p];
            if (a == 0 || a == 255) continue;
            int neighbors = (x > 0 && alpha[p - 1] >= 165 ? 1 : 0) + (x + 1 < width && alpha[p + 1] >= 165 ? 1 : 0) +
                (y > 0 && alpha[p - width] >= 165 ? 1 : 0) + (y + 1 < height && alpha[p + width] >= 165 ? 1 : 0);
            int cutoff = neighbors == 0 ? 110 : 39;
            if (a <= cutoff) pixels[p * 4 + 3] = 0;
            else if (a >= 232 || (a >= 176 && neighbors == 4)) pixels[p * 4 + 3] = 255;
            else pixels[p * 4 + 3] = (byte)Math.Round(255.0 * (a - cutoff) / (232 - cutoff));
        }
    }

    private static ToneReference MeasureTone(byte[] pixels)
    {
        double sum = 0, brightSum = 0;
        int opaque = 0, brightCount = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 240) continue;
            double luma = .2126 * pixels[i + 2] + .7152 * pixels[i + 1] + .0722 * pixels[i];
            sum += luma;
            opaque++;
            if (luma >= 180)
            {
                brightSum += luma;
                brightCount++;
            }
        }
        if (opaque == 0) throw new InvalidDataException("No opaque sprite pixels were found.");
        return new ToneReference
        {
            Mean = sum / opaque,
            Bright = brightCount > 0 ? brightSum / brightCount : sum / opaque
        };
    }

    private static double Shoulder(double value) { return value * (255 - value) / 255; }

    private static void MatchBrightness(byte[] pixels, ToneReference reference)
    {
        // Step 1: Align highlight white-point (L >= 180) so white hair & dress match idle.png without washing out dark lineart.
        var current = MeasureTone(pixels);
        double highlightGain = current.Bright > 10 ? Math.Max(0.95, Math.Min(1.14, reference.Bright / current.Bright)) : 1.0;
        if (Math.Abs(highlightGain - 1.0) >= 0.002)
        {
            var highLut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                // Preserve crisp dark lineart (v < 90) and smoothly ramp white-point gain into highlights.
                double t = Math.Max(0.0, Math.Min(1.0, (i - 70.0) / 110.0));
                double blend = t * t * (3.0 - 2.0 * t);
                double effectiveGain = 1.0 + (highlightGain - 1.0) * blend;
                double mapped = i * effectiveGain;
                highLut[i] = (byte)Math.Max(0, Math.Min(255, Math.Round(mapped)));
            }
            ApplyLut(pixels, highLut);
        }

        // Step 2: Gently align overall mean while keeping highlights anchored close to reference.Bright.
        for (int pass = 0; pass < 2; pass++)
        {
            double sum = 0, weightSum = 0;
            int opaque = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 240) continue;
                double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                sum += .2126 * r + .7152 * g + .0722 * b;
                weightSum += .2126 * Shoulder(r) + .7152 * Shoulder(g) + .0722 * Shoulder(b);
                opaque++;
            }
            if (opaque == 0) break;
            double mean = sum / opaque;
            double avgWeight = weightSum / opaque;
            if (avgWeight < 1 || Math.Abs(reference.Mean - mean) < 0.25) break;
            double strength = Math.Max(-0.24, Math.Min(0.28, (reference.Mean - mean) / avgWeight));
            var tone = new byte[256];
            for (int i = 0; i < 256; i++)
                tone[i] = (byte)Math.Max(0, Math.Min(255, Math.Round(i + strength * Shoulder(i))));
            ApplyLut(pixels, tone);
        }
    }

    private static void ApplyLut(byte[] pixels, byte[] lut)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0) continue;
            pixels[i] = lut[pixels[i]];
            pixels[i + 1] = lut[pixels[i + 1]];
            pixels[i + 2] = lut[pixels[i + 2]];
        }
    }

    private static byte[] ReadPixels(Bitmap bitmap)
    {
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        TransferPixels(bitmap, pixels, false);
        return pixels;
    }

    private static void TransferPixels(Bitmap bitmap, byte[] pixels, bool write)
    {
        int rowBytes = bitmap.Width * 4;
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), write ? ImageLockMode.WriteOnly : ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                var row = IntPtr.Add(data.Scan0, y * data.Stride);
                if (write) Marshal.Copy(pixels, y * rowBytes, row, rowBytes);
                else Marshal.Copy(row, pixels, y * rowBytes, rowBytes);
            }
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static void Visit(int p, Color[] pixels, bool[] seen, List<int> component)
    {
        if (seen[p] || pixels[p].A < 160) return;
        seen[p] = true;
        component.Add(p);
    }

    private static void SaveIcon(Bitmap frame, string path)
    {
        using (var icon = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
        using (var graphics = Graphics.FromImage(icon))
        using (var png = new MemoryStream())
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(frame, new Rectangle(3, 2, 58, 58), new Rectangle(30, 0, 420, 420), GraphicsUnit.Pixel);
            icon.Save(png, ImageFormat.Png);
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)1);
                writer.Write(new byte[] { 64, 64, 0, 0 });
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((int)png.Length);
                writer.Write(22);
                writer.Write(png.ToArray());
            }
        }
    }
}
