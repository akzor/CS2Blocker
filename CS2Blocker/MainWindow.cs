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
    private readonly TextBlock _history = Ui.Label("", 13);
    private readonly Queue<string> _events = new();
    private readonly ProgressBar _progress;
    private readonly WrapPanel _actions;
    private readonly CheckBox _selectAll = Ui.Checkbox(Ui.Label("Выбрать все", 14), "Выбрать все");
    private readonly Button _stop = MainButton("Стоп", "player-stop");
    private readonly Button _clear = MainButton("Снять всё", "trash", danger: true);
    private readonly Button _allCountries = MainButton("Все страны");
    private readonly Button _noCountries = MainButton("Снять выбор");
    private readonly Button _applyCountries = MainButton("Применить", "check", primary: true);
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
            (autoTab, "Автоблокировка", Ui.Icon("adjustments-horizontal", 20)),
            (settings, "Настройки", Ui.Icon("settings", 20, Ui.Muted)),
            (journal, "Журнал", (Control)new Avalonia.Controls.Shapes.Path
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
        journal.Flyout = new Flyout { Content = new ScrollViewer { Content = _history, MaxHeight = 380 } };
        var captions = Part<StackPanel>("CaptionButtons");
        captions.Children.Add(Ui.CaptionButton("minus", "Свернуть в трей", () => WindowState = WindowState.Minimized));
        var maximize = Ui.CaptionButton("square", "Развернуть / восстановить", ToggleMaximize);
        captions.Children.Add(maximize);
        captions.Children.Add(Ui.CaptionButton("x", "Закрыть приложение", Close, true));
        _status = Part<TextBlock>("ActivityText");
        _statusDot = Part<Border>("ActivityDot");
        _progress = Part<ProgressBar>("ActivityProgress");
        _countries = Part<StackPanel>("CountryRows");
        _countryScroll = Part<ScrollViewer>("CountryScroll");
        _countrySearch = Part<TextBox>("CountrySearch");
        _countrySearch.TextChanged += (_, _) => { _countryScroll.Offset = default; PopulateCountries(); };
        var sideHeading = Ui.Label("Автоблокировка", bold: true);
        sideHeading.ClearValue(TextBlock.FontSizeProperty);
        sideHeading.Classes.Add("sideHeading");
        Part<ContentControl>("SidebarTitle").Content = ButtonContent(sideHeading, Ui.Icon("adjustments-horizontal", 21));
        const string schedule = "Проверка каждые 12 часов";
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
            scheduleLabel.Text = compact ? "Проверка раз в 12 ч" : schedule;
            sidebarDescription.Text = e.NewSize.Height < 490 ? "Блокировка по странам" : description;
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
        ToolTip.SetTip(_allCountries, "Отметить все страны, включая скрытые поиском.");
        ToolTip.SetTip(_noCountries, "Снять отметки со всех стран.");
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
        ToolTip.SetTip(_selectAll, "Выбрать все локации с IP, включая скрытые поиском.");
        _selectAll.IsCheckedChanged += (_, _) =>
        {
            if (_updatingSelection) return;
            foreach (var server in _servers) server.Checked = _selectAll.IsChecked == true && server.Addresses.Count > 0;
            RenderRows();
        };
        _actions.Children.Add(_selectAll);
        AddAction("Блокировать", "lock", () => SetSelected(true), true, true);
        AddAction("Разблокировать", "lock-open", () => SetSelected(false), true);
        AddAction("Обновить", "refresh", RefreshServers);
        _pingButton = AddAction("Пинг", "antenna-bars-5", PingAll);
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
            catch (Exception ex) { SetStatus("Не удалось открыть GitHub. " + ex.Message, true); }
        };
        string permission = _isAdmin ? "Права администратора активны" : "Нужны права администратора";
        var adminLabel = Ui.Label(permission, 13, _isAdmin ? Ui.Text : Ui.Accent);
        var adminContent = ButtonContent(adminLabel, Ui.PngIcon("icons/windows-uac.png", 18));
        if (_isAdmin)
        {
            adminBadge.Content = new Border { Child = adminContent, Classes = { "statusBadge" }, Padding = new Thickness(10, 7) };
            ToolTip.SetTip(adminBadge, permission);
        }
        else
        {
            const string restart = "Перезапустить от администратора";
            _adminButton = new Button { Content = adminContent, Classes = { "mainAction", "adminAction" } };
            AutomationProperties.SetName(_adminButton, restart);
            ToolTip.SetTip(_adminButton, restart);
            _adminButton.Click += async (_, _) =>
            {
                try { await RestartAsAdmin(message => SetStatus(message)); }
                catch (Exception ex) { SetStatus("Не удалось перезапустить приложение. " + ex.Message, true); }
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
            adminLabel.Text = compactSummary ? (_isAdmin ? "Администратор" : "Нужны права") : permission;
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
        var headerCheck = Ui.Checkbox(new Border(), "Выбрать все строки");
        headerCheck.Width = 34; headerCheck.MinHeight = 0; headerCheck.HorizontalAlignment = HorizontalAlignment.Center;
        ToolTip.SetTip(headerCheck, "Выбрать все локации с IP, включая скрытые поиском.");
        headerCheck.IsCheckedChanged += (_, _) =>
        {
            if (_updatingSelection) return;
            if (_busy) { _updatingSelection = true; headerCheck.IsChecked = _selectAll.IsChecked; _updatingSelection = false; return; }
            _selectAll.IsChecked = headerCheck.IsChecked;
        };
        _selectAll.IsCheckedChanged += (_, _) => { _updatingSelection = true; headerCheck.IsChecked = _selectAll.IsChecked; _updatingSelection = false; };
        Ui.Put(tableHead, headerCheck);
        string[] headings = { "Локация", "Страна", "IP", "Пинг" };
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
        var open = new NativeMenuItem("Открыть приложение"); open.Click += (_, _) => Dispatcher.UIThread.Post(RestoreWindow);
        var exit = new NativeMenuItem("Выход"); exit.Click += (_, _) => Dispatcher.UIThread.Post(ExitApplication);
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
            if (_tray.NativeMenuExporter == null) { WindowState = WindowState.Normal; SetStatus("Системный трей недоступен.", true); return; }
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
            e.Cancel = true; _pingCancellation?.Cancel(); SetStatus("Дождитесь завершения операции перед выходом.");
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
        SetStatus("Загружаем сохранённый список…");
        try
        {
            (_settings, _servers) = await Task.Run(() =>
            {
                var settings = AppSettings.Load();
                if (settings.BlockedCountries == null) throw new FormatException("Повреждён список стран.");
                foreach (string country in settings.BlockedCountries) _ = new System.Globalization.RegionInfo(country);
                var servers = settings.CatalogJson == null ? new List<ServerEntry>() : ServerCatalog.Parse(settings.CatalogJson);
                return (settings, servers);
            });
        }
        catch (Exception ex)
        {
            _busy = false;
            await Ui.MessageWindow($"Не удалось прочитать настройки. Файл не изменён.\n{ex.Message}").ShowDialog<bool>(this);
            ExitApplication(); return;
        }
        finally { _busy = false; SetEnabled(true); }
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
            feedback?.Invoke("Дождитесь завершения текущей операции.");
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
            feedback?.Invoke("Проверяем релизы GitHub…");
            var release = await AppUpdate.Find(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (release == null)
            {
                feedback?.Invoke("Установлена последняя версия.");
                return;
            }

            bool CanInstall() => !_busy && !HasCountryDraft && (manual || _settingsWindow == null);
            if (!CanInstall())
            {
                _nextUpdateCheck = DateTimeOffset.UtcNow.AddMinutes(1);
                feedback?.Invoke($"Доступна v{release.Version}. Примените выбор стран или дождитесь завершения операции.");
                return;
            }

            string executable = Environment.ProcessPath!;
            downloaded = executable + "." + Guid.NewGuid().ToString("N") + ".download";
            Report($"Скачиваем обновление v{release.Version}…");
            await AppUpdate.Download(release, downloaded, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!CanInstall())
            {
                _nextUpdateCheck = DateTimeOffset.UtcNow.AddMinutes(1);
                Report("Обновление установится после завершения текущей работы.");
                return;
            }

            Report("Устанавливаем обновление и перезапускаем приложение…");
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
            string action = downloaded == null ? "проверить" : "установить";
            if (manual || downloaded != null) Report($"Не удалось {action} обновление. {ex.Message}", true);
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
        if (_isAdmin) dialog.Height = 370;
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
        if (_busy) throw new InvalidOperationException("Дождитесь завершения текущей операции.");
        bool previous = _settings.CloseToTray;
        _settings.CloseToTray = enabled;
        try { _settings.Save(); }
        catch { _settings.CloseToTray = previous; throw; }
    }

    private async Task RestartAsAdmin(Action<string> feedback)
    {
        if (_busy) { feedback("Дождитесь завершения текущей операции."); return; }
        _busy = true; SetEnabled(false);
        try
        {
            feedback("Подтвердите запуск в окне Windows…");
            if (!await Task.Run(DesktopIntegration.StartElevated))
            {
                feedback("Перезапуск отменён.");
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
            SetStatus(ex.Message + (_refreshPending || _policyPending ? " Автопроверка повторится через 5 минут." : ""), true);
        }
        finally { _progress.IsVisible = false; _busy = false; SetEnabled(true); RenderRows(); }
    }

    private void SetEnabled(bool enabled)
    {
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
        _refreshPending = true; SetStatus("Получаем актуальный список Valve…");
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
        SetStatus($"Список Valve обновлён ({added} новых локаций)." + (!_isAdmin ? " Для блокировки нужны права администратора." : ""));
    }

    private async Task ApplyPolicy()
    {
        SetStatus(_isAdmin ? "Применяем правила фаервола…" : "Проверяем правила фаервола…");
        if (!_isAdmin)
        {
            await Firewall.Sync(_servers);
            SetStatus("Для автоблокировки запустите приложение от администратора."); return;
        }
        _policyPending = true;
        foreach (var server in _servers) server.FirewallKnown = false;
        await Firewall.ApplyCountries(_servers, _settings.BlockedCountries);
        await Firewall.Sync(_servers);
        if (_servers.Any(s => s.Addresses.Count > 0 && _settings.BlockedCountries.Contains(s.Country) && !s.Blocked))
            throw new InvalidOperationException("Не все автоматические правила подтверждены фаерволом.");
        _policyPending = false;
        SetStatus("Правила выбранных стран обновлены. Ручные блокировки сохранены.");
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
            _countries.Children.Add(Ui.Label("Страны не найдены", 13, Ui.Muted));
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
        if (selected.Count == 0) { SetStatus("Выберите точки с опубликованными IP-адресами."); return; }
        if (!block && selected.Any(s => _settings.BlockedCountries.Contains(s.Country)))
        {
            SetStatus("Снимите отметку страны слева и нажмите «Применить».", true); return;
        }
        foreach (var server in selected) server.FirewallKnown = false;
        SetStatus(block ? "Блокируем выбранные локации…" : "Снимаем ручные блокировки…");
        await Firewall.SetManual(selected, block); await Firewall.Sync(_servers);
        if (selected.Any(s => s.Blocked != block)) throw new InvalidOperationException("Фаервол не подтвердил все изменения.");
        SetStatus(block ? "Выбранные точки заблокированы. Перезапустите CS2." : "Ручные блокировки сняты. Перезапустите CS2.");
    }

    private async Task ClearAll()
    {
        if (!await Ui.MessageWindow("Снять все блокировки CS2 и отключить автоблокировку стран?", true).ShowDialog<bool>(this)) return;
        var previous = _settings.BlockedCountries; _settings.BlockedCountries = new HashSet<string>();
        try { await Task.Run(_settings.Save); } catch { _settings.BlockedCountries = previous; throw; }
        _countryDraft.Clear();
        _policyPending = true; PopulateCountries();
        SetStatus("Удаляем правила CS2…");
        foreach (var server in _servers) server.FirewallKnown = false;
        await Firewall.Clear(); await Firewall.Sync(_servers);
        if (_servers.Any(s => s.Blocked)) throw new InvalidOperationException("Остались активные блокировки CS2.");
        _policyPending = false; SetStatus("Все правила CS2_Block_* удалены. Автоблокировка отключена.");
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
        SetStatus("Проверяем задержку… Заблокированные точки могут не отвечать.");
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
        SetStatus(cancellation.IsCancellationRequested ? "Проверка остановлена." : $"Проверка завершена · ответили {targets.Count(s => s.PingMs >= 0)} из {targets.Count}.");
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
            var pick = Ui.Checkbox(new Border(), $"Выбрать {server.Description}", server.Checked);
            pick.Width = 34; pick.HorizontalAlignment = HorizontalAlignment.Center; pick.IsEnabled = server.Addresses.Count > 0;
            pick.IsCheckedChanged += (_, _) => { server.Checked = pick.IsChecked == true; border.Classes.Set("selected", server.Checked); UpdateSummary(); };
            Ui.Put(grid, pick);
            string location = server.Country.Length == 0 ? server.Description : System.Text.RegularExpressions.Regex.Replace(server.Description, @"\s*\([^()]*\)\s*$", "");
            var code = Ui.Label(server.Code, 12, Ui.Muted); code.TextAlignment = TextAlignment.Center;
            var name = Ui.Label(location, 15, bold: true); name.TextAlignment = TextAlignment.Center;
            var locationCell = Ui.Stack(1, name, code);
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
            var ping = Ui.Label(server.PingMs == -2 ? "-" : server.PingMs < 0 ? "Таймаут" : $"{server.PingMs} мс", 13);
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
        var label = Ui.Label("Статус", 13, Ui.Muted);
        label.TextAlignment = TextAlignment.Center;
        var content = Ui.Grid("14,*"); content.ColumnSpacing = 5;
        Ui.Put(content, Ui.Icon("adjustments-horizontal", 14, Ui.Muted)); Ui.Put(content, label, 1);
        var menu = new MenuFlyout();
        var button = new Button { Classes = { "mainAction", "filterAction" }, Content = content, Flyout = menu,
            HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(button, "Фильтр по статусу");
        ToolTip.SetTip(button, "Фильтр по статусу");
        foreach (RelayStatus? status in new RelayStatus?[] { null }.Concat(Enum.GetValues<RelayStatus>().Select(value => (RelayStatus?)value)))
        {
            string text = status == null ? "Все статусы" : StatusText(status.Value);
            var item = new MenuItem { Header = text, ToggleType = MenuItemToggleType.Radio, GroupName = "RelayStatus", IsChecked = status == null };
            item.Click += (_, _) =>
            {
                foreach (var option in menu.Items.OfType<MenuItem>()) option.IsChecked = option == item;
                _relayStatusFilter = status;
                label.Text = status == null ? "Статус" : text;
                label.Foreground = status == null ? Ui.Muted : Ui.Accent;
                ToolTip.SetTip(button, $"Фильтр по статусу · {text}");
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
        RelayStatus.Available => "Доступен",
        RelayStatus.Blocked => "Заблокирован",
        RelayStatus.Pending => "Ожидает блокировки",
        RelayStatus.Unknown => "Не проверен",
        _ => "Нет IP от Valve"
    };

    private Control StatusBadge(ServerEntry server, RelayStatus status)
    {
        bool blocked = status == RelayStatus.Blocked;
        string text = blocked && _settings.BlockedCountries.Contains(server.Country) ? "Авто · блок" : StatusText(status);
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
        string rules = uncheckedRules ? "правила не проверены" : $"{blocked} заблокировано";
        string summary = $"{_servers.Count} локаций · {addresses} IP · {rules}";
        _summary.Text = _compactSummary
            ? $"{_servers.Count} локац. · {addresses} IP · {(uncheckedRules ? "проверка правил" : $"{blocked} блок.")}" : summary;
        string last = _settings.LastRefreshUtc?.ToLocalTime().ToString("dd.MM HH:mm") ?? "ещё не было";
        string next = _settings.LastRefreshUtc?.Add(ServerCatalog.RefreshInterval).ToLocalTime().ToString("dd.MM HH:mm") ?? "сейчас";
        ToolTip.SetTip(_summary, $"{summary}\nОбновлено: {last}  ·  Следующее: {next}");
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
