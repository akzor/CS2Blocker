using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace CS2Blocker;

internal static class Ui
{
    public static readonly FontFamily BodyFont = new("Segoe UI Variable, Segoe UI");
    public static readonly FontFamily MonoFont = new("avares://CS2Blocker/Assets/fonts#JetBrains Mono");
    public static readonly SolidColorBrush Background = Brush("#151719"), Surface = Brush("#1D2023"), Hover = Brush("#282B2E"),
        Border = Brush("#42464A"), Text = Brush("#EDECE7"), Muted = Brush("#A9AEB2"), Accent = Brush("#EDB54D"),
        Danger = Brush("#F6958D"), Selection = Brush("#332C20");
    private static readonly Dictionary<string, Bitmap> Images = new();

    public static SolidColorBrush Brush(string value) => new(Color.Parse(value));
    public static Stream Asset(string name) => AssetLoader.Open(new Uri("avares://CS2Blocker/Assets/" + name));
    public static Grid Grid(string columns = "*", string rows = "*") => new()
    {
        ColumnDefinitions = ColumnDefinitions.Parse(columns), RowDefinitions = RowDefinitions.Parse(rows)
    };
    public static void Put(Grid grid, Control child, int column = 0, int row = 0)
    {
        Avalonia.Controls.Grid.SetColumn(child, column);
        Avalonia.Controls.Grid.SetRow(child, row);
        grid.Children.Add(child);
    }
    public static TextBlock Label(string value, double size = 16, IBrush? color = null, bool bold = false) => new()
    {
        Text = value, FontFamily = BodyFont, FontSize = size, Foreground = color ?? Text,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };
    public static StackPanel Stack(double spacing = 0, params Control[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    public static Border Line() => new() { Height = 1, Background = Border, VerticalAlignment = VerticalAlignment.Center };
    public static Border Round(Control child, IBrush? background = null, double radius = 12) => new()
    {
        Background = background ?? Background, BorderBrush = Border, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius), ClipToBounds = true, Child = child
    };
    private static Bitmap Bitmap(string name)
    {
        if (!Images.TryGetValue(name, out var image))
        {
            using var stream = Asset(name);
            Images[name] = image = new Bitmap(stream);
        }
        return image;
    }
    public static Control Icon(string name, double size = 20, IBrush? color = null) => new Border
    {
        Width = size, Height = size, Background = color ?? Text,
        OpacityMask = new ImageBrush(Bitmap("icons/" + name + ".png")) { Stretch = Stretch.Uniform },
        VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
    };
    public static Control Flag(string country)
    {
        string path = "flags/" + country.ToLowerInvariant() + ".png";
        if (!AssetLoader.Exists(new Uri("avares://CS2Blocker/Assets/" + path))) return Icon("world", 26, Muted);
        return new Border
        {
            Width = 28, Height = 20, CornerRadius = new CornerRadius(3), ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Image { Source = Bitmap(path), Stretch = Stretch.UniformToFill }
        };
    }
    public static Control AppIcon(double size = 28) => new Border
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(5), ClipToBounds = true,
        VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        Child = new Image { Source = Bitmap("cs2.png"), Stretch = Stretch.Uniform }
    };

    public static void AttachWindowDrag(Window window, Border bar)
    {
        bar.Background ??= Brushes.Transparent;
        bar.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual visual && (visual is Button || visual.GetVisualAncestors().Any(p => p is Button))) return;
            if (!e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2 && window.CanResize)
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else window.BeginMoveDrag(e);
        }, RoutingStrategies.Tunnel);
    }
    public static Control WithIcon(string name, Control content, IBrush? tint = null)
    {
        var grid = Grid("22,*"); grid.ColumnSpacing = 10;
        Put(grid, Icon(name, 22, tint)); Put(grid, content, 1);
        return grid;
    }
    public static Button Button(string title, string? icon, bool primary = false, bool danger = false, bool flat = false)
    {
        var color = primary ? Background : danger ? Danger : Text;
        var label = Label(title, 14, color);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (icon != null) content.Children.Add(Icon(icon, 17, color));
        content.Children.Add(label);
        var button = new Button
        {
            Content = content, Height = 38, MinHeight = 0, Padding = new Thickness(12, 0),
            CornerRadius = new CornerRadius(8), Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, title);
        button.Template = new FuncControlTemplate<Button>((control, _) =>
        {
            var presenter = new ContentPresenter { Content = control.Content, Padding = control.Padding, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            var border = new Border { CornerRadius = control.CornerRadius, BorderThickness = new Thickness(1), Child = presenter };
            void Update()
            {
                border.Background = primary && control.IsEnabled ? Accent : primary ? Selection
                    : control.IsPointerOver ? danger ? Brush("#452325") : Hover : flat ? Brushes.Transparent : Background;
                border.BorderBrush = control.IsFocused ? Accent : flat ? Brushes.Transparent : primary ? Accent : Border;
                border.Opacity = control.IsEnabled ? 1 : .45;
                if (primary) label.Foreground = control.IsEnabled ? Background : Text;
            }
            control.PropertyChanged += (_, e) =>
            {
                if (e.Property == InputElement.IsPointerOverProperty || e.Property == InputElement.IsFocusedProperty || e.Property == InputElement.IsEnabledProperty) Update();
                if (e.Property == ContentControl.ContentProperty) presenter.Content = control.Content;
            };
            Update(); return border;
        });
        return button;
    }
    public static Button CaptionButton(string icon, string label, Action action, bool danger = false)
    {
        var button = Button(label, null, danger: danger, flat: true);
        button.Content = Icon(icon, 17, Text);
        button.Width = 36; button.Height = 32; button.MinWidth = 0; button.Padding = new Thickness(0);
        button.Click += (_, _) => action();
        ToolTip.SetTip(button, label);
        return button;
    }
    public static CheckBox Checkbox(Control content, string label, bool selected = false)
    {
        var checkbox = new CheckBox { Content = content, IsChecked = selected, MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Cursor = new Cursor(StandardCursorType.Hand) };
        AutomationProperties.SetName(checkbox, label);
        checkbox.Template = new FuncControlTemplate<CheckBox>((control, _) =>
        {
            var box = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1.2), VerticalAlignment = VerticalAlignment.Center, Child = Icon("check", 16, Background) };
            var row = Grid("22,*"); row.ColumnSpacing = 12;
            Put(row, box);
            var presenter = new ContentPresenter { Content = control.Content, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Put(row, presenter, 1);
            var root = new Border { Padding = new Thickness(5, 2), CornerRadius = new CornerRadius(8), Child = row };
            void Update()
            {
                box.Background = control.IsChecked == true ? Accent : Surface;
                box.BorderBrush = control.IsChecked == true || control.IsFocused ? Accent : Muted;
                box.Child!.IsVisible = control.IsChecked == true;
                root.Background = control.IsPointerOver ? Hover : Brushes.Transparent;
                root.Opacity = control.IsEnabled ? 1 : .5;
            }
            control.PropertyChanged += (_, e) =>
            {
                if (e.Property == ToggleButton.IsCheckedProperty || e.Property == InputElement.IsPointerOverProperty || e.Property == InputElement.IsFocusedProperty || e.Property == InputElement.IsEnabledProperty) Update();
            };
            Update(); return root;
        });
        return checkbox;
    }
    public static Window MessageWindow(string message, bool confirm = false)
    {
        var window = new Window
        {
            Title = "CS2 — Блокировщик серверов", Width = 500, Height = 240, CanResize = false,
            WindowDecorations = WindowDecorations.None, Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var body = Grid("*", "*,44"); body.Margin = new Thickness(24);
        var text = Label(message); text.TextWrapping = TextWrapping.Wrap;
        Put(body, new ScrollViewer { Content = text, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 18) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        if (confirm)
        {
            var cancel = Button("Отмена", "x"); cancel.Click += (_, _) => window.Close(false); actions.Children.Add(cancel);
        }
        var ok = Button(confirm ? "Снять всё" : "OK", confirm ? "trash" : "check", primary: !confirm, danger: confirm);
        ok.Click += (_, _) => window.Close(true); actions.Children.Add(ok);
        Put(body, actions, 0, 1); window.Content = Round(body, Surface, 14);
        return window;
    }
    public static void Dispose() { foreach (var bitmap in Images.Values) bitmap.Dispose(); Images.Clear(); }
}
