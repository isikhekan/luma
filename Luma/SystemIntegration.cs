using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Luma;

public static class SystemIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool StartupEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("Luma") is string; }
    }
    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("Luma", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("Luma", false);
    }
    public static bool Open(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); return true; }
        catch (Exception e) { Log.Write(e); return false; }
    }
    public static bool OpenNvidia()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\Control Panel Client\nvcplui.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcplui.exe")
        };
        foreach (var path in candidates) if (File.Exists(path) && Open(path)) return true;
        // DCH driver installs expose NVIDIA Control Panel through the registered AppX application.
        using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        var package = packages?.GetSubKeyNames().FirstOrDefault(x => x.StartsWith("NVIDIACorp.NVIDIAControlPanel_", StringComparison.OrdinalIgnoreCase));
        if (package == null) return false;
        var parts = package.Split('_');
        if (parts.Length < 5) return false;
        string family = parts[0] + "_" + parts[^1];
        return Open($@"shell:AppsFolder\{family}!NVIDIACorp.NVIDIAControlPanel");
    }
}
