// ============================================================================
// SettingsPanel.cs - 卡片式可视化「设置中心」（外观与动作 / 日常陪伴 / AI 大脑）
// 核心原理：通过三标签页面板替代深层右键菜单，所有选项修改后立即同步到 PetWindow
// 并写入 settings.xml 或当前用户开机启动注册表，同时支持随时跳转 AI 大脑配置页。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;

internal sealed class SettingsPanel : Window
{
    private readonly PetWindow pet;
    private readonly ChatBridge chat;

    private readonly Button lookTabBtn;
    private readonly Button companionTabBtn;
    private readonly Button brainTabBtn;
    private readonly ScrollViewer lookView;
    private readonly ScrollViewer companionView;
    private readonly ScrollViewer brainView;

    private readonly Button sizeSmallBtn;
    private readonly Button sizeMediumBtn;
    private readonly Button sizeLargeBtn;
    private readonly CheckBox alwaysOnTopBox;
    private readonly CheckBox animateBox;
    private readonly CheckBox followMouseBox;
    private readonly CheckBox snapEdgesBox;
    private readonly CheckBox compactDockBox;
    private readonly CheckBox idleActionsBox;

    private readonly CheckBox autoStartBox;
    private readonly TextBlock autoStartStatus;
    private readonly CheckBox timeGreetingsBox;
    private readonly CheckBox healthReminderBox;
    private readonly Button min30Btn;
    private readonly Button min45Btn;
    private readonly Button min60Btn;
    private readonly CheckBox codexNoticesBox;
    private readonly CheckBox showCodexBox;
    private readonly Border codexCard;

    private readonly TextBlock brainSummaryText;
    private bool syncing;

    internal SettingsPanel(PetWindow pet, ChatBridge chat)
    {
        this.pet = pet;
        this.chat = chat;

        Title = "GPT 龙娘 · 设置中心";
        Width = 500;
        Height = 590;
        MinWidth = 420;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = PetTheme.WindowBackground();
        FontFamily = PetTheme.UiFont;
        FontSize = 13;

        var root = new DockPanel { Margin = new Thickness(18, 16, 18, 16) };

        var header = new StackPanel();
        header.Children.Add(PetTheme.Label("设置中心", 22, PetTheme.Ink, FontWeights.SemiBold));
        var sub = PetTheme.Label("调整龙娘的外观尺寸、日常陪伴习惯与智能聊天大脑", 12, PetTheme.Muted, FontWeights.Normal);
        sub.Margin = new Thickness(0, 3, 0, 12);
        header.Children.Add(sub);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        lookTabBtn = PetTheme.MakeButton("外观与动作", () => SwitchTab(0));
        companionTabBtn = PetTheme.MakeButton("日常陪伴", () => SwitchTab(1));
        brainTabBtn = PetTheme.MakeButton("AI 大脑", () => SwitchTab(2));
        lookTabBtn.Margin = new Thickness(0, 0, 8, 0);
        companionTabBtn.Margin = new Thickness(0, 0, 8, 0);
        tabs.Children.Add(lookTabBtn);
        tabs.Children.Add(companionTabBtn);
        tabs.Children.Add(brainTabBtn);
        header.Children.Add(tabs);

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // Tab 0: Appearance & Motion
        var lookStack = new StackPanel();
        var sizePanel = new StackPanel();
        sizePanel.Children.Add(PetTheme.Label("体型大小与位置", 14, PetTheme.Ink, FontWeights.SemiBold));
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
        sizeSmallBtn = PetTheme.MakeChipButton("小号 (128px)", () => { pet.ApplySize(128); RefreshSizeChips(); });
        sizeMediumBtn = PetTheme.MakeChipButton("中号 (174px)", () => { pet.ApplySize(174); RefreshSizeChips(); });
        sizeLargeBtn = PetTheme.MakeChipButton("大号 (238px)", () => { pet.ApplySize(238); RefreshSizeChips(); });
        sizeSmallBtn.Margin = new Thickness(0, 0, 8, 0);
        sizeMediumBtn.Margin = new Thickness(0, 0, 8, 0);
        sizeRow.Children.Add(sizeSmallBtn);
        sizeRow.Children.Add(sizeMediumBtn);
        sizeRow.Children.Add(sizeLargeBtn);
        sizePanel.Children.Add(sizeRow);
        var resetBtn = PetTheme.MakeButton("回到桌面右下角", () => pet.ResetPetPosition());
        resetBtn.HorizontalAlignment = HorizontalAlignment.Left;
        sizePanel.Children.Add(resetBtn);
        lookStack.Children.Add(PetTheme.MakeCard(sizePanel));

        var motionPanel = new StackPanel();
        motionPanel.Children.Add(PetTheme.Label("动作与交互开关", 14, PetTheme.Ink, FontWeights.SemiBold));
        alwaysOnTopBox = PetTheme.MakeToggle("始终显示在最前", "让龙娘一直浮在其他软件窗口上方。", true, v => { if (!syncing) pet.SetAlwaysOnTop(v); });
        animateBox = PetTheme.MakeToggle("开启桌宠动效", "包含呼吸起伏、眨眼、挥手、伸懒腰与流畅转场。", true, v => { if (!syncing) pet.SetAnimate(v); });
        followMouseBox = PetTheme.MakeToggle("瞳孔跟随鼠标", "龙娘会用紫眸轻轻跟着你的鼠标指针看。", true, v => { if (!syncing) pet.SetFollowMouse(v); });
        snapEdgesBox = PetTheme.MakeToggle("拖放时自动吸边", "拖到屏幕左右上下边缘时轻轻吸附贴边。", true, v => { if (!syncing) pet.SetSnapEdges(v); });
        compactDockBox = PetTheme.MakeToggle("贴边后自动探头收起", "贴边休息一会后滑入边线外探出小脑袋，腾出屏幕空间。", true, v => { if (!syncing) pet.SetCompactDock(v); });
        idleActionsBox = PetTheme.MakeToggle("待机伸懒腰与悬停歪头", "空闲时偶尔伸伸懒腰，鼠标在头部多停一会会好奇歪头。", true, v => { if (!syncing) pet.SetIdleActions(v); });
        motionPanel.Children.Add(alwaysOnTopBox);
        motionPanel.Children.Add(animateBox);
        motionPanel.Children.Add(followMouseBox);
        motionPanel.Children.Add(snapEdgesBox);
        motionPanel.Children.Add(compactDockBox);
        motionPanel.Children.Add(idleActionsBox);
        lookStack.Children.Add(PetTheme.MakeCard(motionPanel));

        lookView = new ScrollViewer
        {
            Content = lookStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        // Tab 1: Daily Companion
        var compStack = new StackPanel();
        var lifePanel = new StackPanel();
        lifePanel.Children.Add(PetTheme.Label("日常轻陪伴（无需联网与 API）", 14, PetTheme.Ink, FontWeights.SemiBold));
        autoStartBox = PetTheme.MakeToggle("开机自动启动", "每次打开电脑，龙娘会自动出现在桌面陪伴你。", false, v =>
        {
            if (syncing) return;
            bool ok = pet.SetAutoStart(v);
            autoStartStatus.Text = ok
                ? (v ? "已开启开机自启（当前 Windows 用户）。" : "已关闭开机自启。")
                : "写入开机启动项失败，可能被安全软件拦截。";
        });
        autoStartStatus = PetTheme.Label("", 11.5, PetTheme.Muted, FontWeights.Normal);
        autoStartStatus.Margin = new Thickness(24, 0, 0, 6);

        timeGreetingsBox = PetTheme.MakeToggle("早午晚时段问候", "打开桌宠或轻点打招呼时，随早中晚时间送出暖心问候。", true, v => { if (!syncing) pet.SetTimeGreetings(v); });
        healthReminderBox = PetTheme.MakeToggle("久坐 / 喝水轻提醒", "定时伸个懒腰提醒你喝口水、活动一下肩颈。", false, v => { if (!syncing) pet.SetHealthReminder(v, pet.SettingsSnapshot.HealthReminderMinutes); });

        var intervalRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 2, 0, 4) };
        intervalRow.Children.Add(PetTheme.Label("提醒间隔：", 12, PetTheme.Muted, FontWeights.Normal));
        min30Btn = PetTheme.MakeChipButton("30 分钟", () => { pet.SetHealthReminder(pet.SettingsSnapshot.HealthReminder, 30); RefreshIntervalChips(); });
        min45Btn = PetTheme.MakeChipButton("45 分钟", () => { pet.SetHealthReminder(pet.SettingsSnapshot.HealthReminder, 45); RefreshIntervalChips(); });
        min60Btn = PetTheme.MakeChipButton("60 分钟", () => { pet.SetHealthReminder(pet.SettingsSnapshot.HealthReminder, 60); RefreshIntervalChips(); });
        min30Btn.Margin = new Thickness(6, 0, 6, 0);
        min45Btn.Margin = new Thickness(0, 0, 6, 0);
        intervalRow.Children.Add(min30Btn);
        intervalRow.Children.Add(min45Btn);
        intervalRow.Children.Add(min60Btn);

        lifePanel.Children.Add(autoStartBox);
        lifePanel.Children.Add(autoStartStatus);
        lifePanel.Children.Add(timeGreetingsBox);
        lifePanel.Children.Add(healthReminderBox);
        lifePanel.Children.Add(intervalRow);
        compStack.Children.Add(PetTheme.MakeCard(lifePanel));

        var codexPanelStack = new StackPanel();
        codexPanelStack.Children.Add(PetTheme.Label("开发工具联动（Codex）", 14, PetTheme.Ink, FontWeights.SemiBold));
        codexNoticesBox = PetTheme.MakeToggle("Codex 任务思考与庆祝联动", "监测本机 Codex 会话：运行中陪你歪头思考，完成后跳起星光庆祝。", true, v =>
        {
            if (!syncing && pet.SettingsSnapshot.CodexNotices != v) pet.ToggleCodexNotices();
        });
        showCodexBox = PetTheme.MakeToggle("始终在菜单显示 Codex 入口", "未检测到本机 Codex 时默认隐藏开发菜单，勾选可强制展示。", false, v => { if (!syncing) pet.SetShowCodexFeatures(v); });
        var codexBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var openTasksBtn = PetTheme.MakeButton("查看 Codex 任务", () => pet.OpenCodexPanel(false));
        var openQuotasBtn = PetTheme.MakeButton("查看 Codex 额度", () => pet.OpenCodexPanel(true));
        openTasksBtn.Margin = new Thickness(0, 0, 8, 0);
        codexBtns.Children.Add(openTasksBtn);
        codexBtns.Children.Add(openQuotasBtn);

        codexPanelStack.Children.Add(codexNoticesBox);
        codexPanelStack.Children.Add(showCodexBox);
        codexPanelStack.Children.Add(codexBtns);
        codexCard = PetTheme.MakeCard(codexPanelStack);
        compStack.Children.Add(codexCard);

        companionView = new ScrollViewer
        {
            Content = compStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed
        };

        // Tab 2: AI Brain
        var brainStack = new StackPanel();
        var brainCardStack = new StackPanel();
        brainCardStack.Children.Add(PetTheme.Label("当前 AI 聊天大脑", 14, PetTheme.Ink, FontWeights.SemiBold));
        brainSummaryText = PetTheme.Label("", 13.5, PetTheme.AccentDark, FontWeights.SemiBold);
        brainSummaryText.TextWrapping = TextWrapping.Wrap;
        brainSummaryText.Margin = new Thickness(0, 8, 0, 8);
        brainCardStack.Children.Add(brainSummaryText);

        var brainTip = PetTheme.Label("支持大众常用的硅基流动 DeepSeek（免费额度）、智谱 GLM-4-Flash（官方免费）、DeepSeek 官方、Gemini、本机 Ollama 离线模型，以及高级用户的本地 Python 后门。", 12, PetTheme.Muted, FontWeights.Normal);
        brainTip.TextWrapping = TextWrapping.Wrap;
        brainTip.Margin = new Thickness(0, 0, 0, 12);
        brainCardStack.Children.Add(brainTip);

        var brainActions = new StackPanel { Orientation = Orientation.Horizontal };
        var configBrainBtn = PetTheme.MakeButton("配置 / 切换大脑 →", () => pet.OpenBrainPanel(), true);
        var openChatBtn = PetTheme.MakeButton("打开聊天窗口", () => pet.OpenChatPanel());
        configBrainBtn.Margin = new Thickness(0, 0, 8, 0);
        brainActions.Children.Add(configBrainBtn);
        brainActions.Children.Add(openChatBtn);
        brainCardStack.Children.Add(brainActions);

        brainStack.Children.Add(PetTheme.MakeCard(brainCardStack));
        brainView = new ScrollViewer
        {
            Content = brainStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed
        };

        var host = new Grid();
        host.Children.Add(lookView);
        host.Children.Add(companionView);
        host.Children.Add(brainView);
        root.Children.Add(host);
        Content = root;

        RefreshFromPet();
        SwitchTab(0);
    }

    internal void RefreshFromPet()
    {
        syncing = true;
        try
        {
            var s = pet.SettingsSnapshot;
            alwaysOnTopBox.IsChecked = s.AlwaysOnTop;
            animateBox.IsChecked = s.Animate;
            followMouseBox.IsChecked = s.FollowMouse;
            snapEdgesBox.IsChecked = s.SnapEdges;
            compactDockBox.IsChecked = s.CompactDock;
            idleActionsBox.IsChecked = s.IdleActions;
            autoStartBox.IsChecked = s.AutoStart;
            timeGreetingsBox.IsChecked = s.TimeGreetings;
            healthReminderBox.IsChecked = s.HealthReminder;
            codexNoticesBox.IsChecked = s.CodexNotices;
            showCodexBox.IsChecked = s.ShowCodexFeatures;
            codexCard.Visibility = (pet.ShouldShowCodex() || s.ShowCodexFeatures) ? Visibility.Visible : Visibility.Collapsed;
            brainSummaryText.Text = chat.ActiveBrainSummary();
            RefreshSizeChips();
            RefreshIntervalChips();
        }
        finally { syncing = false; }
    }

    internal void SwitchTab(int index)
    {
        lookView.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        companionView.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        brainView.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        lookTabBtn.Background = index == 0 ? PetTheme.AccentSoft : PetTheme.Surface;
        companionTabBtn.Background = index == 1 ? PetTheme.AccentSoft : PetTheme.Surface;
        brainTabBtn.Background = index == 2 ? PetTheme.AccentSoft : PetTheme.Surface;

        lookTabBtn.FontWeight = index == 0 ? FontWeights.SemiBold : FontWeights.Normal;
        companionTabBtn.FontWeight = index == 1 ? FontWeights.SemiBold : FontWeights.Normal;
        brainTabBtn.FontWeight = index == 2 ? FontWeights.SemiBold : FontWeights.Normal;

        if (index == 2) brainSummaryText.Text = chat.ActiveBrainSummary();
    }

    private void RefreshSizeChips()
    {
        int size = pet.SettingsSnapshot.Size;
        PetTheme.StyleChip(sizeSmallBtn, size == 128);
        PetTheme.StyleChip(sizeMediumBtn, size != 128 && size != 238);
        PetTheme.StyleChip(sizeLargeBtn, size == 238);
    }

    private void RefreshIntervalChips()
    {
        int mins = pet.SettingsSnapshot.HealthReminderMinutes;
        PetTheme.StyleChip(min30Btn, mins == 30);
        PetTheme.StyleChip(min45Btn, mins != 30 && mins != 60);
        PetTheme.StyleChip(min60Btn, mins == 60);
    }
}
