using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

internal static class PetChecks
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        var directory = args[0];
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.xml");
        if (File.Exists(path)) File.Delete(path);
        var store = new PetSettingsStore(path);
        store.Save(new PetSettings { CodexNotices = false, ShowCodexFeatures = true });
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        PetWindow window = null;
        try
        {
            window = new PetWindow(store, true);
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(window.IsVisible && window.AllowsTransparency && window.Topmost && !window.ShowInTaskbar, "Transparent dashboard opens");
            Check(new WindowInteropHelper(window).Handle != IntPtr.Zero, "Native window handle is live");
            var sprite = (Image)Field(window, "sprite");
            var bubble = (Border)Field(window, "bubble");
            Call(window, "UpdatePose");
            var eyes = (EyeMotion)Field(window, "eyes");
            Check(sprite.Source == eyes.Frame && bubble.Visibility == Visibility.Collapsed, "Idle sprite and hidden bubble");
            double firstIdle = (double)Field(window, "nextActionAt");
            Check(firstIdle >= 10 && firstIdle <= 12, "First automatic idle enters the requested ten-to-twelve second window");
            CheckEyeMotion(eyes, directory);
            CheckSnapping(window);
            CheckPeek(window);
            CheckActions(window);
            CheckIdleSchedule(window);
            CheckHoverAndThinking(window);
            if (args.Contains("--native")) CheckNativeDragging(window);

            var origin = (Point)typeof(PetWindow).GetMethod("WindowOrigin", Members).Invoke(window, null);
            var body = (Rect)typeof(PetWindow).GetMethod("Silhouette", Members).Invoke(window, new object[] { DockEdge.None });
            Call(window, "InteractAt", origin + new Vector(body.Left + body.Width * 0.5, body.Top + body.Height * 0.6));
            var animations = (PetAnimations)Field(window, "animations");
            Check(sprite.Source == animations.Greeting.At(0) && bubble.Visibility == Visibility.Visible, "Click starts the authored wave sequence and shows a greeting");
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render((Grid)window.Content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(directory, "wave-preview.png"))) encoder.Save(stream);

            var tray = (Forms.NotifyIcon)Field(window, "tray");
            var menu = tray.ContextMenuStrip;
            Check(tray.Visible && tray.Icon != null, "Notification area icon initialized");
            Click(window, menu, "让她睡觉");
            Check(sprite.Source == Field(window, "sleep"), "Sleep action switches the sprite");
            Click(window, menu, "叫醒她");
            Check(sprite.Source == eyes.Frame, "Wake action restores the idle sprite");
            CheckIdleDelay(window, 10, 15, "Waking schedules a fresh idle action");
            Click(window, menu, "大号");
            Check(sprite.Height == 238, "Large size action resizes the sprite");
            Click(window, menu, "显示在最前");
            Check(!window.Topmost, "Always-on-top toggles off");
            Click(window, menu, "桌宠动效");
            Check(CallExists(window, menu, "Codex 任务") && CallExists(window, menu, "Codex 额度"), "Codex commands are in the tray menu");
            CheckNotices(window, menu, directory);
            Click(window, menu, "隐藏到托盘");
            Check(!window.IsVisible, "Hide action conceals the pet window");
            Click(window, menu, "显示桌宠");
            Check(window.IsVisible, "Show action restores the pet window");
            Call(window, "ShowMenu");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Click(window, menu, "回到右下角");
            Check(window.Left >= SystemParameters.WorkArea.Left && window.Top + window.Height <= SystemParameters.WorkArea.Bottom + 1, "Reset brings the window onto the work area");
            Click(window, menu, "退出桌宠");
            Check(!window.IsVisible, "Exit closes the window");
            var settings = store.Load();
            Check(settings.Size == 238 && !settings.AlwaysOnTop && !settings.Animate && settings.HasPosition, "Preferences round-trip to disk");
            window = null;
            CheckMassFeatures(directory);
            CheckLocalLogs(directory);
            var liveThread = args.Skip(1).FirstOrDefault(arg => arg != "--native");
            if (liveThread != null) CheckLiveCodex(liveThread, directory);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (window != null && window.IsVisible) window.Close(); app.Shutdown(); }
    }

    private static void Click(PetWindow window, Forms.ContextMenuStrip menu, string text)
    {
        Call(window, "PopulateTrayMenu", menu);
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            if (item.Text != text) continue;
            item.PerformClick();
            return;
        }
        throw new InvalidOperationException("Missing menu action: " + text);
    }

    private static bool CallExists(PetWindow window, Forms.ContextMenuStrip menu, string text)
    {
        Call(window, "PopulateTrayMenu", menu);
        return menu.Items.Cast<Forms.ToolStripItem>().Any(item => item.Text == text);
    }

    private static void CheckEyeMotion(EyeMotion eyes, string directory)
    {
        int stride = eyes.Frame.PixelWidth * 4;
        var left = new byte[stride * eyes.Frame.PixelHeight];
        var right = new byte[left.Length];
        eyes.SetDirection(-1, 0);
        eyes.Frame.CopyPixels(left, stride, 0);
        SaveBitmap(eyes.Frame, Path.Combine(directory, "look-left.png"));
        eyes.SetDirection(1, 0);
        eyes.Frame.CopyPixels(right, stride, 0);
        SaveBitmap(eyes.Frame, Path.Combine(directory, "look-right.png"));
        int changed = 0;
        for (int index = 0; index < left.Length; index += 4)
        {
            if (left[index] == right[index] && left[index + 1] == right[index + 1] && left[index + 2] == right[index + 2]) continue;
            int x = index / 4 % eyes.Frame.PixelWidth;
            int y = index / stride;
            if (!((x >= 184 && x <= 212) || (x >= 262 && x <= 290)) || y < 204 || y > 243)
                throw new InvalidOperationException("Extreme gaze moved the iris rim or the surrounding eye white.");
            changed++;
        }
        Check(changed > 300, "Extreme gaze moves both pupils and preserves pixels outside the iris interior");
        var spring = new MotionSpring();
        spring.Advance(1, 0.032);
        Check(spring.Value > 0 && spring.Value < 0.5, "Gaze spring approaches the pointer smoothly");
        for (int i = 0; i < 20; i++) spring.Advance(1, 0.032);
        Check(spring.Value > 0.999 && spring.Value <= 1, "Gaze spring settles on the target");
        var gaze = new GazeBehavior(new Random(1));
        var target = gaze.Aim(new Vector(200, 5), new Point(800, 500), 174, 0);
        var resting = gaze.Aim(new Vector(200, 5), new Point(800, 500), 174, 2.6);
        var redirected = gaze.Aim(new Vector(-200, 0), new Point(400, 500), 174, 2.65);
        Check(target.X > 0.95 && resting.X < 0.8 && redirected.X < -0.95, "Gaze relaxes on a still pointer and follows a new direction immediately");
        Check(GazeBehavior.Closure(0.04) > 0 && GazeBehavior.Closure(0.04) < 1 && GazeBehavior.Closure(0.08) == 1 &&
            GazeBehavior.Closure(0.18) > 0 && GazeBehavior.Closure(0.18) < 1 && GazeBehavior.Closure(0.24) == 0, "Blink has distinct closing, hold, opening, and idle phases");
        eyes.SetDirection(0, 0, 0.5);
        SaveBitmap(eyes.Frame, Path.Combine(directory, "mid-blink.png"));
        eyes.SetDirection(0, 0, 1);
        SaveBitmap(eyes.Frame, Path.Combine(directory, "closed-lids.png"));
    }

    private static void CheckSnapping(PetWindow window)
    {
        var area = SystemParameters.WorkArea;
        var points = new[] { new Point(area.Left + 12, area.Top + 120), new Point(area.Left + 100, area.Top + 12),
            new Point(area.Right - window.Width - 12, area.Top + 120), new Point(area.Left + 150, area.Bottom - window.Height - 12) };
        var edges = new[] { DockEdge.Left, DockEdge.Top, DockEdge.Right, DockEdge.Bottom };
        for (int i = 0; i < edges.Length; i++)
        {
            window.Left = points[i].X;
            window.Top = points[i].Y;
            Call(window, "DockInArea", area, points[i], null);
            var edge = (DockEdge)Field(window, "dock");
            Check(edge == edges[i], "Window snaps to " + edges[i]);
            var visual = (Rect)typeof(PetWindow).GetMethod("Silhouette", Members).Invoke(window, new object[] { edge });
            visual.Offset(window.Left, window.Top);
            double gap = i == 0 ? visual.Left - area.Left : i == 1 ? visual.Top - area.Top :
                i == 2 ? area.Right - visual.Right : area.Bottom - visual.Bottom;
            Check(Math.Abs(gap - PetMotion.EdgePadding) < 1, "Visible character sits within three DIPs of the work area");
        }
        Call(window, "ResetPosition");
        var field = typeof(PetWindow).GetField("mouseDown", Members);
        field.SetValue(window, new Point(window.Left + 50, window.Top + 70));
        typeof(PetWindow).GetField("dragOffset", Members).SetValue(window, new Vector(50, 70));
        Call(window, "MovePetFromPointer", new Point(area.Left + 68, area.Top + 220));
        Check((DockEdge)Field(window, "attractingEdge") == DockEdge.Left && window.Left < area.Left + 18, "Approaching a work area edge pulls the pet in before release");
        Call(window, "FinishDrag");
        Check((DockEdge)Field(window, "dock") == DockEdge.Left && window.Left < area.Left, "Pointer drag and release docks the silhouette and leaves the transparent padding past the edge");
        var wellInside = new Rect(area.Left + 80, area.Top + 155, 120, 120);
        Check(PetMotion.NearestEdge(area, wellInside, new Point(area.Left + 20, area.Top + 200), DockEdge.None) == DockEdge.Left,
            "Pointer close to a side is recognized even when the visible art has not reached it");
        var secondary = new Rect(-1280, 160, 1280, 860);
        var shape = new Rect(20, 45, 120, 165);
        var first = PetMotion.Preview(secondary, new Rect(-1310, 400, 160, 230), shape, DockEdge.Left);
        var fartherOutside = PetMotion.Preview(secondary, new Rect(-1440, 400, 160, 230), shape, DockEdge.Left);
        Check(first == fartherOutside && Math.Abs(first.X + shape.Left - (secondary.Left + PetMotion.EdgePadding)) < 0.001,
            "Crossing a negative-coordinate monitor's edge lands once and stops without pushing the pet away");
        var atCorner = new Rect(secondary.Left + 30, secondary.Top + 1, 120, 165);
        Check(PetMotion.NearestEdge(secondary, atCorner, atCorner.TopLeft, DockEdge.Left) == DockEdge.Left,
            "An already latched edge stays selected at the adjacent corner");
    }

    private static void CheckMassFeatures(string directory)
    {
        var roundTripPath = Path.Combine(directory, "mass-settings.xml");
        if (File.Exists(roundTripPath)) File.Delete(roundTripPath);
        var massStore = new PetSettingsStore(roundTripPath);
        massStore.Save(new PetSettings
        {
            AutoStart = true,
            TimeGreetings = false,
            HealthReminder = true,
            HealthReminderMinutes = 60,
            ShowCodexFeatures = false,
            CodexNotices = false
        });
        var loaded = massStore.Load();
        Check(loaded.AutoStart && !loaded.TimeGreetings && loaded.HealthReminder && loaded.HealthReminderMinutes == 60 && !loaded.ShowCodexFeatures,
            "2.5.0 companion preferences round-trip cleanly");

        Check(ChatBridge.Presets.Length >= 5 &&
              ChatBridge.FindPreset("siliconflow") != null &&
              ChatBridge.FindPreset("zhipu") != null &&
              ChatBridge.FindPreset("deepseek") != null &&
              ChatBridge.FindPreset("gemini") != null,
            "Accessible API presets include SiliconFlow DeepSeek, Zhipu Free, DeepSeek, and Gemini");

        var noCodexBridge = new CodexBridge(Path.Combine(directory, "empty-codex"));
        var chat = new ChatBridge(noCodexBridge, Path.Combine(directory, "chat-data"));
        var localModels = chat.DiscoverLocalModels(CancellationToken.None);
        Check(localModels != null, "Local model discovery handles offline or absent services gracefully");

        var testWindow = new PetWindow(massStore, true);
        Set(testWindow, "codexAvailable", false);
        testWindow.Show();
        testWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var tray = (Forms.NotifyIcon)Field(testWindow, "tray");
        Check(!CallExists(testWindow, tray.ContextMenuStrip, "Codex 任务") && !CallExists(testWindow, tray.ContextMenuStrip, "Codex 额度"),
            "Codex menu items stay hidden when Codex is absent for general users");

        testWindow.OpenSettingsPanel();
        var panel = (SettingsPanel)Field(testWindow, "settingsPanel");
        Check(panel != null && panel.IsVisible, "Visual Settings Center opens smoothly");
        testWindow.ApplySize(238);
        testWindow.SetHealthReminder(true, 30);
        Check(testWindow.SettingsSnapshot.Size == 238 && testWindow.SettingsSnapshot.HealthReminder && testWindow.SettingsSnapshot.HealthReminderMinutes == 30,
            "Settings Center actions update pet size and health reminder state");
        testWindow.Close();
    }

    private static void CheckLocalLogs(string directory)
    {
        var home = Path.Combine(directory, "codex-fixture");
        var day = Path.Combine(home, "sessions", DateTime.Today.ToString("yyyy"), DateTime.Today.ToString("MM"), DateTime.Today.ToString("dd"));
        Directory.CreateDirectory(day);
        var log = Path.Combine(day, "rollout-test-15c33866-e4ae-48b2-8dbb-712342159b8d.jsonl");
        var bridge = new CodexBridge(home);
        var now = DateTime.UtcNow.AddSeconds(-2);
        File.WriteAllText(log, Event(now, "task_started"));
        Check(bridge.ReadLocalTasks(CancellationToken.None).Single().Status == CodexTaskStatus.Running, "Recent task_started is shown as running");
        File.AppendAllText(log, Event(now.AddSeconds(1), "task_complete"));
        Check(bridge.ReadLocalTasks(CancellationToken.None).Single().Status == CodexTaskStatus.Completed, "A newly appended task_complete marks it done");
        File.AppendAllText(log, Event(now.AddSeconds(2), "turn_aborted"));
        Check(bridge.ReadLocalTasks(CancellationToken.None).Single().Status == CodexTaskStatus.Interrupted, "A turn_aborted marks an interruption");
    }

    private static void CheckPeek(PetWindow window)
    {
        var settings = (PetSettings)Field(window, "settings");
        settings.CompactDock = true;
        var clock = (System.Diagnostics.Stopwatch)Field(window, "clock");
        Set(window, "dock", DockEdge.Left);
        Call(window, "ApplyDockLayout");
        Call(window, "ClampToScreen");
        double now = clock.Elapsed.TotalSeconds;
        Set(window, "dockedAt", now - 0.3);
        var animations = (PetAnimations)Field(window, "animations");
        var render = typeof(PetWindow).GetMethod("PresentDock", Members);
        var request = new object[] { now, true, new Vector() };
        Check(render.Invoke(window, request) == animations.AtEdge(DockEdge.Left).At(0.3), "Docking renders the authored reaching-hand sequence");
        Set(window, "curiousHoverAt", -1.0);
        Call(window, "UpdateDockMotion", now, false);
        Check((double)Field(window, "dockMotion") == 0, "The dock entrance plays in full before any slide begins");

        Set(window, "dockedAt", -10.0);
        var layout = (Grid)window.Content;
        Set(window, "dockMotion", 0.22);
        Check(render.Invoke(window, request) == animations.AtEdge(DockEdge.Left).At(animations.AtEdge(DockEdge.Left).Duration),
            "The sliding body holds the final braced pose");
        Rect firstMask = ((RectangleGeometry)layout.Clip).Rect;
        Set(window, "dockMotion", 0.5);
        render.Invoke(window, request);
        Check(((RectangleGeometry)layout.Clip).Rect == firstMask && firstMask.Width > window.Width * 0.75,
            "The entire body slide shares a fixed physical-edge mask with no contracting paint rectangle");
        Set(window, "dockMotion", PetMotion.BodySlideSeconds);
        Check(render.Invoke(window, request) == animations.PeekAt(DockEdge.Left).Image && ((Rect)Field(window, "dockViewport")).IsEmpty,
            "The peek image replaces the body only while fully beyond the monitor line");
        Set(window, "dockMotion", PetMotion.DockMotionSeconds);
        Check(render.Invoke(window, request) == animations.PeekAt(DockEdge.Left).Image, "Compact side dock uses a dedicated one-eye edge sprite");
        var compact = (Rect)Field(window, "dockViewport");
        Check(compact.Width <= window.Width * 0.68 && compact.Height < window.Height * 0.76, "A fuller peek fragment sits comfortably at the screen edge");
        Check(((RectangleGeometry)layout.Clip).Rect == firstMask, "Only the native mouse region shrinks during peek playback");
        var seamGuard = PetMotion.WorkAreaInWindow(new Size(180, 240), new Point(850, 100), new Rect(-1000, 0, 2000, 900));
        Check(seamGuard.Right == 150 && seamGuard.Width == 150,
            "Only the chosen monitor is painted even when the HWND extends across a second display seam");
        NativeBox hitBounds;
        var hwnd = new WindowInteropHelper(window).Handle;
        var toDevice = PresentationSource.FromVisual(window).CompositionTarget.TransformToDevice;
        Check(GetWindowRgnBox(hwnd, out hitBounds) == 2 && (hitBounds.Right - hitBounds.Left) <= window.Width * 0.68 * toDevice.M11,
            "Windows limits the mouse hit region to only the compact viewport");
        var size = new Size(180, 240);
        var opaque = new Rect(16, 45, 148, 183);
        foreach (var edge in new[] { DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom })
        {
            var screen = PetMotion.WorkAreaInWindow(size, new Point(-14, 0), new Rect(0, 0, 1000, 800));
            var smaller = PetMotion.PeekPlacement(screen, opaque, edge);
            Check(smaller.Width * smaller.Height < size.Width * size.Height * 0.48, "The corner sprite occupies a reasonable mouse region on " + edge);
            var tab = animations.PeekAt(edge);
            var frame = new Rect(12, 40, 155, 193.75);
            var offset = tab.Align(frame, smaller, edge);
            var imageBounds = new Rect(frame.Left + tab.Bounds.Left * frame.Width + offset.X, frame.Top + tab.Bounds.Top * frame.Height + offset.Y,
                tab.Bounds.Width * frame.Width, tab.Bounds.Height * frame.Height);
            double overlap = edge == DockEdge.Left ? smaller.Left - imageBounds.Left : edge == DockEdge.Top ? smaller.Top - imageBounds.Top :
                edge == DockEdge.Right ? imageBounds.Right - smaller.Right : imageBounds.Bottom - smaller.Bottom;
            Check(Math.Abs(overlap - 0.5) < 0.001 && imageBounds.Width < smaller.Width && imageBounds.Height < smaller.Height,
                "The painted fragment joins the screen lip and fits inside the small viewport on " + edge);
            var justBefore = opaque;
            justBefore.Offset(PetMotion.EdgeSlide(opaque, screen, edge, PetMotion.BodySlideSeconds - 0.00001));
            var hiddenTab = imageBounds;
            hiddenTab.Offset(PetMotion.EdgeSlide(imageBounds, screen, edge, PetMotion.BodySlideSeconds));
            Check(Rect.Intersect(screen, justBefore).IsEmpty && Rect.Intersect(screen, hiddenTab).IsEmpty,
                "Both drawings are hidden at the reversible swap on " + edge);
            var halfway = PetMotion.EdgeSlide(opaque, screen, edge, PetMotion.BodySlideSeconds * 0.5);
            Check(halfway.Length > 0 && PetMotion.EdgeSlide(opaque, screen, edge, 0).Length == 0,
                "The transition moves the opaque bounds off the monitor instead of resizing a rectangle on " + edge);
        }
        Call(window, "ResetPosition");
        Call(window, "UpdatePose");
        var sprite = (Image)Field(window, "sprite");
        Check(layout.Clip == null && sprite.Source != animations.PeekAt(DockEdge.Left).Image, "Undocking brings the entire pet back");
    }

    private static void CheckActions(PetWindow window)
    {
        var animations = (PetAnimations)Field(window, "animations");
        var curious = PetAnimations.Curious;
        int tiltIndex = Array.IndexOf(curious.FrameOrder, 4);
        var recovery = curious.FrameOrder.Skip(tiltIndex + 1);
        Check(PetAnimations.Stretch.DurationMillis >= 2300 && tiltIndex >= 0 && curious.FrameMillis[tiltIndex] == 1750,
            "The full head tilt is held for 1.75 seconds");
        Check(recovery.SequenceEqual(new[] { 5, 6, 1, 0 }) &&
            curious.FrameOrder.Take(tiltIndex).SequenceEqual(new[] { 0, 1, 2, 3 }),
            "The head tilt visits its peak once and recovers without reversing the clip");
        foreach (var sequence in PetAnimations.All)
        {
            Check(sequence.FrameCount >= 4 && sequence.FrameOrder.Distinct().Count() == sequence.FrameCount,
                sequence.Name + " uses each of its separately drawn cels");
        }
        var sprite = (Image)Field(window, "sprite");
        var effects = (Canvas)Field(window, "effects");
        var clock = (System.Diagnostics.Stopwatch)Field(window, "clock");
        for (int i = 1; i <= 3; i++)
        {
            Call(window, "PreviewAction");
            Check((PetAction)Field(window, "action") == (PetAction)i, "Action preview advances to " + (PetAction)i);
            double cadence = (double)Field(window, "nextActionAt") - (double)Field(window, "actionAt");
            Check(cadence >= 14 && cadence < 24, "The automatic scheduler spaces out actions by fourteen to twenty-four seconds");
        }
        var initial = sprite.Source;
        typeof(PetWindow).GetField("actionAt", Members).SetValue(window, clock.Elapsed.TotalSeconds - 0.40);
        Call(window, "UpdatePose");
        Check(sprite.Source == animations.For(PetAction.Celebrate).At(0.40) && sprite.Source != initial && effects.Visibility == Visibility.Visible,
            "Celebration replaces its starting drawing with the jump cel and layers animated sparkles over it");
        var transform = (ScaleTransform)Field(window, "stretch");
        var shift = (TranslateTransform)Field(window, "bob");
        Check(transform.ScaleX >= 0.92 && transform.ScaleX <= 1.08 && transform.ScaleY >= 0.92 && transform.ScaleY <= 1.08 && Math.Abs(shift.Y) <= 10.0,
            "Character poses play through PNG frame swaps with bounded smooth secondary motion");
        typeof(PetWindow).GetField("actionAt", Members).SetValue(window, -10.0);
        Call(window, "UpdatePose");
        Check((PetAction)Field(window, "action") == PetAction.None && effects.Visibility == Visibility.Collapsed, "Animation returns to normal idle after playing");
    }

    private static void CheckIdleSchedule(PetWindow window)
    {
        var clock = (System.Diagnostics.Stopwatch)Field(window, "clock");
        Set(window, "curiousHoverAt", -1.0);
        Set(window, "nextActionAt", clock.Elapsed.TotalSeconds - 0.1);
        Call(window, "UpdatePose");
        Check((PetAction)Field(window, "action") == PetAction.Stretch, "Automatic idle time uses a stretch without mimicking a thinking cue");

        Set(window, "dock", DockEdge.Left);
        Call(window, "ApplyDockLayout");
        Set(window, "dockedAt", -10.0);
        Set(window, "dockHovered", false);
        Set(window, "dockMotion", 0.0);
        Call(window, "UpdatePose");
        Check((double)Field(window, "dockMotion") == 0 && (PetAction)Field(window, "action") != PetAction.None,
            "Auto-docking does not hide an idle animation already playing");
        Set(window, "actionAt", -10.0);
        Call(window, "UpdatePose");
        Check((PetAction)Field(window, "action") == PetAction.None && (double)Field(window, "dockMotion") == 0 &&
            (double)Field(window, "dockQuietUntil") > clock.Elapsed.TotalSeconds, "The dock resumes a short countdown after the idle clip finishes");

        double now = clock.Elapsed.TotalSeconds;
        var bounds = (Rect)typeof(PetWindow).GetMethod("HoverBounds", Members).Invoke(window, null);
        var head = new Point(bounds.Left + bounds.Width * 0.5, bounds.Top + 24);
        var movedOnHead = head + new Vector(16, 7);
        var outside = head + new Vector(0, -70);
        Set(window, "dockMotion", PetMotion.DockMotionSeconds);
        Set(window, "lastCuriousAt", -30.0);
        Call(window, "BeginHover", now, head);
        Set(window, "dockMotion", 0.0);
        Call(window, "TryIdleAction", now + 0.25);
        Check((PetAction)Field(window, "action") == PetAction.None, "Passing across the pet does not immediately trigger Curious");
        Call(window, "UpdateHoverDeparture", now + 0.3, outside);
        Call(window, "UpdateHoverDeparture", now + 0.6, outside);
        Call(window, "TryIdleAction", now + 0.85);
        Check((PetAction)Field(window, "action") == PetAction.None, "Leaving before the dwell time cancels Curious");
        Call(window, "BeginHover", now + 1.0, head);
        Set(window, "dockMotion", PetMotion.DockMotionSeconds);
        Call(window, "TryIdleAction", now + 1.85);
        Check((PetAction)Field(window, "action") == PetAction.None, "A qualifying hover still waits until the pet is expanded");
        Set(window, "dockMotion", 0.0);
        Call(window, "TryIdleAction", now + 2.0);
        Check((PetAction)Field(window, "action") == PetAction.Curious && (double)Field(window, "curiousHoverAt") < 0,
            "Dwelling plays Curious once when the body is fully expanded");
        Set(window, "action", PetAction.None);
        Call(window, "UpdateHoverDeparture", now + 25.0, movedOnHead);
        Call(window, "BeginHover", now + 25.0, movedOnHead);
        Call(window, "TryIdleAction", now + 26.0);
        Check((PetAction)Field(window, "action") == PetAction.None && (bool)Field(window, "hoverNeedsExit"),
            "Small movements across the head cannot trigger a second animation, even after cooldown");
        Call(window, "UpdateHoverDeparture", now + 26.1, outside);
        Call(window, "UpdateHoverDeparture", now + 26.2, head);
        Call(window, "BeginHover", now + 26.2, head);
        Check((double)Field(window, "curiousHoverAt") < 0, "A momentary boundary crossing does not rearm the hover pose");
        Call(window, "UpdateHoverDeparture", now + 27.0, outside);
        Call(window, "UpdateHoverDeparture", now + 27.3, outside);
        Call(window, "BeginHover", now + 27.4, head);
        Call(window, "TryIdleAction", now + 28.3);
        Check((PetAction)Field(window, "action") == PetAction.Curious, "A sustained departure allows the next deliberate hover pose");
        Set(window, "hoverNeedsExit", false);
        Set(window, "lastCuriousAt", -30.0);

        Set(window, "action", PetAction.None);
        Call(window, "ResetPosition");
        Call(window, "ToggleIdleActions");
        Call(window, "ToggleIdleActions");
        CheckIdleDelay(window, 2, 4, "Re-enabling idle actions quickly schedules one visible action");
        Call(window, "RescheduleIdleAfterInteraction", 10.0, 15.0);
        CheckIdleDelay(window, 10, 15, "Landing restores a normal idle delay");
    }

    private static void CheckHoverAndThinking(PetWindow window)
    {
        var clock = (System.Diagnostics.Stopwatch)Field(window, "clock");
        double now = clock.Elapsed.TotalSeconds;
        var bounds = (Rect)typeof(PetWindow).GetMethod("HoverBounds", Members).Invoke(window, null);
        var head = new Point(bounds.Left + bounds.Width * 0.5, bounds.Top + 24);
        Call(window, "BeginHover", now, head);
        Call(window, "SetChatThinking", true);
        Check((double)Field(window, "curiousHoverAt") < 0 && (PetAction)Field(window, "action") == PetAction.Thinking,
            "Thinking replaces a pending hover with a single pose");
        var thinkingStartedAt = (double)Field(window, "actionAt");
        Call(window, "PetHead");
        Check((PetAction)Field(window, "action") == PetAction.Thinking && (double)Field(window, "actionAt") == thinkingStartedAt,
            "Clicking the head during thinking does not interrupt or restart the pose");
        Call(window, "SetChatThinking", false);
        Call(window, "BeginHover", now + 1.0, head);
        Check((double)Field(window, "curiousHoverAt") < 0, "Ending thinking does not unlock a mouse still on the head");

        Set(window, "hoverNeedsExit", false);
        Call(window, "BeginHover", now + 2.0, head);
        Call(window, "TryIdleAction", now + 2.9);
        var startedAt = (double)Field(window, "actionAt");
        Check((PetAction)Field(window, "action") == PetAction.Curious, "An intentional hover starts the curious pose");
        Call(window, "SetChatThinking", true);
        Check((PetAction)Field(window, "action") == PetAction.Curious && (double)Field(window, "actionAt") == startedAt &&
            !(bool)Field(window, "thinkingPosePending"), "Thinking reuses an already playing hover pose without restarting it");
        Set(window, "actionAt", -10.0);
        Call(window, "UpdatePose");
        Call(window, "TryIdleAction", now + 30);
        Check((PetAction)Field(window, "action") == PetAction.None, "An overlapping hover cannot leave a second thinking pose queued");
        Call(window, "SetChatThinking", false);

        Set(window, "lastCuriousAt", clock.Elapsed.TotalSeconds - PetAnimations.Curious.DurationMillis / 1000.0 - 0.2);
        Call(window, "SetChatThinking", true);
        Check((PetAction)Field(window, "action") == PetAction.None && !(bool)Field(window, "thinkingPosePending"),
            "A recently finished hover pose does not cause back-to-back animations");
        Call(window, "SetChatThinking", false);
        Set(window, "hoverNeedsExit", false);
        Set(window, "curiousHoverAt", -1.0);
        Set(window, "lastCuriousAt", -30.0);
        Set(window, "bubbleUntil", 0.0);
    }

    private static void CheckIdleDelay(PetWindow window, double minimum, double maximum, string label)
    {
        var clock = (System.Diagnostics.Stopwatch)Field(window, "clock");
        double delay = (double)Field(window, "nextActionAt") - clock.Elapsed.TotalSeconds;
        Check(delay >= minimum - 0.15 && delay <= maximum, label);
    }

    private static void Set(PetWindow window, string name, object value)
    {
        typeof(PetWindow).GetField(name, Members).SetValue(window, value);
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBox { internal int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern int GetWindowRgnBox(IntPtr handle, out NativeBox rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);

    private static void CheckNativeDragging(PetWindow window)
    {
        var cursor = PetMotion.CursorPosition();
        var area = SystemParameters.WorkArea;
        var settings = (PetSettings)Field(window, "settings");
        bool animate = settings.Animate;
        bool idleActions = settings.IdleActions;
        settings.IdleActions = false;
        window.Activate();
        var sprite = (Image)Field(window, "sprite");
        var toDevice = PresentationSource.FromVisual(window).CompositionTarget.TransformToDevice;
        var center = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
        var destinations = new[] { new Point(area.Left + 31, center.Y), new Point(center.X, area.Top + 33),
            new Point(area.Right - 35, center.Y), new Point(center.X, area.Bottom - 27) };
        var edges = new[] { DockEdge.Left, DockEdge.Top, DockEdge.Right, DockEdge.Bottom };
        var borders = new[] { new Point(area.Left + 1, center.Y), new Point(center.X, area.Top + 1),
            new Point(area.Right - 1, center.Y), new Point(center.X, area.Bottom - 1) };
        Console.WriteLine("Windows displays available: " + Forms.Screen.AllScreens.Length);
        try
        {
            for (int i = 0; i < destinations.Length; i++)
            {
                settings.Animate = i == 0;
                Call(window, "ResetPosition");
                window.Left = center.X - window.Width / 2;
                window.Top = center.Y - window.Height / 2;
                Call(window, "UpdatePose");
                Pump(window);
                Check(((Rect)Field(window, "nativeViewport")).IsEmpty, "Undocked test restores the full native mouse region");
                var grip = sprite.PointToScreen(new Point(sprite.Width * 0.49, sprite.Height * 0.36));
                if (!SetCursorPos((int)grip.X, (int)grip.Y)) throw new InvalidOperationException("Unable to set a temporary test pointer position.");
                Pump(window);
                var sampled = WindowFromPoint(new NativePoint { X = (int)grip.X, Y = (int)grip.Y });
                Check(sampled == new WindowInteropHelper(window).Handle, "Native mouse can reach the exposed face before dragging to " + edges[i]);
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                WaitUntil(window, () => sprite.IsMouseCaptured, "Mouse capture at " + edges[i]);
                var destination = toDevice.Transform(destinations[i]);
                if (!SetCursorPos((int)destination.X, (int)destination.Y)) throw new InvalidOperationException("Unable to move the captured test pointer.");
                WaitUntil(window, () => (bool)Field(window, "dragging"), "Drag threshold at " + edges[i]);
                Pump(window);
                Check(sprite.IsMouseCaptured, "The pointer remains captured while approaching " + edges[i]);
                Point atEdge = (Point)typeof(PetWindow).GetMethod("WindowOrigin", Members).Invoke(window, null);
                var border = toDevice.Transform(borders[i]);
                if (!SetCursorPos((int)border.X, (int)border.Y)) throw new InvalidOperationException("Unable to continue to the actual screen boundary.");
                Pump(window);
                if ((((Point)Field(window, "lastDragPointer")) - borders[i]).Length >= 2 && sprite.IsMouseCaptured)
                    SetCursorPos((int)border.X, (int)border.Y);
                WaitUntil(window, () => (((Point)Field(window, "lastDragPointer")) - borders[i]).Length < 2, "Motion to edge " + edges[i]);
                Point pastEdge = (Point)typeof(PetWindow).GetMethod("WindowOrigin", Members).Invoke(window, null);
                bool horizontal = i == 0 || i == 2;
                Check(Math.Abs(horizontal ? pastEdge.X - atEdge.X : pastEdge.Y - atEdge.Y) < 2,
                    "Holding the native pointer toward the border does not push the pet back on " + edges[i]);
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                WaitUntil(window, () => !(bool)Field(window, "dragging"), "Release at " + edges[i]);
                Check((DockEdge)Field(window, "dock") == edges[i], "Native Windows pointer drag and release docks " + edges[i]);
                var snapped = (Point)typeof(PetWindow).GetMethod("WindowOrigin", Members).Invoke(window, null);
                Pump(window);
                Point settled = (Point)typeof(PetWindow).GetMethod("WindowOrigin", Members).Invoke(window, null);
                Check((settled - snapped).Length < 1, "Native window remains docked after releasing on " + edges[i]);
                WaitUntil(window, () => (double)Field(window, "dockMotion") == PetMotion.DockMotionSeconds &&
                    !((Rect)Field(window, "nativeViewport")).IsEmpty, "Tiny viewport at " + edges[i]);
                var visible = (Rect)Field(window, "dockViewport");
                Check(visible.Width * visible.Height < window.Width * window.Height * 0.13,
                    "Native dock really shrinks the mouse region on " + edges[i]);
                if (i == 0)
                {
                    Thread.Sleep(200);
                    var middle = toDevice.Transform(new Point(window.Left + visible.Left + visible.Width / 2, window.Top + visible.Top + visible.Height / 2));
                    if (!SetCursorPos((int)middle.X, (int)middle.Y)) throw new InvalidOperationException("Unable to hover the compact portion.");
                    WaitUntil(window, () => ((Rect)Field(window, "nativeViewport")).IsEmpty);
                    var away = toDevice.Transform(center);
                    if (!SetCursorPos((int)away.X, (int)away.Y)) throw new InvalidOperationException("Unable to move away from the expanded pet.");
                    WaitUntil(window, () => (double)Field(window, "dockMotion") == PetMotion.DockMotionSeconds,
                        "The mouse-left reveal delay finishes and returns to the small peek frame");
                    Check(true, "Native hover expands the compact hit region and leaving it collapses again");
                }
            }
        }
        finally
        {
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos((int)cursor.X, (int)cursor.Y);
            settings.Animate = animate;
            settings.IdleActions = idleActions;
            Call(window, "ResetPosition");
            Pump(window);
        }
    }

    private static void Pump(Window window)
    {
        Thread.Sleep(65);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void CheckNotices(PetWindow window, Forms.ContextMenuStrip menu, string directory)
    {
        var home = Path.Combine(directory, "notice-fixture");
        var day = Path.Combine(home, "sessions", DateTime.Today.ToString("yyyy"), DateTime.Today.ToString("MM"), DateTime.Today.ToString("dd"));
        Directory.CreateDirectory(day);
        var log = Path.Combine(day, "rollout-test-ad8cadfa-bd1e-4297-b24f-66ee87df49b5.jsonl");
        File.WriteAllText(log, Event(DateTime.UtcNow.AddSeconds(-3), "task_started"));
        WaitUntil(window, () => !(bool)Field(window, "checkingTasks"));
        typeof(PetWindow).GetField("codex", Members).SetValue(window, new CodexBridge(home));
        Set(window, "lastTasks", null);
        Set(window, "codexThinking", false);
        Set(window, "action", PetAction.None);
        Set(window, "greetingStart", -10.0);
        Set(window, "bubbleUntil", 0.0);
        var settings = (PetSettings)Field(window, "settings");
        settings.Animate = true;
        settings.IdleActions = false;
        if (!settings.CodexNotices) window.Dispatcher.Invoke(() => Click(window, menu, "报告 Codex 任务变化"));
        else window.Dispatcher.Invoke(() => Call(window, "PollCodex"));
        WaitUntil(window, () => Field(window, "lastTasks") != null && !(bool)Field(window, "checkingTasks"));
        Check((bool)Field(window, "codexThinking") && (PetAction)Field(window, "action") == PetAction.Thinking,
            "An active Codex task triggers Thinking independently of idle actions");
        var message = (TextBlock)Field(window, "message");
        var bubble = (Border)Field(window, "bubble");
        Set(window, "actionAt", -10.0);
        Call(window, "UpdatePose");
        Check((PetAction)Field(window, "action") == PetAction.None && bubble.Visibility == Visibility.Visible && message.Text.Contains("执行中"),
            "The Codex working message stays visible after the first animation finishes");
        var clock = (System.Diagnostics.Stopwatch)Field(window, "clock");
        Call(window, "TryIdleAction", clock.Elapsed.TotalSeconds + 30);
        Check((PetAction)Field(window, "action") == PetAction.None, "Continuing the same Codex work never queues a periodic pose");
        var overlap = Path.Combine(day, "rollout-test-ee4638dc-1fa6-4c11-a239-6c51778e6ee1.jsonl");
        File.WriteAllText(overlap, Event(DateTime.UtcNow, "task_started"));
        WaitUntil(window, () => ((System.Collections.IDictionary)Field(window, "lastTasks")).Count == 2);
        Call(window, "UpdatePose");
        Check((PetAction)Field(window, "action") == PetAction.None && !(bool)Field(window, "thinkingPosePending"),
            "A second Codex turn overlapping the first does not replay the pose");
        File.AppendAllText(log, Event(DateTime.UtcNow, "task_complete"));
        WaitUntil(window, () => message.Text.Contains("完成"));
        Check((bool)Field(window, "codexThinking") && (PetAction)Field(window, "action") == PetAction.None,
            "Completing one of two active tasks does not interrupt the ongoing work state with another animation");
        Set(window, "bubbleUntil", 0.0);
        Call(window, "UpdatePose");
        Click(window, menu, "报告 Codex 任务变化");
        Check(!(bool)Field(window, "codexThinking") && !((DispatcherTimer)Field(window, "codexTimer")).IsEnabled &&
            bubble.Visibility == Visibility.Collapsed, "Turning notices off clears Codex thinking and its working message");
        window.Dispatcher.Invoke(() => Click(window, menu, "报告 Codex 任务变化"));
        WaitUntil(window, () => (bool)Field(window, "codexThinking"));
        Check((PetAction)Field(window, "action") == PetAction.Thinking, "Turning notices back on detects the task already in progress");
        Click(window, menu, "报告 Codex 任务变化");
        Check((PetAction)Field(window, "action") == PetAction.None, "Turning notices off also stops a thinking animation already in progress");
        window.Dispatcher.Invoke(() => Click(window, menu, "报告 Codex 任务变化"));
        WaitUntil(window, () => (bool)Field(window, "codexThinking"));
        File.AppendAllText(overlap, Event(DateTime.UtcNow, "task_complete"));
        WaitUntil(window, () => message.Text.Contains("完成"));
        Check(!(bool)Field(window, "codexThinking") && message.Text.Contains("Codex 完成 1 项") &&
            (PetAction)Field(window, "action") == PetAction.Celebrate,
            "The timer detects completion, clears the working state and plays the celebration");
        Set(window, "actionAt", -10.0);
        Set(window, "bubbleUntil", 0.0);
        File.AppendAllText(overlap, Event(DateTime.UtcNow, "task_started"));
        WaitUntil(window, () => (PetAction)Field(window, "action") == PetAction.Thinking);
        Check((bool)Field(window, "codexThinking"), "A fresh busy period after completion gets its own single pose");
        Click(window, menu, "报告 Codex 任务变化");
        settings.Animate = false;
        settings.IdleActions = true;
    }

    private static void WaitUntil(Window window, Func<bool> ready, string failure = "Application did not complete a requested test action.")
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && clock.ElapsedMilliseconds < 5000)
        {
            Thread.Sleep(25);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        if (!ready()) throw new TimeoutException(failure);
    }

    private static string Event(DateTime at, string type)
    {
        return "{\"timestamp\":\"" + at.ToString("o") + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"" + type + "\"}}\n";
    }

    private static void CheckLiveCodex(string threadId, string directory)
    {
        var bridge = new CodexBridge();
        Check(bridge.ReadLocalTasks(CancellationToken.None).Any(t => t.Id == threadId && t.Status == CodexTaskStatus.Running), "Current Codex turn detected from its real session log");
        var tasks = bridge.ReadTasks(CancellationToken.None);
        Check(tasks.Any(t => t.Id == threadId && t.Status == CodexTaskStatus.Running), "Installed Codex task listing has live state for this thread");
        var quotas = bridge.ReadQuotas(CancellationToken.None);
        Check(quotas.Any(q => q.Minutes == 300) && quotas.Any(q => q.Minutes == 10080), "Installed Codex returns the real five-hour and weekly subscription windows");
        Check(quotas.All(q => q.Used >= 0 && q.Used <= 100), "Quota percentages have valid bounds");
        var panel = new CodexPanel(bridge);
        panel.Show();
        panel.ShowPage(true);
        var refresh = (Button)typeof(CodexPanel).GetField("refresh", Members).GetValue(panel);
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!refresh.IsEnabled && deadline.ElapsedMilliseconds < 15000)
        {
            Thread.Sleep(40);
            panel.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        var cardList = (StackPanel)typeof(CodexPanel).GetField("cards", Members).GetValue(panel);
        Check(refresh.IsEnabled && cardList.Children.Count >= 2, "Quota panel rendered live quota cards");
        panel.Close();
    }

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }

    private static object Field(PetWindow window, string name) { return typeof(PetWindow).GetField(name, Members).GetValue(window); }
    private static void Call(PetWindow window, string name, params object[] args) { typeof(PetWindow).GetMethod(name, Members).Invoke(window, args); }
    private static void Check(bool result, string name)
    {
        if (!result) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }
}
