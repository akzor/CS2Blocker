using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace CS2Blocker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!DesktopIntegration.WaitForPreviousInstance(args)) return 1;
        BlockerApp.StartInTray = args.Contains("--tray", StringComparer.Ordinal);
        Mutex instance;
        bool first;
        try
        {
            instance = new Mutex(false, @"Local\CS2Blocker");
            try { first = instance.WaitOne(0); }
            catch (AbandonedMutexException) { first = true; }
        }
        catch (UnauthorizedAccessException) { instance = new Mutex(); first = false; }
        using (instance)
        {
            BlockerApp.AlreadyRunning = !first;
            if (!first) instance.Dispose();
            try { return AppBuilder.Configure<BlockerApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args); }
            finally { if (first) instance.ReleaseMutex(); Ui.Dispose(); }
        }
    }
}

internal sealed class BlockerApp : Application
{
    internal static bool AlreadyRunning { get; set; }
    internal static bool StartInTray { get; set; }

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Resources["BlockerFont"] = Ui.BodyFont;
        Resources["BlockerBackground"] = Ui.Background;
        Resources["BlockerSurface"] = Ui.Surface;
        Resources["BlockerHover"] = Ui.Hover;
        Resources["BlockerBorder"] = Ui.Border;
        Resources["BlockerText"] = Ui.Text;
        Resources["BlockerMuted"] = Ui.Muted;
        Resources["BlockerAccent"] = Ui.Accent;
        Resources["BlockerSelection"] = Ui.Selection;
        var text = new Style(x => x.OfType<TextBlock>());
        text.Setters.Add(new Setter(TextBlock.FontFamilyProperty, Ui.BodyFont));
        Styles.Add(text);
        var controls = new Style(x => x.OfType<TemplatedControl>());
        controls.Setters.Add(new Setter(TemplatedControl.FontFamilyProperty, Ui.BodyFont));
        Styles.Add(controls);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = AlreadyRunning
                ? Ui.MessageWindow("The application is already running. Open it from the tray.\nTo launch another copy, choose Exit from the tray menu.")
                : new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
