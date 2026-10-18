using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Luma;

public sealed record TuningSnapshot(string DisplayId, string Name, DateTimeOffset Captured, ColorSettings Color,
    IReadOnlyDictionary<byte, uint> Hardware, NvidiaDisplayState? Nvidia, NvidiaPreference? Preference,
    string? SceneId = null, uint ColorMode = 0, double? BrightnessPercent = null, string[]? ModifiedControls = null);

public static class TuningService
{
    private static readonly System.Threading.SemaphoreSlim Gate = new(1, 1);
    public static bool IsEligible(Display display) => display.Number == 2; // Legacy CLI selection only.
    public static bool IsPrimaryEligible(Display display) => display.Primary;
    public static bool IsTunable(Display display) => true;
    public static string BackupPath(string id) => Path.Combine(AppSettings.Folder, "tuning-" + ProfilePlanner.Key(id) + "-before.json");
    public static string ReportPath => Path.Combine(AppSettings.Folder, "tuning-report.json");
    public static async Task WaitIdleAsync() { await Gate.WaitAsync(); Gate.Release(); }
    public static IEnumerable<MonitorScene> AllScenes(Engine engine) => engine.Displays.Monitors.SelectMany(d => ScenesFor(engine, d));
    public static IEnumerable<MonitorScene> ScenesFor(Engine engine, Display display) => engine.Settings.MonitorProfiles.TryGetValue(display.Id, out var profiles) ? profiles.Scenes : Array.Empty<MonitorScene>();
    public static MonitorScene? FindScene(Engine engine, string id) => AllScenes(engine).FirstOrDefault(x => x.Id == id);
    public static MonitorScene? SceneFor(Engine engine, Display display, string mode) => ScenesFor(engine, display).FirstOrDefault(x => x.Mode == mode);
    public static string? ActiveScene(Engine engine, Display display) => engine.Settings.ActiveMonitorScenes.TryGetValue(display.Id, out var mode) ? SceneFor(engine, display, mode)?.Id : null;
    private static void SetActiveScene(Engine engine, Display display, string? id)
    {
        var scene = id == null ? null : ScenesFor(engine, display).FirstOrDefault(x => x.Id == id);
        if (scene == null) engine.Settings.ActiveMonitorScenes.Remove(display.Id); else engine.Settings.ActiveMonitorScenes[display.Id] = scene.Mode;
    }
    // Old reports are local user data. No model IDs or another user's calibration are shipped.
    private static MonitorScene? ReadLegacyScene(Display display, MonitorScene fallback)
    {
        foreach (string prefix in new[] { "dell-", "lg-" })
        try
        {
            string file = Path.Combine(AppSettings.Folder, "tuning-report-" + prefix + fallback.Mode + ".json");
            if (!File.Exists(file)) continue;
            using var json = JsonDocument.Parse(File.ReadAllText(file)); var root = json.RootElement;
            if (root.GetProperty("DisplayId").GetString() != display.Id) continue;
            var color = root.GetProperty("Software").Deserialize<ColorSettings>() ?? fallback.Color;
            var nv = root.GetProperty("Nvidia").Deserialize<NvidiaDisplayState>();
            double? contrast = null;
            foreach (var feature in root.GetProperty("Hardware").EnumerateArray())
                if (feature.GetProperty("Code").GetString() == "12" && feature.GetProperty("Maximum").GetDouble() > 0)
                    contrast = feature.GetProperty("Current").GetDouble() * 100 / feature.GetProperty("Maximum").GetDouble();
            return fallback with { Color = color.Safe(), Backlight = root.GetProperty("RequestedBacklightPercent").GetDouble(), Contrast = contrast,
                Vibrance = nv?.Vibrance?.Percent, Hue = nv?.Hue, Output = nv?.Output, DisableWcg = true };
        }
        catch (Exception e) { Log.Write(e); }
        return null;
    }
    public static void EnsureProfiles(Engine engine)
    {
        if (!engine.HardwareReady || !engine.NvidiaReady) return;
        foreach (var display in engine.Displays.Monitors)
        {
            if (engine.Settings.MonitorProfiles.TryGetValue(display.Id, out var existing))
            { engine.Settings.MonitorProfiles[display.Id] = ProfilePlanner.Reconcile(existing, display, engine.Nvidia.Displays.GetValueOrDefault(display.Id)); continue; }
            var pair = ProfilePlanner.Create(display, engine.Nvidia.Displays.GetValueOrDefault(display.Id));
            bool migrate = engine.Settings.PrimarySceneId != null || engine.Settings.SecondarySceneId != null;
            var gaming = migrate ? ReadLegacyScene(display, pair.Gaming) : null; var evening = migrate ? ReadLegacyScene(display, pair.Evening) : null;
            if (gaming != null || evening != null) pair = pair with { Strategy = "Bu bilgisayardaki önceki kişisel profiller korundu.", Gaming = gaming ?? pair.Gaming, Evening = evening ?? pair.Evening };
            engine.Settings.MonitorProfiles[display.Id] = pair;
            string? old = display.Primary ? engine.Settings.PrimarySceneId : display.Number == 2 ? engine.Settings.SecondarySceneId : null;
            if (old != null) engine.Settings.ActiveMonitorScenes[display.Id] = old.EndsWith("evening") ? "evening" : "gaming";
        }
        engine.SaveSoon();
    }
    private static bool Software(Display display, MonitorScene scene) => ProfilePlanner.CanApplySoftware(display, scene);
    public static bool Matches(Engine engine, Display display, MonitorScene scene)
    {
        if (ActiveScene(engine, display) != scene.Id) return false;
        if (Software(display, scene) && engine.Settings.Displays.GetValueOrDefault(display.Id) != scene.Color) return false;
        if (scene.Backlight.HasValue && display.BrightnessPercent is double brightness && Math.Abs(brightness - scene.Backlight.Value) > 1) return false;
        if (scene.Contrast is double contrast && display.Features.TryGetValue(0x12, out var f) && Math.Abs(f.Percent - contrast) > 1) return false;
        if (!display.Hdr && engine.Nvidia.Displays.TryGetValue(display.Id, out var nv))
        {
            if (scene.Vibrance is double v && nv.Vibrance != null && Math.Abs(nv.Vibrance.Percent - v) > .6) return false;
            if (scene.Hue is int hue && nv.Hue.HasValue && nv.Hue != hue) return false;
            if (scene.Output is { } output && nv.Output != output) return false;
        }
        return true;
    }
    private static TuningSnapshot Capture(Engine engine, Display display, string[]? controls = null) => new(display.Id, display.Name, DateTimeOffset.Now,
        engine.Settings.Displays.GetValueOrDefault(display.Id, new()), display.Features.Where(x => x.Key is 0x10 or 0x12 or 0x16 or 0x18 or 0x1A).ToDictionary(x => x.Key, x => x.Value.Current),
        engine.Nvidia.Displays.GetValueOrDefault(display.Id), engine.Settings.NvidiaDisplays.GetValueOrDefault(display.Id), ActiveScene(engine, display), display.ActiveColorMode, display.BrightnessPercent, controls);
    private static void SaveBackup(TuningSnapshot before)
    {
        string file = BackupPath(before.DisplayId); Directory.CreateDirectory(AppSettings.Folder);
        if (File.Exists(file))
        {
            var original = JsonSerializer.Deserialize<TuningSnapshot>(File.ReadAllText(file));
            if (original == null || original.DisplayId != before.DisplayId) throw new InvalidDataException("Ekran yedeği doğrulanamadı.");
            if (original.ModifiedControls == null) return; // Preserve the older full backup exactly.
            before = original with { ModifiedControls = original.ModifiedControls.Concat(before.ModifiedControls ?? Array.Empty<string>()).Distinct().ToArray(),
                BrightnessPercent = original.BrightnessPercent ?? before.BrightnessPercent, Nvidia = original.Nvidia ?? before.Nvidia };
        }
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(before, AppSettings.JsonOptions)); File.Move(file + ".tmp", file, true);
    }
    public static async Task ApplyAsync(Engine engine, string displayId, string sceneId)
    {
        if (!await Gate.WaitAsync(0)) throw new InvalidOperationException("Ekran profili uygulanıyor. Tamamlanmasını bekle.");
        bool began = false;
        try
        {
            engine.BeginTuning(); began = true;
            await engine.EnsureHardwareAsync(); await engine.EnsureNvidiaAsync(); EnsureProfiles(engine);
            var display = engine.Displays.Monitors.SingleOrDefault(x => x.Id == displayId) ?? throw new InvalidOperationException("Ekran bağlı değil.");
            var scene = ScenesFor(engine, display).SingleOrDefault(x => x.Id == sceneId) ?? throw new InvalidOperationException("Profil bu ekrana ait değil.");
            var controls = new List<string>(); var notes = new List<string>();
            bool software = Software(display, scene);
            if (scene.Backlight != null && display.BrightnessPercent != null) controls.Add("brightness");
            if (scene.Contrast != null && display.Features.ContainsKey(0x12)) controls.Add("contrast");
            if (software) controls.Add("software");
            if (scene.DisableWcg && display.ActiveColorMode == 1 && !display.Hdr) controls.Add("wcg");
            bool nvidia = !display.Hdr && display.ColorInfoKnown && engine.Nvidia.Displays.ContainsKey(displayId);
            if (nvidia && (scene.Vibrance != null || scene.Hue != null)) controls.Add("nvidia-color");
            if (nvidia && scene.Output != null) controls.Add("nvidia-output");
            var before = Capture(engine, display, controls.ToArray()); SaveBackup(before);
            bool pendingOutput = false;
            try
            {
                bool brightness = false;
                if (controls.Contains("brightness"))
                {
                    brightness = await engine.Displays.SetBrightnessAsync(displayId, scene.Backlight!.Value);
                    if (!brightness) notes.Add("Donanım parlaklığı doğrulanamadı; varsa SDR yazılımsal karartma kullanıldı.");
                }
                if (controls.Contains("contrast") && !await engine.Displays.SetHardwareAsync(displayId, 0x12, (uint)Math.Round(display.Features[0x12].Maximum * scene.Contrast!.Value / 100))) notes.Add("Kontrast değişikliği doğrulanamadı.");
                if (nvidia && engine.Nvidia.Displays.TryGetValue(displayId, out var nv))
                {
                    double? vibrance = null; int? hue = null;
                    try
                    {
                        if (scene.Vibrance is double v && nv.Vibrance != null) { if (Math.Abs(v - nv.Vibrance.Percent) > .6) await engine.Nvidia.SetVibranceAsync(displayId, v); vibrance = v; }
                        if (scene.Hue is int h && nv.Hue.HasValue) { if (h != nv.Hue) await engine.Nvidia.SetHueAsync(displayId, h); hue = h; }
                        if (vibrance != null || hue != null) engine.Settings.NvidiaDisplays[displayId] = new(vibrance, hue);
                    }
                    catch (Exception e) { Log.Write(e); notes.Add("NVIDIA renk kontrolü uygulanamadı; diğer kontroller kullanıldı."); }
                    if (scene.Output is { } output && nv.Output != null && output != nv.Output)
                    {
                        if (await engine.Nvidia.SupportsOutputAsync(displayId, output))
                        {
                            File.WriteAllText(NvidiaOutputRecovery.FilePath, JsonSerializer.Serialize(new NvidiaOutputRecovery(displayId, nv.Output, display.ActiveColorMode)));
                            pendingOutput = true; await engine.Nvidia.SetOutputAsync(displayId, output);
                            var mode = new Native.DevMode { Size = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<Native.DevMode>() };
                            if (!Native.EnumDisplaySettings(display.Device, -1, ref mode) || mode.PelsWidth != display.PixelWidth || mode.PelsHeight != display.PixelHeight || mode.DisplayFrequency != display.Refresh) throw new InvalidOperationException("Çözünürlük / Hz değişti; çıkış geri alınacak.");
                        }
                        else notes.Add("Kişisel NVIDIA çıkış profili bu bağlantıda desteklenmiyor; mevcut çıkış korundu.");
                    }
                }
                if (controls.Contains("wcg")) await engine.Displays.SetWideColorAsync(displayId, false);
                var color = scene.Color;
                if (software && scene.Mode == "evening" && !brightness && scene.Backlight != null) color = color with { Brightness = Math.Min(color.Brightness, 85) };
                engine.SetDisplayColor(displayId, color); SetActiveScene(engine, display, scene.Id); await engine.FlushColorsAsync();
                if (software && display.ColorStatus != "Renk ayarı uygulandı")
                {
                    await engine.Displays.RestoreColorAsync(displayId); display.SoftwareControlFailed = true; software = false;
                    engine.Settings.Displays[displayId] = before.Color;
                    notes.Add("Sürücü SDR eğrisini doğrulamadı; başlangıç eğrisi geri gönderildi. Donanım kontrolleri kullanılmaya devam ediyor.");
                }
                if (!software) notes.Add(display.Hdr ? "HDR korunuyor; SDR renk eğrisi atlandı." : display.ActiveColorMode == 1 ? "Windows renk yönetimi korunuyor; SDR eğrisi atlandı." : "SDR renk kontrolü sunulmuyor; erişilebilir donanım kontrolleri kullanıldı.");
                var report = new { Applied = DateTimeOffset.Now, Scene = scene.Id, scene.Name, scene.Mode, DisplayId = display.Id, Monitor = display.Name, display.AdapterName, display.GpuVendor,
                    MeasurementBasedCalibration = false, RequestedBacklightPercent = scene.Backlight, Hardware = display.Features.Values.Select(x => new { Code = x.Code.ToString("X2"), x.Current, x.Maximum }),
                    display.BrightnessBackend, display.WindowsBrightnessPercent, Software = color, SoftwareApplied = software, Nvidia = engine.Nvidia.Displays.GetValueOrDefault(displayId),
                    Resolution = display.Detail, ColorMode = display.ActiveColorMode, GammaReadback = display.ColorStatus, Backup = BackupPath(displayId), Notes = notes };
                engine.Settings.Save(); string json = JsonSerializer.Serialize(report, AppSettings.JsonOptions);
                File.WriteAllText(ReportPath, json); File.WriteAllText(Path.Combine(AppSettings.Folder, "tuning-report-" + scene.Id + ".json"), json);
                if (pendingOutput) File.Delete(NvidiaOutputRecovery.FilePath);
                engine.AnnounceSceneApplied();
            }
            catch { try { await RestoreSnapshotAsync(engine, before); } catch (Exception e) { Log.Write(e); } throw; }
        }
        finally { if (began) engine.EndTuning(); Gate.Release(); }
    }
    public static async Task RestoreAsync(Engine engine, string id)
    {
        if (!await Gate.WaitAsync(0)) throw new InvalidOperationException("Ekran profili uygulanıyor.");
        bool began = false;
        try
        {
            engine.BeginTuning(); began = true;
            var saved = JsonSerializer.Deserialize<TuningSnapshot>(File.ReadAllText(BackupPath(id))) ?? throw new InvalidDataException("Yedek okunamadı.");
            if (saved.DisplayId != id) throw new InvalidDataException("Yedek bu ekrana ait değil.");
            await RestoreSnapshotAsync(engine, saved); engine.Settings.ActiveMonitorScenes.Remove(id);
            engine.Settings.AdaptiveEnabled = false; engine.AdaptiveSettingsChanged(); engine.Settings.Save(); engine.AnnounceSceneApplied();
        }
        finally { if (began) engine.EndTuning(); Gate.Release(); }
    }
    private static async Task RestoreSnapshotAsync(Engine engine, TuningSnapshot saved)
    {
        var display = engine.Displays.Monitors.Single(x => x.Id == saved.DisplayId); var failures = new List<Exception>();
        bool Changed(string control) => saved.ModifiedControls == null || saved.ModifiedControls.Contains(control);
        async Task Attempt(Func<Task> action) { try { await action(); } catch (Exception e) { Log.Write(e); failures.Add(e); } }
        if (Changed("brightness"))
        {
            if (saved.Hardware.TryGetValue(0x10, out uint brightness)) await Attempt(async () => { if (!await engine.Displays.SetHardwareAsync(saved.DisplayId, 0x10, brightness)) throw new InvalidOperationException("Parlaklık geri yüklenemedi."); });
            else if (saved.BrightnessPercent is double value) await Attempt(async () => { if (!await engine.Displays.SetBrightnessAsync(saved.DisplayId, value)) throw new InvalidOperationException("Windows parlaklığı geri yüklenemedi."); });
        }
        if (Changed("contrast") && saved.Hardware.TryGetValue(0x12, out uint contrast)) await Attempt(async () => { if (!await engine.Displays.SetHardwareAsync(saved.DisplayId, 0x12, contrast)) throw new InvalidOperationException("Kontrast geri yüklenemedi."); });
        if (saved.Nvidia is { } nv && !display.Hdr)
        {
            if (Changed("nvidia-color") && nv.Vibrance != null) await Attempt(() => engine.Nvidia.SetVibranceAsync(saved.DisplayId, nv.Vibrance.Percent));
            if (Changed("nvidia-color") && nv.Hue is int hue) await Attempt(() => engine.Nvidia.SetHueAsync(saved.DisplayId, hue));
            if (Changed("nvidia-output") && nv.Output != null) await Attempt(() => engine.Nvidia.SetOutputAsync(saved.DisplayId, nv.Output));
        }
        if (Changed("wcg") && !display.Hdr && display.ColorInfoKnown && saved.ColorMode <= 1) await Attempt(() => engine.Displays.SetWideColorAsync(saved.DisplayId, saved.ColorMode == 1));
        if (saved.Preference == null) engine.Settings.NvidiaDisplays.Remove(saved.DisplayId); else engine.Settings.NvidiaDisplays[saved.DisplayId] = saved.Preference;
        if (Changed("software")) await Attempt(async () => { engine.SetDisplayColor(saved.DisplayId, saved.Color); await engine.FlushColorsAsync(); });
        SetActiveScene(engine, display, saved.SceneId); engine.Settings.Save();
        if (failures.Count > 0) throw new AggregateException("Bazı ayarlar geri yüklenemedi. Yedek korunuyor.", failures);
        if (File.Exists(NvidiaOutputRecovery.FilePath)) File.Delete(NvidiaOutputRecovery.FilePath);
    }
}
