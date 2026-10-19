using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Luma;

public sealed record ColorSettings(double Temperature = 6500, double Brightness = 100,
    double Contrast = 100, double Gamma = 1.0)
{
    public ColorSettings Safe() => new(Math.Clamp(Temperature, 2500, 7500),
        Math.Clamp(Brightness, 50, 100), Math.Clamp(Contrast, 75, 125), Math.Clamp(Gamma, 0.7, 1.5));
    public static ColorSettings Lerp(ColorSettings a, ColorSettings b, double t) => new(
        a.Temperature + (b.Temperature - a.Temperature) * t, a.Brightness + (b.Brightness - a.Brightness) * t,
        a.Contrast + (b.Contrast - a.Contrast) * t, a.Gamma + (b.Gamma - a.Gamma) * t);
}

public sealed record Profile(string Id, string Name, string Icon, string Description, ColorSettings Color)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => Defaults().Any(x => x.Id == Id) ? Loc.T(Name) : Name;
    public override string ToString() => DisplayName;
    public static List<Profile> Defaults() => new()
    {
        new("day", "Gündüz", "\uE706", "Doğal ve dengeli", new()),
        new("night", "Gece", "\uE708", "Sıcak, loş tonlar", new(3400, 80)),
        new("read", "Okuma", "\uE736", "Sayfalara odaklan", new(4500, 90, 95)),
        new("game", "Oyun", "\uE7FC", "Nötr ve net", new(6500, 100, 104, 1.08)),
        new("cinema", "Sinema", "\uE714", "Biraz daha atmosfer", new(5600, 88, 108)),
        new("focus", "Odak", "\uE7C1", "Sakin bir çalışma alanı", new(5200, 92))
    };
}

public sealed class ScheduleEntry
{
    public string Time { get; set; } = "20:00";
    public string ProfileId { get; set; } = "night";
    public bool Enabled { get; set; } = true;
}

public sealed class AppSettings
{
    public string Language { get; set; } = "auto";
    public bool ProfileNotifications { get; set; } = true;
    public bool GameOverlay { get; set; } = true;
    public bool RtssInstallApproved { get; set; }
    public bool ScheduleEnabled { get; set; }
    public bool SmoothTransitions { get; set; } = true;
    public bool PauseFullscreen { get; set; }
    public bool WrapCursor { get; set; }
    public bool BreakReminder { get; set; }
    public int BreakMinutes { get; set; } = 30;
    public bool Hotkeys { get; set; } = true;
    public bool Pinned { get; set; }
    public string ActiveProfileId { get; set; } = "day";
    public Dictionary<string, ColorSettings> Displays { get; set; } = new();
    public Dictionary<string, NvidiaPreference> NvidiaDisplays { get; set; } = new();
    public string? SecondarySceneId { get; set; }
    public string? PrimarySceneId { get; set; }
    public bool AdaptiveEnabled { get; set; } = true;
    public Dictionary<string, MonitorProfileSet> MonitorProfiles { get; set; } = new();
    public Dictionary<string, string> ActiveMonitorScenes { get; set; } = new();
    public string DayStarts { get; set; } = "07:00";
    public string EveningStarts { get; set; } = "19:00";
    public List<string> GameExecutables { get; set; } = new();
    public List<string> IgnoredGameExecutables { get; set; } = new();
    public List<Profile> CustomProfiles { get; set; } = new();
    public List<ScheduleEntry> Schedule { get; set; } = new()
    {
        new() { Time = "07:00", ProfileId = "day" },
        new() { Time = "18:30", ProfileId = "read" },
        new() { Time = "22:00", ProfileId = "night" }
    };

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Luma");
    public static string FilePath => Path.Combine(Folder, "settings.json");
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static AppSettings Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
            value.Displays ??= new(); value.NvidiaDisplays ??= new(); value.CustomProfiles ??= new(); value.Schedule ??= new();
            value.MonitorProfiles = (value.MonitorProfiles ?? new()).Where(x => ProfilePlanner.Valid(x.Value, x.Key)).ToDictionary(x => x.Key, x => x.Value);
            value.ActiveMonitorScenes = (value.ActiveMonitorScenes ?? new()).Where(x => x.Value is "gaming" or "evening").ToDictionary(x => x.Key, x => x.Value);
            value.NvidiaDisplays = value.NvidiaDisplays.Where(x => x.Value != null).ToDictionary(x => x.Key, x => new NvidiaPreference(x.Value.Vibrance is double v && double.IsFinite(v) ? Math.Clamp(v, 0, 100) : null, x.Value.Hue is int h ? Math.Clamp(h, 0, 359) : null));
            if (value.SecondarySceneId is not ("lg-gaming" or "lg-evening")) value.SecondarySceneId = null;
            if (value.PrimarySceneId is not ("dell-gaming" or "dell-evening")) value.PrimarySceneId = null;
            if (!TimeOnly.TryParseExact(value.DayStarts, "HH:mm", out _)) value.DayStarts = "07:00";
            if (!TimeOnly.TryParseExact(value.EveningStarts, "HH:mm", out _) || value.EveningStarts == value.DayStarts) value.EveningStarts = value.DayStarts == "19:00" ? "20:00" : "19:00";
            value.GameExecutables = (value.GameExecutables ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
            value.IgnoredGameExecutables = (value.IgnoredGameExecutables ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
            value.Displays = value.Displays.Where(x => x.Value != null).ToDictionary(x => x.Key, x => x.Value.Safe());
            value.CustomProfiles = value.CustomProfiles.Where(x => x != null && x.Color != null && !string.IsNullOrWhiteSpace(x.Id)).Select(x => x with { Color = x.Color.Safe() }).ToList();
            value.Schedule = value.Schedule.Where(x => x != null && TimeOnly.TryParseExact(x.Time, "HH:mm", out _)).ToList();
            value.Language = Loc.Normalize(value.Language);
            value.BreakMinutes = Math.Clamp(value.BreakMinutes, 15, 120);
            return value;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (Exception e) { Log.Write(e); return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, FilePath, true);
    }
}

public static class Scheduler
{
    public static string? Resolve(IEnumerable<ScheduleEntry> entries, TimeOnly now)
    {
        var valid = entries.Where(x => x.Enabled && TimeOnly.TryParseExact(x.Time, "HH:mm", out _))
            .OrderBy(x => TimeOnly.ParseExact(x.Time, "HH:mm")).ToList();
        if (valid.Count == 0) return null;
        return (valid.LastOrDefault(x => TimeOnly.ParseExact(x.Time, "HH:mm") <= now) ?? valid[^1]).ProfileId;
    }
    public static DateTime? NextBoundary(IEnumerable<ScheduleEntry> entries, DateTime now)
    {
        var times = entries.Where(x => x.Enabled && TimeOnly.TryParseExact(x.Time, "HH:mm", out _))
            .Select(x => now.Date + TimeOnly.ParseExact(x.Time, "HH:mm").ToTimeSpan())
            .Select(x => x <= now ? x.AddDays(1) : x).OrderBy(x => x).ToList();
        return times.Count == 0 ? null : times[0];
    }
}

public static class ColorMath
{
    public static (double R, double G, double B) Temperature(double kelvin)
    {
        var t = Math.Clamp(kelvin, 2500, 7500) / 100;
        double r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
        double g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
        double b = t >= 66 ? 255 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        if (Math.Abs(kelvin - 6500) < 1) return (1, 1, 1);
        return (Math.Clamp(r / 255, 0, 1), Math.Clamp(g / 255, 0, 1), Math.Clamp(b / 255, 0, 1));
    }
    public static ushort[] Identity() => Enumerable.Range(0, 768).Select(x => (ushort)((x % 256) * 257)).ToArray();
    public static ushort[] Build(ColorSettings input, ushort[] baseline)
    {
        var s = input.Safe(); var rgb = Temperature(s.Temperature);
        var factors = new[] { rgb.R, rgb.G, rgb.B }; var result = new ushort[768];
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < 256; i++)
            {
                var value = Math.Clamp((Math.Pow(i / 255.0, 1 / s.Gamma) - .5) * s.Contrast / 100 + .5, 0, 1);
                var index = (int)Math.Round(value * 255);
                var mapped = baseline[c * 256 + index] * factors[c] * s.Brightness / 100;
                var safe = Math.Clamp(mapped, Math.Max(0, i * 257 - 32767), Math.Min(65535, i * 257 + 32767));
                result[c * 256 + i] = (ushort)Math.Round(safe);
            }
        return result;
    }
}

public static class Log
{
    private static readonly object Gate = new();
    public static void Write(Exception exception) => Write(exception.ToString());
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                var path = Path.Combine(AppSettings.Folder, "luma.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
            }
        }
        catch { /* Logging must never prevent recovery. */ }
    }
}
