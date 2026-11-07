using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CS2Blocker;

internal sealed class ServerEntry
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public string Country { get; set; } = "";
    public List<string> Addresses { get; set; } = new();
    [JsonIgnore] public int PingMs { get; set; } = -2;
    [JsonIgnore] public bool Blocked { get; set; }
    [JsonIgnore] public bool FirewallKnown { get; set; }
    [JsonIgnore] public bool Checked { get; set; }
    [JsonIgnore] public string RuleName => $"CS2_Block_{Code}";
}

internal static class ServerCatalog
{
    public const string Url = "https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730";
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);
    private static readonly Lazy<Dictionary<string, string>> RegionCodes = new(() =>
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            var region = new RegionInfo(culture.Name);
            result.TryAdd(region.EnglishName, region.TwoLetterISORegionName);
        }
        return result;
    });
    private static readonly Dictionary<string, string> CountryNames = new()
    {
        ["AR"] = "Аргентина", ["AU"] = "Австралия", ["AT"] = "Австрия",
        ["BR"] = "Бразилия", ["GB"] = "Великобритания", ["DE"] = "Германия",
        ["HK"] = "Гонконг", ["GU"] = "Гуам", ["IN"] = "Индия",
        ["ES"] = "Испания", ["CN"] = "Китай", ["NL"] = "Нидерланды",
        ["AE"] = "ОАЭ", ["PE"] = "Перу", ["PL"] = "Польша", ["US"] = "США",
        ["SG"] = "Сингапур", ["FI"] = "Финляндия", ["FR"] = "Франция",
        ["CL"] = "Чили", ["SE"] = "Швеция", ["ZA"] = "ЮАР",
        ["KR"] = "Южная Корея", ["JP"] = "Япония"
    };

    public static string CountryName(string code) => CountryNames.TryGetValue(code, out var name)
        ? name : code.Length == 0 ? "Страна неизвестна" : new RegionInfo(code).EnglishName;

    public static bool IsDue(DateTimeOffset? last, DateTimeOffset now) =>
        last == null || now < last.Value || now - last.Value >= RefreshInterval;

    public static string ResolveCountry(string description)
    {
        var match = Regex.Match(description, @"\(([^()]*)\)\s*$");
        string location = match.Success ? match.Groups[1].Value.Trim() : description.Trim();
        if (location == "Hong Kong") return "HK";
        if (location == "South Korea") return "KR";
        if (location is "England" or "Scotland" or "Wales" or "Northern Ireland") return "GB";
        if (location == "UAE") return "AE";
        const string states = "Alabama|Alaska|Arizona|Arkansas|California|Colorado|Connecticut|Delaware|Florida|Georgia|Hawaii|Idaho|Illinois|Indiana|Iowa|Kansas|Kentucky|Louisiana|Maine|Maryland|Massachusetts|Michigan|Minnesota|Mississippi|Missouri|Montana|Nebraska|Nevada|New Hampshire|New Jersey|New Mexico|New York|North Carolina|North Dakota|Ohio|Oklahoma|Oregon|Pennsylvania|Rhode Island|South Carolina|South Dakota|Tennessee|Texas|Utah|Vermont|Virginia|Washington|West Virginia|Wisconsin|Wyoming|District of Columbia";
        if (states.Split('|').Contains(location, StringComparer.OrdinalIgnoreCase) &&
            (location != "Georgia" || description.StartsWith("Atlanta (", StringComparison.OrdinalIgnoreCase))) return "US";
        return RegionCodes.Value.GetValueOrDefault(location, "");
    }

    public static List<ServerEntry> Parse(string json)
    {
        var pops = JObject.Parse(json)["pops"] as JObject
            ?? throw new FormatException("В ответе Valve отсутствует список pops.");
        var servers = new List<ServerEntry>();
        foreach (var pop in pops.Properties())
        {
            if (!Regex.IsMatch(pop.Name, @"\A[a-zA-Z0-9_]{1,32}\z") || pop.Value is not JObject data)
                throw new FormatException("Некорректный код точки Valve.");
            string description = data["desc"]?.Value<string>() ?? pop.Name;
            var addresses = new List<string>();
            if (data["relays"] is JArray relays)
                foreach (var relay in relays)
                {
                    string value = relay["ipv4"]?.Value<string>() ?? "";
                    if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                        throw new FormatException($"Некорректный IPv4 у {pop.Name}.");
                    addresses.Add(ip.ToString());
                }
            servers.Add(new ServerEntry
            {
                Code = pop.Name.ToUpperInvariant(), Description = description,
                Country = ResolveCountry(description), Addresses = addresses.Distinct().ToList()
            });
        }
        if (servers.Count == 0) throw new FormatException("Valve вернула пустой список серверов.");
        return servers.OrderBy(s => CountryName(s.Country)).ThenBy(s => s.Description).ToList();
    }
}

internal sealed class AppSettings
{
    public HashSet<string> BlockedCountries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? LastRefreshUtc { get; set; }
    public string? CatalogJson { get; set; }

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS2Blocker", "settings.json");

    public static AppSettings Load() => File.Exists(FilePath)
        ? JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(FilePath))
            ?? throw new FormatException("Пустой файл настроек.")
        : new AppSettings();

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(this, Formatting.Indented));
        File.Move(temporary, FilePath, true);
    }
}
