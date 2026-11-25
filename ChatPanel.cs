// ============================================================================
// ChatPanel.cs - 纯代码构建的「聊天对话」与「选择大脑」双页签交互窗口
// 核心原理：
//   1. 顶部双页签切换对话流与卡片式 AI 大脑向导，自动高亮当前生效的后端预设；
//   2. 内置本机 Ollama/LM Studio 一键扫描下拉框、开发者高级选项可折叠收纳，
//      并根据 codexAvailable 自动决定是否展示本机 Codex 卡片；
//   3. 支持请求期间取消任务（CancellationTokenSource）与触发主窗体思考/庆祝动作。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

internal sealed class ChatPanel : Window
{
    private readonly ChatBridge bridge;
    private readonly Action<ChatReply> onReply;
    private readonly Action<bool> onThinking;
    private readonly bool codexAvailable;
    private readonly TextBlock statusText;
    private readonly TextBlock providerBadgeText;
    private readonly Button chatTabBtn;
    private readonly Button brainTabBtn;
    private readonly Button clearBtn;

    private readonly DockPanel chatView;
    private readonly ScrollViewer chatScroll;
    private readonly StackPanel messageList = new StackPanel();
    private readonly TextBox inputBox;
    private readonly Button sendBtn;

    private readonly ScrollViewer brainView;
    private readonly Dictionary<string, Border> presetCardBorders = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
    private readonly PasswordBox apiKeyBox;
    private readonly Button getKeyBtn;
    private readonly TextBlock presetDescText;
    private readonly Button scanLocalBtn;
    private readonly ComboBox localModelBox;
    private readonly TextBlock localScanStatus;
    private readonly Border codexSectionCard;
    private readonly Button useCodexBtn;
    private readonly ComboBox providerBox;
    private readonly TextBox apiUrlBox;
    private readonly TextBox modelBox;
    private readonly TextBox codexModelBox;
    private readonly TextBox localEndpointBox;
    private readonly TextBox historyTurnsBox;
    private readonly TextBox personaBox;
    private readonly TextBlock configHint;
    private readonly Button saveConfigBtn;
    private readonly Button testConfigBtn;

    private readonly List<LocalModelInfo> discoveredModels = new List<LocalModelInfo>();
    private string selectedPresetId = "siliconflow";
    private string selectedKeyUrl = "https://cloud.siliconflow.cn/";
    private CancellationTokenSource activeRequest;
    private bool closed;

    internal ChatPanel(ChatBridge bridge, Action<ChatReply> onReply, Action<bool> onThinking = null, bool codexAvailable = true)
    {
        this.bridge = bridge;
        this.onReply = onReply;
        this.onThinking = onThinking;
        this.codexAvailable = codexAvailable;

        Title = "GPT 龙娘 · 聊天与大脑";
        Width = 520;
        Height = 670;
        MinWidth = 420;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = PetTheme.WindowBackground();
        FontFamily = PetTheme.UiFont;
        FontSize = 13;

        var root = new DockPanel { Margin = new Thickness(18, 16, 18, 16) };

        var header = new StackPanel();
        var titleRow = new DockPanel();
        providerBadgeText = PetTheme.Label("✦ 准备中", 11.5, PetTheme.AccentDark, FontWeights.SemiBold);
        var badge = new Border
        {
            Background = PetTheme.AccentSoft,
            BorderBrush = PetTheme.BorderTone,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = providerBadgeText
        };
        DockPanel.SetDock(badge, Dock.Right);
        titleRow.Children.Add(badge);
        titleRow.Children.Add(PetTheme.Label("和 GPT 龙娘聊天", 21, PetTheme.Ink, FontWeights.SemiBold));
        header.Children.Add(titleRow);

        var navRow = new DockPanel { Margin = new Thickness(0, 12, 0, 8) };
        clearBtn = PetTheme.MakeButton("清空记忆", OnClearMemory);
        DockPanel.SetDock(clearBtn, Dock.Right);
        navRow.Children.Add(clearBtn);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        chatTabBtn = PetTheme.MakeButton("对话", () => SwitchTab(false));
        brainTabBtn = PetTheme.MakeButton("选择大脑", () => SwitchTab(true));
        chatTabBtn.Margin = new Thickness(0, 0, 8, 0);
        tabs.Children.Add(chatTabBtn);
        tabs.Children.Add(brainTabBtn);
        navRow.Children.Add(tabs);
        header.Children.Add(navRow);

        statusText = PetTheme.Label("", 12, PetTheme.Muted, FontWeights.Normal);
        statusText.Margin = new Thickness(2, 0, 2, 10);
        header.Children.Add(statusText);

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // 1. Chat view
        chatView = new DockPanel();
        var inputCard = new Border
        {
            Background = PetTheme.Surface,
            BorderBrush = PetTheme.BorderTone,
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 8, 8, 8),
            Margin = new Thickness(0, 10, 0, 0),
            Effect = PetTheme.CardShadow(0.10)
        };
        var inputBar = new DockPanel();
        sendBtn = PetTheme.MakeButton("发送", OnSendOrCancel, true);
        sendBtn.MinWidth = 74;
        sendBtn.VerticalAlignment = VerticalAlignment.Bottom;
        DockPanel.SetDock(sendBtn, Dock.Right);
        inputBar.Children.Add(sendBtn);

        inputBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 32,
            MaxHeight = 108,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(4, 5, 8, 5),
            Background = Brushes.Transparent,
            Foreground = PetTheme.Ink,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        inputBox.PreviewKeyDown += InputBoxKeyDown;
        inputBar.Children.Add(inputBox);
        inputCard.Child = inputBar;
        DockPanel.SetDock(inputCard, Dock.Bottom);
        chatView.Children.Add(inputCard);

        chatScroll = new ScrollViewer
        {
            Content = messageList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 2, 4, 2)
        };
        chatView.Children.Add(chatScroll);

        // 2. Brain selection view (accessible for non-Codex users)
        var brainStack = new StackPanel();

        // Card A: Online API presets (SiliconFlow DeepSeek, Zhipu Free, DeepSeek Official, Gemini, OpenAI)
        var onlineStack = new StackPanel();
        onlineStack.Children.Add(PetTheme.Label("1. 在线智能大脑（适合大多数用户 · 支持免费额度）", 14, PetTheme.Ink, FontWeights.SemiBold));
        onlineStack.Children.Add(PetTheme.Label("选一张大脑卡片，点「去官网领 Key」，粘贴进来点保存即可聊天：", 12, PetTheme.Muted, FontWeights.Normal));

        var presetWrap = new WrapPanel { Margin = new Thickness(0, 10, 0, 4) };
        foreach (var preset in ChatBridge.Presets)
            presetWrap.Children.Add(BuildPresetCard(preset));
        onlineStack.Children.Add(presetWrap);

        presetDescText = PetTheme.Label("", 11.5, PetTheme.AccentDark, FontWeights.Normal);
        presetDescText.TextWrapping = TextWrapping.Wrap;
        presetDescText.Margin = new Thickness(2, 2, 2, 8);
        onlineStack.Children.Add(presetDescText);

        var keyLabelRow = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
        getKeyBtn = PetTheme.MakeChipButton("去官网免费领 Key ↗", OpenSelectedKeyUrl);
        DockPanel.SetDock(getKeyBtn, Dock.Right);
        keyLabelRow.Children.Add(getKeyBtn);
        var keyTitle = PetTheme.Label("API Key（密钥）", 12.5, PetTheme.Ink, FontWeights.SemiBold);
        keyTitle.VerticalAlignment = VerticalAlignment.Center;
        keyLabelRow.Children.Add(keyTitle);
        onlineStack.Children.Add(keyLabelRow);

        apiKeyBox = new PasswordBox
        {
            Margin = new Thickness(0, 2, 0, 4),
            Padding = new Thickness(9, 7, 9, 7),
            Background = PetTheme.InputBg,
            Foreground = PetTheme.Ink,
            BorderBrush = PetTheme.BorderTone,
            BorderThickness = new Thickness(1)
        };
        onlineStack.Children.Add(apiKeyBox);
        brainStack.Children.Add(PetTheme.MakeCard(onlineStack));

        // Card B: Local free models (Ollama / LM Studio)
        var localStack = new StackPanel();
        localStack.Children.Add(PetTheme.Label("2. 本机免费模型（Ollama / LM Studio · 完全离线免 Key）", 14, PetTheme.Ink, FontWeights.SemiBold));
        var localTip = PetTheme.Label("适合已安装 Ollama（如 ollama run deepseek-r1:7b）或 LM Studio 的电脑，自动扫描即可使用。", 12, PetTheme.Muted, FontWeights.Normal);
        localTip.TextWrapping = TextWrapping.Wrap;
        localTip.Margin = new Thickness(0, 2, 0, 8);
        localStack.Children.Add(localTip);

        var scanRow = new DockPanel();
        scanLocalBtn = PetTheme.MakeButton("扫描本机模型", OnScanLocalModels);
        DockPanel.SetDock(scanLocalBtn, Dock.Left);
        scanRow.Children.Add(scanLocalBtn);

        localModelBox = new ComboBox
        {
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 6, 8, 6),
            Background = PetTheme.InputBg,
            Foreground = PetTheme.Ink,
            IsEnabled = false
        };
        localModelBox.SelectionChanged += OnLocalModelSelected;
        scanRow.Children.Add(localModelBox);
        localStack.Children.Add(scanRow);

        localScanStatus = PetTheme.Label("点击左侧按钮自动探测本机 11434 (Ollama) / 1234 (LM Studio) 端口。", 11.5, PetTheme.Muted, FontWeights.Normal);
        localScanStatus.TextWrapping = TextWrapping.Wrap;
        localScanStatus.Margin = new Thickness(0, 6, 0, 0);
        localStack.Children.Add(localScanStatus);
        brainStack.Children.Add(PetTheme.MakeCard(localStack));

        // Card C: Local Codex (shown cleanly when installed or selected)
        var codexStack = new StackPanel();
        codexStack.Children.Add(PetTheme.Label("3. 本机 Codex（已登录免 Key 直连）", 14, PetTheme.Ink, FontWeights.SemiBold));
        var codexRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        useCodexBtn = PetTheme.MakeButton("切回本机 Codex 模式", () => SelectBackendMode("codex", "已切换为本机 Codex（免 API Key）模式，点下方「保存并生效」即可。"));
        DockPanel.SetDock(useCodexBtn, Dock.Right);
        codexRow.Children.Add(useCodexBtn);
        var codexNote = PetTheme.Label("检测到你的电脑已安装 Codex，可直接复用账号对话。", 12, PetTheme.Muted, FontWeights.Normal);
        codexNote.TextWrapping = TextWrapping.Wrap;
        codexNote.VerticalAlignment = VerticalAlignment.Center;
        codexNote.Margin = new Thickness(0, 0, 10, 0);
        codexRow.Children.Add(codexNote);
        codexStack.Children.Add(codexRow);
        codexSectionCard = PetTheme.MakeCard(codexStack);
        brainStack.Children.Add(codexSectionCard);

        // Card D: Developer / Advanced expander
        var advStack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        advStack.Children.Add(SectionTitle("当前底层后端模式"));
        providerBox = new ComboBox
        {
            Margin = new Thickness(0, 4, 0, 10),
            Padding = new Thickness(8, 6, 8, 6),
            Background = PetTheme.InputBg,
            Foreground = PetTheme.Ink
        };
        providerBox.Items.Add(new ComboBoxItem { Content = "在线 API（OpenAI 兼容格式）", Tag = "openai" });
        providerBox.Items.Add(new ComboBoxItem { Content = "本机免费模型（Ollama / LM Studio）", Tag = "local_llm" });
        providerBox.Items.Add(new ComboBoxItem { Content = "本机 Codex（免 Key）", Tag = "codex" });
        providerBox.Items.Add(new ComboBoxItem { Content = "本地 Python HTTP 后门（开发者）", Tag = "http" });
        advStack.Children.Add(providerBox);

        advStack.Children.Add(SectionTitle("自定义 API 地址（Base URL）"));
        apiUrlBox = MakeInput();
        advStack.Children.Add(apiUrlBox);

        advStack.Children.Add(SectionTitle("自定义模型名称"));
        modelBox = MakeInput();
        advStack.Children.Add(modelBox);

        advStack.Children.Add(SectionTitle("本地 Python HTTP 后门地址（POST JSON）"));
        var httpRow = new DockPanel { Margin = new Thickness(0, 4, 0, 10) };
        var useHttpBtn = PetTheme.MakeChipButton("启用后门", () => SelectBackendMode("http", "已切换为本地 Python HTTP 后门模式，点下方「保存并生效」即可。"));
        useHttpBtn.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(useHttpBtn, Dock.Right);
        httpRow.Children.Add(useHttpBtn);
        localEndpointBox = MakeInput();
        localEndpointBox.Margin = new Thickness(0);
        httpRow.Children.Add(localEndpointBox);
        advStack.Children.Add(httpRow);

        advStack.Children.Add(SectionTitle("Codex 指定模型（留空使用默认）"));
        codexModelBox = MakeInput();
        advStack.Children.Add(codexModelBox);

        advStack.Children.Add(SectionTitle("上下文记忆轮数（1~50）"));
        historyTurnsBox = MakeInput();
        advStack.Children.Add(historyTurnsBox);

        advStack.Children.Add(SectionTitle("龙娘人设 Prompt"));
        personaBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 88,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(9, 7, 9, 7),
            Background = PetTheme.InputBg,
            Foreground = PetTheme.Ink,
            BorderBrush = PetTheme.BorderTone,
            BorderThickness = new Thickness(1)
        };
        advStack.Children.Add(personaBox);

        var advExpander = new Expander
        {
            Header = PetTheme.Label("开发者与高级选项（自定义地址 / Python 后门 / 改人设）", 12.5, PetTheme.Muted, FontWeights.SemiBold),
            IsExpanded = false,
            Content = advStack
        };
        brainStack.Children.Add(PetTheme.MakeCard(advExpander, new Thickness(12, 10, 12, 10)));

        // Action row
        var actionBar = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
        saveConfigBtn = PetTheme.MakeButton("保存并生效", OnSaveConfig, true);
        saveConfigBtn.Margin = new Thickness(0, 0, 8, 0);
        testConfigBtn = PetTheme.MakeButton("测试连通", OnTestConnection);
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(saveConfigBtn);
        btns.Children.Add(testConfigBtn);
        DockPanel.SetDock(btns, Dock.Left);
        actionBar.Children.Add(btns);
        brainStack.Children.Add(actionBar);

        configHint = PetTheme.Label("", 12, PetTheme.Muted, FontWeights.Normal);
        configHint.TextWrapping = TextWrapping.Wrap;
        configHint.Margin = new Thickness(2, 2, 2, 4);
        brainStack.Children.Add(configHint);

        brainView = new ScrollViewer
        {
            Content = brainStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed
        };

        var bodyHost = new Grid();
        bodyHost.Children.Add(chatView);
        bodyHost.Children.Add(brainView);
        root.Children.Add(bodyHost);
        Content = root;

        Closed += (sender, args) =>
        {
            closed = true;
            if (activeRequest != null) activeRequest.Cancel();
        };

        LoadConfigToForm();
        RenderHistory();
        SwitchTab(false);
        UpdateStatus(null, false);
    }

    internal static ControlTemplate BuildPillButtonTemplate(double radius)
    {
        return PetTheme.BuildPillButtonTemplate(radius);
    }

    internal void OpenBrainSettings()
    {
        SwitchTab(true);
    }

    internal void FocusInput()
    {
        SwitchTab(false);
        Dispatcher.BeginInvoke(new Action(() => inputBox.Focus()));
    }

    private Border BuildPresetCard(ApiPreset preset)
    {
        var stack = new StackPanel();
        var badge = new Border
        {
            Background = PetTheme.AccentSoft,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 5),
            Child = PetTheme.Label(preset.Badge, 10.5, PetTheme.AccentDark, FontWeights.SemiBold)
        };
        stack.Children.Add(badge);
        var title = PetTheme.Label(preset.Title, 13, PetTheme.Ink, FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(title);

        var card = new Border
        {
            Width = 140,
            Background = PetTheme.InputBg,
            BorderBrush = PetTheme.BorderTone,
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            Child = stack
        };
        card.MouseLeftButtonUp += (sender, args) => ApplyPreset(preset);
        presetCardBorders[preset.Id] = card;
        return card;
    }

    private void ApplyPreset(ApiPreset preset)
    {
        if (preset == null) return;
        selectedPresetId = preset.Id;
        selectedKeyUrl = preset.KeyUrl;
        SelectProvider("openai");
        apiUrlBox.Text = preset.BaseUrl;
        modelBox.Text = preset.DefaultModel;
        presetDescText.Text = preset.Description;
        HighlightSelectedPreset();
        configHint.Foreground = PetTheme.AccentDark;
        configHint.Text = "已选中「" + preset.Title + "」，填入 API Key 后点「保存并生效」即可。";
    }

    private void HighlightSelectedPreset()
    {
        var cfgProvider = CurrentSelectedProvider();
        bool isOnline = String.Equals(cfgProvider, "openai", StringComparison.OrdinalIgnoreCase);
        foreach (var kv in presetCardBorders)
        {
            bool active = isOnline && String.Equals(kv.Key, selectedPresetId, StringComparison.OrdinalIgnoreCase);
            kv.Value.Background = active ? PetTheme.AccentSoft : PetTheme.InputBg;
            kv.Value.BorderBrush = active ? PetTheme.Accent : PetTheme.BorderTone;
            kv.Value.BorderThickness = new Thickness(active ? 1.8 : 1.0);
        }
    }

    private void SelectBackendMode(string provider, string message)
    {
        SelectProvider(provider);
        HighlightSelectedPreset();
        configHint.Foreground = PetTheme.AccentDark;
        configHint.Text = message;
    }

    private void OpenSelectedKeyUrl()
    {
        if (String.IsNullOrWhiteSpace(selectedKeyUrl)) return;
        try { Process.Start(new ProcessStartInfo(selectedKeyUrl) { UseShellExecute = true }); }
        catch { }
    }

    private async void OnScanLocalModels()
    {
        scanLocalBtn.IsEnabled = false;
        localScanStatus.Foreground = PetTheme.Muted;
        localScanStatus.Text = "正在扫描本机 Ollama / LM Studio…";
        using (var source = new CancellationTokenSource())
        {
            try
            {
                var found = await Task.Run(() => bridge.DiscoverLocalModels(source.Token), source.Token);
                if (closed) return;
                discoveredModels.Clear();
                localModelBox.Items.Clear();
                if (found.Length == 0)
                {
                    localModelBox.IsEnabled = false;
                    localScanStatus.Foreground = PetTheme.Muted;
                    localScanStatus.Text = "未检测到本机模型服务。可先安装 Ollama（ollama.com）并运行：ollama run deepseek-r1:7b";
                    return;
                }
                foreach (var item in found)
                {
                    discoveredModels.Add(item);
                    localModelBox.Items.Add(item.DisplayName);
                }
                localModelBox.IsEnabled = true;
                localModelBox.SelectedIndex = 0;
                localScanStatus.Foreground = PetTheme.SuccessTone;
                localScanStatus.Text = "共发现 " + found.Length + " 个本机免费模型！已选中第一个，点「保存并生效」即可离线畅聊。";
            }
            finally
            {
                if (!closed) scanLocalBtn.IsEnabled = true;
            }
        }
    }

    private void OnLocalModelSelected(object sender, SelectionChangedEventArgs e)
    {
        int idx = localModelBox.SelectedIndex;
        if (idx < 0 || idx >= discoveredModels.Count) return;
        SelectProvider("local_llm");
        HighlightSelectedPreset();
    }

    private void SwitchTab(bool showBrain)
    {
        chatView.Visibility = showBrain ? Visibility.Collapsed : Visibility.Visible;
        brainView.Visibility = showBrain ? Visibility.Visible : Visibility.Collapsed;
        chatTabBtn.Background = showBrain ? PetTheme.Surface : PetTheme.AccentSoft;
        brainTabBtn.Background = showBrain ? PetTheme.AccentSoft : PetTheme.Surface;
        chatTabBtn.FontWeight = showBrain ? FontWeights.Normal : FontWeights.SemiBold;
        brainTabBtn.FontWeight = showBrain ? FontWeights.SemiBold : FontWeights.Normal;
        if (showBrain) LoadConfigToForm();
    }

    private void RenderHistory()
    {
        messageList.Children.Clear();
        var items = bridge.GetHistory();
        if (items.Length == 0)
        {
            AddWelcomeTip();
            return;
        }
        foreach (var item in items)
            AppendBubble(item.Role == "user", item.Content, item.Time);
        ScrollToBottom();
    }

    private void AddWelcomeTip()
    {
        bool ready = bridge.IsConfiguredReady(codexAvailable);
        var stack = new StackPanel();
        string msg = ready
            ? "你好呀！在下方输入内容按 Enter 即可和我聊天（Shift+Enter 换行）。\n也可以随时点上方「选择大脑」切换 DeepSeek、智谱免费版、Gemini 或本机离线模型。"
            : "你好呀！还差一步给龙娘选好「聊天大脑」就能开聊啦：\n• 在线免费额度：硅基流动 DeepSeek / 智谱 GLM-4-Flash / Gemini\n• 本机离线免 Key：自动扫描 Ollama / LM Studio 模型";
        stack.Children.Add(new TextBlock
        {
            Text = msg,
            Foreground = PetTheme.Ink,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 19
        });
        if (!ready)
        {
            var jumpBtn = PetTheme.MakeButton("去选择龙娘大脑 →", () => SwitchTab(true), true);
            jumpBtn.HorizontalAlignment = HorizontalAlignment.Left;
            jumpBtn.Margin = new Thickness(0, 8, 0, 0);
            jumpBtn.Padding = new Thickness(12, 5, 12, 5);
            stack.Children.Add(jumpBtn);
        }
        var tip = new Border
        {
            Tag = "welcome",
            Background = PetTheme.AccentSoft,
            BorderBrush = PetTheme.BorderTone,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(2, 0, 2, 12),
            Child = stack
        };
        messageList.Children.Add(tip);
    }

    private void InputBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            if (activeRequest == null) SendCurrentInput();
        }
    }

    private void OnSendOrCancel()
    {
        if (activeRequest != null)
        {
            activeRequest.Cancel();
            return;
        }
        SendCurrentInput();
    }

    private async void SendCurrentInput()
    {
        string text = (inputBox.Text ?? "").Trim();
        if (text.Length == 0) return;

        inputBox.Clear();
        if (messageList.Children.Count == 1 && messageList.Children[0] is Border && Equals(((Border)messageList.Children[0]).Tag, "welcome"))
            messageList.Children.Clear();

        AppendBubble(true, text, DateTime.Now.ToString("HH:mm"));
        ScrollToBottom();

        var source = new CancellationTokenSource();
        activeRequest = source;
        sendBtn.Content = "停止";
        sendBtn.Background = PetTheme.ErrorTone;
        sendBtn.BorderBrush = PetTheme.ErrorTone;
        inputBox.IsEnabled = false;
        UpdateStatus("龙娘正在思考回复…", false);
        if (onThinking != null) onThinking(true);

        try
        {
            var reply = await Task.Run(() => bridge.Send(text, source.Token), source.Token);
            if (closed || source.IsCancellationRequested) return;
            if (onThinking != null) onThinking(false);
            AppendBubble(false, reply.Text, DateTime.Now.ToString("HH:mm"));
            ScrollToBottom();
            UpdateStatus("已回复 · " + reply.ProviderUsed, false);
            if (onReply != null) onReply(reply);
        }
        catch (OperationCanceledException)
        {
            if (onThinking != null) onThinking(false);
            if (!closed) UpdateStatus("已取消本次发送。", false);
        }
        catch (Exception ex)
        {
            if (onThinking != null) onThinking(false);
            if (!closed)
            {
                AddErrorTip("发送失败：" + ex.Message);
                ScrollToBottom();
                UpdateStatus("发送失败，可检查「选择大脑」。", true);
            }
        }
        finally
        {
            if (activeRequest == source)
            {
                activeRequest = null;
                if (!closed)
                {
                    sendBtn.Content = "发送";
                    sendBtn.Background = PetTheme.Accent;
                    sendBtn.BorderBrush = PetTheme.Accent;
                    inputBox.IsEnabled = true;
                    inputBox.Focus();
                }
            }
            source.Dispose();
        }
    }

    private void OnClearMemory()
    {
        bridge.ClearHistory();
        RenderHistory();
        UpdateStatus("已清空本地对话记忆。", false);
    }

    private void LoadConfigToForm()
    {
        var cfg = bridge.GetConfig();
        SelectProvider(cfg.Provider);
        selectedPresetId = cfg.PresetId;
        var preset = ChatBridge.FindPreset(selectedPresetId) ?? ChatBridge.Presets[0];
        selectedKeyUrl = preset.KeyUrl;
        presetDescText.Text = preset.Description;
        apiUrlBox.Text = cfg.ApiBaseUrl;
        apiKeyBox.Password = cfg.ApiKey;
        modelBox.Text = cfg.Model;
        codexModelBox.Text = cfg.CodexModel;
        localEndpointBox.Text = cfg.LocalEndpoint;
        historyTurnsBox.Text = cfg.MaxHistoryTurns.ToString(CultureInfo.InvariantCulture);
        personaBox.Text = cfg.SystemPersona;

        if (!String.IsNullOrWhiteSpace(cfg.LocalLlmModel) && discoveredModels.Count == 0)
        {
            discoveredModels.Add(new LocalModelInfo
            {
                Provider = "本地模型",
                BaseUrl = cfg.LocalLlmBaseUrl,
                ModelId = cfg.LocalLlmModel,
                DisplayName = cfg.LocalLlmModel
            });
            localModelBox.Items.Clear();
            localModelBox.Items.Add(cfg.LocalLlmModel);
            localModelBox.IsEnabled = true;
            localModelBox.SelectedIndex = 0;
        }
        codexSectionCard.Visibility = (codexAvailable || String.Equals(cfg.Provider, "codex", StringComparison.OrdinalIgnoreCase))
            ? Visibility.Visible : Visibility.Collapsed;
        HighlightSelectedPreset();
    }

    private string CurrentSelectedProvider()
    {
        var selected = providerBox.SelectedItem as ComboBoxItem;
        return selected != null && selected.Tag is string ? (string)selected.Tag : "openai";
    }

    private ChatConfig ReadConfigFromForm()
    {
        var current = bridge.GetConfig();
        string provider = CurrentSelectedProvider();
        int turns;
        if (!Int32.TryParse((historyTurnsBox.Text ?? "").Trim(), out turns)) turns = 12;

        string localUrl = current.LocalLlmBaseUrl;
        string localModel = current.LocalLlmModel;
        int idx = localModelBox.SelectedIndex;
        if (idx >= 0 && idx < discoveredModels.Count)
        {
            localUrl = discoveredModels[idx].BaseUrl;
            localModel = discoveredModels[idx].ModelId;
        }

        return new ChatConfig
        {
            Provider = provider,
            PresetId = selectedPresetId,
            ApiBaseUrl = (apiUrlBox.Text ?? "").Trim(),
            ApiKey = apiKeyBox.Password ?? "",
            Model = (modelBox.Text ?? "").Trim(),
            LocalLlmBaseUrl = localUrl,
            LocalLlmModel = localModel,
            CodexModel = (codexModelBox.Text ?? "").Trim(),
            LocalEndpoint = (localEndpointBox.Text ?? "").Trim(),
            MaxHistoryTurns = turns,
            SystemPersona = (personaBox.Text ?? "").Trim()
        };
    }

    private void SelectProvider(string provider)
    {
        for (int i = 0; i < providerBox.Items.Count; i++)
        {
            var item = providerBox.Items[i] as ComboBoxItem;
            if (item != null && String.Equals(item.Tag as string, provider, StringComparison.OrdinalIgnoreCase))
            {
                providerBox.SelectedIndex = i;
                return;
            }
        }
        providerBox.SelectedIndex = 0;
    }

    private void OnSaveConfig()
    {
        var cfg = ReadConfigFromForm();
        bridge.SaveConfig(cfg);
        configHint.Foreground = PetTheme.SuccessTone;
        configHint.Text = "配置已保存 · 当前大脑：" + bridge.ActiveBrainSummary();
        UpdateStatus(null, false);
        if (messageList.Children.Count == 1 && messageList.Children[0] is Border && Equals(((Border)messageList.Children[0]).Tag, "welcome"))
            RenderHistory();
    }

    private async void OnTestConnection()
    {
        var cfg = ReadConfigFromForm();
        testConfigBtn.IsEnabled = false;
        configHint.Foreground = PetTheme.Muted;
        configHint.Text = "正在测试连接…";
        using (var source = new CancellationTokenSource())
        {
            try
            {
                string result = await Task.Run(() => bridge.TestConnection(cfg, source.Token), source.Token);
                if (!closed)
                {
                    configHint.Foreground = PetTheme.SuccessTone;
                    configHint.Text = result;
                }
            }
            catch (Exception ex)
            {
                if (!closed)
                {
                    configHint.Foreground = PetTheme.ErrorTone;
                    configHint.Text = "测试失败：" + ex.Message;
                }
            }
            finally
            {
                if (!closed) testConfigBtn.IsEnabled = true;
            }
        }
    }

    private void UpdateStatus(string customText, bool isError)
    {
        providerBadgeText.Text = "✦ " + bridge.ProviderDisplayName(bridge.GetConfig().Provider);
        statusText.Foreground = isError ? PetTheme.ErrorTone : PetTheme.Muted;
        if (!String.IsNullOrWhiteSpace(customText))
        {
            statusText.Text = customText;
            return;
        }
        statusText.Text = "多轮记忆自动保存 · Enter 发送，Shift+Enter 换行";
    }

    private void AppendBubble(bool isUser, string text, string time)
    {
        var container = new StackPanel
        {
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(isUser ? 54 : 2, 0, isUser ? 2 : 54, 12)
        };

        var meta = PetTheme.Label((isUser ? "你" : "GPT 龙娘") + (String.IsNullOrEmpty(time) ? "" : " · " + time), 11, PetTheme.Muted, FontWeights.Normal);
        meta.HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        meta.Margin = new Thickness(4, 0, 4, 3);
        container.Children.Add(meta);

        var contentBox = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = isUser ? Brushes.White : PetTheme.Ink,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = PetTheme.UiFont,
            FontSize = 13,
            Padding = new Thickness(0)
        };

        var card = new Border
        {
            Background = isUser ? (Brush)PetTheme.AccentGradient() : PetTheme.Surface,
            BorderBrush = isUser ? PetTheme.Accent : PetTheme.BorderTone,
            BorderThickness = new Thickness(1),
            CornerRadius = isUser ? new CornerRadius(14, 4, 14, 14) : new CornerRadius(4, 14, 14, 14),
            Padding = new Thickness(13, 9, 13, 9),
            Effect = PetTheme.CardShadow(isUser ? 0.14 : 0.08),
            Child = contentBox
        };
        container.Children.Add(card);
        messageList.Children.Add(container);
    }

    private void AddErrorTip(string text)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = PetTheme.ErrorTone,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 19
        });
        var jump = PetTheme.MakeChipButton("检查大脑配置 →", () => SwitchTab(true));
        jump.HorizontalAlignment = HorizontalAlignment.Left;
        jump.Margin = new Thickness(0, 7, 0, 0);
        stack.Children.Add(jump);

        messageList.Children.Add(new Border
        {
            Tag = "error",
            Background = PetTheme.ErrorSoft,
            BorderBrush = PetTheme.ErrorBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(2, 0, 2, 12),
            Child = stack
        });
    }

    private void ScrollToBottom()
    {
        Dispatcher.BeginInvoke(new Action(() => chatScroll.ScrollToEnd()));
    }

    private static TextBlock SectionTitle(string text)
    {
        return PetTheme.Label(text, 12, PetTheme.Muted, FontWeights.SemiBold);
    }

    private static TextBox MakeInput()
    {
        return new TextBox
        {
            Margin = new Thickness(0, 4, 0, 10),
            Padding = new Thickness(9, 7, 9, 7),
            Background = PetTheme.InputBg,
            Foreground = PetTheme.Ink,
            BorderBrush = PetTheme.BorderTone,
            BorderThickness = new Thickness(1)
        };
    }
}
