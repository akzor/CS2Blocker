using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace CS2Blocker;

internal sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Ui.AttachWindowDrag(this, this.FindControl<Border>("TitleBar")!);
    }
}
