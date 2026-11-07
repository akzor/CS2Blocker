using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CS2Blocker;

internal sealed class MainWindow : Window
{
    private const string Columns = "36,1.6*,1.1*,38,65,1.2*";
    private static readonly System.Net.Http.HttpClient CatalogClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly bool _isAdmin = Firewall.IsAdmin();
    private readonly StackPanel _countries = Ui.Stack();
    private readonly StackPanel _rows = Ui.Stack();
    private readonly ScrollViewer _rowScroll = new();
    private readonly TextBlock _summary = Ui.Label("", 17, bold: true);
    private readonly TextBlock _status = Ui.Label("", 14, Ui.Muted);
    private readonly TextBlock _history = Ui.Label("", 13);
    private readonly Queue<string> _events = new();
    private readonly ProgressBar _progress = new() { IsVisible = false, Height = 5, Width = 120, Foreground = Ui.Accent };
    private readonly WrapPanel _actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly CheckBox _selectAll = Ui.Checkbox(Ui.Label("Выбрать все", 15), "Выбрать все");
    private readonly Button _stop = Ui.Button("Стоп", "player-stop");
    private readonly Button _clear = Ui.Button("Снять всё", "trash", danger: true);
    private readonly Button _allCountries = Ui.Button("Все страны", "check");
    private readonly Button _noCountries = Ui.Button("Снять выбор", "minus");
    private readonly Button _applyCountries = Ui.Button("Применить", "check", primary: true);
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
    private bool _updateInProgress;
    private Window? _settingsWindow;
    private DateTimeOffset _retryAfter;
    private DateTimeOffset _nextUpdateCheck;

    public MainWindow()
    {
        Title = "CS2 — Блокировщик серверов";
        Width = 1080; Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        FontFamily = Ui.BodyFont; FontSize = 16;
        using (var stream = Ui.Asset("cs2.ico")) Icon = new WindowIcon(stream);

        var root = Ui.Grid("*", "46,38,*");
        var caption = Ui.Grid("32,*,36,36,36,36"); caption.ColumnSpacing = 3; caption.Margin = new Thickness(16, 0, 10, 0);
        Ui.Put(caption, Ui.AppIcon());
        var settings = Ui.CaptionButton("settings", "Настройки", () => { _ = OpenSettings(); });
        Ui.Put(caption, settings, 2);
        var minimize = Ui.CaptionButton("minus", "Свернуть в трей", () => WindowState = WindowState.Minimized);
        var maximize = Ui.CaptionButton("square", "Развернуть / восстановить", ToggleMaximize);
        Ui.Put(caption, minimize, 3); Ui.Put(caption, maximize, 4);
        Ui.Put(caption, Ui.CaptionButton("x", "Закрыть приложение", Close, true), 5);
        var titleBar = new Border { Background = Ui.Surface, BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = caption };
        Ui.AttachWindowDrag(this, titleBar);
        Ui.Put(root, titleBar);

        var activity = Ui.Grid("88,*,88"); activity.ColumnSpacing = 8; activity.Margin = new Thickness(12, 0);
        _status.TextAlignment = TextAlignment.Center;
        _history.TextWrapping = TextWrapping.Wrap; _history.MaxWidth = 420;
        var log = Ui.Button("Журнал", "clock", flat: true);
        log.Height = 32; log.Padding = new Thickness(6, 0); log.HorizontalAlignment = HorizontalAlignment.Stretch;
        log.Flyout = new Flyout { Content = _history };
        _progress.HorizontalAlignment = HorizontalAlignment.Center; _progress.VerticalAlignment = VerticalAlignment.Center;
        Ui.Put(activity, _progress); Ui.Put(activity, _status, 1); Ui.Put(activity, log, 2);
        Ui.Put(root, new Border { Background = Ui.Background, BorderBrush = Ui.Border,
            BorderThickness = new Thickness(0, 0, 0, 1), Child = activity }, 0, 1);

        var body = Ui.Grid("244,*");
        var sidebar = Ui.Grid("*", "32,Auto,38,38,8,*,8,82"); sidebar.Margin = new Thickness(12, 8, 12, 8);
        var sideTitle = Ui.WithIcon("adjustments-horizontal", Ui.Label("Автоблокировка", 19, bold: true));
        sideTitle.HorizontalAlignment = HorizontalAlignment.Center;
        Ui.Put(sidebar, sideTitle);
        var helper = Ui.Label("Выберите страны. Новые IP\nблокируются автоматически.", 13, Ui.Muted);
        helper.TextWrapping = TextWrapping.Wrap; helper.TextAlignment = TextAlignment.Center;
        helper.Margin = new Thickness(0, 4, 0, 8);
        Ui.Put(sidebar, helper, 0, 1);
        var countryActions = Ui.Grid("*,*"); countryActions.ColumnSpacing = 8;
        foreach (var button in new[] { _allCountries, _noCountries, _applyCountries })
        {
            button.Height = 34; button.MinHeight = 0; button.Padding = new Thickness(8, 0);
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.IsEnabled = _isAdmin;
        }
        _allCountries.Content = Ui.Label("Все страны", 13);
        _noCountries.Content = Ui.Label("Снять выбор", 13);
        _allCountries.Click += (_, _) => SelectCountries(true);
        _noCountries.Click += (_, _) => SelectCountries(false);
        _applyCountries.IsEnabled = false;
        _applyCountries.Click += async (_, _) => await Operate(ApplyCountryDraft);
        Ui.Put(countryActions, _allCountries); Ui.Put(countryActions, _noCountries, 1);
        Ui.Put(sidebar, countryActions, 0, 2); Ui.Put(sidebar, _applyCountries, 0, 3); Ui.Put(sidebar, Ui.Line(), 0, 4);
        Ui.Put(sidebar, new ScrollViewer { Content = _countries, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 0, 5);
        Ui.Put(sidebar, Ui.Line(), 0, 6);
        _clear.IsEnabled = _isAdmin;
        _clear.Click += async (_, _) => await Operate(ClearAll);
        var trayHint = Ui.Label("Работает и в трее", 12.5, Ui.Muted); trayHint.TextAlignment = TextAlignment.Center;
        var sideFooter = Ui.Stack(3,
            Ui.WithIcon("clock", Ui.Label("Проверка каждые 12 часов", 13)),
            trayHint, _clear);
        sideFooter.HorizontalAlignment = HorizontalAlignment.Center;
        _clear.HorizontalAlignment = HorizontalAlignment.Center;
        Ui.Put(sidebar, sideFooter, 0, 7);
        Ui.Put(body, new Border { Background = Ui.Surface, Child = sidebar });

        var main = Ui.Grid("*", "Auto,*,34"); main.Margin = new Thickness(12, 10, 10, 0);
        _selectAll.Width = 130; _selectAll.Height = 38; _selectAll.Margin = new Thickness(3, 0, 3, 6);
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
        _stop.IsVisible = false; _stop.Margin = new Thickness(3, 0, 3, 6); _stop.Click += (_, _) => _pingCancellation?.Cancel();
        _actions.Children.Add(_stop);
        Ui.Put(main, _actions);

        var catalog = Ui.Grid("*", "38,*");
        var tableHead = Ui.Grid(Columns); tableHead.HorizontalAlignment = HorizontalAlignment.Left;
        _rowScroll.ScrollChanged += (_, _) =>
        {
            if (_rowScroll.Viewport.Width > 0) tableHead.Width = _rowScroll.Viewport.Width;
        };
        var headerCheck = Ui.Checkbox(new Border(), "Выбрать все строки");
        headerCheck.Width = 34; headerCheck.MinHeight = 0; headerCheck.HorizontalAlignment = HorizontalAlignment.Center;
        headerCheck.IsCheckedChanged += (_, _) =>
        {
            if (_updatingSelection) return;
            if (_busy) { _updatingSelection = true; headerCheck.IsChecked = _selectAll.IsChecked; _updatingSelection = false; return; }
            _selectAll.IsChecked = headerCheck.IsChecked;
        };
        _selectAll.IsCheckedChanged += (_, _) => { _updatingSelection = true; headerCheck.IsChecked = _selectAll.IsChecked; _updatingSelection = false; };
        Ui.Put(tableHead, headerCheck);
        string[] headings = { "Локация", "Страна", "IP", "Пинг", "Статус" };
        for (int i = 0; i < headings.Length; i++)
        {
            var label = Ui.Label(headings[i], 13, Ui.Muted, bold: true); label.TextAlignment = TextAlignment.Center;
            Ui.Put(tableHead, label, i + 1);
        }
        Ui.Put(catalog, new Border { Background = Ui.Surface, BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = tableHead });
        _rowScroll.Content = _rows;
        _rowScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _rowScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        Ui.Put(catalog, _rowScroll, 0, 1);
        Ui.Put(main, Ui.Round(catalog), 0, 1);
        _progress.Width = 72;
        _summary.FontSize = 13.5; _summary.FontWeight = FontWeight.Normal; _summary.Foreground = Ui.Muted;
        _summary.TextAlignment = TextAlignment.Center;
        Ui.Put(main, _summary, 0, 2);
        Ui.Put(body, main, 1); Ui.Put(root, body, 0, 2);
        var outer = Ui.Round(root, radius: 18);
        Content = outer;
        FitToScreen();
        Screens.Changed += ScreenChanged;

        var menu = new NativeMenu();
        var open = new NativeMenuItem("Открыть приложение"); open.Click += (_, _) => Dispatcher.UIThread.Post(RestoreWindow);
        var exit = new NativeMenuItem("Выход"); exit.Click += (_, _) => Dispatcher.UIThread.Post(Close);
        menu.Items.Add(open); menu.Items.Add(exit);
        using var icon = Ui.Asset("cs2.ico");
        _tray = new TrayIcon { Icon = new WindowIcon(icon), ToolTipText = Title, Menu = menu, IsVisible = false };
        _tray.Clicked += (_, _) => Dispatcher.UIThread.Post(RestoreWindow);
        _trayIcons = TrayIcon.GetIcons(Application.Current!) ?? new TrayIcons();
        TrayIcon.SetIcons(Application.Current!, _trayIcons); _trayIcons.Add(_tray);
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            maximize.Content = Ui.Icon(WindowState == WindowState.Maximized ? "copy" : "square", 17);
            outer.CornerRadius = new CornerRadius(WindowState == WindowState.Maximized ? 0 : 18);
            if (WindowState == WindowState.Normal) FitToScreen();
            if (WindowState != WindowState.Minimized) return;
            if (_tray.NativeMenuExporter == null) { WindowState = WindowState.Normal; SetStatus("Системный трей недоступен.", true); return; }
            _tray.IsVisible = true; Hide();
        };
        Closing += (_, e) =>
        {
            if (_closingForRestart || !_busy || e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown) return;
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
            Close(); return;
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
        dialog.DataContext = new SettingsViewModel(_isAdmin, dialog.Close, RestartAsAdmin, CheckForUpdate);
        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            dialog.Width = Math.Min(dialog.Width, (screen.WorkingArea.Width - 8) / screen.Scaling);
            dialog.MaxHeight = Math.Min(dialog.MaxHeight, (screen.WorkingArea.Height - 8) / screen.Scaling);
        }
        _settingsWindow = dialog;
        try { await dialog.ShowDialog(this); }
        finally { _settingsWindow = null; }
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

    private Button AddAction(string text, string icon, Func<Task> action, bool admin = false, bool primary = false)
    {
        var button = Ui.Button(text, icon, primary, flat: true);
        button.Tag = admin; button.IsEnabled = !admin || _isAdmin;
        button.MinWidth = 82;
        button.Margin = new Thickness(3, 0, 3, 6);
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
        foreach (Control control in _actions.Children)
            if (control != _stop) control.IsEnabled = enabled && (!(control.Tag is true) || _isAdmin);
        _countries.IsEnabled = enabled && _isAdmin; _rows.IsEnabled = enabled; _clear.IsEnabled = enabled && _isAdmin;
        _allCountries.IsEnabled = enabled && _isAdmin;
        _noCountries.IsEnabled = enabled && _isAdmin;
        _applyCountries.IsEnabled = enabled && _isAdmin && HasCountryDraft;
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
        SetStatus($"Список Valve обновлён · новых точек: {added}." + (!_isAdmin ? " Для блокировки нужны права администратора." : ""));
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
        foreach (string code in _servers.Select(s => s.Country).Concat(_settings.BlockedCountries).Concat(_countryDraft).Where(c => c.Length > 0)
            .Distinct().OrderByDescending(c => _settings.BlockedCountries.Contains(c)).ThenBy(ServerCatalog.CountryName))
        {
            var content = Ui.Grid("28,*,24"); content.ColumnSpacing = 8;
            Ui.Put(content, Ui.Flag(code)); Ui.Put(content, Ui.Label(ServerCatalog.CountryName(code), 14), 1);
            var count = Ui.Label(_servers.Count(s => s.Country == code).ToString(), 13, Ui.Muted);
            count.TextAlignment = TextAlignment.Center; Ui.Put(content, count, 2);
            var checkbox = Ui.Checkbox(content, ServerCatalog.CountryName(code), _countryDraft.Contains(code));
            checkbox.Height = 38;
            checkbox.IsCheckedChanged += (_, _) =>
            {
                if (_populatingCountries) return;
                if (checkbox.IsChecked == true) _countryDraft.Add(code); else _countryDraft.Remove(code);
                _applyCountries.IsEnabled = !_busy && _isAdmin && HasCountryDraft;
            };
            _countries.Children.Add(checkbox);
        }
        _populatingCountries = false;
    }

    private bool HasCountryDraft => !_countryDraft.SetEquals(_settings.BlockedCountries);

    private void SelectCountries(bool select)
    {
        if (_busy || !_isAdmin) return;
        if (select) _countryDraft.UnionWith(_servers.Select(s => s.Country).Where(c => c.Length > 0).Concat(_settings.BlockedCountries));
        else _countryDraft.Clear();
        PopulateCountries();
        _applyCountries.IsEnabled = HasCountryDraft;
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
        foreach (var server in _servers)
        {
            var grid = Ui.Grid(Columns); grid.Height = 44;
            var border = new Border { BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 0, 0, 1), Background = server.Checked ? Ui.Selection : Ui.Background, Child = grid };
            var pick = Ui.Checkbox(new Border(), $"Выбрать {server.Description}", server.Checked);
            pick.Width = 34; pick.HorizontalAlignment = HorizontalAlignment.Center; pick.IsEnabled = server.Addresses.Count > 0;
            pick.IsCheckedChanged += (_, _) => { server.Checked = pick.IsChecked == true; border.Background = server.Checked ? Ui.Selection : Ui.Background; UpdateSummary(); };
            Ui.Put(grid, pick);
            string location = server.Country.Length == 0 ? server.Description : System.Text.RegularExpressions.Regex.Replace(server.Description, @"\s*\([^()]*\)\s*$", "");
            var code = Ui.Label(server.Code, 12, Ui.Muted); code.FontFamily = Ui.MonoFont; code.TextAlignment = TextAlignment.Center;
            var name = Ui.Label(location, 15, bold: true); name.TextAlignment = TextAlignment.Center;
            var locationCell = Ui.Stack(1, name, code);
            locationCell.Margin = new Thickness(6, 0); locationCell.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(locationCell, server.Description + "\n" + string.Join("\n", server.Addresses)); Ui.Put(grid, locationCell, 1);
            var country = Ui.Grid("28,*"); country.ColumnSpacing = 7; country.Margin = new Thickness(5, 0);
            country.HorizontalAlignment = HorizontalAlignment.Center;
            Ui.Put(country, Ui.Flag(server.Country)); Ui.Put(country, Ui.Label(ServerCatalog.CountryName(server.Country), 13), 1);
            ToolTip.SetTip(country, ServerCatalog.CountryName(server.Country)); Ui.Put(grid, country, 2);
            var addressCount = Ui.Label(server.Addresses.Count.ToString(), 13); addressCount.FontFamily = Ui.MonoFont;
            addressCount.TextAlignment = TextAlignment.Center; Ui.Put(grid, addressCount, 3);
            var ping = Ui.Label(server.PingMs == -2 ? "—" : server.PingMs < 0 ? "Таймаут" : $"{server.PingMs} мс", 13);
            ping.FontFamily = Ui.MonoFont; ping.TextAlignment = TextAlignment.Center; Ui.Put(grid, ping, 4);
            Ui.Put(grid, StatusBadge(server), 5);
            border.DoubleTapped += async (_, e) =>
            {
                if (_busy || !_isAdmin || e.Source is Visual visual && (visual is CheckBox || visual.GetVisualAncestors().Any(p => p is CheckBox))) return;
                await Operate(() => ChangeManual(new List<ServerEntry> { server }, !server.Blocked));
            };
            _rows.Children.Add(border);
        }
        _rowScroll.Offset = offset; UpdateSummary();
    }

    private Control StatusBadge(ServerEntry server)
    {
        bool automatic = _settings.BlockedCountries.Contains(server.Country), blocked = server.FirewallKnown && server.Blocked;
        string text = server.Addresses.Count == 0 ? "Нет IP от Valve" : !server.FirewallKnown ? "Не проверен" : blocked ? automatic ? "Авто · блок" : "Заблокирован" : automatic ? "Ожидает блокировки" : "Доступен";
        bool unknown = server.Addresses.Count == 0 || !server.FirewallKnown;
        var color = blocked ? Ui.Accent : unknown ? Ui.Muted : Ui.Text;
        var label = Ui.Label(text, 12, color); label.TextAlignment = TextAlignment.Center;
        var badge = Ui.Round(label, blocked ? Ui.Selection : Ui.Surface, 8);
        badge.Padding = new Thickness(6, 0); badge.Height = 26; badge.Margin = new Thickness(4, 0);
        badge.VerticalAlignment = VerticalAlignment.Center;
        badge.MaxWidth = 175; ToolTip.SetTip(badge, text); return badge;
    }

    private void UpdateSummary()
    {
        _updatingSelection = true;
        _selectAll.IsChecked = _servers.Any(s => s.Addresses.Count > 0) && _servers.Where(s => s.Addresses.Count > 0).All(s => s.Checked);
        _updatingSelection = false;
        string rules = _servers.Any(s => s.Addresses.Count > 0 && !s.FirewallKnown)
            ? "правила не проверены" : $"{_servers.Count(s => s.Blocked)} заблокировано";
        _summary.Text = $"{_servers.Count} локаций · {_servers.Sum(s => s.Addresses.Count)} IP · {rules}";
        string last = _settings.LastRefreshUtc?.ToLocalTime().ToString("dd.MM HH:mm") ?? "ещё не было";
        string next = _settings.LastRefreshUtc?.Add(ServerCatalog.RefreshInterval).ToLocalTime().ToString("dd.MM HH:mm") ?? "сейчас";
        ToolTip.SetTip(_summary, $"Обновлено: {last}  ·  Следующее: {next}");
    }
    private void SetStatus(string message, bool error = false)
    {
        string entry = $"{DateTime.Now:HH:mm:ss} · {message}";
        if (_events.Count == 8) _events.Dequeue();
        _events.Enqueue(entry);
        _history.Text = string.Join("\n\n", _events.Reverse());
        _status.Text = entry; _status.Foreground = error ? Ui.Danger : Ui.Muted; ToolTip.SetTip(_status, message);
    }
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void RestoreWindow() { Show(); WindowState = WindowState.Normal; _tray.IsVisible = false; Activate(); }

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
