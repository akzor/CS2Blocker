using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CS2Blocker;

internal sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly Action _close;
    private readonly Func<Action<string>, Task> _restart;
    private readonly Func<Action<string>, Task> _check;
    private readonly Action<bool> _setCloseToTray;
    private bool _startupEnabled;
    private bool _closeToTray;
    private bool _busy;
    private string _startupDetail = "Приложение запустится в трее";
    private string _closeToTrayDetail = "Проверка серверов продолжится в трее";
    private string _adminFeedback = "";
    private string _updateStatus = "Автопроверка раз в 15 минут";

    public SettingsViewModel(bool isAdmin, Action close, Func<Action<string>, Task> restart, Func<Action<string>, Task> check,
        bool closeToTray, Action<bool> setCloseToTray)
    {
        NeedsAdmin = !isAdmin;
        _close = close;
        _restart = restart;
        _check = check;
        _closeToTray = closeToTray;
        _setCloseToTray = setCloseToTray;
        try { _startupEnabled = DesktopIntegration.StartupEnabled; }
        catch (Exception ex)
        {
            StartupAvailable = false;
            _startupDetail = "Не удалось прочитать автозапуск. " + ex.Message;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool NeedsAdmin { get; }
    public string AdminStatus => NeedsAdmin ? "Нет прав" : "Есть права";
    public bool StartupAvailable { get; } = true;
    public string StartupDetail => _startupDetail;
    public string CloseToTrayDetail => _closeToTrayDetail;
    public string AdminFeedback => _adminFeedback;
    public bool HasAdminFeedback => _adminFeedback.Length > 0;
    public string VersionLabel => $"Обновления · v{AppUpdate.CurrentVersion}";
    public bool CanUpdate => AppUpdate.IsSupported;
    public string UpdateStatus => CanUpdate ? _updateStatus : "Для проверки нужна сборка из релиза GitHub";
    public bool Busy => _busy;

    public bool StartupEnabled
    {
        get => _startupEnabled;
        set
        {
            if (!StartupAvailable || _startupEnabled == value) return;
            try
            {
                DesktopIntegration.SetStartup(value);
                _startupEnabled = value;
                _startupDetail = value ? "Автозапуск включён" : "Автозапуск выключен";
            }
            catch (Exception ex)
            {
                _startupDetail = "Не удалось изменить автозапуск. " + ex.Message;
            }
            Notify();
            Notify(nameof(StartupDetail));
        }
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (_closeToTray == value) return;
            try
            {
                _setCloseToTray(value);
                _closeToTray = value;
                _closeToTrayDetail = "Проверка серверов продолжится в трее";
            }
            catch (Exception ex) { _closeToTrayDetail = "Не удалось сохранить настройку. " + ex.Message; }
            Notify();
            Notify(nameof(CloseToTrayDetail));
        }
    }

    public void CloseWindow() => _close();
    public void RestartAsAdmin() => _ = Run(_restart, message =>
    {
        _adminFeedback = message;
        Notify(nameof(AdminFeedback));
        Notify(nameof(HasAdminFeedback));
    });
    public void CheckUpdates() => _ = Run(_check, message =>
    {
        _updateStatus = message;
        Notify(nameof(UpdateStatus));
    });

    private async Task Run(Func<Action<string>, Task> action, Action<string> report)
    {
        if (_busy) return;
        _busy = true;
        Notify(nameof(Busy));
        try { await action(report); }
        catch (Exception ex) { report(ex.Message); }
        finally { _busy = false; Notify(nameof(Busy)); }
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
