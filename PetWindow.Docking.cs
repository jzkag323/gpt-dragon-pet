// ============================================================================
// PetWindow.Docking.cs - 屏幕四边吸附、双阶段藏边滑出与探头裁剪状态机
// 核心原理：
//   1. 贴边后先展示抓边预备动作，闲置后启动两阶段 dockMotion（共 1.04s）：
//      - 阶段 1（0~0.64s）：保持完整立绘平滑滑出屏幕工作区边界；
//      - 阶段 2（0.64~1.04s）：无缝切为专用 PeekTab 探头图从屏幕边线外探出半个脑袋；
//   2. 同时更新 WPF layout.Clip 与 Win32 SetWindowRgn，确保越界部分完全不挡鼠标与像素。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

internal sealed partial class PetWindow
{
    private DockEdge FindEdge(Rect area, Point origin, Point? pointer)
    {
        if (!settings.SnapEdges) return DockEdge.None;
        var art = Silhouette(DockEdge.None);
        double topShift = VerticalOffset(DockEdge.None) - VerticalOffset(DockEdge.Top);
        art.Y -= topShift;
        art.Height += topShift;
        art.Offset(origin.X, origin.Y);
        return PetMotion.NearestEdge(area, art, pointer, attractingEdge);
    }

    private void DockInArea(Rect area, Point requested, Point? pointer)
    {
        dock = FindEdge(area, requested, pointer);
        ApplyDockLayout();
        PlaceInArea(area, requested);
    }

    private void UpdateDockMotion(double time, bool busy)
    {
        if (dock == DockEdge.None || dragging)
        {
            ClearDockCrop();
            dockMotion = 0;
            lastDockFrameAt = -1;
            dockHoverEnteredAt = -1;
            return;
        }

        double delta = lastDockFrameAt < 0 ? 0.016 : Math.Max(0, Math.Min(0.085, time - lastDockFrameAt));
        lastDockFrameAt = time;
        var cursor = CursorInDips();
        var viewport = dockMotion == 0 ? new Rect(0, 0, Width, Height) : dockViewport;
        bool near = false;
        if (!viewport.IsEmpty)
        {
            viewport.Offset(Left, Top);
            near = viewport.Contains(cursor);
        }
        if (!near)
        {
            dockHoverEnteredAt = -1;
            if (dockMotion == 0 || time >= dockHoverUntil) dockHovered = false;
        }
        else
        {
            if (dockMotion > 0)
            {
                if (dockHoverEnteredAt < 0) dockHoverEnteredAt = time;
                if (DockSettled(time) && time - dockHoverEnteredAt >= PetMotion.DockHoverDwellSeconds)
                    dockHovered = true;
            }
            else if (dockPointerKnown && (cursor - dockPointer).LengthSquared > 16 && DockSettled(time))
            {
                dockHovered = true;
            }
        }
        if (dockPointerKnown && (cursor - dockPointer).LengthSquared > 9) dockPointer = cursor;
        if (!dockPointerKnown) { dockPointer = cursor; dockPointerKnown = true; }
        if (near && dockHovered) dockHoverUntil = time + 1.25;
        if (busy) dockQuietUntil = time + 0.38;

        bool compact = settings.CompactDock && !busy && !dockHovered && !dockMenuOpen && !mouseDown.HasValue &&
            (curiousHoverAt < 0 || time - curiousHoverAt < CuriousHoverDwell) && DockSettled(time) && time >= dockHoverUntil &&
            time >= dockQuietUntil && time >= dockOpenUntil && time >= bubbleUntil;
        double target = compact ? PetMotion.DockMotionSeconds : 0;
        dockMotion = settings.Animate
            ? compact ? Math.Min(target, dockMotion + delta) : Math.Max(0, dockMotion - delta)
            : target;
        if (dockMotion > 0 && settings.IdleActions && action == PetAction.None) nextActionAt += delta;
    }

    private BitmapSource PresentDock(double time, bool idlePose, out Vector offset)
    {
        offset = new Vector();
        if (dock == DockEdge.None || dragging) return null;
        var size = new Size(Width, Height);
        Rect screen = PetMotion.WorkAreaInWindow(size, WindowOrigin(), CurrentWorkArea());
        if (screen.IsEmpty) screen = new Rect(0, 0, 0, 0);
        var silhouette = Silhouette(dock);
        var frame = new Rect((Width - sprite.Width) / 2, VerticalOffset(dock), sprite.Width, sprite.Height);
        var edgeClip = animations.AtEdge(dock);
        double age = time - dockedAt;
        if (dockMotion <= 0)
        {
            ClearDockCrop();
            if (idlePose && !sleeping && settings.Animate && edgeClip != null && age < edgeClip.Duration)
                return BraceFrame(edgeClip, age, frame, silhouette, out offset);
            return null;
        }

        BitmapSource art;
        Rect body;
        bool peek = PetMotion.IsPeekPhase(dockMotion);
        if (peek)
        {
            var tab = animations.PeekAt(dock);
            Rect placement = PetMotion.PeekPlacement(screen, silhouette, dock);
            if (placement.IsEmpty) placement = screen;
            offset = tab.Align(frame, placement, dock);
            art = tab.Image;
            body = EdgePeekTab.Project(frame, tab.Bounds);
        }
        else if (edgeClip != null && !sleeping)
        {
            art = BraceFrame(edgeClip, edgeClip.Duration, frame, silhouette, out offset);
            body = EdgePeekTab.Project(frame, edgeClip.Outline(edgeClip.Sequence.FrameAt(edgeClip.Sequence.DurationMillis)));
        }
        else
        {
            art = sleeping ? sleep : eyes.Frame;
            body = silhouette;
        }
        body.Offset(offset);
        var movement = PetMotion.EdgeSlide(body, screen, dock, dockMotion);
        offset += movement;
        body.Offset(movement);
        body.Inflate(1.5, 1.5);
        dockViewport = Rect.Intersect(screen, body);
        dockCrop.Rect = screen;
        if (layout.Clip != dockCrop) layout.Clip = dockCrop;
        ApplyNativeDockCrop(peek ? dockViewport : screen);
        return art;
    }

    private void ApplyNativeDockCrop(Rect viewport)
    {
        var source = PresentationSource.FromVisual(this) as HwndSource;
        if (source == null || source.CompositionTarget == null) return;
        var transform = source.CompositionTarget.TransformToDevice;
        var deviceRect = new Rect(0, 0, 0, 0);
        if (!viewport.IsEmpty)
        {
            var first = transform.Transform(viewport.TopLeft);
            var last = transform.Transform(viewport.BottomRight);
            double x = Math.Ceiling(first.X), y = Math.Ceiling(first.Y);
            deviceRect = new Rect(x, y, Math.Max(0, Math.Floor(last.X) - x), Math.Max(0, Math.Floor(last.Y) - y));
        }
        if (deviceRect != nativeViewport && PetMotion.ClipWindow(source.Handle, deviceRect)) nativeViewport = deviceRect;
    }

    private void ClearDockCrop()
    {
        if (layout.Clip != null) layout.Clip = null;
        dockViewport = Rect.Empty;
        if (nativeViewport.IsEmpty) return;
        var source = PresentationSource.FromVisual(this) as HwndSource;
        if (source == null || PetMotion.ClipWindow(source.Handle, null)) nativeViewport = Rect.Empty;
    }

    private BitmapSource BraceFrame(AnimationClip clip, double seconds, Rect frame, Rect silhouette, out Vector offset)
    {
        int cel = clip.Sequence.FrameAt(seconds * 1000);
        Rect art = EdgePeekTab.Project(frame, clip.Outline(cel));
        offset = new Vector(dock == DockEdge.Left ? silhouette.Left - art.Left : silhouette.Right - art.Right, 0);
        return clip.Cel(cel);
    }

    private void OpenDockTemporarily()
    {
        dockOpenUntil = clock.Elapsed.TotalSeconds + 5;
        ShowPet();
    }

    private void CloseDockMenu()
    {
        dockMenuOpen = false;
        dockQuietUntil = Math.Max(dockQuietUntil, clock.Elapsed.TotalSeconds + 0.38);
    }
}
