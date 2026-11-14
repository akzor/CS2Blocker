<div align="center">
  <img src="CS2Blocker/Assets/valve-blocker.png" width="64" height="64" alt="Valve Blocker">
  <h1>Valve Blocker</h1>
  <p>Block CS2 relays by country or location using Windows Firewall.</p>
  <p><a href="https://github.com/akzor/CS2Blocker/releases/latest">Download for Windows</a> · <a href="https://github.com/akzor/CS2Blocker/releases">Release notes</a></p>
</div>

![Valve Blocker main window](docs/screenshot.png)

## Getting started

1. Download `CS2Blocker.exe` from the latest release. No separate .NET installation is required.
2. Click **Administrator access required** and confirm the Windows prompt.
3. Select countries under **Auto-block** and click **Apply**. **All countries** selects the entire list.

To block individual locations, select rows and click **Block**. Search by location, code or IP. The **Status** menu filters the results. **Ping** measures latency, and **Activity** shows recent events.

## Removing blocks

- Click **Unblock** to remove manual blocks from selected locations.
- Click **Deselect all**, then **Apply**, to remove automatic country rules. Manual blocks stay active.
- **Clear all** removes every `CS2_Block_*` rule created by the application and disables automatic blocking.

Exiting the application keeps existing firewall rules active.

## Tray and updates

Closing the window minimizes it to the tray by default. Choose **Exit** from the tray menu to quit. Startup and close behavior can be changed in **Settings**.

The Valve list refreshes every 12 hours, including while minimized to the tray. Keep the application running as administrator to block newly published IPs automatically.

Version 0.2.0 and later check GitHub Releases at startup and every 15 minutes. Downloads are verified using SHA-256 before installation. Use **Check** in Settings for a manual check. Users on 0.1.0 must download a newer executable manually.

Settings and the cached list are stored in `%LOCALAPPDATA%\CS2Blocker\settings.json`.

## Building

Requires Windows x64 and .NET SDK 8 or later.

```powershell
git clone https://github.com/akzor/CS2Blocker.git
cd CS2Blocker
dotnet publish CS2Blocker/CS2Blocker.csproj -c Release -o release
```

The executable is written to `release/CS2Blocker.exe`.

## Credits

- UI built with [Avalonia](https://github.com/AvaloniaUI/Avalonia).
- Icons from [Tabler Icons](https://github.com/tabler/tabler-icons), licensed under [MIT](CS2Blocker/Assets/icons/LICENSE.txt).
- Font licenses are included in [Assets/fonts](CS2Blocker/Assets/fonts).
- Flags from [Flagpedia](https://flagpedia.net/).
- Administrator icon supplied by Windows.
- Counter-Strike 2 artwork belongs to Valve. [Steam](https://steamcommunity.com/app/730)

Unofficial utility. Not affiliated with Valve.
