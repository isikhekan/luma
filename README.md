# Luma

A Windows tray app for monitor controls, hardware-aware Gaming / Evening profiles, and automatic game detection.

Available in **Türkçe, English, Español and 简体中文**. The default follows the Windows UI language, with English as the fallback. Change it instantly in **Settings → Language** (Türkçe: **Ayarlar → Dil / Language**). Menus, tray actions, presets, hardware controls, notifications and application error messages follow your selection. Device names, game titles and custom profile names stay as entered. Native Windows file dialogs and vendor-provided diagnostic text may follow Windows or the driver language.

**[Download the Windows x64 app](https://github.com/isikhekan/luma/releases/latest/download/Luma-win-x64.zip)** · [Releases](https://github.com/isikhekan/luma/releases)

## Download and run

1. Download **Luma-win-x64.zip** from Releases. The GitHub source-code ZIP is for developers.
2. Extract the complete ZIP to a folder.
3. Run **Luma.exe** for portable use, or **KUR.cmd** to install for your Windows account, create shortcuts and start Luma with Windows.

Luma itself requires no administrator rights or separate .NET installation. The optional RTSS dependency may request Windows administrator approval. The public build is unsigned. Windows may show a publisher/reputation prompt; check the repository and published SHA-256 checksum before running a downloaded executable. Windows x64 only; tested on Windows 11. macOS, Linux and native Windows ARM64 builds are not included.

## What Luma detects

Each connected monitor is identified separately and mapped to its Windows display adapter. NVIDIA, AMD and Intel names / PCI vendor identifiers are recognized, including systems where different monitors use different adapters. Controls depend on the panel, cable, adapter and driver.

| Available capability | Gaming | Evening |
| --- | --- | --- |
| DDC/CI monitor backlight | Keep the monitor's initial brightness | Reduce backlight relative to its initial value |
| Windows internal-panel brightness | Use the Windows brightness provider | Reduce supported panel brightness |
| SDR gamma control without real brightness control | Neutral software filter | Warm tone and software dimming |
| NVIDIA display color controls | Preserve initial supported color values | Moderate existing vibrance where available |
| HDR or Windows automatic color management | Preserve the color mode | Skip incompatible SDR curves; use available hardware brightness |
| Unsupported controls | Leave them alone | Use remaining controls and show limitations |

Profiles are generated for **every monitor**, with no model whitelist or primary/secondary restriction. Generic profiles preserve contrast, RGB gains, resolution, refresh rate, output color format and bit depth. They do not enable/disable HDR or automatic color management. An existing user's explicit legacy profiles can be migrated from that user's local reports; no personal calibration is bundled.

NVIDIA digital vibrance, hue, color output and selected global 3D settings are available inside Luma when supported by the driver. Native AMD Adrenalin / Intel driver-setting APIs are **not** implemented; AMD and Intel systems use the monitor and Windows controls they expose. NVIDIA DVC / hue use optional private entry points and can become unavailable after driver updates. Driver libraries are loaded from the Windows system directory and are not redistributed.

Laptop brightness uses the Windows WMI provider when it can be mapped to that exact display. A missing DDC/CI brightness channel does not prevent an otherwise supported SDR profile. Hardware changes are read back; rejected SDR control falls back to remaining hardware controls. Live testing has covered a Windows 11 NVIDIA/DDC desktop. AMD / Intel / laptop / HDR combinations are covered by capability tests; compatibility with every physical device is not guaranteed.

Appearance presets are **not measured color calibration**. Software cannot turn an LCD into OLED, create local dimming, or change physical pixel response. White-temperature labels are targets, not colorimeter measurements.

## Automatic Gaming / Evening

Game + clock automation is enabled by default for new settings. A detected running game selects each monitor's own Gaming profile. Alt+Tab and minimization keep the game active. After the last game exits, a short grace period prevents flicker during process handovers, then the current local clock selects the profile:

- 07:00–19:00: Gaming
- 19:00–07:00: Evening

Times are editable in **Automation**. Manual selection lasts until the next game/clock context change. Pause temporarily suspends profile application. The classic schedule is available when game + clock automation is disabled.

A small indicator slides in from the right after a game profile, time profile or supported setting is applied. It disappears automatically after four seconds and does not take keyboard focus or intercept clicks. Repeated slider updates are coalesced. Toggle **Settings → Profile notifications** or use **Test notification** to preview it.

### Fullscreen game notifications (RTSS)

**Settings → In-game fullscreen notifications** uses RivaTuner Statistics Server (RTSS) to render the card inside supported games, including exclusive fullscreen. Luma detects standard folders, registered custom installation locations and an already-running server. Existing installations are reused without upgrading or reinstalling them.

If RTSS is missing, Luma automatically offers setup in Settings when you first open its panel. Read the publisher and Windows Package Manager terms, then choose **Accept and install automatically** once. Luma asks Windows Package Manager to download and silently install the reviewed **Guru3D.RTSS 7.3.7** package, then starts RTSS and verifies the notification connection. No download URL, installer EXE, RTSS profile or individual game EXE needs to be chosen. The fresh RTSS defaults enable OSD, disable its permanent FPS counter and start minimized; Luma checks for disabled notification settings and prepares its own overlay slot automatically. Existing RTSS profiles, exclusions, OSD positions and FPS limits are preserved. A disabled existing OSD preference is reported instead of silently overwritten.

Windows may still show its administrator/UAC prompt; Luma cannot approve that prompt for you. Pending setup waits until no detected game or fullscreen application is active before launching the package manager. Setup progress, cancellation, unavailable packages/network and connection failures appear in the selected app language, with an explicit retry action. One setup attempt runs at a time. Cancellation withdraws the saved installation approval, and failed attempts are not repeatedly restarted in the background. Approved setup can run automatically on a later Luma start if RTSS is missing again. Turning off either notification option or exiting Luma stops pending acquisition; an installer already elevated by Windows may finish independently.

RTSS is an optional third-party application under its [publisher's terms](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/). Its binaries and installer are **not included in Luma's repository or release**. Acquisition uses the fixed package ID, reviewed version and `winget` source with the package manager's normal hash/security checks; it never uses a security-check bypass or forces a reboot. The reviewed [Microsoft community package manifest](https://github.com/microsoft/winget-pkgs/blob/master/manifests/g/Guru3D/RTSS/7.3.7/Guru3D.RTSS.installer.yaml) points to the Guru3D distribution mirror. Windows Package Manager needs [Microsoft App Installer](https://apps.microsoft.com/detail/9nblggh4nns1); if it is absent or disabled, Luma provides that official recovery link. Offline or policy-restricted Windows installations may require that prerequisite to be restored first. Luma's uninstaller leaves the separate RTSS installation in place.

The bridge publishes a cached image in its own RTSS OSD slot. It preserves the current language, compensates for the game's OSD zoom, waits for the actual renderer after game launch, and clears the card on focus change or expiry. It does not activate another window, minimize the game, change fullscreen mode, change RTSS profiles, alter frame limits or modify game files. Existing RTSS OSDs are preserved. There is no animation/update loop while the card is stationary or idle.

RTSS must be allowed by the game, OSD must be enabled, and its graphics renderer must support the image overlay. Anti-cheat, protected processes, unsupported renderers and user exclusions can prevent it from displaying; Luma does not bypass those restrictions. The Settings page reports the connection status. When an in-game notification cannot be delivered, Luma does not force a desktop window over the game.

**Zero FPS impact cannot be guaranteed.** Synthetic client-publish timing is not an in-game FPS benchmark. Local validation covers the real RTSS shared-memory bridge, GPU-rendered cards and cleanup; it does not certify every game or exclusive-fullscreen mode. Microsoft explains the rendering and performance limits of exclusive fullscreen overlays in [Demystifying Fullscreen Optimizations](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/).

Installed games are scanned on startup and once per minute. Sources include Steam libraries, Epic manifests, Riot installations, supported GOG/Ubisoft/EA/Battle.net registry entries, Xbox MicrosoftGame.config installations and recognized game folders. Executables inside a detected game are indexed automatically; selecting EXE files is optional. Launchers, crash reporters, installers, SDK tools, dedicated servers and benchmark/background utilities are excluded. Protected or unusual installations may not be discoverable. Game detection does not inject into games or change their files. The optional fullscreen notification renderer uses RTSS's existing graphics hooks.

## Backups, controls and shortcuts

Original per-display settings are saved locally before modification. **Displays → Restore this display's previous settings** restores the changed controls for that display and disables automatic switching. Generic profiles only restore the controls they touched. Normal app exit restores the initial software gamma curve; physical monitor and driver preferences remain until restored.

- Ctrl+Alt+L: show/hide the tray panel
- Ctrl+Alt+P: pause/resume
- Ctrl+Alt+0: reset the software filter, disable automation and cursor wrapping
- Ctrl+Alt+1…6: standard software presets
- Esc: hide the panel / revert a pending display arrangement

DDC/CI controls also include available contrast, RGB gains, speaker volume and input sources. Monitor arrangement changes have a rollback confirmation. Optional cursor wrapping connects the outer left/right desktop edges. Input switching can interrupt the image and requires an explicit action.

## Privacy

Luma does not send telemetry or upload game lists. Monitor/color features work offline. Optional, approved RTSS setup uses Windows Package Manager, which contacts its package infrastructure and the publisher's distribution mirror; Luma passes the fixed public package identifier and install options, without monitor settings or game paths. Settings, monitor IDs, local game paths, driver backups, scan catalogs and logs are stored under **%LOCALAPPDATA%\\Luma**. These files are not part of the repository or downloadable package. Local logs may contain device identifiers and paths; review them before sharing publicly.

Public releases contain program files, generic documentation and runtime license notices. Debug symbols and local build paths are excluded. Windows account names, personal calibration files and local diagnostic screenshots are not included.

## Development

Windows and the .NET 10 SDK are required. The app is WPF / WinForms with native Windows APIs and no third-party NuGet dependencies.

```powershell
dotnet build Luma.slnx -c Release
dotnet run --project Luma.Checks -c Release
.\scripts\build-release.ps1
```

The checks cover gamma safety, time boundaries, per-display identity, generic NVIDIA/AMD/Intel capabilities, WMI identity matching, HDR/WCG preservation, game manifest parsing and recognition of an automatically discovered test process. `--hardware`, `--nvidia` and `--games` are optional local diagnostic modes. They may produce private local artifacts; do not commit them. `--nvidia-drstest` explicitly writes the same effective FXAA value, verifies it, and restores inheritance. Ordinary checks do not modify display or driver settings.

`Luma.exe --render-test <folder> --language es` renders real WPF views and the profile indicator offscreen. It also exercises live language changes through the actual Settings selector without saving preview settings. Use `tr`, `en`, `es` or `zh-Hans`. Screenshots generated on a real machine may contain private hardware names; they are excluded from publication.

Translations are embedded in `Luma/Locales/*.json` and work offline. Turkish source messages are stable catalog keys. Keep all catalog keys and formatting placeholders in sync when contributing translations; `Luma.Checks` verifies coverage, formatting, preference persistence and notification deduplication, shared-memory bounds/ownership/locking and OSD zoom compensation. Setup checks simulate missing/existing installations, game deferral, consent, cancellation, concurrent requests, failed acquisition and recovery without installing anything. `Luma.Checks --rtss-probe` checks a locally running RTSS instance; adding `--write` explicitly tests a temporary owned OSD slot and clears it. `--rtss-setup-probe` checks local dependency/OSD status and queries the reviewed WinGet package without installing it. Local acquisition validation also downloaded the real archive with successful WinGet hash verification; a clean-machine install/UAC path has not been exercised locally. Standard profiles and driver options keep stable identifiers across language changes.

Release packaging creates release/Luma-win-x64.zip and SHA256SUMS.txt. The GitHub Release workflow can build and publish an existing version tag. Use the checksums when distributing binaries. The app is MIT licensed; bundled .NET / Windows Desktop runtime license notices are included in releases.

## Turkce kullanim

ZIP'i tamamen cikartip **Luma.exe** dosyasini ac. Kurulum ve Windows baslangici icin **KUR.cmd** dosyasini calistir. Uygulama tepside calisir. **Ekranlar** bolumunde her monitorun algilanan ekran karti, parlaklik yontemi ve Gaming / Aksam profilleri gorunur. **Otomasyon** oyunlari kendisi bulur; saatleri ve otomatik gecisleri buradan yonetebilirsin. NVIDIA disindaki ekran kartlarinda desteklenen monitor / Windows kontrolleri kullanilir.

## API references

- [Microsoft DDC/CI controls](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature)
- [Microsoft SDR gamma constraints](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setdevicegammaramp)
- [Windows monitor brightness](https://learn.microsoft.com/en-us/windows/win32/wmicoreprov/wmimonitorbrightness)
- [NVIDIA NVAPI](https://developer.nvidia.com/nvapi/get-started)
