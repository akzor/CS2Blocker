using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace CS2Blocker;

internal static class DesktopIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CS2Blocker";
    private static string Executable => Environment.ProcessPath
        ?? throw new InvalidOperationException("Не удалось определить путь приложения.");

    internal static string StartupCommand(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') ||
            !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Некорректный путь приложения.");
        return $"\"{executable}\" --tray";
    }

    public static bool StartupEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, StartupCommand(Executable), StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetStartup(bool enabled)
    {
        using var key = enabled ? Registry.CurrentUser.CreateSubKey(RunKey, true)
            : Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null)
        {
            if (enabled) throw new InvalidOperationException("Не удалось открыть настройки автозапуска Windows.");
            return;
        }
        WriteStartup(key, enabled, Executable);
    }

    internal static void WriteStartup(RegistryKey key, bool enabled, string executable)
    {
        if (enabled) key.SetValue(ValueName, StartupCommand(executable), RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
    }

    internal static ProcessStartInfo ElevatedStartInfo(string executable, int parentId)
    {
        _ = StartupCommand(executable);
        if (parentId <= 0) throw new ArgumentOutOfRangeException(nameof(parentId));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("--restart-from");
        start.ArgumentList.Add(parentId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return start;
    }

    public static bool StartElevated()
    {
        try
        {
            using var process = Process.Start(ElevatedStartInfo(Executable, Environment.ProcessId));
            return process != null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }
    }

    public static bool WaitForPreviousInstance(string[] args)
    {
        int index = Array.IndexOf(args, "--restart-from");
        if (index < 0) return true;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int pid) || pid <= 0 || pid == Environment.ProcessId) return false;
        try
        {
            using var previous = Process.GetProcessById(pid);
            if (!string.Equals(previous.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase)) return false;
            return previous.WaitForExit(30_000);
        }
        catch (ArgumentException) { return true; }
        catch (Win32Exception) { return false; }
        catch (InvalidOperationException) { return true; }
    }
}
