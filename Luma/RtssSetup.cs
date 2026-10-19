using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Luma;

public enum RtssSetupState { NotChecked, NeedsConsent, NeedsPackageManager, Deferred, Installing, Connecting, Ready, Cancelled, Failed, SettingsDisabled }

public interface IRtssSetupPlatform
{
    string? FindServer();
    bool BridgeAvailable();
    string? FindPackageManager();
    Task<int> InstallAsync(string packageManager, CancellationToken token);
    bool StartServer();
    bool NotificationsDisabled(string server);
}

/// <summary>One serialized acquisition attempt. WinGet owns download verification, elevation and installation.</summary>
public sealed class RtssSetup
{
    private readonly IRtssSetupPlatform _platform;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private Task? _pending;
    public RtssSetupState State { get; private set; }
    public bool Busy => State is RtssSetupState.Deferred or RtssSetupState.Installing or RtssSetupState.Connecting;
    public int? ExitCode { get; private set; }
    public bool Missing { get; private set; }
    public event Action? Changed;
    public string Status => StatusFor(State);
    public static string StatusFor(RtssSetupState state) => state switch
    {
        RtssSetupState.NeedsConsent => "RTSS eksik · otomatik kurulum için onay bekleniyor",
        RtssSetupState.NeedsPackageManager => "Windows paket yöneticisi bulunamadı · App Installer gerekli",
        RtssSetupState.Deferred => "RTSS kurulumu oyun veya tam ekran uygulama kapanınca başlayacak",
        RtssSetupState.Installing => "RTSS indiriliyor ve kuruluyor… Windows izin isteyebilir",
        RtssSetupState.Connecting => "RTSS bağlantısı hazırlanıyor…",
        RtssSetupState.Ready => "RTSS hazır · tam ekran oyun bildirimi etkin",
        RtssSetupState.Cancelled => "RTSS kurulumu durduruldu · yeniden deneyebilirsin",
        RtssSetupState.Failed => "RTSS kurulumu veya bağlantısı tamamlanamadı · yeniden dene",
        RtssSetupState.SettingsDisabled => "RTSS bildirimi mevcut ayarlarda kapalı · tercihin korundu",
        _ => "Tam ekran bildirimi hazırlanıyor…"
    };
    public RtssSetup(IRtssSetupPlatform? platform = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    { _platform = platform ?? new WindowsRtssSetup(); _delay = delay ?? Task.Delay; }
    private void Set(RtssSetupState state) { State = state; Changed?.Invoke(); }
    public Task EnsureAsync(bool installApproved, Func<bool> safeToInstall, CancellationToken token)
    {
        if (_pending is { IsCompleted: false }) return _pending;
        return _pending = RunAsync(installApproved, safeToInstall, token);
    }
    private async Task RunAsync(bool installApproved, Func<bool> safeToInstall, CancellationToken token)
    {
        ExitCode = null;
        try
        {
            token.ThrowIfCancellationRequested();
            string? server = _platform.FindServer();
            Missing = server == null && !_platform.BridgeAvailable();
            if (Missing)
            {
                if (!installApproved) { Set(RtssSetupState.NeedsConsent); return; }
                string? manager = _platform.FindPackageManager();
                if (manager == null) { Set(RtssSetupState.NeedsPackageManager); return; }
                if (!safeToInstall()) Set(RtssSetupState.Deferred);
                while (!safeToInstall()) await _delay(TimeSpan.FromSeconds(2), token);
                token.ThrowIfCancellationRequested();
                // Another installer may have completed while the game was running.
                server = _platform.FindServer();
                if (server == null && !_platform.BridgeAvailable())
                {
                    Set(RtssSetupState.Installing);
                    ExitCode = await _platform.InstallAsync(manager, token);
                    token.ThrowIfCancellationRequested();
                    // An exit code alone never proves that a usable server was installed.
                    server = _platform.FindServer();
                    Missing = server == null && !_platform.BridgeAvailable();
                    if (ExitCode is 1223 or unchecked((int)0x800704c7) or unchecked((int)0x80004004) or unchecked((int)0x8a150005) or unchecked((int)0x8a15006a) or unchecked((int)0x8a150077))
                    { Set(RtssSetupState.Cancelled); return; }
                    if (Missing) { Set(RtssSetupState.Failed); return; }
                }
            }
            Missing = server == null && !_platform.BridgeAvailable();
            if (server != null && _platform.NotificationsDisabled(server)) { Set(RtssSetupState.SettingsDisabled); return; }
            if (_platform.BridgeAvailable()) { Set(RtssSetupState.Ready); return; }
            Set(RtssSetupState.Connecting);
            if (!_platform.StartServer()) { Set(RtssSetupState.Failed); return; }
            for (int i = 0; i < 80; i++)
            {
                token.ThrowIfCancellationRequested();
                if (_platform.BridgeAvailable()) { Set(RtssSetupState.Ready); return; }
                await _delay(TimeSpan.FromMilliseconds(250), token);
            }
            Set(RtssSetupState.Failed);
        }
        catch (OperationCanceledException) { Set(RtssSetupState.Cancelled); }
        catch (Exception e) when (e is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        { Log.Write(e); Set(RtssSetupState.Failed); }
    }
}

public sealed class WindowsRtssSetup : IRtssSetupPlatform
{
    public const string PackageId = "Guru3D.RTSS";
    public const string PackageVersion = "7.3.7";
    public const string PublisherUrl = "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/";
    public const string PackageManagerUrl = "https://apps.microsoft.com/detail/9nblggh4nns1";
    public const string PackageManagerTermsUrl = "https://aka.ms/winget-license";
    public static IReadOnlyList<string> InstallArguments { get; } = Array.AsReadOnly(new[]
    {
        "install", "--id", PackageId, "--exact", "--version", PackageVersion, "--source", "winget",
        "--silent", "--no-upgrade", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity"
    });
    public string? FindServer() => RtssInstallation.Find();
    public bool BridgeAvailable() { using var client = new RtssClient(); return client.Available; }
    public bool StartServer() => RtssClient.StartInstalledServer();
    public string? FindPackageManager()
    {
        // Resolve the Windows app execution alias only, never a PATH/current-directory lookalike.
        string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : null;
    }
    public async Task<bool> CheckPackageAsync(CancellationToken token)
    {
        string? manager = FindPackageManager(); if (manager == null) return false;
        var info = new ProcessStartInfo(manager) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "show", "--id", PackageId, "--exact", "--version", PackageVersion, "--source", "winget", "--disable-interactivity" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info); if (process == null) return false;
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token); var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        try { await process.WaitForExitAsync(token); await Task.WhenAll(output, error); return process.ExitCode == 0; }
        finally { if (token.IsCancellationRequested) StopRequester(process); }
    }
    public async Task<int> InstallAsync(string packageManager, CancellationToken token)
    {
        var info = new ProcessStartInfo(packageManager)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };
        foreach (string argument in InstallArguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start Windows Package Manager.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        // Drain both pipes without retaining potentially private package-manager diagnostics.
        Task output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        Task error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { StopRequester(process); return -1; }
        catch (OperationCanceledException)
        {
            // Stop the requester; never kill an elevated installer during a system transaction.
            StopRequester(process);
            throw;
        }
        catch (TimeoutException) { return process.HasExited ? process.ExitCode : -1; }
    }
    private static void StopRequester(Process process)
    { try { if (!process.HasExited) process.Kill(); } catch (Win32Exception) { } catch (InvalidOperationException) { } }
    public bool NotificationsDisabled(string server)
    {
        string folder = Path.GetDirectoryName(server)!;
        bool IsZero(string relative, string section, string key)
        {
            string file = Path.Combine(folder, relative);
            return File.Exists(file) && RtssInstallation.ReadIniValue(File.ReadAllText(file), section, key) is "0" or "00000000";
        }
        // Fresh RTSS defaults enable OSD, disable permanent statistics and start minimized.
        // Preserve exclusions, detection level, FPS caps and existing clients' settings.
        return IsZero("Profiles/Global", "OSD", "EnableOSD") || IsZero("Profiles/Global", "Hooking", "EnableHooking")
            || IsZero("Profiles/Config", "Shared", "Flags");
    }
}

public static class RtssInstallation
{
    public static string? Find()
    {
        var candidates = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS");
                if (key?.GetValue("InstallLocation") is string location && Path.IsPathFullyQualified(location.Trim('"')))
                    candidates.Add(Path.Combine(location.Trim('"'), "RTSS.exe"));
                if (key?.GetValue("DisplayIcon") is string icon && ExecutableFromIcon(icon) is string exe) candidates.Add(exe);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
        }
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "RivaTuner Statistics Server", "RTSS.exe"));
        foreach (var process in Process.GetProcessesByName("RTSS"))
        {
            using (process) { try { if (process.MainModule?.FileName is string path) candidates.Add(path); } catch (Exception e) when (e is Win32Exception or InvalidOperationException) { } }
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(IsServer);
    }
    public static string? ExecutableFromIcon(string icon)
    {
        string value = icon.Trim();
        if (value.StartsWith('"')) { int end = value.IndexOf('"', 1); if (end < 0) return null; value = value[1..end]; }
        else { int comma = value.LastIndexOf(','); if (comma >= 0 && int.TryParse(value[(comma + 1)..].Trim(), out _)) value = value[..comma].Trim(); }
        return Path.IsPathFullyQualified(value) && Path.GetExtension(value).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? value : null;
    }
    public static bool IsServer(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !Path.GetFileName(path).Equals("RTSS.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(info.OriginalFilename, "RTSS.exe", StringComparison.OrdinalIgnoreCase)
                && (info.ProductName == "RTSS" || info.ProductName?.Contains("RivaTuner", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    public static string? ReadIniValue(string text, string section, string key)
    {
        bool inside = false; string? result = null;
        foreach (string line in text.Split('\n'))
        {
            string value = line.Trim();
            if (value.Length == 0 || value[0] is ';' or '#') continue;
            if (value.StartsWith('[') && value.EndsWith(']')) { inside = value[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase); continue; }
            int eq = value.IndexOf('=');
            if (inside && eq >= 0 && value[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) result = value[(eq + 1)..].Split(';')[0].Trim();
        }
        return result;
    }
}
