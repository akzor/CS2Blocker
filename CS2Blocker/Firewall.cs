using System.Buffers.Binary;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CS2Blocker;

internal static class Firewall
{
    private const string Prefix = "CS2_Block_";
    private const string AutoPrefix = Prefix + "Auto_";
    private const int Inbound = 1, Outbound = 2, Block = 0;
    private const int AnyProtocol = 256, AllProfiles = int.MaxValue;

    internal readonly record struct AddressRange(uint First, uint Last)
    {
        public bool Contains(uint address) => address >= First && address <= Last;
    }

    private sealed record Rule(string Name, int Direction, int Action, bool Enabled, string Addresses, object Handle)
    {
        public List<AddressRange> Ranges { get; } = ParseRanges(Addresses);
    }

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
            server.Addresses.Count == 0 ||
            server.Addresses.Any(a => !IPAddress.TryParse(a, out var ip) ||
                ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != a))
            throw new ArgumentException("Некорректные данные правила фаервола.");
    }

    public static Task ApplyCountries(List<ServerEntry> servers, HashSet<string> countries) => Task.Run(() =>
    {
        RequireAdmin();
        if (countries.Any(country => !Regex.IsMatch(country, @"\A[A-Z]{2}\z")))
            throw new ArgumentException("Некорректная страна.");
        var selected = servers.Where(s => countries.Contains(s.Country) && s.Addresses.Count > 0).ToList();
        selected.ForEach(Validate);

        dynamic rules = OpenRules();
        List<Rule> existing = ReadRules(rules);
        var byName = existing.ToLookup(rule => rule.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var server in selected)
            Upsert(rules, byName, AutoPrefix + server.Country + "_" + server.Code, server.Addresses);
        foreach (var rule in existing)
        {
            var match = Regex.Match(rule.Name, @"\ACS2_Block_Auto_([A-Z]{2})_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && !countries.Contains(match.Groups[1].Value.ToUpperInvariant()))
                rules.Remove(rule.Name);
        }
    });

    public static Task SetManual(List<ServerEntry> servers, bool block) => Task.Run(() =>
    {
        RequireAdmin();
        servers.ForEach(Validate);
        dynamic rules = OpenRules();
        if (block)
        {
            List<Rule> existing = ReadRules(rules);
            var byName = existing.ToLookup(rule => rule.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var server in servers)
                Upsert(rules, byName, server.RuleName, server.Addresses);
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in servers)
        {
            names.Add(server.RuleName);
            names.Add(server.RuleName + "_Inbound");
            names.Add(server.RuleName + "_Outbound");
        }
        foreach (var rule in ReadRules(rules))
            if (names.Contains(rule.Name)) rules.Remove(rule.Name);
    });

    public static Task Clear() => Task.Run(() =>
    {
        RequireAdmin();
        dynamic rules = OpenRules();
        foreach (var rule in ReadRules(rules)) rules.Remove(rule.Name);
        if (ReadRules(rules).Count != 0)
            throw new InvalidOperationException("Остались правила CS2_Block_*.");
    });

    public static async Task Sync(List<ServerEntry> servers)
    {
        var snapshot = await Task.Run(() =>
        {
            List<Rule> rules = ReadRules(OpenRules());
            return rules.Where(rule => rule.Enabled && rule.Action == Block)
                .ToLookup(rule => rule.Name, StringComparer.OrdinalIgnoreCase);
        });
        foreach (var server in servers)
        {
            string auto = AutoPrefix + server.Country + "_" + server.Code;
            bool Covers(int direction, string suffix)
            {
                var ranges = snapshot[server.RuleName].Concat(snapshot[server.RuleName + suffix])
                    .Concat(snapshot[auto + suffix]).Where(rule => rule.Direction == direction)
                    .SelectMany(rule => rule.Ranges).ToArray();
                return server.Addresses.All(ip => TryIPv4(ip, out uint address) && ranges.Any(range => range.Contains(address)));
            }
            server.Blocked = server.Addresses.Count > 0 && Covers(Inbound, "_Inbound") && Covers(Outbound, "_Outbound");
            server.FirewallKnown = true;
        }
    }

    private static object OpenRules()
    {
        dynamic policy = CreateCom("HNetCfg.FwPolicy2");
        return policy.Rules;
    }

    private static object CreateCom(string progId)
    {
        var type = Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException("Windows Firewall недоступен.");
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows Firewall недоступен.");
    }

    private static List<Rule> ReadRules(object rules)
    {
        var result = new List<Rule>();
        foreach (dynamic entry in (IEnumerable)rules)
        {
            string name = entry.Name;
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new Rule(name, (int)entry.Direction, (int)entry.Action, (bool)entry.Enabled,
                (string)entry.RemoteAddresses, (object)entry));
        }
        return result;
    }

    private static void Upsert(object collection, ILookup<string, Rule> existing, string name, List<string> addresses)
    {
        dynamic rules = collection;
        string remote = string.Join(",", addresses);
        foreach (var (direction, suffix) in new[] { (Inbound, "_Inbound"), (Outbound, "_Outbound") })
        {
            string ruleName = name + suffix;
            var matches = existing[ruleName].ToArray();
            if (matches.Length == 0)
            {
                dynamic rule = CreateCom("HNetCfg.FWRule");
                rule.Name = ruleName;
                rule.Direction = direction;
                rule.Protocol = AnyProtocol;
                rule.Profiles = AllProfiles;
                rule.Action = Block;
                rule.RemoteAddresses = remote;
                rule.Enabled = true;
                rules.Add(rule);
                continue;
            }
            foreach (var match in matches)
            {
                dynamic rule = match.Handle;
                if (!SameAddresses(match, addresses)) rule.RemoteAddresses = remote;
                if (match.Direction != direction) rule.Direction = direction;
                if (match.Action != Block) rule.Action = Block;
                if (!match.Enabled) rule.Enabled = true;
            }
        }
    }

    private static bool SameAddresses(Rule rule, List<string> addresses) =>
        rule.Ranges.Count == rule.Addresses.Split(',').Length &&
        rule.Ranges.Sum(range => (long)range.Last - range.First + 1) == addresses.Count &&
        addresses.All(ip => TryIPv4(ip, out uint address) && rule.Ranges.Any(range => range.Contains(address)));

    internal static List<AddressRange> ParseRanges(string remote)
    {
        var ranges = new List<AddressRange>();
        foreach (string value in remote.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (value is "*" or "Any") { ranges.Add(new(0, uint.MaxValue)); continue; }
            var bounds = value.Split('-', 2);
            if (bounds.Length == 2)
            {
                if (TryIPv4(bounds[0], out uint first) && TryIPv4(bounds[1], out uint last) && first <= last)
                    ranges.Add(new(first, last));
            }
            else if (TryIPv4(value, out uint single)) ranges.Add(new(single, single));
            else if (IPNetwork.TryParse(value.Replace("/255.255.255.255", "/32", StringComparison.Ordinal), out var network)
                && network.BaseAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                uint start = BinaryPrimitives.ReadUInt32BigEndian(network.BaseAddress.GetAddressBytes());
                uint mask = network.PrefixLength == 0 ? 0 : uint.MaxValue << (32 - network.PrefixLength);
                ranges.Add(new(start, start | ~mask));
            }
        }
        return ranges;
    }

    private static bool TryIPv4(string value, out uint address)
    {
        address = 0;
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        address = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        return true;
    }

    private static void RequireAdmin()
    {
        if (!IsAdmin()) throw new InvalidOperationException("Для изменения фаервола запустите приложение от администратора.");
    }
}
