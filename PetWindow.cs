// ============================================================================
// PetWindow.cs - 桌宠主窗体骨架、单实例入口与 60fps 渲染管线（partial 主文件）
// 核心原理：
//   1. WPF 透明分层窗体（AllowsTransparency=true + WindowStyle=None + Topmost）：
//      背景完全透明，仅渲染内嵌 PNG 精灵图层与气泡/星光特效；
//   2. Mutex + EventWaitHandle 实现单实例互斥，重复双击 EXE 时直接唤醒已运行桌宠；
//   3. 16ms 渲染循环（UpdatePose）协同：内嵌帧序列播放、像素级眼球追踪、
//      fadeOverlay 帧间交叉淡入淡出，以及呼吸/弹性着陆/拖拽倾斜等二次程序化动效。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("GPT 龙娘桌宠")]
[assembly: AssemblyDescription("ZipZipPipe 的 GPT 龙娘同人桌宠")]
[assembly: AssemblyVersion("2.5.0.0")]
[assembly: AssemblyFileVersion("2.5.0.0")]
[assembly: AssemblyInformationalVersion("2.5.0")]

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 通过命名 Mutex 确保单实例；若已有实例在运行，发送 EventWaitHandle 信号唤醒其显示。
        bool firstInstance;
        using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\GptDragonPetShow"))
        using (var mutex = new Mutex(true, "Local\\GptDragonPet", out firstInstance))
        {
            if (!firstInstance)
            {
                signal.Set();
                return;
            }

            try
            {
                var app = new Application();
                var window = new PetWindow(new PetSettingsStore(), true);
                var showWait = ThreadPool.RegisterWaitForSingleObject(signal,
                    (state, timedOut) => window.Dispatcher.BeginInvoke(new Action(window.ShowPet)), null, -1, false);
                try { app.Run(window); } finally { showWait.Unregister(null); }
            }
            catch (Exception error)
            {
                MessageBox.Show("桌宠启动失败。\n" + error.Message, "GPT 龙娘桌宠", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

internal sealed partial class PetWindow : Window
{
    private const int Small = 128;
    private const int Medium = 174;
    private const int Large = 238;
    private const double BubbleSpace = 43;
    private const double FadeDuration = 0.042;
    private const double EntryFadeDuration = 0.115;
    private const double CuriousHoverDwell = 0.8;
    private const double CuriousHoverExitDwell = 0.25;
    private const double CuriousHoverMargin = 10;
    private const double CuriousCooldown = 18;
    private const double SharedCuriousPoseGap = 1.5;
    private const string CodexWorkingMessage = "Codex 执行中，陪你一起思考…";

    private static readonly string[] Greetings = { "你好呀！", "我在这里陪你。", "今天也慢慢来。", "龙尾摇一摇！" };
    private static readonly string[] HeadPatLines = { "嘿嘿，龙角有点痒呀～", "摸摸头，充满电啦！", "嗯哼～好舒服呀。", "再多夸夸我嘛～" };
    private static readonly string[] CheerLines = { "好耶！星光送给你～", "被你逗笑啦，今天超开心！", "耶！我们一起加油！" };

    private readonly PetSettingsStore store;
    private readonly PetSettings settings;
    private readonly Grid layout;
    private readonly Image sprite;
    private readonly Image fadeOverlay;
    private readonly Border bubble;
    private readonly TextBlock message;
    private readonly Canvas effects = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly List<TextBlock> sparkles = new List<TextBlock>();
    private readonly BitmapSource idle = LoadFrame("idle");
    private readonly BitmapSource blink = LoadFrame("blink");
    private readonly BitmapSource wave = LoadFrame("wave");
    private readonly BitmapSource sleep = LoadFrame("sleep");
    private readonly PetAnimations animations = new PetAnimations(LoadFrame);
    private readonly EyeMotion eyes;
    private readonly ScaleTransform stretch = new ScaleTransform(1, 1);
    private readonly RotateTransform tilt = new RotateTransform();
    private readonly TranslateTransform bob = new TranslateTransform();
    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer codexTimer;
    private readonly DispatcherTimer companionTimer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Forms.NotifyIcon tray;
    private readonly Drawing.Icon trayIcon;
    private readonly GazeBehavior gazeBehavior = new GazeBehavior(new Random());
    private readonly Random actionRandom = new Random();

    private Point? mouseDown;
    private Vector dragOffset;
    private Point lastDragPointer;
    private Point rawDragPosition;
    private DockEdge attractingEdge;
    private Rect dragArea = Rect.Empty;
    private double dockedAt = -10;
    private double lastDockFrameAt = -1;
    private double dockMotion;
    private readonly RectangleGeometry dockCrop = new RectangleGeometry();
    private Rect dockViewport = Rect.Empty;
    private Rect nativeViewport = Rect.Empty;
    private Point dockPointer;
    private bool dockPointerKnown, dockHovered;
    private double dockOpenUntil;
    private double dockQuietUntil, dockHoverUntil;
    private double dockHoverEnteredAt = -1;
    private bool dockMenuOpen;
    private bool dragging;
    private double dragTilt;
    private readonly MotionSpring gazeX = new MotionSpring();
    private readonly MotionSpring gazeY = new MotionSpring();
    private double lastGazeAt = -1;
    private double lastPoseAt = -1;
    private double fadeStart = -10;
    private double activeFadeDuration = FadeDuration;
    private double landedAt = -10;
    private double hoveredAt = -10;
    private PetAction action;
    private double actionAt = -10;
    private double nextActionAt = 11;
    private double lastCuriousAt = -20;
    private double curiousHoverAt = -1;
    private double hoverOutsideAt = -1;
    private bool hoverNeedsExit;
    private int actionPreview;
    private DockEdge dock;
    private bool sleeping;
    private double greetingStart = -10;
    private double bubbleUntil;
    private int greetingIndex;
    private int patIndex;
    private int cheerIndex;
    private int tapStreak;
    private double lastTapAt = -10;
    private bool codexThinking;
    private bool chatThinking;
    private bool thinkingPosePending;
    private int noticeGeneration;
    private double lastHealthReminderAt;
    private bool startupGreeted;
    private bool codexAvailable;
    private readonly CodexBridge codex = new CodexBridge();
    private readonly ChatBridge chat;
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private Dictionary<string, CodexTask> lastTasks;
    private bool checkingTasks;
    private CodexPanel codexPanel;
    private ChatPanel chatPanel;
    private SettingsPanel settingsPanel;

    internal PetWindow(PetSettingsStore store, bool withTray)
    {
        this.store = store;
        settings = store.Load();
        if (withTray) settings.AutoStart = AutoStartManager.IsEnabled();
        codexAvailable = codex.HasLocalCodex();
        chat = new ChatBridge(codex);
        eyes = new EyeMotion(idle, blink);
        Enum.TryParse(settings.Dock, out dock);
        if (!settings.SnapEdges) dock = DockEdge.None;

        Title = "GPT 龙娘桌宠";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = settings.AlwaysOnTop;
        UseLayoutRounding = true;

        layout = new Grid { ClipToBounds = true };
        var spriteTransform = new TransformGroup { Children = { stretch, tilt, bob } };
        sprite = new Image
        {
            Source = idle,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.Hand,
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new Point(0.5, 0.90),
            Margin = new Thickness(0, 0, 0, 9),
            RenderTransform = spriteTransform
        };
        RenderOptions.SetBitmapScalingMode(sprite, BitmapScalingMode.HighQuality);
        layout.Children.Add(sprite);

        fadeOverlay = new Image
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new Point(0.5, 0.90),
            Margin = new Thickness(0, 0, 0, 9),
            RenderTransform = spriteTransform,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Opacity = 0
        };
        RenderOptions.SetBitmapScalingMode(fadeOverlay, BitmapScalingMode.HighQuality);
        layout.Children.Add(fadeOverlay);

        for (int i = 0; i < 3; i++)
        {
            var sparkle = new TextBlock
            {
                Text = i == 1 ? "✧" : "✦",
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = i == 1 ? 15 : 12,
                Foreground = i == 1 ? PetTheme.Brush(228, 183, 101) : PetTheme.Brush(154, 110, 211)
            };
            sparkles.Add(sparkle);
            effects.Children.Add(sparkle);
        }
        layout.Children.Add(effects);

        message = new TextBlock
        {
            FontFamily = PetTheme.UiFont,
            FontSize = 12.5,
            Foreground = PetTheme.Brush(58, 41, 92),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 172
        };
        bubble = new Border
        {
            Child = message,
            Background = PetTheme.BubbleGradient(),
            BorderBrush = PetTheme.Brush(193, 169, 230),
            BorderThickness = new Thickness(1.1),
            CornerRadius = new CornerRadius(15, 15, 15, 6),
            Padding = new Thickness(13, 6, 13, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 4, 4, 0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Effect = PetTheme.CardShadow(0.22)
        };
        layout.Children.Add(bubble);
        Content = layout;
        ApplyDockLayout();

        SetSize(settings.Size == Small || settings.Size == Large ? settings.Size : Medium, false);
        if (settings.HasPosition && IsFinite(settings.Left) && IsFinite(settings.Top))
        {
            Left = settings.Left;
            Top = settings.Top;
        }
        else ResetPosition();

        sprite.MouseLeftButtonDown += MousePressed;
        sprite.AddHandler(Mouse.MouseMoveEvent, new MouseEventHandler(MouseMoved), true);
        sprite.MouseLeftButtonUp += MouseReleased;
        sprite.LostMouseCapture += (sender, args) => FinishDrag();
        sprite.MouseEnter += (sender, args) =>
        {
            if (dragging || sleeping) return;
            BeginHover(clock.Elapsed.TotalSeconds, CursorInWindow());
        };
        sprite.MouseRightButtonUp += (sender, args) => ShowMenu();
        Loaded += (sender, args) => ClampToScreen();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;

        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (sender, args) => UpdatePose();
        IsVisibleChanged += (sender, args) => { if (IsVisible) timer.Start(); else timer.Stop(); };

        codexTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        codexTimer.Tick += (sender, args) => PollCodex();
        Loaded += (sender, args) =>
        {
            DetectCodexAsync();
            if (settings.CodexNotices && ShouldMonitorCodex())
            {
                codexTimer.Start();
                PollCodex();
            }
        };

        lastHealthReminderAt = clock.Elapsed.TotalSeconds;
        companionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        companionTimer.Tick += (sender, args) => TickCompanion();
        if (withTray)
        {
            Loaded += (sender, args) =>
            {
                companionTimer.Start();
                ScheduleStartupGreeting();
            };
        }

        if (withTray)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("pet.ico"))
            using (var loadedIcon = new Drawing.Icon(stream)) trayIcon = (Drawing.Icon)loadedIcon.Clone();
            tray = new Forms.NotifyIcon { Text = Title, Icon = trayIcon };
            var menu = new Forms.ContextMenuStrip();
            menu.Opening += (sender, args) => { dockMenuOpen = true; PopulateTrayMenu(menu); };
            menu.Closed += (sender, args) => CloseDockMenu();
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (sender, args) => OpenDockTemporarily();
            tray.Visible = true;
        }
    }

    internal PetSettings SettingsSnapshot { get { return settings; } }
    internal bool CodexAvailable { get { return codexAvailable; } }
    internal bool ShouldShowCodex() { return codexAvailable || settings.ShowCodexFeatures; }
    private bool ShouldMonitorCodex() { return codexAvailable || settings.ShowCodexFeatures; }

    private void DetectCodexAsync()
    {
        Task.Run(() =>
        {
            try { return !String.IsNullOrEmpty(codex.LocateExecutable(lifetime.Token)); }
            catch (OperationCanceledException) { return false; }
        }).ContinueWith(t =>
        {
            if (lifetime.IsCancellationRequested) return;
            if (!t.IsFaulted && t.Result && !codexAvailable)
            {
                codexAvailable = true;
                if (settings.CodexNotices && !codexTimer.IsEnabled)
                {
                    codexTimer.Start();
                    PollCodex();
                }
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ScheduleStartupGreeting()
    {
        if (startupGreeted || !settings.TimeGreetings) return;
        var once = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2.4) };
        once.Tick += (sender, args) =>
        {
            once.Stop();
            if (startupGreeted || lifetime.IsCancellationRequested || !settings.TimeGreetings || !IsVisible) return;
            startupGreeted = true;
            double now = clock.Elapsed.TotalSeconds;
            if (sleeping || dragging || dockMotion > 0 || action != PetAction.None || GreetingPending(now) || now < bubbleUntil) return;
            message.Text = TimePeriodGreeting();
            bubbleUntil = now + 3.6;
            UpdatePose();
        };
        once.Start();
    }

    private void DisplaySettingsChanged(object sender, EventArgs args)
    {
        Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && !dragging) ClampToScreen(); }));
    }

    private static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

    private static BitmapSource LoadFrame(string name)
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name + ".png"))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
    }

    private Point CursorInDips()
    {
        var pointer = PetMotion.CursorPosition();
        var target = PresentationSource.FromVisual(this);
        return target == null || target.CompositionTarget == null ? pointer : target.CompositionTarget.TransformFromDevice.Transform(pointer);
    }

    private Point CursorInWindow()
    {
        var pointer = CursorInDips();
        var origin = WindowOrigin();
        return new Point(pointer.X - origin.X, pointer.Y - origin.Y);
    }

    private Point WindowOrigin()
    {
        var source = PresentationSource.FromVisual(this) as HwndSource;
        if (source == null || source.CompositionTarget == null) return new Point(Left, Top);
        var origin = PetMotion.NativeWindowOrigin(source.Handle);
        return origin.HasValue ? source.CompositionTarget.TransformFromDevice.Transform(origin.Value) : new Point(Left, Top);
    }

    private void MoveWindow(Point position)
    {
        var source = PresentationSource.FromVisual(this) as HwndSource;
        if (source != null && source.CompositionTarget != null &&
            PetMotion.MoveWindow(source.Handle, source.CompositionTarget.TransformToDevice.Transform(position))) return;
        Left = position.X;
        Top = position.Y;
    }

    private void UpdatePose()
    {
        double time = clock.Elapsed.TotalSeconds;
        if (hoverNeedsExit) UpdateHoverDeparture(time, CursorInWindow());
        else if (sprite.IsMouseOver) BeginHover(time, CursorInWindow());
        if (dockMotion > 0 && settings.Animate)
        {
            double expandedAt = time + dockMotion;
            if (GreetingPending(time) && greetingStart < expandedAt)
            {
                double wait = expandedAt - greetingStart;
                greetingStart = expandedAt;
                bubbleUntil += wait;
                nextActionAt += wait;
            }
            else if (action != PetAction.None && actionAt < expandedAt)
            {
                nextActionAt += expandedAt - actionAt;
                actionAt = expandedAt;
            }
        }
        var actionClip = animations.For(action);
        if (actionClip != null && time >= actionAt + actionClip.Duration) action = PetAction.None;
        TryIdleAction(time);
        actionClip = animations.For(action);
        double waveTime = time - greetingStart;
        bool waving = waveTime >= 0 && GreetingPending(time);
        AnimationClip clip = waving && settings.Animate ? animations.Greeting : null;
        double elapsed = waving ? waveTime : 0;
        if (settings.Animate && !dragging && !sleeping && !waving && actionClip != null && time >= actionAt)
        {
            clip = actionClip;
            elapsed = time - actionAt;
        }
        bool busy = action != PetAction.None || waving || clip != null || GreetingPending(time);
        UpdateDockMotion(time, busy);
        Vector dockShift;
        BitmapSource dockPose = PresentDock(time, clip == null && !waving, out dockShift);
        double poseDelta = lastPoseAt < 0 ? 0.016 : Math.Max(0.001, Math.Min(0.065, time - lastPoseAt));
        lastPoseAt = time;
        BitmapSource pose = dockPose ?? (sleeping ? sleep : clip != null ? clip.At(elapsed) : waving ? wave : eyes.Frame);
        if (pose == eyes.Frame) TrackGaze(time);
        // 动作帧切换时启用短暂的旧帧叠加淡出，消除逐帧动画切换的闪烁感。
        if (sprite.Source != pose)
        {
            if (settings.Animate && !dragging && dockMotion == 0 && sprite.Source != null && pose != null)
            {
                bool idleBoundary = sprite.Source == eyes.Frame || pose == eyes.Frame;
                activeFadeDuration = idleBoundary ? EntryFadeDuration : FadeDuration;
                fadeOverlay.Source = sprite.Source;
                fadeStart = time;
            }
            sprite.Source = pose;
        }
        UpdateCrossfade(time);
        bool transientBubble = time < bubbleUntil;
        if (dockMotion == 0 && (transientBubble || codexThinking))
        {
            if (!transientBubble) message.Text = CodexWorkingMessage;
            bubble.Opacity = transientBubble ? Math.Min(1, (bubbleUntil - time) / 0.25) : 1.0;
            bubble.Visibility = Visibility.Visible;
        }
        else
        {
            bubble.Visibility = Visibility.Collapsed;
            bubble.Opacity = 1.0;
        }
        double sparkle = dockMotion == 0 && clip != null && action == PetAction.Celebrate && !waving ? Math.Sin(Math.PI * elapsed / clip.Duration) : 0;
        ShowSparkles(sparkle);
        if (settings.Animate)
        {
            double sinceLanding = time - landedAt;
            double sinceHover = time - hoveredAt;
            double rebound = sinceLanding < 0.65 ? Math.Sin(sinceLanding * 17) * Math.Exp(-sinceLanding * 6) : 0;
            double perk = sinceHover < 0.42 ? Math.Sin(sinceHover / 0.42 * Math.PI) : 0;
            double tiltBlend = 1 - Math.Exp(-10.0 * poseDelta);
            dragTilt *= Math.Exp(-7.8 * poseDelta);

            DockEdge anchor = dragging ? attractingEdge : dock;
            bool verticalAnchor = anchor == DockEdge.Top || anchor == DockEdge.Bottom;
            bool horizontalAnchor = anchor == DockEdge.Left || anchor == DockEdge.Right;

            double actBobY = 0, actAngle = 0, actScaleX = 1, actScaleY = 1;
            if (clip != null && dockPose == null && clip.Duration > 0)
            {
                double progress = Math.Max(0, Math.Min(1, elapsed / clip.Duration));
                double env = Math.Sin(Math.PI * progress);
                if (waving)
                {
                    actBobY = -2.8 * Math.Abs(Math.Sin(progress * Math.PI * 3.0)) * env;
                    actAngle = 2.1 * Math.Sin(progress * Math.PI * 4.0) * env;
                    double breath = Math.Sin(progress * Math.PI * 2.0) * env;
                    actScaleY = 1 + 0.022 * breath;
                    actScaleX = 1 - 0.015 * breath;
                }
                else if (action == PetAction.Stretch)
                {
                    double reach = Math.Sin(Math.PI * Math.Pow(progress, 0.85));
                    actBobY = -3.4 * reach;
                    actScaleY = 1 + 0.052 * reach;
                    actScaleX = 1 - 0.032 * reach;
                    actAngle = -1.6 * env;
                }
                else if (action == PetAction.Curious || action == PetAction.Thinking)
                {
                    double lean = env * env;
                    actBobY = -1.0 * env;
                    actAngle = 1.4 * lean;
                    actScaleY = 1 + 0.024 * lean;
                }
                else if (action == PetAction.Celebrate)
                {
                    double hop = Math.Abs(Math.Sin(progress * Math.PI * 2.0));
                    double squash = Math.Sin(progress * Math.PI * 4.0) * env;
                    actBobY = -6.5 * hop * env;
                    actAngle = 2.8 * Math.Sin(progress * Math.PI * 4.0) * env;
                    actScaleX = 1 + 0.038 * squash;
                    actScaleY = 1 - 0.038 * squash;
                }
                if (verticalAnchor) { actBobY = 0; actScaleY = 1; }
                if (horizontalAnchor) { actScaleX = 1; actAngle *= 0.45; }
            }

            bob.X = dockShift.X;
            if (dockPose != null)
            {
                bool settledPeek = PetMotion.IsPeekPhase(dockMotion) && dockMotion >= PetMotion.DockMotionSeconds - 0.01;
                if (settledPeek)
                {
                    double breathe = Math.Sin(time * 1.9);
                    double peekNudge = 1.6 * (0.5 + 0.5 * breathe);
                    bob.X = dockShift.X + (dock == DockEdge.Left ? peekNudge : dock == DockEdge.Right ? -peekNudge : 0);
                    bob.Y = dockShift.Y + (dock == DockEdge.Top ? peekNudge : dock == DockEdge.Bottom ? -peekNudge : 0.9 * Math.Sin(time * 1.35));
                    tilt.Angle = (dock == DockEdge.Left ? 1.4 : dock == DockEdge.Right ? -1.4 : 0) * Math.Sin(time * 1.15);
                }
                else
                {
                    bob.Y = dockShift.Y;
                    tilt.Angle = 0;
                }
                stretch.ScaleX = 1;
                stretch.ScaleY = 1;
            }
            else if (clip != null)
            {
                bob.Y = dockShift.Y + actBobY;
                tilt.Angle += (actAngle - tilt.Angle) * tiltBlend;
                stretch.ScaleX = actScaleX;
                stretch.ScaleY = actScaleY;
            }
            else
            {
                bob.Y = verticalAnchor ? dockShift.Y : (dragging ? -3 : (sleeping ? 0.65 : 1.4) * Math.Sin(time * 1.68) - 3 * perk + 3 * rebound);
                double angle = anchor != DockEdge.None ? 0 : dragging ? dragTilt : sleeping ? 0 : gazeX.Value * 1.85;
                tilt.Angle += (angle - tilt.Angle) * tiltBlend;
                stretch.ScaleX = horizontalAnchor ? 1 : 1 + 0.09 * rebound - (dragging && anchor == DockEdge.None ? 0.03 : 0);
                stretch.ScaleY = verticalAnchor ? 1 : 1 - 0.10 * rebound + (dragging && anchor == DockEdge.None ? 0.035 : 0);
            }
        }
        else
        {
            bob.X = dockShift.X;
            bob.Y = dockShift.Y;
            tilt.Angle = 0;
            stretch.ScaleX = stretch.ScaleY = 1;
        }
    }

    private void UpdateCrossfade(double time)
    {
        if (!settings.Animate || dragging || dockMotion > 0 || fadeOverlay.Source == null)
        {
            fadeOverlay.Visibility = Visibility.Collapsed;
            fadeOverlay.Source = null;
            return;
        }
        double age = time - fadeStart;
        if (age < 0 || age >= activeFadeDuration)
        {
            fadeOverlay.Visibility = Visibility.Collapsed;
            fadeOverlay.Source = null;
            return;
        }
        double t = age / activeFadeDuration;
        double smooth = t * t * (3.0 - 2.0 * t);
        fadeOverlay.Opacity = 1.0 - smooth;
        fadeOverlay.Visibility = Visibility.Visible;
    }

    private void ShowSparkles(double amount)
    {
        effects.Visibility = amount > 0.025 ? Visibility.Visible : Visibility.Collapsed;
        if (effects.Visibility == Visibility.Collapsed) return;
        effects.Opacity = amount * 0.9;
        double y = VerticalOffset(dock) + sprite.Height * 0.12;
        for (int i = 0; i < sparkles.Count; i++)
        {
            Canvas.SetLeft(sparkles[i], Width * (0.17 + i * 0.31) + (i - 1) * 5 * amount);
            Canvas.SetTop(sparkles[i], y + (i == 1 ? -19 : 4) - 8 * amount);
        }
    }

    private void TrackGaze(double time)
    {
        var aim = new Vector();
        double focus = 0;
        if (settings.FollowMouse && settings.Animate && IsVisible && PresentationSource.FromVisual(sprite) != null)
        {
            var pointer = PetMotion.CursorPosition();
            var point = sprite.PointFromScreen(pointer);
            var displacement = new Vector(point.X - sprite.Width * 0.49, point.Y - sprite.Height * 0.37);
            aim = gazeBehavior.Aim(displacement, pointer, sprite.Height, time);
            focus = Math.Max(0, 1 - displacement.Length / (sprite.Height * 0.72));
        }
        double delta = lastGazeAt < 0 ? 0.032 : Math.Max(0.001, Math.Min(0.065, time - lastGazeAt));
        lastGazeAt = time;
        gazeX.Advance(aim.X, delta);
        gazeY.Advance(aim.Y, delta);
        double lids = settings.Animate ? gazeBehavior.Lids(time) : 0;
        eyes.SetDirection(gazeX.Value, gazeY.Value, lids, focus);
    }

    internal void ApplySize(int size) { SetSize(size == Small || size == Large ? size : Medium, true); }

    private void SetSize(int size, bool preserveFoot = true)
    {
        double previousHeight = Height;
        double previousWidth = Width;
        settings.Size = size;
        sprite.Height = size;
        sprite.Width = size * (double)idle.PixelWidth / idle.PixelHeight;
        fadeOverlay.Height = sprite.Height;
        fadeOverlay.Width = sprite.Width;
        Height = size + BubbleSpace + 9;
        Width = Math.Max(142, sprite.Width + 16);
        if (preserveFoot)
        {
            Left += (previousWidth - Width) / 2;
            Top += previousHeight - Height;
            ClampToScreen();
            SaveSettings();
        }
    }

    internal void ResetPetPosition()
    {
        ResetPosition();
        SaveSettings();
        ShowPet();
    }

    private void ResetPosition()
    {
        dock = DockEdge.None;
        ApplyDockLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 6;
    }

    private Rect CurrentWorkArea(Point? pointer = null)
    {
        var source = PresentationSource.FromVisual(this) as HwndSource;
        if (source == null || source.CompositionTarget == null) return SystemParameters.WorkArea;
        var toDevice = source.CompositionTarget.TransformToDevice;
        var area = PetMotion.MonitorArea(source.Handle, pointer.HasValue ? (Point?)toDevice.Transform(pointer.Value) : null, true);
        if (!area.HasValue) return SystemParameters.WorkArea;
        var fromDevice = source.CompositionTarget.TransformFromDevice;
        return new Rect(fromDevice.Transform(area.Value.TopLeft), fromDevice.Transform(area.Value.BottomRight));
    }

    private static double VerticalOffset(DockEdge edge) { return edge == DockEdge.Top ? 9 : BubbleSpace; }

    private Rect Silhouette(DockEdge edge)
    {
        var art = eyes.OpaqueBounds;
        double scale = sprite.Height / idle.PixelHeight;
        return new Rect((Width - sprite.Width) / 2 + art.X * scale, VerticalOffset(edge) + art.Y * scale,
            art.Width * scale, art.Height * scale);
    }

    private void ApplyDockLayout()
    {
        bool top = dock == DockEdge.Top;
        sprite.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        sprite.Margin = top ? new Thickness(0, 9, 0, 0) : new Thickness(0, 0, 0, 9);
        fadeOverlay.VerticalAlignment = sprite.VerticalAlignment;
        fadeOverlay.Margin = sprite.Margin;
        bubble.VerticalAlignment = top ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        bubble.Margin = top ? new Thickness(2, 0, 2, 5) : new Thickness(2, 5, 2, 0);
    }

    private void ClampToScreen() { PlaceInArea(CurrentWorkArea()); }
    private void PlaceInArea(Rect area) { PlaceInArea(area, new Point(Left, Top)); }

    private void PlaceInArea(Rect area, Point requested)
    {
        var position = PetMotion.Place(area, new Rect(requested, new Size(Width, Height)), Silhouette(dock), dock);
        MoveWindow(position);
    }

    internal void ShowPet()
    {
        if (!IsVisible) Show();
        ClampToScreen();
        UpdatePose();
    }

    internal void SaveSettings()
    {
        settings.HasPosition = true;
        var origin = WindowOrigin();
        settings.Left = origin.X;
        settings.Top = origin.Y;
        settings.Dock = dock.ToString();
        store.Save(settings);
    }

    internal void SetAlwaysOnTop(bool enabled) { Topmost = settings.AlwaysOnTop = enabled; SaveSettings(); }
    internal void SetAnimate(bool enabled) { settings.Animate = enabled; UpdatePose(); SaveSettings(); }
    internal void SetFollowMouse(bool enabled) { settings.FollowMouse = enabled; SaveSettings(); }
    internal void SetSnapEdges(bool enabled)
    {
        settings.SnapEdges = enabled;
        if (!settings.SnapEdges) { dock = DockEdge.None; ApplyDockLayout(); }
        SaveSettings();
    }
    internal void SetCompactDock(bool enabled) { settings.CompactDock = enabled; SaveSettings(); }
    internal void SetIdleActions(bool enabled)
    {
        if (settings.IdleActions == enabled) return;
        ToggleIdleActions();
    }
    internal void SetTimeGreetings(bool enabled) { settings.TimeGreetings = enabled; SaveSettings(); }
    internal void SetHealthReminder(bool enabled, int minutes)
    {
        settings.HealthReminder = enabled;
        if (minutes == 30 || minutes == 45 || minutes == 60) settings.HealthReminderMinutes = minutes;
        lastHealthReminderAt = clock.Elapsed.TotalSeconds;
        SaveSettings();
    }
    internal bool SetAutoStart(bool enabled)
    {
        bool ok = AutoStartManager.SetEnabled(enabled);
        settings.AutoStart = ok ? enabled : AutoStartManager.IsEnabled();
        SaveSettings();
        return ok;
    }
    internal void SetShowCodexFeatures(bool enabled)
    {
        settings.ShowCodexFeatures = enabled;
        if (enabled && settings.CodexNotices && !codexTimer.IsEnabled)
        {
            codexTimer.Start();
            PollCodex();
        }
        SaveSettings();
    }

    protected override void OnClosed(EventArgs args)
    {
        timer.Stop();
        codexTimer.Stop();
        companionTimer.Stop();
        lifetime.Cancel();
        if (codexPanel != null) codexPanel.Close();
        if (chatPanel != null) chatPanel.Close();
        if (settingsPanel != null) settingsPanel.Close();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        SaveSettings();
        if (tray != null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip.Dispose();
            tray.Dispose();
            trayIcon.Dispose();
        }
        base.OnClosed(args);
    }
}
