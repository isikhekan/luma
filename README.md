# Luma

A Windows tray app for monitor controls, hardware-aware Gaming / Evening profiles, and automatic game detection.

**[Download the Windows x64 app](https://github.com/isikhekan/luma/releases/latest/download/Luma-win-x64.zip)** · [Releases](https://github.com/isikhekan/luma/releases)

## Download and run

1. Download **Luma-win-x64.zip** from Releases. The GitHub source-code ZIP is for developers.
2. Extract the complete ZIP to a folder.
3. Run **Luma.exe** for portable use, or **KUR.cmd** to install for your Windows account, create shortcuts and start Luma with Windows.

No administrator rights or separate .NET installation are required. The initial public build is unsigned. Windows may show a publisher/reputation prompt; check the repository and published SHA-256 checksum before running a downloaded executable. Windows x64 only; tested on Windows 11. macOS, Linux and native Windows ARM64 builds are not included.

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

Times are editable in **Otomasyon**. Manual selection lasts until the next game/clock context change. Pause temporarily suspends profile application. The classic schedule is available when game + clock automation is disabled.

Installed games are scanned on startup and once per minute. Sources include Steam libraries, Epic manifests, Riot installations, supported GOG/Ubisoft/EA/Battle.net registry entries, Xbox MicrosoftGame.config installations and recognized game folders. Executables inside a detected game are indexed automatically; selecting EXE files is optional. Launchers, crash reporters, installers, SDK tools, dedicated servers and benchmark/background utilities are excluded. Protected or unusual installations may not be discoverable. No injection or game modification is used.

## Backups, controls and shortcuts

Original per-display settings are saved locally before modification. **Ekranlar → Bu ekranın önceki ayarlarını geri yükle** restores the changed controls for that display and disables automatic switching. Generic profiles only restore the controls they touched. Normal app exit restores the initial software gamma curve; physical monitor and driver preferences remain until restored.

- Ctrl+Alt+L: show/hide the tray panel
- Ctrl+Alt+P: pause/resume
- Ctrl+Alt+0: reset the software filter, disable automation and cursor wrapping
- Ctrl+Alt+1…6: standard software presets
- Esc: hide the panel / revert a pending display arrangement

DDC/CI controls also include available contrast, RGB gains, speaker volume and input sources. Monitor arrangement changes have a rollback confirmation. Optional cursor wrapping connects the outer left/right desktop edges. Input switching can interrupt the image and requires an explicit action.

## Privacy

Luma does not send telemetry, upload game lists or contact a server. Settings, monitor IDs, local game paths, driver backups, scan catalogs and logs are stored under **%LOCALAPPDATA%\\Luma**. These files are not part of the repository or downloadable package. Local logs may contain device identifiers and paths; review them before sharing publicly.

Public releases contain program files, generic documentation and runtime license notices. Debug symbols and local build paths are excluded. Windows account names, personal calibration files and local diagnostic screenshots are not included.

## Development

Windows and the .NET 10 SDK are required. The app is WPF / WinForms with native Windows APIs and no third-party NuGet dependencies.

```powershell
dotnet build Luma.slnx -c Release
dotnet run --project Luma.Checks -c Release
.\scripts\build-release.ps1
```

The checks cover gamma safety, time boundaries, per-display identity, generic NVIDIA/AMD/Intel capabilities, WMI identity matching, HDR/WCG preservation, game manifest parsing and recognition of an automatically discovered test process. `--hardware`, `--nvidia` and `--games` are optional local diagnostic modes. They may produce private local artifacts; do not commit them. `--nvidia-drstest` explicitly writes the same effective FXAA value, verifies it, and restores inheritance. Ordinary checks do not modify display or driver settings.

`Luma.exe --render-test <folder>` renders real WPF views offscreen. Screenshots generated on a real machine may contain private hardware names; they are excluded from publication.

Release packaging creates release/Luma-win-x64.zip and SHA256SUMS.txt. The GitHub Release workflow can build and publish an existing version tag. Use the checksums when distributing binaries. The app is MIT licensed; bundled .NET / Windows Desktop runtime license notices are included in releases.

## Turkce kullanim

ZIP'i tamamen cikartip **Luma.exe** dosyasini ac. Kurulum ve Windows baslangici icin **KUR.cmd** dosyasini calistir. Uygulama tepside calisir. **Ekranlar** bolumunde her monitorun algilanan ekran karti, parlaklik yontemi ve Gaming / Aksam profilleri gorunur. **Otomasyon** oyunlari kendisi bulur; saatleri ve otomatik gecisleri buradan yonetebilirsin. NVIDIA disindaki ekran kartlarinda desteklenen monitor / Windows kontrolleri kullanilir.

## API references

- [Microsoft DDC/CI controls](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature)
- [Microsoft SDR gamma constraints](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setdevicegammaramp)
- [Windows monitor brightness](https://learn.microsoft.com/en-us/windows/win32/wmicoreprov/wmimonitorbrightness)
- [NVIDIA NVAPI](https://developer.nvidia.com/nvapi/get-started)
