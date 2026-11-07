using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CS2Blocker;

internal static class AppUpdate
{
    private const string Repository = "akzor/CS2Blocker";
    private const string AssetName = "CS2Blocker.exe";
    private const long MaximumSize = 128 * 1024 * 1024;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = 1024 * 1024
    };

    static AppUpdate() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("CS2Blocker");

    internal sealed record Release(Version Version, string Url, string Digest, long Size);
    public static Version CurrentVersion { get; } = new(typeof(AppUpdate).Assembly.GetName().Version!.ToString(3));
    public static bool IsSupported => Environment.ProcessPath != null && !File.Exists(Path.Combine(AppContext.BaseDirectory, "CS2Blocker.dll"));

    public static async Task<Release?> Find(CancellationToken cancellation)
    {
        string json = await Http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellation).ConfigureAwait(false);
        return ReadRelease(json, CurrentVersion);
    }

    internal static Release? ReadRelease(string json, Version current)
    {
        var data = JObject.Parse(json);
        string? tag = (string?)data["tag_name"];
        if (tag == null || !Regex.IsMatch(tag, @"\Av[0-9]+\.[0-9]+\.[0-9]+\z") ||
            !Version.TryParse(tag[1..], out var version) || version <= current ||
            (bool?)data["draft"] != false || (bool?)data["prerelease"] != false)
            return null;

        var asset = (data["assets"] as JArray)?.OfType<JObject>()
            .SingleOrDefault(item => (string?)item["name"] == AssetName);
        string url = $"https://github.com/{Repository}/releases/download/{tag}/{AssetName}";
        string? digest = (string?)asset?["digest"];
        long size = (long?)asset?["size"] ?? 0;
        if ((string?)asset?["browser_download_url"] != url || (string?)asset?["state"] != "uploaded" ||
            digest == null || !Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z") || size <= 0 || size > MaximumSize)
            throw new InvalidDataException("Релиз GitHub не содержит проверяемого CS2Blocker.exe.");
        return new Release(version, url, digest[7..], size);
    }

    public static async Task Download(Release release, string destination, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var response = await Http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.Asynchronous);
        byte[] buffer = new byte[81920];
        long remaining = release.Size;
        while (remaining > 0)
        {
            int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), timeout.Token).ConfigureAwait(false);
            if (count == 0) throw new InvalidDataException("Обновление скачалось не полностью.");
            await file.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
            remaining -= count;
        }
        if (await source.ReadAsync(buffer.AsMemory(0, 1), timeout.Token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Размер обновления не совпадает с данными GitHub.");
        file.Position = 0;
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, timeout.Token).ConfigureAwait(false));
        if (!hash.Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма обновления не совпадает.");
    }

    public static void Install(string executable, string downloaded, bool startInTray)
    {
        string backup = executable + ".previous";
        foreach (string path in new[] { executable, downloaded, backup })
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Обновление через ссылку на файл не поддерживается.");

        bool replaced = false;
        try
        {
            File.Replace(downloaded, executable, backup);
            replaced = true;
            var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)! };
            start.ArgumentList.Add("--restart-from");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (startInTray) start.ArgumentList.Add("--tray");
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить обновлённую версию.");
        }
        catch
        {
            if (replaced || (!File.Exists(executable) && File.Exists(backup)))
                File.Move(backup, executable, true);
            throw;
        }
    }
}
