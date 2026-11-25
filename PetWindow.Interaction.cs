// ============================================================================
// PetWindow.Interaction.cs - 桌面点击交互、拖拽换屏、悬停歪头与日常陪伴提醒
// 核心原理：
//   1. 按 Silhouette 垂直比例划分头部摸摸头（relY < 0.45）与身体挥手，2.4s 内三连击触发庆祝；
//   2. 拖拽时通过 DragWorkArea 施加 15 DIP 跨屏防抖滞回，并结合 PetMotion.Preview 磁吸预览；
//   3. 悬停 0.8s 后触发好奇歪头（18s 冷却 + HoverNeedsExit 离开复位防抖），避免反复原地鬼畜。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Input;
using Forms = System.Windows.Forms;

internal sealed partial class PetWindow
{
    private void MousePressed(object sender, MouseButtonEventArgs args)
    {
        if (args.ClickCount == 2)
        {
            if (dock != DockEdge.None) OpenDockTemporarily();
            OpenChatPanel();
            args.Handled = true;
            return;
        }
        mouseDown = CursorInDips();
        lastDragPointer = mouseDown.Value;
        dragOffset = mouseDown.Value - WindowOrigin();
        sprite.CaptureMouse();
        args.Handled = true;
    }

    private void MouseMoved(object sender, MouseEventArgs args)
    {
        if (!mouseDown.HasValue || !sprite.IsMouseCaptured) return;
        MovePetFromPointer(CursorInDips());
    }

    private void MouseReleased(object sender, MouseButtonEventArgs args)
    {
        if (!mouseDown.HasValue) return;
        var release = CursorInDips();
        if ((release - lastDragPointer).LengthSquared > 0.25) MovePetFromPointer(release);
        bool wasDragging = dragging;
        FinishDrag();
        sprite.ReleaseMouseCapture();
        if (!wasDragging) InteractAt(release);
        args.Handled = true;
    }

    private void MovePetFromPointer(Point pointer)
    {
        if (!mouseDown.HasValue) return;
        Vector distance = pointer - mouseDown.Value;
        if (!dragging)
        {
            if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragging = true;
            action = PetAction.None;
            greetingStart = -10;
            dragOffset.Y += VerticalOffset(DockEdge.None) - VerticalOffset(dock);
            dock = DockEdge.None;
            attractingEdge = DockEdge.None;
            dragArea = Rect.Empty;
            dockMotion = 0;
            dockHovered = false;
            curiousHoverAt = -1;
            ClearDockCrop();
            ApplyDockLayout();
        }
        lastDragPointer = pointer;
        rawDragPosition = pointer - dragOffset;
        var window = new Rect(rawDragPosition, new Size(Width, Height));
        var area = DragWorkArea(pointer);
        attractingEdge = FindEdge(area, rawDragPosition, pointer);
        var preview = PetMotion.Preview(area, window, Silhouette(DockEdge.None), attractingEdge);
        dragTilt = attractingEdge == DockEdge.None ? Math.Max(-11, Math.Min(11, (preview.X - Left) * 0.55)) : 0;
        MoveWindow(preview);
    }

    private Rect DragWorkArea(Point pointer)
    {
        var candidate = CurrentWorkArea(pointer);
        if (candidate == dragArea) return dragArea;
        if (!dragArea.IsEmpty)
        {
            const double crossing = 15;
            var seam = dragArea;
            seam.Inflate(crossing, crossing);
            if (seam.Contains(pointer)) return dragArea;
        }
        attractingEdge = DockEdge.None;
        dragArea = candidate;
        return dragArea;
    }

    private void FinishDrag()
    {
        mouseDown = null;
        if (!dragging) return;
        dragging = false;
        DockInArea(DragWorkArea(lastDragPointer), rawDragPosition, lastDragPointer);
        attractingEdge = DockEdge.None;
        dragArea = Rect.Empty;
        if (dock == DockEdge.None) landedAt = clock.Elapsed.TotalSeconds;
        else
        {
            dockedAt = clock.Elapsed.TotalSeconds;
            dockMotion = 0;
            dockPointer = lastDragPointer;
            dockPointerKnown = true;
            dockHovered = false;
            dockOpenUntil = 0;
            dockQuietUntil = dockHoverUntil = 0;
            landedAt = -10;
            bob.X = bob.Y = tilt.Angle = 0;
            stretch.ScaleX = stretch.ScaleY = 1;
        }
        RescheduleIdleAfterInteraction(10, 15);
        SaveSettings();
    }

    private void InteractAt(Point screenDip)
    {
        double now = clock.Elapsed.TotalSeconds;
        tapStreak = (now - lastTapAt <= 2.4) ? tapStreak + 1 : 1;
        lastTapAt = now;
        if (tapStreak >= 3)
        {
            tapStreak = 0;
            CelebrateWithPet();
            return;
        }
        var origin = WindowOrigin();
        var body = Silhouette(dock);
        double relY = body.Height > 1 ? (screenDip.Y - origin.Y - body.Top) / body.Height : 0.6;
        if (relY >= 0 && relY < 0.45) PetHead();
        else Greet();
    }

    private void PetHead()
    {
        double now = clock.Elapsed.TotalSeconds;
        sleeping = false;
        curiousHoverAt = -1;
        dockOpenUntil = Math.Max(dockOpenUntil, now + 4.0);
        if (action != PetAction.Thinking)
        {
            greetingStart = -10;
            StartAction((patIndex % 3 == 2) ? PetAction.Stretch : PetAction.Curious);
        }
        message.Text = HeadPatLines[patIndex++ % HeadPatLines.Length];
        bubbleUntil = now + 3.2;
        UpdatePose();
    }

    private void CelebrateWithPet()
    {
        double now = clock.Elapsed.TotalSeconds;
        ShowPet();
        sleeping = false;
        greetingStart = -10;
        curiousHoverAt = -1;
        dockOpenUntil = Math.Max(dockOpenUntil, now + 4.2);
        StartAction(PetAction.Celebrate);
        message.Text = CheerLines[cheerIndex++ % CheerLines.Length];
        bubbleUntil = now + 3.4;
        UpdatePose();
    }

    private void Greet()
    {
        sleeping = false;
        action = PetAction.None;
        curiousHoverAt = -1;
        greetingStart = clock.Elapsed.TotalSeconds + (settings.Animate ? dockMotion : 0);
        dockOpenUntil = greetingStart + 3;
        nextActionAt = greetingStart + 14;
        bubbleUntil = greetingStart + 2.75;
        if (settings.TimeGreetings && greetingIndex % 4 == 3)
            message.Text = TimePeriodGreeting();
        else
            message.Text = Greetings[greetingIndex % Greetings.Length];
        greetingIndex++;
        UpdatePose();
    }

    private static string TimePeriodGreeting()
    {
        int hour = DateTime.Now.Hour;
        if (hour >= 5 && hour < 11) return "早安！今天也要元气满满哦～";
        if (hour >= 11 && hour < 14) return "中午啦，记得按时吃午饭呀。";
        if (hour >= 14 && hour < 18) return "下午好，累了就休息一会吧。";
        if (hour >= 18 && hour < 23) return "晚上辛苦啦，有我陪着你呢。";
        return "夜深啦，早点休息不要熬太晚哦。";
    }

    private void TickCompanion()
    {
        if (lifetime.IsCancellationRequested || !settings.HealthReminder) return;
        double now = clock.Elapsed.TotalSeconds;
        double interval = Math.Max(1, settings.HealthReminderMinutes) * 60.0;
        if (now - lastHealthReminderAt < interval) return;
        if (sleeping || dragging || codexThinking || chatThinking || action != PetAction.None || GreetingPending(now)) return;

        lastHealthReminderAt = now;
        dockOpenUntil = Math.Max(dockOpenUntil, now + 5.0);
        if (IsVisible)
        {
            StartAction(PetAction.Stretch);
            message.Text = "坐了好一会儿啦，喝口水伸个懒腰吧～";
            bubbleUntil = now + 4.8;
            UpdatePose();
        }
        else if (tray != null)
        {
            tray.ShowBalloonTip(4000, Title, "坐了好一会儿啦，起来喝口水伸个懒腰吧～", Forms.ToolTipIcon.Info);
        }
    }

    private void ToggleSleep()
    {
        sleeping = !sleeping;
        action = PetAction.None;
        curiousHoverAt = -1;
        greetingStart = -10;
        message.Text = sleeping ? "晚安……" : "我醒啦！";
        bubbleUntil = clock.Elapsed.TotalSeconds + 2;
        if (!sleeping) RescheduleIdleAfterInteraction(10, 15);
        UpdatePose();
    }

    private void StartAction(PetAction next)
    {
        action = next;
        actionAt = clock.Elapsed.TotalSeconds + (settings.Animate ? dockMotion : 0);
        nextActionAt = actionAt + 14 + actionRandom.NextDouble() * 10;
    }

    private bool GreetingPending(double time) { return time < greetingStart + animations.Greeting.Duration; }

    private bool DockSettled(double time)
    {
        var clip = animations.AtEdge(dock);
        double duration = clip == null ? PetMotion.DockHoldSeconds : Math.Max(PetMotion.DockHoldSeconds, clip.Duration);
        return time - dockedAt >= duration;
    }

    private void RescheduleIdleAfterInteraction(double minimum, double maximum)
    {
        double now = clock.Elapsed.TotalSeconds;
        if (settings.IdleActions && action == PetAction.None && !GreetingPending(now))
            nextActionAt = now + minimum + actionRandom.NextDouble() * (maximum - minimum);
    }

    private void BeginHover(double time, Point pointer)
    {
        if (hoverNeedsExit || !HoverBounds().Contains(pointer)) return;
        hoverNeedsExit = true;
        hoverOutsideAt = -1;
        if (sleeping || dragging || mouseDown.HasValue || dockMenuOpen || codexThinking || chatThinking) return;
        hoveredAt = time;
        gazeBehavior.Focused(time);
        if (settings.Animate && settings.IdleActions && time - lastCuriousAt >= CuriousCooldown)
            curiousHoverAt = time;
    }

    private Rect HoverBounds()
    {
        var bounds = Silhouette(dock);
        if (dockMotion > 0 && !dockViewport.IsEmpty) bounds.Union(dockViewport);
        bounds.Inflate(CuriousHoverMargin, CuriousHoverMargin);
        return bounds;
    }

    private void UpdateHoverDeparture(double time, Point pointer)
    {
        if (!hoverNeedsExit) return;
        if (HoverBounds().Contains(pointer))
        {
            hoverOutsideAt = -1;
            return;
        }
        curiousHoverAt = -1;
        if (hoverOutsideAt < 0) hoverOutsideAt = time;
        if (time - hoverOutsideAt < CuriousHoverExitDwell) return;
        hoverNeedsExit = false;
        hoverOutsideAt = -1;
        nextActionAt = Math.Max(nextActionAt, time + 2);
    }

    private void TryIdleAction(double time)
    {
        if (!settings.Animate || sleeping || dragging || mouseDown.HasValue || dockMenuOpen ||
            action != PetAction.None || GreetingPending(time) ||
            (dock != DockEdge.None && !DockSettled(time))) return;

        if (codexThinking || chatThinking)
        {
            if (!thinkingPosePending) return;
            if (dockMotion > 0)
            {
                dockOpenUntil = Math.Max(dockOpenUntil, time + PetMotion.DockMotionSeconds + 1);
                return;
            }
            thinkingPosePending = false;
            StartAction(PetAction.Thinking);
            return;
        }

        if (!settings.IdleActions || dockMotion > 0) return;
        if (curiousHoverAt >= 0)
        {
            if (time - curiousHoverAt < CuriousHoverDwell) return;
            curiousHoverAt = -1;
            lastCuriousAt = time;
            StartAction(PetAction.Curious);
        }
        else if (!hoverNeedsExit && time >= nextActionAt)
        {
            StartAction(PetAction.Stretch);
        }
    }

    private void ToggleIdleActions()
    {
        settings.IdleActions = !settings.IdleActions;
        curiousHoverAt = -1;
        if (settings.IdleActions)
        {
            RescheduleIdleAfterInteraction(2, 4);
            dockOpenUntil = Math.Max(dockOpenUntil, clock.Elapsed.TotalSeconds + 4.5);
        }
        SaveSettings();
    }

    private void PreviewAction()
    {
        ShowPet();
        sleeping = false;
        greetingStart = -10;
        dockOpenUntil = clock.Elapsed.TotalSeconds + PetMotion.DockMotionSeconds + 3.6;
        StartAction((PetAction)(1 + actionPreview++ % 3));
        UpdatePose();
    }
}
