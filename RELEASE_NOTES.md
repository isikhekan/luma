Luma 1.4.0

- Detects each monitor and its display adapter, including NVIDIA, AMD and Intel identifiers.
- Generates individual Gaming and Evening profiles from the controls available on each display.
- Uses DDC/CI backlight control, Windows panel brightness when available, or an SDR software fallback.
- Preserves generic monitor contrast, RGB gains, output format, refresh rate, HDR and automatic color management.
- NVIDIA controls are enabled only when the connected display and driver expose them. AMD/Intel use monitor and Windows controls; native AMD/Intel driver panels are not implemented.
- Automatically discovers installed games and switches profiles when games start/exit.
- Includes a self-contained Windows x64 executable and optional per-user installer. No separate .NET installation is required.
- Settings, backups, hardware identifiers, game catalogs and logs remain local and are excluded from the distribution.

Download Luma-win-x64.zip, extract it, and run Luma.exe. Run KUR.cmd for installation, shortcuts and Windows startup.

The initial public build is unsigned. Hardware-specific capabilities vary. Real NVIDIA/DDC behavior has been checked on a Windows 11 desktop; AMD/Intel, HDR and laptop capability combinations have automated tests, not live hardware certification. Profiles are appearance presets, not measured calibration or OLED emulation.
