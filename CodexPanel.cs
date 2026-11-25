// ============================================================================
// CodexPanel.cs - Codex 任务记录与配额使用率查看窗口
// 核心原理：复用 PetTheme 卡片与进度条样式，异步拉取 CodexBridge 数据并展示。
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

internal sealed class CodexPanel : Window
{
    private readonly CodexBridge bridge;
    private readonly StackPanel cards = new StackPanel();
    private readonly TextBlock heading;
    private readonly TextBlock status;
    private readonly Button refresh;
    private readonly Button tasks;
    private readonly Button quotas;
    private CancellationTokenSource request;
    private bool quotaPage;
    private bool closed;

    internal CodexPanel(CodexBridge bridge)
    {
        this.bridge = bridge;
        Title = "GPT 龙娘 · Codex";
        Width = 450;
        Height = 560;
        MinWidth = 365;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = PetTheme.WindowBackground();
        FontFamily = PetTheme.UiFont;
        FontSize = 13;

        var root = new DockPanel { Margin = new Thickness(22) };
        var header = new StackPanel();
        heading = PetTheme.Label("Codex 任务", 24, PetTheme.Ink, FontWeights.SemiBold);
        header.Children.Add(heading);
        var actions = new DockPanel { Margin = new Thickness(0, 14, 0, 10) };
        refresh = PetTheme.MakeButton("刷新", () => LoadPage());
        DockPanel.SetDock(refresh, Dock.Right);
        actions.Children.Add(refresh);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        tasks = PetTheme.MakeButton("任务", () => ShowPage(false));
        quotas = PetTheme.MakeButton("额度", () => ShowPage(true));
        tasks.Margin = new Thickness(0, 0, 6, 0);
        tabs.Children.Add(tasks);
        tabs.Children.Add(quotas);
        actions.Children.Add(tabs);
        header.Children.Add(actions);
        status = PetTheme.Label("正在读取…", 12, PetTheme.Muted, FontWeights.Normal);
        status.Margin = new Thickness(0, 0, 0, 10);
        header.Children.Add(status);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var scroll = new ScrollViewer
        {
            Content = cards,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        root.Children.Add(scroll);
        Content = root;
        Closed += (sender, args) => { closed = true; if (request != null) request.Cancel(); };
    }

    internal void ShowPage(bool quota)
    {
        quotaPage = quota;
        heading.Text = quota ? "Codex 额度" : "Codex 任务";
        tasks.Background = quota ? PetTheme.Surface : PetTheme.AccentSoft;
        quotas.Background = quota ? PetTheme.AccentSoft : PetTheme.Surface;
        LoadPage();
    }

    private async void LoadPage()
    {
        if (request != null) request.Cancel();
        var source = new CancellationTokenSource();
        request = source;
        bool readQuota = quotaPage;
        refresh.IsEnabled = false;
        status.Text = readQuota ? "正在从 Codex 查询额度…" : "正在读取本机 Codex 会话…";
        cards.Children.Clear();
        try
        {
            if (readQuota)
            {
                var data = await Task.Run(() => bridge.ReadQuotas(source.Token), source.Token);
                if (closed || source.IsCancellationRequested) return;
                foreach (var item in data) AddQuota(item);
                status.Text = "Codex 订阅额度 · 已刷新 " + DateTime.Now.ToString("HH:mm:ss");
            }
            else
            {
                var data = await Task.Run(() => bridge.ReadTasks(source.Token), source.Token);
                if (closed || source.IsCancellationRequested) return;
                if (data.Length == 0) AddNote("还没有可显示的本机 Codex 会话。");
                foreach (var item in data) AddTask(item);
                status.Text = "最近的本机会话 · 已刷新 " + DateTime.Now.ToString("HH:mm:ss");
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException error) { if (!closed && !source.IsCancellationRequested) ShowError(error.Message); }
        catch (System.IO.IOException) { if (!closed && !source.IsCancellationRequested) ShowError("本机 Codex 数据暂时无法读取，可稍后刷新。"); }
        catch (UnauthorizedAccessException) { if (!closed && !source.IsCancellationRequested) ShowError("当前 Windows 账户无法读取本机 Codex 会话。"); }
        finally
        {
            if (request == source)
            {
                request = null;
                if (!closed) refresh.IsEnabled = true;
            }
            source.Dispose();
        }
    }

    private void ShowError(string text)
    {
        status.Text = "暂时无法获取";
        AddNote(text);
    }

    private void AddTask(CodexTask task)
    {
        var row = Card();
        var top = new DockPanel();
        var tag = PetTheme.Label(TaskLabel(task.Status), 11, task.Status == CodexTaskStatus.Completed ? PetTheme.SuccessTone : PetTheme.Accent, FontWeights.SemiBold);
        tag.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(tag, Dock.Right);
        top.Children.Add(tag);
        var title = PetTheme.Label(task.Title, 14, PetTheme.Ink, FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        top.Children.Add(title);
        row.Children.Add(top);
        DateTime when = task.EventTime == DateTime.MinValue ? task.LastWrite : task.EventTime;
        if (when != DateTime.MinValue) row.Children.Add(PetTheme.Label(when.ToLocalTime().ToString("MM-dd HH:mm"), 11, PetTheme.Muted, FontWeights.Normal));
    }

    private void AddQuota(CodexQuota quota)
    {
        var card = Card();
        var top = new DockPanel();
        double remaining = 100 - quota.Used;
        string window = quota.Minutes == 10080 ? "每周" : quota.Minutes > 0 && quota.Minutes % 1440 == 0
            ? (quota.Minutes / 1440) + " 天" : quota.Minutes > 0 && quota.Minutes % 60 == 0
            ? (quota.Minutes / 60) + " 小时" : quota.Minutes > 0 ? quota.Minutes + " 分钟" : "额度窗口";
        var number = PetTheme.Label("剩余 " + remaining.ToString("0.#") + "%", 16, remaining <= 10 ? PetTheme.ErrorTone : PetTheme.Accent, FontWeights.SemiBold);
        DockPanel.SetDock(number, Dock.Right);
        top.Children.Add(number);
        top.Children.Add(PetTheme.Label(quota.Bucket + " · " + window, 15, PetTheme.Ink, FontWeights.SemiBold));
        card.Children.Add(top);
        card.Children.Add(new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = remaining,
            Height = 7,
            Foreground = PetTheme.Accent,
            Background = PetTheme.Lavender,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 9, 0, 7)
        });
        if (quota.Reset.HasValue) card.Children.Add(PetTheme.Label("重置时间 " + quota.Reset.Value.ToLocalTime().ToString("MM-dd HH:mm"), 12, PetTheme.Muted, FontWeights.Normal));
    }

    private void AddNote(string text)
    {
        var note = PetTheme.Label(text, 13, PetTheme.Muted, FontWeights.Normal);
        note.TextWrapping = TextWrapping.Wrap;
        Card().Children.Add(note);
    }

    private StackPanel Card()
    {
        var panel = new StackPanel();
        cards.Children.Add(PetTheme.MakeCard(panel));
        return panel;
    }

    private static string TaskLabel(CodexTaskStatus status)
    {
        switch (status)
        {
            case CodexTaskStatus.Running: return "进行中";
            case CodexTaskStatus.Completed: return "已完成";
            case CodexTaskStatus.Interrupted: return "已中断";
            case CodexTaskStatus.Failed: return "出错";
            case CodexTaskStatus.Waiting: return "等待输入";
            case CodexTaskStatus.NeedsApproval: return "等待批准";
            default: return "状态未确认";
        }
    }
}
