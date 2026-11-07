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
    private const string Columns = "44,*,210,58,90,188";
    private readonly bool _isAdmin = Firewall.IsAdmin();
    private readonly StackPanel _countries = Ui.Stack();
    private readonly StackPanel _rows = Ui.Stack();
    private readonly ScrollViewer _rowScroll = new();
    private readonly TextBlock _summary = Ui.Label("", 17, bold: true);
    private readonly TextBlock _schedule = Ui.Label("", 15, Ui.Muted);
    private readonly TextBlock _status = Ui.Label("", 14, Ui.Muted);
    private readonly ProgressBar _progress = new() { IsVisible = false, Height = 5, Width = 120, Foreground = Ui.Accent };
    private readonly WrapPanel _actions = new() { Orientation = Orientation.Horizontal };
    private readonly CheckBox _selectAll = Ui.Checkbox(Ui.Label("Выбрать все", 15), "Выбрать все");
    private readonly Button _stop = Ui.Button("Стоп", "player-stop");
    private readonly Button _clear = Ui.Button("Снять всё", "trash", danger: true);
    private readonly Button _allCountries = Ui.Button("Все страны", "check");
    private readonly Button _noCountries = Ui.Button("Снять выбор", "minus");
    private Button _pingButton = null!;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly TrayIcon _tray;
    private readonly TrayIcons _trayIcons;
    private AppSettings _settings = new();
    private List<ServerEntry> _servers = new();
    private CancellationTokenSource? _pingCancellation;
    private bool _busy, _populatingCountries, _updatingSelection, _policyPending, _refreshPending;
    private bool _closingForRestart;
    private Window? _settingsWindow;
    private DateTimeOffset _retryAfter;

    public MainWindow()
    {
        Title = "CS2 — Блокировщик серверов";
        Width = 1440; Height = 1024; MinWidth = 1180; MinHeight = 780;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        FontFamily = Ui.BodyFont; FontSize = 16;
        using (var stream = Ui.Asset("cs2.ico")) Icon = new WindowIcon(stream);

        var root = Ui.Grid("*", "46,*");
        var caption = Ui.Grid("32,*,26,26,26,26"); caption.ColumnSpacing = 6; caption.Margin = new Thickness(16, 0, 12, 0);
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

        var body = Ui.Grid("344,*");
        var sidebar = Ui.Grid("*", "44,82,40,16,*,18,102"); sidebar.Margin = new Thickness(22, 20, 22, 16);
        Ui.Put(sidebar, Ui.WithIcon("adjustments-horizontal", Ui.Label("Автоблокировка", 23, bold: true)));
        var helper = Ui.Label("Новые IP выбранных стран\nблокируются автоматически.", 16, Ui.Muted); helper.TextWrapping = TextWrapping.Wrap;
        Ui.Put(sidebar, helper, 0, 1);
        var countryActions = Ui.Grid("*,*"); countryActions.ColumnSpacing = 8;
        foreach (var button in new[] { _allCountries, _noCountries })
        {
            button.Height = 34; button.MinHeight = 0; button.Padding = new Thickness(8, 0);
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.IsEnabled = _isAdmin;
        }
        ToolTip.SetTip(_allCountries, "Выбрать все страны и сразу применить автоблокировку одним действием.");
        ToolTip.SetTip(_noCountries, "Снять автоматические блокировки всех стран. Ручные правила сохранятся.");
        _allCountries.Click += async (_, _) => await SetCountries(new HashSet<string>(
            _servers.Select(s => s.Country).Concat(_settings.BlockedCountries).Where(c => c.Length > 0), StringComparer.OrdinalIgnoreCase));
        _noCountries.Click += async (_, _) => await SetCountries(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Ui.Put(countryActions, _allCountries); Ui.Put(countryActions, _noCountries, 1);
        Ui.Put(sidebar, countryActions, 0, 2); Ui.Put(sidebar, Ui.Line(), 0, 3);
        Ui.Put(sidebar, new ScrollViewer { Content = _countries, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 0, 4);
        Ui.Put(sidebar, Ui.Line(), 0, 5);
        _clear.IsEnabled = _isAdmin;
        _clear.Click += async (_, _) => await Operate(ClearAll);
        var sideFooter = Ui.Stack(9,
            Ui.WithIcon("clock", Ui.Label("Проверка каждые 12 часов", 15)),
            Ui.Label("Пока приложение открыто, включая трей", 12.5, Ui.Muted), _clear);
        _clear.HorizontalAlignment = HorizontalAlignment.Left;
        Ui.Put(sidebar, sideFooter, 0, 6);
        Ui.Put(body, new Border { Background = Ui.Surface, Child = sidebar });

        var main = Ui.Grid("*", "66,*,82"); main.Margin = new Thickness(24, 18, 18, 0);
        _selectAll.Width = 158; _selectAll.Height = 44; _selectAll.Margin = new Thickness(0, 0, 12, 8);
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
        _stop.IsVisible = false; _stop.Margin = new Thickness(0, 0, 12, 8); _stop.Click += (_, _) => _pingCancellation?.Cancel();
        _actions.Children.Add(_stop);
        Ui.Put(main, _actions);

        var catalog = Ui.Grid("*", "46,*");
        var tableHead = Ui.Grid(Columns); tableHead.Margin = new Thickness(0, 0, 16, 0);
        var headerCheck = Ui.Checkbox(new Border(), "Выбрать все строки");
        headerCheck.Width = 34; headerCheck.HorizontalAlignment = HorizontalAlignment.Center;
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
            var label = Ui.Label(headings[i], 14.5, Ui.Muted); label.Margin = new Thickness(10, 0, 0, 0); Ui.Put(tableHead, label, i + 1);
        }
        Ui.Put(catalog, new Border { Background = Ui.Surface, BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = tableHead });
        _rowScroll.Content = _rows;
        _rowScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _rowScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        Ui.Put(catalog, _rowScroll, 0, 1);
        Ui.Put(main, Ui.Round(catalog), 0, 1);
        var footer = Ui.Grid("*,130", "24,*"); footer.ColumnSpacing = 8; footer.Margin = new Thickness(0, 8, 0, 0);
        _summary.FontSize = 13.5; _summary.FontWeight = FontWeight.Normal; _summary.Foreground = Ui.Muted;
        Ui.Put(footer, _summary); Ui.Put(footer, _status, 0, 1); Ui.Put(footer, _progress, 1, 1);
        Ui.Put(main, new Border { BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 14, 0, 0), Child = footer }, 0, 2);
        Ui.Put(body, main, 1); Ui.Put(root, body, 0, 1);
        var outer = Ui.Round(root, radius: 18);
        Content = outer;

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
            maximize.Content = Ui.Icon(WindowState == WindowState.Maximized ? "copy" : "square", 13);
            outer.CornerRadius = new CornerRadius(WindowState == WindowState.Maximized ? 0 : 18);
            if (WindowState != WindowState.Minimized) return;
            if (_tray.NativeMenuExporter == null) { WindowState = WindowState.Normal; SetStatus("Системный трей недоступен.", true); return; }
            _tray.IsVisible = true; Hide();
        };
        Closing += (_, e) =>
        {
            if (_closingForRestart || !_busy || e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown) return;
            e.Cancel = true; _pingCancellation?.Cancel(); SetStatus("Дождитесь завершения операции перед выходом.");
        };
        Closed += (_, _) => { _timer.Stop(); _trayIcons.Remove(_tray); _tray.Dispose(); };
        _timer.Tick += async (_, _) =>
        {
            UpdateSummary();
            if (_busy || DateTimeOffset.UtcNow < _retryAfter) return;
            if (_refreshPending || ServerCatalog.IsDue(_settings.LastRefreshUtc, DateTimeOffset.UtcNow)) await Operate(RefreshServers);
            else if (_policyPending && _isAdmin) await Operate(ApplyPolicy);
        };
        Opened += async (_, _) =>
        {
            if (BlockerApp.StartInTray) WindowState = WindowState.Minimized;
            await Initialize();
        };
    }

    private async Task Initialize()
    {
        try
        {
            _settings = AppSettings.Load();
            if (_settings.BlockedCountries == null) throw new FormatException("Повреждён список стран.");
            foreach (string country in _settings.BlockedCountries) _ = new System.Globalization.RegionInfo(country);
            if (_settings.CatalogJson != null) _servers = ServerCatalog.Parse(_settings.CatalogJson);
        }
        catch (Exception ex)
        {
            await Ui.MessageWindow($"Не удалось прочитать настройки. Файл не изменён.\n{ex.Message}").ShowDialog<bool>(this);
            Close(); return;
        }
        PopulateCountries(); RenderRows();
        if (ServerCatalog.IsDue(_settings.LastRefreshUtc, DateTimeOffset.UtcNow) || _servers.Count == 0) await Operate(RefreshServers);
        else await Operate(ApplyPolicy);
        _timer.Start();
    }

    private async Task OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        var dialog = new Window
        {
            Title = "Настройки CS2 Blocker", Width = 550, Height = 390, CanResize = false,
            WindowDecorations = WindowDecorations.None, Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = Ui.BodyFont
        };
        _settingsWindow = dialog;
        var root = Ui.Grid("*", "38,*");
        var caption = Ui.Grid("*,26"); caption.Margin = new Thickness(20, 0, 12, 0);
        Ui.Put(caption, Ui.Label("Настройки", 19, bold: true));
        Ui.Put(caption, Ui.CaptionButton("x", "Закрыть настройки", dialog.Close, true), 1);
        var bar = new Border { Background = Ui.Surface, Child = caption };
        Ui.AttachWindowDrag(dialog, bar); Ui.Put(root, bar);

        var body = Ui.Grid("*", "40,Auto,Auto,44,*"); body.RowSpacing = 15; body.Margin = new Thickness(24, 18, 24, 18);
        var startup = Ui.Checkbox(Ui.Label("Запускать с Windows"), "Запускать с Windows");
        var details = Ui.Label("Автозапуск открывает приложение в трее. Для изменения блокировок нужны права администратора.", 14, Ui.Muted);
        details.TextWrapping = TextWrapping.Wrap;
        var feedback = Ui.Label("", 14, Ui.Muted); feedback.TextWrapping = TextWrapping.Wrap; feedback.VerticalAlignment = VerticalAlignment.Top;
        try { startup.IsChecked = DesktopIntegration.StartupEnabled; }
        catch (Exception ex) { startup.IsEnabled = false; feedback.Text = "Не удалось прочитать автозапуск: " + ex.Message; feedback.Foreground = Ui.Danger; }
        bool updating = false;
        startup.IsCheckedChanged += (_, _) =>
        {
            if (updating) return;
            try
            {
                DesktopIntegration.SetStartup(startup.IsChecked == true);
                feedback.Text = startup.IsChecked == true ? "Автозапуск включён." : "Автозапуск выключен.";
                feedback.Foreground = Ui.Muted;
            }
            catch (Exception ex)
            {
                updating = true; startup.IsChecked = !startup.IsChecked; updating = false;
                feedback.Text = "Не удалось изменить автозапуск: " + ex.Message; feedback.Foreground = Ui.Danger;
            }
        };
        var restart = Ui.Button(_isAdmin ? "Уже запущено от администратора" : "Перезапустить от администратора", "shield");
        restart.HorizontalAlignment = HorizontalAlignment.Left;
        restart.IsEnabled = !_isAdmin;
        restart.Click += async (_, _) =>
        {
            if (_busy) { feedback.Text = "Дождитесь завершения текущей операции."; return; }
            _busy = true; SetEnabled(false); restart.IsEnabled = false;
            try
            {
                _settings.Save();
                feedback.Text = "Подтвердите запуск в окне Windows…";
                if (!await Task.Run(DesktopIntegration.StartElevated)) { feedback.Text = "Перезапуск отменён. Приложение остаётся открытым."; return; }
                _closingForRestart = true;
                dialog.Close(); Close();
            }
            catch (Exception ex) { feedback.Text = "Не удалось перезапустить: " + ex.Message; feedback.Foreground = Ui.Danger; }
            finally
            {
                if (!_closingForRestart) { _busy = false; SetEnabled(true); restart.IsEnabled = !_isAdmin; }
            }
        };
        Ui.Put(body, startup); Ui.Put(body, details, 0, 1);
        Ui.Put(body, Ui.Label(_schedule.Text ?? "", 13, Ui.Muted), 0, 2);
        Ui.Put(body, restart, 0, 3); Ui.Put(body, feedback, 0, 4);
        Ui.Put(root, body, 0, 1); dialog.Content = Ui.Round(root, radius: 14);
        try { await dialog.ShowDialog(this); }
        finally { _settingsWindow = null; }
    }

    private Button AddAction(string text, string icon, Func<Task> action, bool admin = false, bool primary = false)
    {
        var button = Ui.Button(text, icon, primary);
        button.Tag = admin; button.IsEnabled = !admin || _isAdmin;
        button.Margin = new Thickness(0, 0, 12, 8);
        button.Click += async (_, _) => await Operate(action);
        _actions.Children.Add(button);
        return button;
    }

    private async Task Operate(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true; SetEnabled(false);
        try { await operation(); _retryAfter = DateTimeOffset.MinValue; }
        catch (Exception ex)
        {
            _retryAfter = DateTimeOffset.UtcNow.AddMinutes(5);
            SetStatus(ex.Message + (_refreshPending || _policyPending ? " Автопроверка повторится через 5 минут." : ""), true);
        }
        finally { _busy = false; SetEnabled(true); RenderRows(); }
    }

    private void SetEnabled(bool enabled)
    {
        foreach (Control control in _actions.Children)
            if (control != _stop) control.IsEnabled = enabled && (!(control.Tag is true) || _isAdmin);
        _countries.IsEnabled = enabled; _rows.IsEnabled = enabled; _clear.IsEnabled = enabled && _isAdmin;
        _allCountries.IsEnabled = enabled && _isAdmin;
        _noCountries.IsEnabled = enabled && _isAdmin;
    }

    private async Task RefreshServers()
    {
        _refreshPending = true; SetStatus("Получаем актуальный список Valve…");
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        string json = await http.GetStringAsync(ServerCatalog.Url);
        var updated = ServerCatalog.Parse(json);
        foreach (var server in updated) server.Checked = server.Addresses.Count > 0 && (_servers.FirstOrDefault(s => s.Code == server.Code)?.Checked ?? false);
        int added = updated.Count(s => !_servers.Any(old => old.Code == s.Code));
        _servers = updated; PopulateCountries();
        await ApplyPolicy();
        _settings.CatalogJson = json; _settings.LastRefreshUtc = DateTimeOffset.UtcNow; _settings.Save();
        _refreshPending = false;
        SetStatus($"Список Valve обновлён · новых точек: {added}." + (!_isAdmin ? " Для блокировки нужны права администратора." : ""));
    }

    private async Task ApplyPolicy()
    {
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
        foreach (string code in _servers.Select(s => s.Country).Concat(_settings.BlockedCountries).Where(c => c.Length > 0)
            .Distinct().OrderByDescending(c => _settings.BlockedCountries.Contains(c)).ThenBy(ServerCatalog.CountryName))
        {
            var content = Ui.Grid("28,*,28"); content.ColumnSpacing = 12;
            Ui.Put(content, Ui.Flag(code)); Ui.Put(content, Ui.Label(ServerCatalog.CountryName(code)), 1);
            Ui.Put(content, Ui.Label(_servers.Count(s => s.Country == code).ToString(), 14, Ui.Muted), 2);
            var checkbox = Ui.Checkbox(content, ServerCatalog.CountryName(code), _settings.BlockedCountries.Contains(code));
            checkbox.Height = 46; checkbox.Tag = code;
            checkbox.IsCheckedChanged += (_, _) => CountryChecked(checkbox, code);
            ToolTip.SetTip(checkbox, "Блокировать все текущие и новые IP страны. Снятие отметки удаляет только автоматические правила.");
            _countries.Children.Add(checkbox);
        }
        _populatingCountries = false;
    }

    private async void CountryChecked(CheckBox checkbox, string code)
    {
        if (_populatingCountries) return;
        void Revert()
        {
            _populatingCountries = true; checkbox.IsChecked = !checkbox.IsChecked; _populatingCountries = false;
        }
        if (!_isAdmin) { Revert(); SetStatus("Для выбора стран запустите приложение от администратора.", true); return; }
        var selected = new HashSet<string>(_settings.BlockedCountries, StringComparer.OrdinalIgnoreCase);
        if (checkbox.IsChecked == true) selected.Add(code); else selected.Remove(code);
        await SetCountries(selected);
    }

    private async Task SetCountries(HashSet<string> selected)
    {
        if (_busy || !_isAdmin) return;
        var previous = _settings.BlockedCountries;
        _settings.BlockedCountries = selected;
        try { _settings.Save(); }
        catch (Exception ex)
        {
            _settings.BlockedCountries = previous;
            PopulateCountries();
            SetStatus("Не удалось сохранить выбор: " + ex.Message, true);
            return;
        }
        _populatingCountries = true;
        foreach (var checkbox in _countries.Children.OfType<CheckBox>())
            checkbox.IsChecked = checkbox.Tag is string code && _settings.BlockedCountries.Contains(code);
        _populatingCountries = false;
        _policyPending = true;
        await Operate(ApplyPolicy);
    }

    private async Task SetSelected(bool block) => await ChangeManual(_servers.Where(s => s.Checked).ToList(), block);
    private async Task ChangeManual(List<ServerEntry> selected, bool block)
    {
        selected = selected.Where(s => s.Addresses.Count > 0).ToList();
        if (selected.Count == 0) { SetStatus("Выберите точки с опубликованными IP-адресами."); return; }
        if (!block && selected.Any(s => _settings.BlockedCountries.Contains(s.Country)))
        {
            SetStatus("Сначала снимите отметку страны слева: она выбрана для автоблокировки.", true); return;
        }
        foreach (var server in selected) server.FirewallKnown = false;
        await Firewall.SetManual(selected, block); await Firewall.Sync(_servers);
        if (selected.Any(s => s.Blocked != block)) throw new InvalidOperationException("Фаервол не подтвердил все изменения.");
        SetStatus(block ? "Выбранные точки заблокированы. Перезапустите CS2." : "Ручные блокировки сняты. Перезапустите CS2.");
    }

    private async Task ClearAll()
    {
        if (!await Ui.MessageWindow("Снять все блокировки CS2 и отключить автоблокировку стран?", true).ShowDialog<bool>(this)) return;
        var previous = _settings.BlockedCountries; _settings.BlockedCountries = new HashSet<string>();
        try { _settings.Save(); } catch { _settings.BlockedCountries = previous; throw; }
        _policyPending = true; PopulateCountries();
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
        _progress.IsVisible = true; _progress.Maximum = targets.Count; _progress.Value = 0;
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
            var grid = Ui.Grid(Columns); grid.Height = 54;
            var border = new Border { BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 0, 0, 1), Background = server.Checked ? Ui.Selection : Ui.Background, Child = grid };
            var pick = Ui.Checkbox(new Border(), $"Выбрать {server.Description}", server.Checked);
            pick.Width = 34; pick.HorizontalAlignment = HorizontalAlignment.Center; pick.IsEnabled = server.Addresses.Count > 0;
            pick.IsCheckedChanged += (_, _) => { server.Checked = pick.IsChecked == true; border.Background = server.Checked ? Ui.Selection : Ui.Background; UpdateSummary(); };
            Ui.Put(grid, pick);
            string location = server.Country.Length == 0 ? server.Description : System.Text.RegularExpressions.Regex.Replace(server.Description, @"\s*\([^()]*\)\s*$", "");
            var code = Ui.Label(server.Code, 12.5, Ui.Muted); code.FontFamily = Ui.MonoFont;
            var locationCell = Ui.Stack(2, Ui.Label(location, 16, bold: true), code);
            locationCell.Margin = new Thickness(10, 0, 8, 0); locationCell.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(locationCell, server.Description + "\n" + string.Join("\n", server.Addresses)); Ui.Put(grid, locationCell, 1);
            var country = Ui.Grid("28,*"); country.ColumnSpacing = 11; country.Margin = new Thickness(10, 0, 8, 0);
            Ui.Put(country, Ui.Flag(server.Country)); Ui.Put(country, Ui.Label(ServerCatalog.CountryName(server.Country), 15.5), 1);
            ToolTip.SetTip(country, ServerCatalog.CountryName(server.Country)); Ui.Put(grid, country, 2);
            var addressCount = Ui.Label(server.Addresses.Count.ToString(), 14.5); addressCount.FontFamily = Ui.MonoFont; addressCount.Margin = new Thickness(10, 0, 0, 0); Ui.Put(grid, addressCount, 3);
            var ping = Ui.Label(server.PingMs == -2 ? "—" : server.PingMs < 0 ? "Таймаут" : $"{server.PingMs} мс", 14.5); ping.FontFamily = Ui.MonoFont; ping.Margin = new Thickness(10, 0, 0, 0); Ui.Put(grid, ping, 4);
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
        var badge = Ui.Round(Ui.WithIcon(blocked ? "lock" : unknown ? "minus" : "check", Ui.Label(text, 14, color), color), blocked ? Ui.Selection : Ui.Surface, 10);
        badge.Padding = new Thickness(8, 0); badge.Height = 32; badge.Margin = new Thickness(5, 0, 8, 0);
        badge.HorizontalAlignment = HorizontalAlignment.Left; badge.VerticalAlignment = VerticalAlignment.Center;
        badge.MaxWidth = 175; ToolTip.SetTip(badge, text); return badge;
    }

    private void UpdateSummary()
    {
        _updatingSelection = true;
        _selectAll.IsChecked = _servers.Any(s => s.Addresses.Count > 0) && _servers.Where(s => s.Addresses.Count > 0).All(s => s.Checked);
        _updatingSelection = false;
        _summary.Text = $"{_servers.Count} локаций · {_servers.Sum(s => s.Addresses.Count)} IP · {_servers.Count(s => s.FirewallKnown && s.Blocked)} заблокировано";
        string last = _settings.LastRefreshUtc?.ToLocalTime().ToString("dd.MM HH:mm") ?? "ещё не было";
        string next = _settings.LastRefreshUtc?.Add(ServerCatalog.RefreshInterval).ToLocalTime().ToString("dd.MM HH:mm") ?? "сейчас";
        _schedule.Text = $"Обновлено: {last}  ·  Следующее: {next}";
    }
    private void SetStatus(string message, bool error = false)
    {
        _status.Text = message; _status.Foreground = error ? Ui.Danger : Ui.Muted; ToolTip.SetTip(_status, message);
    }
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void RestoreWindow() { Show(); WindowState = WindowState.Normal; _tray.IsVisible = false; Activate(); }
}
