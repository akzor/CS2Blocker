using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CS2Blocker;

internal static class Firewall
{
    public static bool IsAdmin()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static void Validate(ServerEntry server)
    {
        if (!Regex.IsMatch(server.Code, @"\A[A-Z0-9_]{1,32}\z") ||
            !Regex.IsMatch(server.Country, @"\A(?:[A-Z]{2})?\z") ||
            server.Addresses.Any(a => !IPAddress.TryParse(a, out var ip) ||
                ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != a))
            throw new ArgumentException("Некорректные данные правила фаервола.");
    }

    internal static string RuleScript(ServerEntry server, bool automatic)
    {
        Validate(server);
        if (server.Addresses.Count == 0) throw new ArgumentException("У точки нет IP-адресов.");
        var script = new StringBuilder();
        string prefix = automatic ? $"CS2_Block_Auto_{server.Country}_{server.Code}" : server.RuleName;
        string ips = string.Join(",", server.Addresses.Select(ip => $"'{ip}'"));
        foreach (string direction in new[] { "Inbound", "Outbound" })
        {
            string name = $"{prefix}_{direction}";
            script.AppendLine($"$rule = Get-NetFirewallRule -Name '{name}' -ErrorAction SilentlyContinue");
            script.AppendLine($"if ($rule) {{ $rule | Set-NetFirewallRule -Enabled True -Action Block; $rule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress @({ips}) }} else {{ New-NetFirewallRule -Name '{name}' -DisplayName '{name}' -Direction {direction} -Action Block -Enabled True -Profile Any -RemoteAddress @({ips}) | Out-Null }}");
        }
        return script.ToString();
    }

    internal static string CountryScript(List<ServerEntry> servers, HashSet<string> countries)
    {
        var selected = servers.Where(s => s.Country.Length > 0 && countries.Contains(s.Country) && s.Addresses.Count > 0).ToList();
        var script = new StringBuilder();
        foreach (var server in selected) script.AppendLine(RuleScript(server, true));
        string keep = string.Join(",", countries.Order().Select(c =>
        {
            if (!Regex.IsMatch(c, @"\A[A-Z]{2}\z")) throw new ArgumentException("Некорректная страна.");
            return $"'{c}'";
        }));
        script.AppendLine($"$countries = @({keep})");
        script.AppendLine("Get-NetFirewallRule -Name 'CS2_Block_Auto_*' -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^CS2_Block_Auto_([A-Z]{2})_' -and $countries -notcontains $Matches[1] } | Remove-NetFirewallRule");
        return script.ToString();
    }

    public static Task ApplyCountries(List<ServerEntry> servers, HashSet<string> countries) => Run(CountryScript(servers, countries));

    public static async Task SetManual(List<ServerEntry> servers, bool block)
    {
        var script = new StringBuilder();
        foreach (var server in servers)
        {
            Validate(server);
            if (block) script.AppendLine(RuleScript(server, false));
            else script.AppendLine($"Get-NetFirewallRule -DisplayName '{server.RuleName}' , '{server.RuleName}_Inbound' , '{server.RuleName}_Outbound' -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
        }
        if (script.Length > 0) await Run(script.ToString());
    }

    public static Task Clear() => Run("Get-NetFirewallRule | Where-Object { $_.DisplayName -like 'CS2_Block_*' } | Remove-NetFirewallRule");

    public static async Task Sync(List<ServerEntry> servers)
    {
        string json = await Run("@(Get-NetFirewallRule | Where-Object { $_.DisplayName -like 'CS2_Block_*' -and $_.Enabled -eq 'True' -and $_.Action -eq 'Block' } | ForEach-Object { [pscustomobject]@{ DisplayName=$_.DisplayName; Direction=[int]$_.Direction; Addresses=@(($_ | Get-NetFirewallAddressFilter).RemoteAddress) } }) | ConvertTo-Json -Depth 4 -Compress", requiresAdmin: false);
        SetStates(servers, json);
    }

    internal static void SetStates(List<ServerEntry> servers, string json)
    {
        var rules = string.IsNullOrWhiteSpace(json) ? new JArray() : JToken.Parse(json) is JArray array ? array : new JArray(JToken.Parse(json));
        foreach (var server in servers)
        {
            bool Has(string direction) => server.Addresses.All(ip => rules.Any(r =>
                r["Direction"]?.ToString() == direction &&
                new[] { server.RuleName, $"{server.RuleName}_{(direction == "1" ? "Inbound" : "Outbound")}",
                    $"CS2_Block_Auto_{server.Country}_{server.Code}_{(direction == "1" ? "Inbound" : "Outbound")}" }.Contains(r["DisplayName"]?.ToString()) &&
                r["Addresses"] is JArray addresses && addresses.Any(a => a.ToString() == ip || a.ToString() is "Any" or "*")));
            server.Blocked = server.Addresses.Count > 0 && Has("1") && Has("2");
            server.FirewallKnown = true;
        }
    }

    private static async Task<string> Run(string script, bool requiresAdmin = true)
    {
        if (requiresAdmin && !IsAdmin()) throw new InvalidOperationException("Для изменения фаервола запустите приложение от администратора.");
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        string path = Path.Combine(Path.GetTempPath(), $"cs2_{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(path,
                "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8; try {\n" + script + "\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }",
                new UTF8Encoding(true));
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить PowerShell.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
                throw new TimeoutException("Фаервол не ответил за 2 минуты. Проверьте правила перед повтором.");
            }
            string result = await output;
            string failure = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("Ошибка фаервола: " + failure.Trim());
            return result;
        }
        finally { File.Delete(path); }
    }
}
