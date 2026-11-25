// ============================================================================
// PetWindow.Ui.cs - 右键菜单、系统托盘菜单、多窗口面板联动与 Codex 状态提示
// 核心原理：
//   1. 基于 Codex 可用性动态显示或隐藏开发菜单，让无 Codex 的大众用户界面简洁无干扰；
//   2. ChatPanel / SettingsPanel / CodexPanel 统一按需懒加载单例窗口并在关闭时回收；
//   3. PollCodex() 每 2 秒轮询本地任务流：运行中切 Thinking 思考姿势，完成时冒泡庆祝。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

internal sealed partial class PetWindow
{
    private void OpenChatPanel(bool focusBrainTab)
    {
        if (chatPanel == null)
        {
            chatPanel = new ChatPanel(chat, ShowChatReaction, SetChatThinking, codexAvailable);
            chatPanel.Closed += (sender, args) => { chatPanel = null; SetChatThinking(false); };
            chatPanel.Show();
        }
        chatPanel.Activate();
        if (focusBrainTab) chatPanel.OpenBrainSettings();
        else chatPanel.FocusInput();
    }

    internal void OpenChatPanel() { OpenChatPanel(false); }
    internal void OpenBrainPanel() { OpenChatPanel(true); }

    internal void OpenSettingsPanel()
    {
        if (settingsPanel == null)
        {
            settingsPanel = new SettingsPanel(this, chat);
            settingsPanel.Closed += (sender, args) => settingsPanel = null;
            settingsPanel.Show();
        }
        else settingsPanel.RefreshFromPet();
        settingsPanel.Activate();
    }

    internal void OpenCodexPanel(bool quota)
    {
        if (codexPanel == null)
        {
            codexPanel = new CodexPanel(codex);
            codexPanel.Closed += (sender, args) => codexPanel = null;
            codexPanel.Show();
        }
        codexPanel.Activate();
        codexPanel.ShowPage(quota);
    }

    private void SetChatThinking(bool thinking)
    {
        if (chatThinking == thinking) return;
        double now = clock.Elapsed.TotalSeconds;
        bool wasThinking = codexThinking || chatThinking;
        chatThinking = thinking;
        UpdateThinkingPose(wasThinking);
        if (thinking && !sleeping && !dragging)
        {
            dockOpenUntil = Math.Max(dockOpenUntil, now + 5.0);
            if (IsVisible && action == PetAction.None && !GreetingPending(now))
            {
                message.Text = "让我想想怎么回答你…";
                bubbleUntil = now + 3.0;
            }
        }
        if (IsVisible) UpdatePose();
    }

    private void UpdateThinkingPose(bool wasThinking)
    {
        bool isThinking = codexThinking || chatThinking;
        if (isThinking == wasThinking) return;
        if (isThinking)
        {
            curiousHoverAt = -1;
            hoveredAt = -10;
            double lastHoverFinished = lastCuriousAt + animations.For(PetAction.Curious).Duration;
            thinkingPosePending = action != PetAction.Curious &&
                clock.Elapsed.TotalSeconds >= lastHoverFinished + SharedCuriousPoseGap;
        }
        else
        {
            thinkingPosePending = false;
            if (action == PetAction.Thinking) action = PetAction.None;
        }
    }

    private void ShowChatReaction(ChatReply reply)
    {
        if (reply == null) return;
        double now = clock.Elapsed.TotalSeconds;
        dockOpenUntil = Math.Max(dockOpenUntil, now + 5.0);
        string mood = (reply.Mood ?? "").ToLowerInvariant();
        if (mood == "sleep")
        {
            sleeping = true;
            action = PetAction.None;
            greetingStart = -10;
        }
        else
        {
            sleeping = false;
            if (mood == "celebrate")
            {
                greetingStart = settings.Animate ? -10 : now;
                StartAction(PetAction.Celebrate);
            }
            else if (mood == "curious")
            {
                greetingStart = -10;
                StartAction(PetAction.Curious);
            }
            else if (mood == "wave")
            {
                action = PetAction.None;
                greetingStart = now;
            }
        }
        if (IsVisible)
        {
            message.Text = ChatBridge.TrimPreview(reply.Text, 22);
            bubbleUntil = now + 4.5;
            UpdatePose();
        }
    }

    internal void ToggleCodexNotices()
    {
        settings.CodexNotices = !settings.CodexNotices;
        noticeGeneration++;
        lastTasks = null;
        if (settings.CodexNotices) { codexTimer.Start(); PollCodex(); }
        else
        {
            codexTimer.Stop();
            bool wasThinking = codexThinking || chatThinking;
            codexThinking = false;
            UpdateThinkingPose(wasThinking);
            if (IsVisible) UpdatePose();
        }
        SaveSettings();
    }

    private async void PollCodex()
    {
        if (checkingTasks || !settings.CodexNotices || lifetime.IsCancellationRequested) return;
        checkingTasks = true;
        int generation = noticeGeneration;
        try
        {
            var tasks = await Task.Run(() => codex.ReadLocalTasks(lifetime.Token), lifetime.Token);
            if (lifetime.IsCancellationRequested || generation != noticeGeneration) return;
            var current = new Dictionary<string, CodexTask>();
            int finished = 0, interrupted = 0;
            bool anyRunning = false;
            foreach (var task in tasks)
            {
                current[task.Id] = task;
                if (task.Status == CodexTaskStatus.Running && (DateTime.UtcNow - task.LastWrite).TotalMinutes <= 15)
                    anyRunning = true;
                CodexTask old;
                if (lastTasks == null || (DateTime.UtcNow - task.EventTime).TotalSeconds > 90) continue;
                if (lastTasks.TryGetValue(task.Id, out old) && old.Status == task.Status && old.EventTime == task.EventTime) continue;
                if (task.Status == CodexTaskStatus.Completed) finished++;
                if (task.Status == CodexTaskStatus.Interrupted) interrupted++;
            }
            bool codexWorkChanged = anyRunning != codexThinking;
            bool wasThinking = codexThinking || chatThinking;
            codexThinking = anyRunning;
            UpdateThinkingPose(wasThinking);
            lastTasks = current;

            if (anyRunning && codexWorkChanged)
            {
                if (IsVisible)
                {
                    sleeping = false;
                    dockOpenUntil = Math.Max(dockOpenUntil, clock.Elapsed.TotalSeconds + 4.8);
                }
                else if (tray != null && finished == 0 && interrupted == 0)
                {
                    tray.ShowBalloonTip(4000, Title, CodexWorkingMessage, Forms.ToolTipIcon.Info);
                }
            }
            if (finished > 0) ReportCodex("Codex 完成 " + finished + " 项啦！", !anyRunning);
            else if (interrupted > 0) ReportCodex("Codex 中断了 " + interrupted + " 项哦", false);
            else if (IsVisible && codexWorkChanged) UpdatePose();
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { checkingTasks = false; }
    }

    private void ReportCodex(string text, bool celebrate)
    {
        double now = clock.Elapsed.TotalSeconds;
        if (!IsVisible)
        {
            if (tray != null) tray.ShowBalloonTip(4000, Title, text, Forms.ToolTipIcon.Info);
            return;
        }
        dockOpenUntil = Math.Max(dockOpenUntil, now + 4.8);
        if (celebrate)
        {
            sleeping = false;
            greetingStart = settings.Animate ? -10 : now;
            StartAction(PetAction.Celebrate);
        }
        message.Text = text;
        bubbleUntil = now + 4.5;
        UpdatePose();
    }

    private sealed class MenuEntry
    {
        internal readonly string Label;
        internal readonly Action Action;
        internal readonly bool? Checked;
        internal readonly bool Enabled;
        internal MenuEntry(string label, Action action, bool? check = null, bool enabled = true)
        { Label = label; Action = action; Checked = check; Enabled = enabled; }
    }

    private IEnumerable<MenuEntry> ContextMenuEntries()
    {
        yield return new MenuEntry("和龙娘聊天", OpenChatPanel);
        yield return new MenuEntry("摸摸头", PetHead, enabled: settings.Animate);
        yield return new MenuEntry("击掌庆祝", CelebrateWithPet, enabled: settings.Animate);
        if (dock != DockEdge.None) yield return new MenuEntry("暂时展开", OpenDockTemporarily);
        yield return new MenuEntry(sleeping ? "叫醒她" : "让她睡觉", ToggleSleep);
        yield return null;
        yield return new MenuEntry("设置中心…", OpenSettingsPanel);
        if (ShouldShowCodex())
        {
            yield return new MenuEntry("Codex 任务", () => OpenCodexPanel(false));
            yield return new MenuEntry("Codex 额度", () => OpenCodexPanel(true));
        }
        yield return null;
        yield return new MenuEntry("回到右下角", ResetPetPosition);
        yield return new MenuEntry(IsVisible ? "隐藏到托盘" : "显示桌宠", () => { if (IsVisible) Hide(); else ShowPet(); });
        yield return new MenuEntry("退出桌宠", Close);
    }

    private IEnumerable<MenuEntry> MenuEntries()
    {
        yield return new MenuEntry("和龙娘聊天", OpenChatPanel);
        yield return new MenuEntry("设置中心…", OpenSettingsPanel);
        yield return null;
        yield return new MenuEntry(sleeping ? "叫醒她" : "让她睡觉", ToggleSleep);
        yield return new MenuEntry("显示在最前", () => SetAlwaysOnTop(!settings.AlwaysOnTop), settings.AlwaysOnTop);
        yield return new MenuEntry("桌宠动效", () => SetAnimate(!settings.Animate), settings.Animate);
        yield return new MenuEntry("瞳孔跟随鼠标", () => SetFollowMouse(!settings.FollowMouse), settings.FollowMouse);
        yield return new MenuEntry("拖放时自动吸边", () => SetSnapEdges(!settings.SnapEdges), settings.SnapEdges);
        yield return new MenuEntry("贴边自动收起", () => SetCompactDock(!settings.CompactDock), settings.CompactDock);
        if (dock != DockEdge.None) yield return new MenuEntry("暂时展开", OpenDockTemporarily);
        yield return new MenuEntry("待机小动作", ToggleIdleActions, settings.IdleActions);
        yield return new MenuEntry("摸摸头", PetHead, enabled: settings.Animate);
        yield return new MenuEntry("击掌庆祝", CelebrateWithPet, enabled: settings.Animate);
        yield return null;
        yield return new MenuEntry("小号", () => SetSize(Small), settings.Size == Small);
        yield return new MenuEntry("中号", () => SetSize(Medium), settings.Size == Medium);
        yield return new MenuEntry("大号", () => SetSize(Large), settings.Size == Large);
        yield return new MenuEntry("回到右下角", ResetPetPosition);
        if (ShouldShowCodex())
        {
            yield return null;
            yield return new MenuEntry("Codex 任务", () => OpenCodexPanel(false));
            yield return new MenuEntry("Codex 额度", () => OpenCodexPanel(true));
            yield return new MenuEntry("报告 Codex 任务变化", ToggleCodexNotices, settings.CodexNotices);
        }
        yield return null;
        yield return new MenuEntry(IsVisible ? "隐藏到托盘" : "显示桌宠", () => { if (IsVisible) Hide(); else ShowPet(); });
        yield return new MenuEntry("退出桌宠", Close);
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = sprite,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            HasDropShadow = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = PetTheme.BuildContextMenuTemplate()
        };
        var itemStyle = PetTheme.BuildMenuItemStyle();
        foreach (var entry in ContextMenuEntries())
        {
            if (entry == null)
            {
                menu.Items.Add(new Separator
                {
                    Margin = new Thickness(10, 4, 10, 4),
                    Height = 1,
                    Background = PetTheme.Divider
                });
                continue;
            }
            var selected = entry;
            var item = new MenuItem
            {
                Header = entry.Label,
                IsCheckable = entry.Checked.HasValue,
                IsChecked = entry.Checked ?? false,
                IsEnabled = entry.Enabled,
                Style = itemStyle
            };
            item.Click += (sender, args) => selected.Action();
            menu.Items.Add(item);
        }
        menu.Opened += (sender, args) => { dockMenuOpen = true; curiousHoverAt = -1; };
        menu.Closed += (sender, args) => CloseDockMenu();
        menu.IsOpen = true;
    }

    private void PopulateTrayMenu(Forms.ContextMenuStrip menu)
    {
        while (menu.Items.Count > 0)
        {
            var previous = menu.Items[0];
            menu.Items.RemoveAt(0);
            previous.Dispose();
        }
        foreach (var entry in MenuEntries())
        {
            if (entry == null) { menu.Items.Add(new Forms.ToolStripSeparator()); continue; }
            var selected = entry;
            var item = new Forms.ToolStripMenuItem(entry.Label) { Checked = entry.Checked ?? false, Enabled = entry.Enabled };
            item.Click += (sender, args) => selected.Action();
            menu.Items.Add(item);
        }
    }
}
