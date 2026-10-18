using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Luma;

public sealed record MonitorScene(string Id, string Name, string Mode, ColorSettings Color,
    double? Backlight = null, double? Vibrance = null, double? Contrast = null, int? Hue = null,
    NvidiaOutput? Output = null, bool DisableWcg = false, bool SoftwareEnabled = true)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => Name.EndsWith(" · Gaming", StringComparison.Ordinal) ? Name[..^9] + " · " + Loc.T("Gaming")
        : Name.EndsWith(" · Akşam", StringComparison.Ordinal) ? Name[..^8] + " · " + Loc.T("Akşam") : Name;
}
public sealed record MonitorProfileSet(string DisplayId, string MonitorName, string AdapterName, string Vendor,
    string Strategy, MonitorScene Gaming, MonitorScene Evening)
{
    public IEnumerable<MonitorScene> Scenes => new[] { Gaming, Evening };
}
public static class ProfilePlanner
{
    public static string Key(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..12];
    public static string Vendor(string deviceId, string name) => deviceId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) || name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
        : deviceId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) || name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD"
        : deviceId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) || name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "Diğer / Windows";
    public static MonitorProfileSet Create(Display display, NvidiaDisplayState? nv)
    {
        double? brightness = display.BrightnessPercent;
        double? eveningBrightness = brightness.HasValue ? Math.Round(Math.Min(brightness.Value * .55, 30)) : null;
        bool nvidiaColors = nv != null && display.ColorInfoKnown && !display.Hdr;
        double? vibrance = nvidiaColors ? nv?.Vibrance?.Percent : null;
        int? hue = nvidiaColors ? nv?.Hue : null;
        string prefix = "monitor-" + Key(display.Id);
        var gaming = new MonitorScene(prefix + "-gaming", display.Name + " · Gaming", "gaming", new(), brightness, vibrance, Hue: hue);
        var evening = new MonitorScene(prefix + "-evening", display.Name + " · Akşam", "evening",
            new(6000, brightness.HasValue ? 100 : 85, 100, .98), eveningBrightness,
            vibrance.HasValue ? Math.Min(vibrance.Value, 55) : null, Hue: hue);
        return new(display.Id, display.Name, display.AdapterName, display.GpuVendor,
            "Mevcut parlaklık Gaming için korunur; Akşam arka ışık azalır. Kontrast, RGB ve çıkış biçimi korunur.", gaming, evening);
    }
    public static bool CanApplySoftware(Display display, MonitorScene scene) => scene.SoftwareEnabled && !display.SoftwareControlFailed && display.GammaAvailable && display.ColorInfoKnown && !display.Hdr && (display.ActiveColorMode == 0 || scene.DisableWcg && display.ActiveColorMode == 1);
    public static MonitorProfileSet Reconcile(MonitorProfileSet set, Display display, NvidiaDisplayState? nv)
    {
        var fresh = Create(display, nv); var gaming = set.Gaming; var evening = set.Evening;
        if (gaming.Backlight == null && fresh.Gaming.Backlight != null)
        { gaming = gaming with { Backlight = fresh.Gaming.Backlight }; evening = evening with { Backlight = fresh.Evening.Backlight, Color = evening.Color with { Brightness = 100 } }; }
        if (gaming.Vibrance == null && fresh.Gaming.Vibrance != null)
        { gaming = gaming with { Vibrance = fresh.Gaming.Vibrance }; evening = evening with { Vibrance = fresh.Evening.Vibrance }; }
        if (gaming.Hue == null && fresh.Gaming.Hue != null)
        { gaming = gaming with { Hue = fresh.Gaming.Hue }; evening = evening with { Hue = fresh.Evening.Hue }; }
        return set with { MonitorName = display.Name, AdapterName = display.AdapterName, Vendor = display.GpuVendor, Gaming = gaming, Evening = evening };
    }
    public static bool Valid(MonitorProfileSet set, string id)
    {
        bool Scene(MonitorScene scene, string mode) => scene != null && scene.Id == "monitor-" + Key(id) + "-" + mode && scene.Mode == mode && scene.Color != null && scene.Color == scene.Color.Safe()
            && new[] { scene.Backlight, scene.Vibrance, scene.Contrast }.All(x => x == null || double.IsFinite(x.Value) && x >= 0 && x <= 100)
            && (scene.Hue == null || scene.Hue is >= 0 and <= 359);
        return set != null && set.DisplayId == id && Scene(set.Gaming, "gaming") && Scene(set.Evening, "evening");
    }
}
