using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CS2Blocker;

internal sealed partial class MainWindow : Window
{
    private enum RelayStatus { Available, Blocked, Pending, Unknown, NoAddresses }

    private const string RepositoryUrl = "https://github.com/akzor/CS2Blocker";
    private const string WideColumns = "48,1.36*,1.02*,46,112,1.05*";
    private const string CompactColumns = "34,1.4*,1.16*,28,50,1.16*";
    private string _columns = WideColumns;
    private RelayStatus? _relayStatusFilter;
    private static readonly System.Net.Http.HttpClient CatalogClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly bool _isAdmin = Firewall.IsAdmin();
    private readonly StackPanel _countries;
    private readonly StackPanel _rows;
    private readonly ScrollViewer _rowScroll, _countryScroll;
    private readonly TextBox _locationSearch, _countrySearch;
    private readonly TextBlock _summary, _status, _noRelays;
    private readonly Border _statusDot;
    private readonly ContentControl _applyHost;
    private readonly Button _sidebarTitle;
    private readonly Action _updateSidebar;
    private readonly TextBlock _history = Ui.Label("", 13);
    private readonly Queue<string> _events = new();
    private readonly ProgressBar _progress;
    private readonly WrapPanel _actions;
    private readonly CheckBox _selectAll = Ui.Checkbox(Ui.Label("Select all", 14), "Select all");
    private readonly Button _stop = MainButton("Stop", "player-stop");
    private readonly Button _clear = MainButton("Clear all", "trash", danger: true);
    private readonly Button _allCountries = MainButton("All countries");
    private readonly Button _noCountries = MainButton("Deselect all");
    private readonly Button _applyCountries = MainButton("Apply", "check", primary: true);
    private readonly Button? _adminButton;
    private Button _pingButton = null!;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly TrayIcon _tray;
    private readonly TrayIcons _trayIcons;
    private AppSettings _settings = new();
    private readonly HashSet<string> _countryDraft = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _updateCancellation = new();
    private List<ServerEntry> _servers = new();
    private CancellationTokenSource? _pingCancellation;
    private bool _busy, _populatingCountries, _updatingSelection, _policyPending, _refreshPending;
    private bool _closingForRestart;
    private bool _exitRequested;
    private bool _updateInProgress;
    private bool _compactSummary;
    private Window? _settingsWindow;
    private DateTimeOffset _retryAfter;
    private DateTimeOffset _nextUpdateCheck;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Ui.AnimateWindowOpening(this);
        using (var stream = Ui.Asset("valve-blocker.ico")) Icon = new WindowIcon(stream);
        T Part<T>(string name) where T : Control => this.FindControl<T>(name)!;

        var outer = Part<Border>("Shell");
        var titleBar = Part<Border>("TitleBar");
        Ui.AttachWindowDrag(this, titleBar);
        Part<ContentControl>("BrandIcon").Content = Ui.PngIcon("valve-blocker.png", 32);
        var brandName = Part<TextBlock>("BrandName");
        var autoTab = Part<Button>("AutoTab");
        var settings = Part<Button>("SettingsTab");
        var journal = Part<Button>("JournalTab");
        var tabs = new[]
        {
            (autoTab, "Auto-block", Ui.Icon("adjustments-horizontal", 20)),
            (settings, "Settings", Ui.Icon("settings", 20, Ui.Muted)),
            (journal, "Activity", (Control)new Avalonia.Controls.Shapes.Path
            {
                Data = (Geometry)Resources["JournalGeometry"]!, Stroke = Ui.Muted, StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round, Stretch = Stretch.Uniform, Width = 20, Height = 20,
                VerticalAlignment = VerticalAlignment.Center
            })
        };
        foreach (var (tab, label, tabIcon) in tabs)
        {
            tab.Content = ButtonContent(Ui.Label(label, 15, tab == autoTab ? Ui.Text : Ui.Muted, tab == autoTab), tabIcon);
            ToolTip.SetTip(tab, label);
        }
        settings.Click += async (_, _) => await OpenSettings();
        _history.TextWrapping = TextWrapping.Wrap; _history.MaxWidth = 420;
        journal.Flyout = new Ui.AnimatedFlyout { Content = new ScrollViewer { Content = _history, MaxHeight = 380 } };
        var captions = Part<StackPanel>("CaptionButtons");
        captions.Children.Add(Ui.CaptionButton("minus", "Minimize to tray", () => WindowState = WindowState.Minimized));
        var maximize = Ui.CaptionButton("square", "Maximize / restore", ToggleMaximize);
        captions.Children.Add(maximize);
        captions.Children.Add(Ui.CaptionButton("x", "Close application", Close, true));
        _status = Part<TextBlock>("ActivityText");
        _statusDot = Part<Border>("ActivityDot");
        _progress = Part<ProgressBar>("ActivityProgress");
        _countries = Part<StackPanel>("CountryRows");
        _countryScroll = Part<ScrollViewer>("CountryScroll");
        _countrySearch = Part<TextBox>("CountrySearch");
        _countrySearch.TextChanged += (_, _) => { _countryScroll.Offset = default; PopulateCountries(); };
        var sideHeading = Ui.Label("Auto-block", bold: true);
        sideHeading.ClearValue(TextBlock.FontSizeProperty);
        sideHeading.Classes.Add("sideHeading");
        var sidebarTitle = _sidebarTitle = Part<Button>("SidebarTitle");
        sidebarTitle.Content = sideHeading;
        var sidebar = Part<Border>("CountrySidebar");
        var mainBody = Part<Grid>("MainBody");
        var sidebarDetails = Part<StackPanel>("SidebarDetails");
        var sidebarFooter = Part<StackPanel>("SidebarFooter");
        void ResizeSidebar() => sidebar.Width = _settings.CountrySidebarCollapsed ? 200 : Math.Clamp(mainBody.Bounds.Width * 0.24, 200, 290);
        _updateSidebar = () =>
        {
            bool sidebarCollapsed = _settings.CountrySidebarCollapsed;
            string hint = sidebarCollapsed ? "Expand auto-block controls" : "Collapse to country list";
            ToolTip.SetTip(sidebarTitle, hint);
            AutomationProperties.SetName(sidebarTitle, hint);
            sideHeading.Text = sidebarCollapsed ? "Countries" : "Auto-block";
            sidebarDetails.MaxHeight = sidebarCollapsed ? 0 : 170;
            sidebarFooter.MaxHeight = sidebarCollapsed ? 0 : 120;
            sidebarDetails.Opacity = sidebarFooter.Opacity = sidebarCollapsed ? 0 : 1;
            sidebarDetails.IsEnabled = sidebarFooter.IsEnabled = !sidebarCollapsed;
            if (sidebarCollapsed) _countrySearch.Text = "";
            ResizeSidebar();
        };
        _updateSidebar();
        mainBody.SizeChanged += (_, _) => ResizeSidebar();
        sidebarTitle.Click += (_, _) =>
        {
            if (_busy) return;
            _settings.CountrySidebarCollapsed = !_settings.CountrySidebarCollapsed;
            try { _settings.Save(); }
            catch (Exception ex)
            {
                _settings.CountrySidebarCollapsed = !_settings.CountrySidebarCollapsed;
                SetStatus("Cannot save sidebar state. " + ex.Message, true);
                return;
            }
            _updateSidebar();
        };
        const string schedule = "Checks every 12 hours";
        var scheduleLabel = Ui.Label(schedule, 13);
        var scheduleGroup = ButtonContent(scheduleLabel, Ui.Icon("clock", 19));
        ToolTip.SetTip(scheduleGroup, schedule);
        Part<ContentControl>("ScheduleLabel").Content = scheduleGroup;
        var sidebarDescription = Part<TextBlock>("SidebarDescription");
        string description = sidebarDescription.Text!;
        SizeChanged += (_, e) =>
        {
            bool compact = e.NewSize.Width < 900 || e.NewSize.Height < 620;
            Classes.Set("compact", compact);
            brandName.IsVisible = e.NewSize.Width >= 690;
            scheduleLabel.Text = compact ? "Checks every 12 h" : schedule;
            sidebarDescription.Text = e.NewSize.Height < 490 ? "Block by country" : description;
            foreach (var (tab, _, _) in tabs)
                ((StackPanel)tab.Content!).Children[1].IsVisible = e.NewSize.Width >= 570;
        };
        var countryActions = Part<Grid>("CountryActions");
        foreach (var button in new[] { _allCountries, _noCountries, _applyCountries })
        {
            button.Padding = new Thickness(3, 0);
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.IsEnabled = _isAdmin;
        }
        ((TextBlock)_allCountries.Content!).FontSize = 12.5;
        ((TextBlock)_noCountries.Content!).FontSize = 12.5;
        ToolTip.SetTip(_allCountries, "Select all countries, including those hidden by search.");
        ToolTip.SetTip(_noCountries, "Deselect all countries.");
        _allCountries.Click += (_, _) => SelectCountries(true);
        _noCountries.Click += (_, _) => SelectCountries(false);
        _applyHost = Part<ContentControl>("ApplyHost");
        _applyHost.Content = _applyCountries;
        _applyCountries.IsEnabled = false;
        _applyCountries.Click += async (_, _) => await Operate(ApplyCountryDraft);
        Ui.Put(countryActions, _allCountries); Ui.Put(countryActions, _noCountries, 1);
        _clear.IsEnabled = _isAdmin;
        _clear.HorizontalAlignment = HorizontalAlignment.Center;
        _clear.MinWidth = 142;
        _clear.Click += async (_, _) => await Operate(ClearAll);
        Part<StackPanel>("SidebarFooter").Children.Add(_clear);

        _actions = Part<WrapPanel>("RelayActions");
        _selectAll.Height = 38; _selectAll.Margin = new Thickness(0, 0, 8, 0);
        ToolTip.SetTip(_selectAll, "Select all locations with IPs, including those hidden by search.");
        _selectAll.IsCheckedChanged += (_, _) =>
        {
            if (_updatingSelection) return;
            foreach (var server in _servers) server.Checked = _selectAll.IsChecked == true && server.Addresses.Count > 0;
            RenderRows();
        };
        _actions.Children.Add(_selectAll);
        AddAction("Block", "lock", () => SetSelected(true), true, true);
        AddAction("Unblock", "lock-open", () => SetSelected(false), true);
        AddAction("Refresh", "refresh", RefreshServers);
        _pingButton = AddAction("Ping", "antenna-bars-5", PingAll);
        _stop.Classes.Add("relayAction");
        _stop.IsVisible = false; _stop.Margin = new Thickness(0, 0, 8, 0); _stop.Click += (_, _) => _pingCancellation?.Cancel();
        _actions.Children.Add(_stop);
        _locationSearch = Part<TextBox>("LocationSearch");
        autoTab.Click += (_, _) => _locationSearch.Focus();
        _noRelays = Part<TextBlock>("NoRelays");
        _summary = Part<TextBlock>("RelaySummary");
        var adminBadge = Part<ContentControl>("AdminBadge");
        var repository = Part<Button>("RepositoryLink");
        ToolTip.SetTip(repository, RepositoryUrl);
        repository.Click += (_, _) =>
        {
            try { using var browser = Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("Cannot open GitHub. " + ex.Message, true); }
        };
        string permission = _isAdmin ? "Administrator access active" : "Administrator access required";
        var adminLabel = Ui.Label(permission, 13, _isAdmin ? Ui.Text : Ui.Accent);
        var adminContent = ButtonContent(adminLabel, Ui.PngIcon("icons/windows-uac.png", 18));
        if (_isAdmin)
        {
            adminBadge.Content = new Border { Child = adminContent, Classes = { "statusBadge" }, Padding = new Thickness(10, 7) };
            ToolTip.SetTip(adminBadge, permission);
        }
        else
        {
            const string restart = "Restart as administrator";
            _adminButton = new Button { Content = adminContent, Classes = { "mainAction", "adminAction" } };
            AutomationProperties.SetName(_adminButton, restart);
            ToolTip.SetTip(_adminButton, restart);
            _adminButton.Click += async (_, _) =>
            {
                try { await RestartAsAdmin(message => SetStatus(message)); }
                catch (Exception ex) { SetStatus("Cannot restart the application. " + ex.Message, true); }
            };
            adminBadge.Content = _adminButton;
        }
        var searchFrame = Part<Border>("LocationSearchFrame");
        var toolbar = Part<Grid>("Toolbar");
        _rows = Part<StackPanel>("RelayRows");
        _rowScroll = Part<ScrollViewer>("RelayScroll");
        _locationSearch.TextChanged += (_, _) => { _rowScroll.Offset = default; RenderRows(); };
        var tableHead = Ui.Grid(_columns); tableHead.HorizontalAlignment = HorizontalAlignment.Left;
        toolbar.SizeChanged += (_, e) =>
        {
            bool stacked = e.NewSize.Width < 840;
            Grid.SetColumn(searchFrame, stacked ? 0 : 1);
            Grid.SetRow(searchFrame, stacked ? 1 : 0);
            searchFrame.Width = stacked ? double.NaN : 208;
            searchFrame.Margin = new Thickness(0, stacked ? 5 : 0, 0, 0);
            bool compactSummary = e.NewSize.Width < 520;
            adminLabel.Text = compactSummary ? (_isAdmin ? "Administrator" : "Admin required") : permission;
            if (_compactSummary != compactSummary)
            {
                _compactSummary = compactSummary;
                UpdateSummary();
            }
            string columns = e.NewSize.Width < 720 ? CompactColumns : WideColumns;
            if (columns == _columns) return;
            _columns = columns;
            tableHead.ColumnDefinitions = ColumnDefinitions.Parse(columns);
            foreach (var row in _rows.Children.OfType<Border>())
                if (row.Child is Grid grid) grid.ColumnDefinitions = ColumnDefinitions.Parse(columns);
        };
        _rowScroll.ScrollChanged += (_, _) =>
        {
            if (_rowScroll.Viewport.Width > 0) tableHead.Width = _rowScroll.Viewport.Width;
        };
        var headerCheck = Ui.Checkbox(new Border(), "Select all rows");
        headerCheck.Width = 34; headerCheck.MinHeight = 0; headerCheck.HorizontalAlignment = HorizontalAlignment.Center;
        ToolTip.SetTip(headerCheck, "Select all locations with IPs, including those hidden by search.");
        headerCheck.IsCheckedChanged += (_, _) =>
        {
            if (_updatingSelection) return;
            if (_busy) { _updatingSelection = true; headerCheck.IsChecked = _selectAll.IsChecked; _updatingSelection = false; return; }
            _selectAll.IsChecked = headerCheck.IsChecked;
        };
        _selectAll.IsCheckedChanged += (_, _) => { _updatingSelection = true; headerCheck.IsChecked = _selectAll.IsChecked; _updatingSelection = false; };
        Ui.Put(tableHead, headerCheck);
        string[] headings = { "Location", "Country", "IP", "Ping" };
        for (int i = 0; i < headings.Length; i++)
        {
            var label = Ui.Label(headings[i], 13, Ui.Muted);
            label.TextAlignment = TextAlignment.Center;
            Ui.Put(tableHead, label, i + 1);
        }
        Ui.Put(tableHead, CreateStatusFilter(), 5);
        Part<ContentControl>("TableHeaderHost").Content = tableHead;
        FitToScreen();
        Screens.Changed += ScreenChanged;

        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open application"); open.Click += (_, _) => Dispatcher.UIThread.Post(RestoreWindow);
        var exit = new NativeMenuItem("Exit"); exit.Click += (_, _) => Dispatcher.UIThread.Post(ExitApplication);
        menu.Items.Add(open); menu.Items.Add(exit);
        _tray = new TrayIcon { Icon = Icon, ToolTipText = Title, Menu = menu, IsVisible = false };
        _tray.Clicked += (_, _) => Dispatcher.UIThread.Post(RestoreWindow);
        _trayIcons = TrayIcon.GetIcons(Application.Current!) ?? new TrayIcons();
        TrayIcon.SetIcons(Application.Current!, _trayIcons); _trayIcons.Add(_tray);
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            maximize.Content = Ui.Icon(WindowState == WindowState.Maximized ? "copy" : "square", 14);
            outer.CornerRadius = new CornerRadius(WindowState == WindowState.Maximized ? 0 : 15);
            if (WindowState == WindowState.Normal) FitToScreen();
            if (WindowState != WindowState.Minimized) return;
            if (_tray.NativeMenuExporter == null) { WindowState = WindowState.Normal; SetStatus("The system tray is unavailable.", true); return; }
            _tray.IsVisible = true; Hide();
        };
        Closing += (_, e) =>
        {
            if (_closingForRestart || e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown) return;
            if (!_exitRequested && _settings.CloseToTray && _tray.NativeMenuExporter != null)
            {
                e.Cancel = true;
                Dispatcher.UIThread.Post(() => WindowState = WindowState.Minimized);
                return;
            }
            if (!_busy) return;
            e.Cancel = true; _pingCancellation?.Cancel(); SetStatus("Wait for the operation to finish before exiting.");
        };
        Closed += (_, _) => { _updateCancellation.Cancel(); Screens.Changed -= ScreenChanged; _timer.Stop(); _trayIcons.Remove(_tray); _tray.Dispose(); };
        _timer.Tick += async (_, _) =>
        {
            UpdateSummary();
            if (_busy || DateTimeOffset.UtcNow < _retryAfter) return;
            if (_refreshPending || ServerCatalog.IsDue(_settings.LastRefreshUtc, DateTimeOffset.UtcNow)) await Operate(RefreshServers);
            else if (_policyPending && _isAdmin) await Operate(ApplyPolicy);
            await CheckForUpdate();
        };
        Opened += async (_, _) =>
        {
            FitToScreen();
            if (BlockerApp.StartInTray) WindowState = WindowState.Minimized;
            await Initialize();
        };
    }

    private async Task Initialize()
    {
        _busy = true; SetEnabled(false);
        SetStatus("Loading the saved list…");
        try
        {
            (_settings, _servers) = await Task.Run(() =>
            {
                var settings = AppSettings.Load();
                if (settings.BlockedCountries == null) throw new FormatException("The country list is corrupted.");
                foreach (string country in settings.BlockedCountries) _ = new System.Globalization.RegionInfo(country);
                var servers = settings.CatalogJson == null ? new List<ServerEntry>() : ServerCatalog.Parse(settings.CatalogJson);
                return (settings, servers);
            });
        }
        catch (Exception ex)
        {
            _busy = false;
            await Ui.MessageWindow($"Cannot read settings. The file was left unchanged.\n{ex.Message}").ShowDialog<bool>(this);
            ExitApplication(); return;
        }
        finally { _busy = false; SetEnabled(true); }
        _updateSidebar();
        _countryDraft.UnionWith(_settings.BlockedCountries);
        PopulateCountries(); RenderRows();
        if (ServerCatalog.IsDue(_settings.LastRefreshUtc, DateTimeOffset.UtcNow) || _servers.Count == 0) await Operate(RefreshServers);
        else await Operate(ApplyPolicy);
        _timer.Start();
        await CheckForUpdate();
    }

    private async Task CheckForUpdate(Action<string>? feedback = null)
    {
        bool manual = feedback != null;
        if (!AppUpdate.IsSupported) return;
        if (_updateInProgress || _busy)
        {
            feedback?.Invoke("Wait for the current operation to finish.");
            return;
        }
        if (!manual && (_settingsWindow != null || HasCountryDraft || DateTimeOffset.UtcNow < _nextUpdateCheck)) return;

        _updateInProgress = true;
        _nextUpdateCheck = DateTimeOffset.UtcNow.AddMinutes(15);
        var cancellation = _updateCancellation.Token;
        string? downloaded = null;
        void Report(string message, bool error = false)
        {
            feedback?.Invoke(message);
            if (!_busy || manual) SetStatus(message, error);
        }

        try
        {
            feedback?.Invoke("Checking GitHub releases…");
            var release = await AppUpdate.Find(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (release == null)
            {
                feedback?.Invoke("You have the latest version.");
                return;
            }

            bool CanInstall() => !_busy && !HasCountryDraft && (manual || _settingsWindow == null);
            if (!CanInstall())
            {
                _nextUpdateCheck = DateTimeOffset.UtcNow.AddMinutes(1);
                feedback?.Invoke($"Version {release.Version} is available. Apply your country selection or wait for the operation to finish.");
                return;
            }

            string executable = Environment.ProcessPath!;
            downloaded = executable + "." + Guid.NewGuid().ToString("N") + ".download";
            Report($"Downloading update v{release.Version}…");
            await AppUpdate.Download(release, downloaded, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!CanInstall())
            {
                _nextUpdateCheck = DateTimeOffset.UtcNow.AddMinutes(1);
                Report("The update will install when the current operation finishes.");
                return;
            }

            Report("Installing the update and restarting…");
            _busy = true; SetEnabled(false);
            try
            {
                bool startInTray = !IsVisible;
                await Task.Run(() => AppUpdate.Install(executable, downloaded, startInTray));
                _closingForRestart = true;
                _settingsWindow?.Close();
                Close();
            }
            finally
            {
                if (!_closingForRestart) { _busy = false; SetEnabled(true); }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            string action = downloaded == null ? "check for" : "install";
            if (manual || downloaded != null) Report($"Could not {action} the update. {ex.Message}", true);
        }
        finally
        {
            _updateInProgress = false;
            if (downloaded != null)
            {
                try { File.Delete(downloaded); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        var dialog = new SettingsWindow();
        if (_isAdmin) dialog.Height = 395;
        dialog.DataContext = new SettingsViewModel(_isAdmin, dialog.Close, RestartAsAdmin, CheckForUpdate,
            _settings.CloseToTray, SetCloseToTray);
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
        {
            dialog.Width = Math.Min(dialog.Width, Math.Max(1, (screen.WorkingArea.Width - 8) / screen.Scaling));
            dialog.Height = Math.Min(dialog.Height, Math.Max(1, (screen.WorkingArea.Height - 8) / screen.Scaling));
        }
        _settingsWindow = dialog;
        try { await dialog.ShowDialog(this); }
        finally { _settingsWindow = null; }
    }

    private void SetCloseToTray(bool enabled)
    {
        if (_busy) throw new InvalidOperationException("Wait for the current operation to finish.");
        bool previous = _settings.CloseToTray;
        _settings.CloseToTray = enabled;
        try { _settings.Save(); }
        catch { _settings.CloseToTray = previous; throw; }
    }

    private async Task RestartAsAdmin(Action<string> feedback)
    {
        if (_busy) { feedback("Wait for the current operation to finish."); return; }
        _busy = true; SetEnabled(false);
        try
        {
            feedback("Confirm the Windows prompt…");
            if (!await Task.Run(DesktopIntegration.StartElevated))
            {
                feedback("Restart cancelled.");
                return;
            }
            _closingForRestart = true;
            _settingsWindow?.Close();
            Close();
        }
        finally
        {
            if (!_closingForRestart) { _busy = false; SetEnabled(true); }
        }
    }

    private static StackPanel ButtonContent(TextBlock text, Control icon) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, text }
    };

    private static Button MainButton(string text, string? icon = null, bool primary = false, bool danger = false)
    {
        var color = primary ? Ui.Background : danger ? Ui.Danger : Ui.Text;
        var label = Ui.Label(text, 13.5, color);
        var button = new Button { Content = icon == null ? label : ButtonContent(label, Ui.Icon(icon, 17, color)) };
        button.Classes.Add("mainAction");
        button.Classes.Set("primary", primary);
        button.Classes.Set("danger", danger);
        AutomationProperties.SetName(button, text);
        return button;
    }

    private Button AddAction(string text, string icon, Func<Task> action, bool admin = false, bool primary = false)
    {
        var button = MainButton(text, icon, primary);
        button.Classes.Add("relayAction");
        button.Classes.Set("blockAction", primary);
        button.Tag = admin; button.IsEnabled = !admin || _isAdmin;
        button.Margin = new Thickness(0, 0, 8, 4);
        button.Click += async (_, _) => await Operate(action);
        _actions.Children.Add(button);
        return button;
    }

    private async Task Operate(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true; SetEnabled(false);
        _progress.IsIndeterminate = true; _progress.IsVisible = true;
        try { await operation(); _retryAfter = DateTimeOffset.MinValue; }
        catch (Exception ex)
        {
            _retryAfter = DateTimeOffset.UtcNow.AddMinutes(5);
            SetStatus(ex.Message + (_refreshPending || _policyPending ? " Automatic checks will retry in 5 minutes." : ""), true);
        }
        finally { _progress.IsVisible = false; _busy = false; SetEnabled(true); RenderRows(); }
    }

    private void SetEnabled(bool enabled)
    {
        _sidebarTitle.IsEnabled = enabled;
        if (_adminButton != null) _adminButton.IsEnabled = enabled;
        foreach (Control control in _actions.Children)
            if (control != _stop) control.IsEnabled = enabled && (!(control.Tag is true) || _isAdmin);
        _countries.IsEnabled = enabled && _isAdmin; _rows.IsEnabled = enabled; _clear.IsEnabled = enabled && _isAdmin;
        _allCountries.IsEnabled = enabled && _isAdmin;
        _noCountries.IsEnabled = enabled && _isAdmin;
        UpdateCountryAction();
    }

    private async Task RefreshServers()
    {
        _refreshPending = true; SetStatus("Fetching the latest Valve list…");
        string json = await CatalogClient.GetStringAsync(ServerCatalog.Url);
        var updated = await Task.Run(() => ServerCatalog.Parse(json));
        var previous = _servers.ToLookup(s => s.Code);
        foreach (var server in updated)
            server.Checked = server.Addresses.Count > 0 && previous[server.Code].Any(s => s.Checked);
        int added = updated.Count(s => !previous.Contains(s.Code));
        _servers = updated; PopulateCountries(); RenderRows();
        _settings.CatalogJson = json; _settings.LastRefreshUtc = DateTimeOffset.UtcNow;
        await Task.Run(_settings.Save);
        _refreshPending = false;
        await ApplyPolicy();
        SetStatus($"Valve list refreshed ({added} new locations)." + (!_isAdmin ? " Administrator access is required to block relays." : ""));
    }

    private async Task ApplyPolicy()
    {
        SetStatus(_isAdmin ? "Applying firewall rules…" : "Checking firewall rules…");
        if (!_isAdmin)
        {
            await Firewall.Sync(_servers);
            SetStatus("Run as administrator to enable automatic blocking."); return;
        }
        _policyPending = true;
        foreach (var server in _servers) server.FirewallKnown = false;
        await Firewall.ApplyCountries(_servers, _settings.BlockedCountries);
        await Firewall.Sync(_servers);
        if (_servers.Any(s => s.Addresses.Count > 0 && _settings.BlockedCountries.Contains(s.Country) && !s.Blocked))
            throw new InvalidOperationException("The firewall did not confirm all automatic rules.");
        _policyPending = false;
        SetStatus("Country rules updated. Manual blocks preserved.");
    }

    private void PopulateCountries()
    {
        _populatingCountries = true; _countries.Children.Clear();
        string query = _countrySearch.Text?.Trim() ?? "";
        foreach (string code in _servers.Select(s => s.Country).Concat(_settings.BlockedCountries).Concat(_countryDraft).Where(c => c.Length > 0)
            .Distinct().OrderByDescending(c => _settings.BlockedCountries.Contains(c)).ThenBy(ServerCatalog.CountryName))
        {
            string countryName = ServerCatalog.CountryName(code);
            if (!countryName.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
                !code.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var content = Ui.Grid("28,*,24"); content.ColumnSpacing = 8;
            var name = Ui.Label(countryName, 14); name.TextAlignment = TextAlignment.Center;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            Ui.Put(content, Ui.Flag(code)); Ui.Put(content, name, 1);
            var count = Ui.Label(_servers.Count(s => s.Country == code).ToString(), 13, Ui.Muted);
            count.TextAlignment = TextAlignment.Center; Ui.Put(content, count, 2);
            var checkbox = Ui.Checkbox(content, countryName, _countryDraft.Contains(code), highlightSelection: true);
            checkbox.Height = 36;
            checkbox.IsCheckedChanged += (_, _) =>
            {
                if (_populatingCountries) return;
                if (checkbox.IsChecked == true) _countryDraft.Add(code); else _countryDraft.Remove(code);
                UpdateCountryAction();
            };
            _countries.Children.Add(checkbox);
        }
        if (query.Length > 0 && _countries.Children.Count == 0)
            _countries.Children.Add(Ui.Label("No countries found", 13, Ui.Muted));
        _populatingCountries = false;
    }

    private bool HasCountryDraft => !_countryDraft.SetEquals(_settings.BlockedCountries);

    private void UpdateCountryAction()
    {
        _applyHost.IsVisible = HasCountryDraft;
        _applyCountries.IsEnabled = !_busy && _isAdmin && _applyHost.IsVisible;
    }

    private void SelectCountries(bool select)
    {
        if (_busy || !_isAdmin) return;
        if (select) _countryDraft.UnionWith(_servers.Select(s => s.Country).Where(c => c.Length > 0).Concat(_settings.BlockedCountries));
        else _countryDraft.Clear();
        PopulateCountries();
        UpdateCountryAction();
    }

    private async Task ApplyCountryDraft()
    {
        if (!_isAdmin || !HasCountryDraft) return;
        var previous = _settings.BlockedCountries;
        _settings.BlockedCountries = new HashSet<string>(_countryDraft, StringComparer.OrdinalIgnoreCase);
        try { await Task.Run(_settings.Save); }
        catch { _settings.BlockedCountries = previous; throw; }
        _policyPending = true;
        await ApplyPolicy();
        PopulateCountries();
    }

    private async Task SetSelected(bool block) => await ChangeManual(_servers.Where(s => s.Checked).ToList(), block);
    private async Task ChangeManual(List<ServerEntry> selected, bool block)
    {
        selected = selected.Where(s => s.Addresses.Count > 0).ToList();
        if (selected.Count == 0) { SetStatus("Select locations with published IP addresses."); return; }
        if (!block && selected.Any(s => _settings.BlockedCountries.Contains(s.Country)))
        {
            SetStatus("Deselect the country on the left and click Apply.", true); return;
        }
        foreach (var server in selected) server.FirewallKnown = false;
        SetStatus(block ? "Blocking selected locations…" : "Removing manual blocks…");
        await Firewall.SetManual(selected, block); await Firewall.Sync(_servers);
        if (selected.Any(s => s.Blocked != block)) throw new InvalidOperationException("The firewall did not confirm all changes.");
        SetStatus(block ? "Selected locations blocked. Restart CS2." : "Manual blocks removed. Restart CS2.");
    }

    private async Task ClearAll()
    {
        if (!await Ui.MessageWindow("Remove all CS2 blocks and disable automatic country blocking?", true).ShowDialog<bool>(this)) return;
        var previous = _settings.BlockedCountries; _settings.BlockedCountries = new HashSet<string>();
        try { await Task.Run(_settings.Save); } catch { _settings.BlockedCountries = previous; throw; }
        _countryDraft.Clear();
        _policyPending = true; PopulateCountries();
        SetStatus("Removing CS2 rules…");
        foreach (var server in _servers) server.FirewallKnown = false;
        await Firewall.Clear(); await Firewall.Sync(_servers);
        if (_servers.Any(s => s.Blocked)) throw new InvalidOperationException("Some CS2 blocks are still active.");
        _policyPending = false; SetStatus("All CS2_Block_* rules removed. Automatic blocking disabled.");
    }

    private async Task PingAll()
    {
        if (_servers.Count == 0) await RefreshServers();
        var targets = _servers.Where(s => s.Addresses.Count > 0).ToList();
        if (targets.Count == 0) return;
        using var cancellation = new CancellationTokenSource(); _pingCancellation = cancellation;
        using var semaphore = new SemaphoreSlim(12);
        _pingButton.IsVisible = false; _stop.IsVisible = true; _stop.IsEnabled = true;
        _progress.IsIndeterminate = false; _progress.IsVisible = true; _progress.Maximum = targets.Count; _progress.Value = 0;
        foreach (var server in targets) server.PingMs = -2;
        SetStatus("Measuring latency… Blocked locations may not respond.");
        try
        {
            await Task.WhenAll(targets.Select(async server =>
            {
                await semaphore.WaitAsync(cancellation.Token);
                try { server.PingMs = await MeasurePing(server.Addresses[0], cancellation.Token); _progress.Value++; }
                finally { semaphore.Release(); }
            }));
        }
        catch (OperationCanceledException) { }
        finally { _pingCancellation = null; _stop.IsVisible = false; _pingButton.IsVisible = true; _progress.IsVisible = false; }
        _servers = _servers.OrderBy(s => s.PingMs < 0 ? int.MaxValue : s.PingMs).ToList();
        SetStatus(cancellation.IsCancellationRequested ? "Check stopped." : $"Check complete · {targets.Count(s => s.PingMs >= 0)} of {targets.Count} responded.");
    }

    private static async Task<int> MeasurePing(string address, CancellationToken cancellation)
    {
        try
        {
            using var ping = new Ping(); var reply = await ping.SendPingAsync(address, 1500).WaitAsync(cancellation);
            if (reply.Status == IPStatus.Success) return (int)reply.RoundtripTime;
        }
        catch (PingException) { }
        try
        {
            using var tcp = new TcpClient(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(1500); var watch = Stopwatch.StartNew();
            await tcp.ConnectAsync(address, 27015, timeout.Token); return (int)watch.ElapsedMilliseconds;
        }
        catch (SocketException) { return -1; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return -1; }
    }

    private void RenderRows()
    {
        var offset = _rowScroll.Offset; _rows.Children.Clear();
        string query = _locationSearch.Text?.Trim() ?? "";
        foreach (var server in _servers)
        {
            var status = GetRelayStatus(server);
            if (_relayStatusFilter is { } selected && selected != status) continue;
            string countryName = ServerCatalog.CountryName(server.Country);
            if (!server.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
                !server.Code.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !countryName.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
                !server.Addresses.Any(ip => ip.Contains(query, StringComparison.Ordinal))) continue;
            var grid = Ui.Grid(_columns); grid.Height = 41;
            var border = new Border { Child = grid, Classes = { "relayRow" } };
            border.Classes.Set("selected", server.Checked);
            var pick = Ui.Checkbox(new Border(), $"Select {server.Description}", server.Checked);
            pick.Width = 34; pick.HorizontalAlignment = HorizontalAlignment.Center; pick.IsEnabled = server.Addresses.Count > 0;
            pick.IsCheckedChanged += (_, _) => { server.Checked = pick.IsChecked == true; border.Classes.Set("selected", server.Checked); UpdateSummary(); };
            Ui.Put(grid, pick);
            string location = server.Country.Length == 0 ? server.Description : System.Text.RegularExpressions.Regex.Replace(server.Description, @"\s*\([^()]*\)\s*$", "");
            var name = Ui.Label(location, 15, bold: true); name.TextAlignment = TextAlignment.Center;
            var locationCell = Ui.Stack(1, name);
            locationCell.Margin = new Thickness(9, 0); locationCell.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(locationCell, server.Description + "\n" + string.Join("\n", server.Addresses)); Ui.Put(grid, locationCell, 1);
            var country = Ui.Grid("28,*"); country.ColumnSpacing = 7; country.Margin = new Thickness(4, 0);
            country.HorizontalAlignment = HorizontalAlignment.Center;
            var countryText = Ui.Label(countryName, 13); countryText.TextAlignment = TextAlignment.Center;
            countryText.ClearValue(TextBlock.FontSizeProperty);
            countryText.Classes.Add("relayCountryName");
            Ui.Put(country, Ui.Flag(server.Country)); Ui.Put(country, countryText, 1);
            ToolTip.SetTip(country, countryName); Ui.Put(grid, country, 2);
            var addressCount = Ui.Label(server.Addresses.Count.ToString(), 14);
            addressCount.TextAlignment = TextAlignment.Center; Ui.Put(grid, addressCount, 3);
            var ping = Ui.Label(server.PingMs == -2 ? "-" : server.PingMs < 0 ? "Timeout" : $"{server.PingMs} ms", 13);
            ping.TextAlignment = TextAlignment.Center; Ui.Put(grid, ping, 4);
            Ui.Put(grid, StatusBadge(server, status), 5);
            border.DoubleTapped += async (_, e) =>
            {
                if (_busy || !_isAdmin || e.Source is Visual visual && (visual is CheckBox || visual.GetVisualAncestors().Any(p => p is CheckBox))) return;
                await Operate(() => ChangeManual(new List<ServerEntry> { server }, !server.Blocked));
            };
            _rows.Children.Add(border);
        }
        _noRelays.IsVisible = (query.Length > 0 || _relayStatusFilter != null) && _rows.Children.Count == 0;
        _rowScroll.Offset = offset; UpdateSummary();
    }

    private Button CreateStatusFilter()
    {
        var label = Ui.Label("Status", 13, Ui.Muted);
        label.TextAlignment = TextAlignment.Center;
        var content = Ui.Grid("14,*"); content.ColumnSpacing = 5;
        Ui.Put(content, Ui.Icon("adjustments-horizontal", 14, Ui.Muted)); Ui.Put(content, label, 1);
        var menu = new Ui.AnimatedMenuFlyout();
        var button = new Button { Classes = { "mainAction", "filterAction" }, Content = content, Flyout = menu,
            HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(button, "Filter by status");
        ToolTip.SetTip(button, "Filter by status");
        foreach (RelayStatus? status in new RelayStatus?[] { null }.Concat(Enum.GetValues<RelayStatus>().Select(value => (RelayStatus?)value)))
        {
            string text = status == null ? "All statuses" : StatusText(status.Value);
            var item = new MenuItem { Header = text, ToggleType = MenuItemToggleType.Radio, GroupName = "RelayStatus", IsChecked = status == null };
            item.Click += (_, _) =>
            {
                foreach (var option in menu.Items.OfType<MenuItem>()) option.IsChecked = option == item;
                _relayStatusFilter = status;
                label.Text = status == null ? "Status" : text;
                label.Foreground = status == null ? Ui.Muted : Ui.Accent;
                ToolTip.SetTip(button, $"Filter by status · {text}");
                _rowScroll.Offset = default;
                RenderRows();
            };
            menu.Items.Add(item);
        }
        return button;
    }

    private RelayStatus GetRelayStatus(ServerEntry server)
    {
        if (server.Addresses.Count == 0) return RelayStatus.NoAddresses;
        if (!server.FirewallKnown) return RelayStatus.Unknown;
        if (server.Blocked) return RelayStatus.Blocked;
        return _settings.BlockedCountries.Contains(server.Country) ? RelayStatus.Pending : RelayStatus.Available;
    }

    private static string StatusText(RelayStatus status) => status switch
    {
        RelayStatus.Available => "Available",
        RelayStatus.Blocked => "Blocked",
        RelayStatus.Pending => "Pending block",
        RelayStatus.Unknown => "Unchecked",
        _ => "No Valve IPs"
    };

    private Control StatusBadge(ServerEntry server, RelayStatus status)
    {
        bool blocked = status == RelayStatus.Blocked;
        string text = blocked && _settings.BlockedCountries.Contains(server.Country) ? "Auto · blocked" : StatusText(status);
        bool unknown = status is RelayStatus.NoAddresses or RelayStatus.Unknown;
        var color = blocked ? Ui.Accent : unknown ? Ui.Muted : Ui.Text;
        var label = Ui.Label(text, 13, color); label.TextAlignment = TextAlignment.Center;
        label.ClearValue(TextBlock.FontSizeProperty);
        var badge = new Border { Child = label, Classes = { "statusBadge", "relayStatus" },
            Height = 28, VerticalAlignment = VerticalAlignment.Center };
        badge.Classes.Set("blocked", blocked);
        ToolTip.SetTip(badge, text); return badge;
    }

    private void UpdateSummary()
    {
        _updatingSelection = true;
        _selectAll.IsChecked = _servers.Any(s => s.Addresses.Count > 0) && _servers.Where(s => s.Addresses.Count > 0).All(s => s.Checked);
        _updatingSelection = false;
        bool uncheckedRules = _servers.Any(s => s.Addresses.Count > 0 && !s.FirewallKnown);
        int blocked = _servers.Count(s => s.Blocked), addresses = _servers.Sum(s => s.Addresses.Count);
        string rules = uncheckedRules ? "rules unchecked" : $"{blocked} blocked";
        string summary = $"{_servers.Count} locations · {addresses} IP · {rules}";
        _summary.Text = _compactSummary
            ? $"{_servers.Count} loc. · {addresses} IP · {(uncheckedRules ? "checking rules" : $"{blocked} blocked")}" : summary;
        string last = _settings.LastRefreshUtc?.ToLocalTime().ToString("dd.MM HH:mm") ?? "never";
        string next = _settings.LastRefreshUtc?.Add(ServerCatalog.RefreshInterval).ToLocalTime().ToString("dd.MM HH:mm") ?? "now";
        ToolTip.SetTip(_summary, $"{summary}\nUpdated: {last}  ·  Next: {next}");
    }
    private void SetStatus(string message, bool error = false)
    {
        string entry = $"{DateTime.Now:HH:mm:ss} · {message}";
        if (_events.Count == 8) _events.Dequeue();
        _events.Enqueue(entry);
        _history.Text = string.Join("\n\n", _events.Reverse());
        _statusDot.Classes.Set("error", error);
        _status.Text = entry; _status.Foreground = error ? Ui.Danger : Ui.Muted; ToolTip.SetTip(_status, message);
    }
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void RestoreWindow() { Show(); WindowState = WindowState.Normal; _tray.IsVisible = false; Activate(); }

    private void ExitApplication()
    {
        _exitRequested = true;
        try { Close(); }
        finally { _exitRequested = false; }
    }

    private void ScreenChanged(object? sender, EventArgs e) => FitToScreen();

    private void FitToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        double availableWidth = Math.Max(1, (area.Width - 8) / screen.Scaling);
        double availableHeight = Math.Max(1, (area.Height - 8) / screen.Scaling);
        MinWidth = Math.Min(740, availableWidth);
        MinHeight = Math.Min(420, availableHeight);
        if (WindowState != WindowState.Normal) return;
        Width = Math.Clamp(Width, MinWidth, availableWidth);
        Height = Math.Clamp(Height, MinHeight, availableHeight);
        if (!IsVisible) return;
        Position = new PixelPoint(
            Math.Clamp(Position.X, area.X, area.Right - (int)Math.Ceiling(Width * screen.Scaling)),
            Math.Clamp(Position.Y, area.Y, area.Bottom - (int)Math.Ceiling(Height * screen.Scaling)));
    }
}
