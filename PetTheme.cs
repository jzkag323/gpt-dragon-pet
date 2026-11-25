// ============================================================================
// PetTheme.cs - 全应用统一紫丁香视觉主题（纯 C# 代码构建 UI，无 XAML）
// 核心原理：
//   1. 所有 SolidColorBrush / LinearGradientBrush / DropShadowEffect 在创建后
//      立即调用 .Freeze() 冻结为只读对象，消除跨线程与 WPF 渲染重绘开销；
//   2. 使用 FrameworkElementFactory 纯代码组装圆角胶囊按钮与开关模板，使整套
//      程序可直接由 Windows 自带 csc.exe 单命令编译为独立 EXE。
// ============================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

internal static class PetTheme
{
    internal static readonly SolidColorBrush Ink = Brush(48, 36, 65);
    internal static readonly SolidColorBrush Muted = Brush(108, 92, 132);
    internal static readonly SolidColorBrush Accent = Brush(118, 78, 178);
    internal static readonly SolidColorBrush AccentDark = Brush(96, 60, 152);
    internal static readonly SolidColorBrush AccentSoft = Brush(235, 224, 252);
    internal static readonly SolidColorBrush Lavender = Brush(241, 233, 253);
    internal static readonly SolidColorBrush Surface = Brush(254, 252, 255);
    internal static readonly SolidColorBrush InputBg = Brush(251, 248, 255);
    internal static readonly SolidColorBrush BorderTone = Brush(220, 207, 240);
    internal static readonly SolidColorBrush Divider = Brush(233, 224, 246);
    internal static readonly SolidColorBrush ErrorTone = Brush(178, 60, 88);
    internal static readonly SolidColorBrush ErrorSoft = Brush(253, 240, 243);
    internal static readonly SolidColorBrush ErrorBorder = Brush(236, 185, 198);
    internal static readonly SolidColorBrush SuccessTone = Brush(44, 122, 90);
    internal static readonly SolidColorBrush SuccessSoft = Brush(234, 248, 241);
    internal static readonly FontFamily UiFont = new FontFamily("Microsoft YaHei UI");

    internal static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    internal static LinearGradientBrush WindowBackground()
    {
        var brush = new LinearGradientBrush(Color.FromRgb(250, 246, 255), Color.FromRgb(242, 235, 252), 90);
        brush.Freeze();
        return brush;
    }

    internal static LinearGradientBrush AccentGradient()
    {
        var brush = new LinearGradientBrush(Color.FromRgb(129, 86, 194), Color.FromRgb(106, 68, 166), 45);
        brush.Freeze();
        return brush;
    }

    internal static LinearGradientBrush BubbleGradient()
    {
        var brush = new LinearGradientBrush(Color.FromRgb(254, 252, 255), Color.FromRgb(243, 236, 254), 90);
        brush.Freeze();
        return brush;
    }

    internal static DropShadowEffect CardShadow(double opacity = 0.09)
    {
        var effect = new DropShadowEffect
        {
            Color = Color.FromRgb(118, 88, 168),
            BlurRadius = 12,
            ShadowDepth = 2,
            Opacity = opacity
        };
        effect.Freeze();
        return effect;
    }

    internal static ControlTemplate BuildPillButtonTemplate(double radius)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "BtnBorder";
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.88));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.76));
        template.Triggers.Add(pressed);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.48));
        template.Triggers.Add(disabled);

        return template;
    }

    internal static Button MakeButton(string text, Action click, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            FontFamily = UiFont,
            FontSize = 13,
            Padding = new Thickness(14, 6, 14, 6),
            Background = primary ? (Brush)Accent : Surface,
            Foreground = primary ? Brushes.White : Ink,
            BorderBrush = primary ? Accent : BorderTone,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Template = BuildPillButtonTemplate(9)
        };
        if (click != null) button.Click += (sender, args) => click();
        return button;
    }

    internal static Button MakeChipButton(string text, Action click, bool selected = false)
    {
        var button = new Button
        {
            Content = text,
            FontFamily = UiFont,
            FontSize = 12,
            Padding = new Thickness(11, 5, 11, 5),
            Background = selected ? AccentSoft : Lavender,
            Foreground = selected ? AccentDark : Ink,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            BorderBrush = selected ? Accent : BorderTone,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Template = BuildPillButtonTemplate(8)
        };
        if (click != null) button.Click += (sender, args) => click();
        return button;
    }

    internal static void StyleChip(Button button, bool selected)
    {
        if (button == null) return;
        button.Background = selected ? AccentSoft : Lavender;
        button.Foreground = selected ? AccentDark : Ink;
        button.BorderBrush = selected ? Accent : BorderTone;
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    internal static Border MakeCard(UIElement child, Thickness? padding = null)
    {
        return new Border
        {
            Background = Surface,
            BorderBrush = Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = padding ?? new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Effect = CardShadow(0.08),
            Child = child
        };
    }

    internal static TextBlock Label(string text, double size, Brush color, FontWeight weight)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = UiFont,
            FontSize = size,
            Foreground = color,
            FontWeight = weight
        };
    }

    internal static CheckBox MakeToggle(string title, string subtitle, bool initial, Action<bool> onChanged)
    {
        var stack = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = UiFont,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink,
            TextWrapping = TextWrapping.Wrap
        });
        if (!String.IsNullOrWhiteSpace(subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontFamily = UiFont,
                FontSize = 11.5,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }
        var box = new CheckBox
        {
            Content = stack,
            IsChecked = initial,
            VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 6),
            Cursor = Cursors.Hand
        };
        if (onChanged != null)
        {
            box.Checked += (sender, args) => onChanged(true);
            box.Unchecked += (sender, args) => onChanged(false);
        }
        return box;
    }

    internal static ControlTemplate BuildContextMenuTemplate()
    {
        var template = new ControlTemplate(typeof(ContextMenu));
        var outer = new FrameworkElementFactory(typeof(Border));
        outer.SetValue(Border.BackgroundProperty, Surface);
        outer.SetValue(Border.BorderBrushProperty, BorderTone);
        outer.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        outer.SetValue(Border.PaddingProperty, new Thickness(5, 6, 5, 6));
        outer.SetValue(Border.MarginProperty, new Thickness(6));
        outer.SetValue(UIElement.EffectProperty, new DropShadowEffect
        {
            Color = Color.FromRgb(86, 62, 128),
            BlurRadius = 14,
            ShadowDepth = 3,
            Opacity = 0.22
        });
        var host = new FrameworkElementFactory(typeof(StackPanel));
        host.SetValue(Panel.IsItemsHostProperty, true);
        outer.AppendChild(host);
        template.VisualTree = outer;
        return template;
    }

    internal static Style BuildMenuItemStyle()
    {
        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, UiFont));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.5));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 5, 14, 5)));

        var template = new ControlTemplate(typeof(MenuItem));
        var bg = new FrameworkElementFactory(typeof(Border));
        bg.Name = "ItemBorder";
        bg.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        bg.SetValue(Border.PaddingProperty, new Thickness(8, 5, 14, 5));
        bg.SetValue(Border.MarginProperty, new Thickness(1));

        var row = new FrameworkElementFactory(typeof(DockPanel));
        var checkMark = new FrameworkElementFactory(typeof(TextBlock));
        checkMark.Name = "CheckMark";
        checkMark.SetValue(TextBlock.TextProperty, "✓");
        checkMark.SetValue(TextBlock.WidthProperty, 16.0);
        checkMark.SetValue(TextBlock.ForegroundProperty, Accent);
        checkMark.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        checkMark.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
        checkMark.SetValue(DockPanel.DockProperty, Dock.Left);
        row.AppendChild(checkMark);

        var header = new FrameworkElementFactory(typeof(ContentPresenter));
        header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        header.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(header);

        bg.AppendChild(row);
        template.VisualTree = bg;

        var hover = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Lavender, "ItemBorder"));
        template.Triggers.Add(hover);

        var checkedTrigger = new Trigger { Property = MenuItem.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckMark"));
        template.Triggers.Add(checkedTrigger);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }
}
