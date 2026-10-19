using System.Runtime.InteropServices;
using System.Text.Json;
using Luma;

if (args.Contains("--game-probe"))
{
    int seconds = args.SkipWhile(x => x != "--probe-seconds").Skip(1).FirstOrDefault() is string duration && int.TryParse(duration, out int requested) ? Math.Clamp(requested, 1, 60) : 35;
    await Task.Delay(TimeSpan.FromSeconds(seconds)); return;
}
if (args.Contains("--rtss-probe")) { RtssChecks.Probe(args.Contains("--write")); return; }
if (args.Contains("--rtss-setup-probe"))
{
    var platform = new WindowsRtssSetup(); string? server = platform.FindServer();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    Console.WriteLine(JsonSerializer.Serialize(new { Installed = server != null, BridgeAvailable = platform.BridgeAvailable(),
        PackageManagerAvailable = platform.FindPackageManager() != null, ReviewedPackageAvailable = await platform.CheckPackageAsync(deadline.Token),
        NotificationsDisabled = server != null && platform.NotificationsDisabled(server) })); return;
}

int assertions = 0;
void Check(bool condition, string name) { assertions++; if (!condition) throw new Exception("FAIL: " + name); }
RtssChecks.Run(Check);
await RtssSetupChecks.RunAsync(Check);

Check(Loc.Resolve("auto", new System.Globalization.CultureInfo("tr-TR")) == "tr", "Turkish system language");
Check(Loc.Resolve("auto", new System.Globalization.CultureInfo("es-MX")) == "es", "Spanish variants");
Check(Loc.Resolve("auto", new System.Globalization.CultureInfo("zh-TW")) == "zh-Hans", "Chinese fallback");
Check(Loc.Resolve("auto", new System.Globalization.CultureInfo("de-DE")) == "en", "Unsupported system language falls back to English");
Check(Loc.Normalize("../../invalid") == "auto", "Only known language codes accepted");
var sourceKeys = Loc.Catalog("en").Keys.OrderBy(x => x).ToArray();
var driverValues = NvidiaService.Definitions.SelectMany(d => d.Options.Select(o => (d.Id, o.Value))).ToArray();
var placeholder = new System.Text.RegularExpressions.Regex(@"\{\d+(?:,[^}:]+)?(?::[^}]+)?\}");
foreach (string language in new[] { "en", "es", "zh-Hans" })
{
    var catalog = Loc.Catalog(language);
    Check(catalog.Keys.OrderBy(x => x).SequenceEqual(sourceKeys), "Identical translation coverage: " + language);
    Loc.Set(language);
    foreach (var pair in catalog)
    {
        Check(!string.IsNullOrWhiteSpace(pair.Value), "Nonempty translation: " + pair.Key);
        Check(placeholder.Matches(pair.Key).Select(x => x.Value).OrderBy(x => x).SequenceEqual(placeholder.Matches(pair.Value).Select(x => x.Value).OrderBy(x => x)), "Translation placeholders preserved: " + pair.Key);
        Check(Loc.T(pair.Key) == pair.Value, "Exact translation: " + pair.Key);
        // Every format must resolve with real numeric/time values, without raw placeholders.
        if (placeholder.IsMatch(pair.Key))
        {
            object[] values = Enumerable.Range(0, 6).Select(_ => pair.Key.Contains("HH:mm") ? (object)new DateTime(2026, 10, 19, 19, 0, 0) : (object)42).ToArray();
            string formatted = string.Format(Loc.Culture, pair.Value, values);
            Check(!placeholder.IsMatch(formatted), "Formatted translation: " + pair.Key);
            string legacy = string.Format(System.Globalization.CultureInfo.InvariantCulture, pair.Key, values);
            Check(Loc.T(legacy) == formatted, "Legacy status templates: " + pair.Key);
        }
    }
    Check(Loc.T("Gaming · gündüz (07:00–19:00)") == string.Format(Loc.Culture, catalog["Gaming · gündüz ({0}–{1})"], "07:00", "19:00"), "Dynamic daytime status translated");
    Check(Loc.T("Oyun bekleniyor · 12 oyun otomatik eklendi") == string.Format(Loc.Culture, catalog["Oyun bekleniyor · {0} oyun otomatik eklendi"], 12), "Dynamic game count translated");
    Check(Loc.F($"{3} ekran algılandı.") == string.Format(Loc.Culture, catalog["{0} ekran algılandı."], 3), "Typed formatting");
    Check(new Profile("custom-id", "Gece", "", "", new()).DisplayName == "Gece", "Custom profile name preserved");
    Check(new MonitorScene("monitor-id", "Gece · Akşam", "evening", new()).DisplayName == "Gece · " + catalog["Akşam"], "Hardware name preserved in monitor profile");
    Check(driverValues.SequenceEqual(NvidiaService.Definitions.SelectMany(d => d.Options.Select(o => (d.Id, o.Value)))), "Driver option values stay stable across languages");
    var roundtrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { Language = language, ProfileNotifications = false }));
    Check(roundtrip?.Language == language && !roundtrip.ProfileNotifications, "Language and notification preference persisted");
}
Loc.Set("tr");
Check(Loc.T("Ekranlar") == "Ekranlar" && Profile.Defaults()[0].DisplayName == "Gündüz", "Turkish source language");
Check(JsonSerializer.Deserialize<AppSettings>("{}") is { Language: "auto", ProfileNotifications: true }, "Existing settings migrate without reset");
var feedback = new FeedbackGate(); var feedbackTime = DateTimeOffset.UtcNow;
Check(feedback.Accept("gaming", feedbackTime), "First notification accepted");
Check(!feedback.Accept("gaming", feedbackTime.AddMilliseconds(500)), "Duplicate notification suppressed");
Check(feedback.Accept("evening", feedbackTime.AddMilliseconds(600)), "New profile replaces prior notification");
Check(feedback.Accept("evening", feedbackTime.AddSeconds(3)), "Later notification accepted");
Check(FeedbackGate.AutomaticTitle(true, false) == "Oyun algılandı", "Game entry title");
Check(FeedbackGate.AutomaticTitle(false, true) == "Oyun kapandı", "Game exit title");
Check(FeedbackGate.AutomaticTitle(false, false) == "Saat profili uygulandı", "Clock transition title");

var identity = ColorMath.Identity();
Check(identity.Length == 768 && identity[0] == 0 && identity[255] == 65535, "16-bit RGB identity");
Check(ColorMath.Build(new(), identity).SequenceEqual(identity), "Neutral profile preserves calibration");
var calibrated = identity.Select(x => (ushort)Math.Round(Math.Pow(x / 65535.0, 1.08) * 65535)).ToArray();
Check(ColorMath.Build(new(), calibrated).SequenceEqual(calibrated), "Neutral profile preserves non-linear ICC baseline");
foreach (var temperature in new[] { 2500, 3400, 4500, 6500, 7500 })
foreach (var brightness in new[] { 50, 80, 100 })
foreach (var gamma in new[] { .7, 1.0, 1.5 })
foreach (var contrast in new[] { 75, 100, 125 })
{
    var ramp = ColorMath.Build(new(temperature, brightness, contrast, gamma), identity);
    Check(ramp.Length == 768, "Ramp shape");
    for (int c = 0; c < 3; c++)
    for (int i = 0; i < 256; i++)
    {
        Check(Math.Abs(ramp[c * 256 + i] - i * 257) <= 32767, "Windows readable-screen constraint");
        if (i > 0) Check(ramp[c * 256 + i] >= ramp[c * 256 + i - 1], "Monotonic channel");
    }
}
Check(new ColorSettings(-500, -50, 1000, -4).Safe() == new ColorSettings(2500, 50, 125, .7), "Imported settings clamped");
Check(ColorSettings.Lerp(new(6500), new(3500), .5).Temperature == 5000, "Transition interpolation");
var schedule = new List<ScheduleEntry> { new() { Time = "07:00", ProfileId = "day" }, new() { Time = "22:00", ProfileId = "night" } };
Check(Scheduler.Resolve(schedule, new(2, 0)) == "night", "Schedule crosses midnight");
Check(Scheduler.Resolve(schedule, new(7, 0)) == "day", "Schedule exact boundary");
Check(Scheduler.Resolve(schedule, new(21, 59)) == "day", "Schedule before boundary");
schedule.Add(new() { Time = "23:00", ProfileId = "focus", Enabled = false });
Check(Scheduler.Resolve(schedule, new(23, 30)) == "night", "Disabled schedule ignored");
Check(Scheduler.Resolve(new[] { new ScheduleEntry { Time = "invalid" } }, new(12, 0)) == null, "Invalid schedule ignored");
Check(Scheduler.Resolve(Array.Empty<ScheduleEntry>(), new(12, 0)) == null, "Empty schedule");
var now = new DateTime(2026, 10, 18, 23, 0, 0);
Check(Scheduler.NextBoundary(schedule, now) == new DateTime(2026, 10, 19, 7, 0, 0), "Next-day manual override expiry");
Check(Scheduler.NextBoundary(schedule, new(2026, 10, 18, 7, 0, 0)) == new DateTime(2026, 10, 18, 22, 0, 0), "Boundary is strictly future");
var adaptive = new AdaptivePolicy();
DateTimeOffset At(int hour, int minute = 0, int second = 0) => new(2026, 10, 18, hour, minute, second, TimeSpan.FromHours(3));
Check(adaptive.Observe(At(6, 59), false, new(7, 0), new(19, 0)).Scene == "evening", "Early morning uses evening");
Check(adaptive.Observe(At(7), false, new(7, 0), new(19, 0)).Reason == "day", "Day exact boundary");
Check(adaptive.Observe(At(18, 59), false, new(7, 0), new(19, 0)).Scene == "gaming", "Daytime gaming preset");
Check(adaptive.Observe(At(19), false, new(7, 0), new(19, 0)).Scene == "evening", "Evening exact boundary");
Check(adaptive.Observe(At(22), true, new(7, 0), new(19, 0)).Reason == "game", "Game overrides evening");
Check(adaptive.Observe(At(22, 0, 7), false, new(7, 0), new(19, 0)).GameActive, "Process handover grace period");
Check(adaptive.Observe(At(22, 0, 8), false, new(7, 0), new(19, 0)).Scene == "evening", "Last game exits to current evening clock");
Check(adaptive.Observe(At(23), true, new(7, 0), new(19, 0)).Scene == "gaming", "Restarted game overrides clock again");
Check(adaptive.Observe(At(23, 0, 2), true, new(7, 0), new(19, 0)).Reason == "game", "Game remains active independent of foreground");
Check(new AdaptiveDecision("gaming", "day", false).Context != new AdaptiveDecision("gaming", "game", true).Context, "Game launch overrides daytime manual edits even when default scene is Gaming");
Check(new AdaptivePolicy().Observe(At(2), false, new(22, 0), new(6, 0)).Scene == "gaming", "Custom day schedule spans midnight");
Check(new AdaptivePolicy().Observe(At(6), false, new(22, 0), new(6, 0)).Scene == "evening", "Cross-midnight evening boundary");
var generic = new Display { Id = "test-monitor-a", Name = "Generic Panel", AdapterName = "AMD Radeon", AdapterDeviceId = "PCI\\VEN_1002", ColorInfoKnown = true, GammaAvailable = true,
    Features = new Dictionary<byte, HardwareFeature> { [0x10] = new() { Code = 0x10, Current = 80, Maximum = 100 }, [0x12] = new() { Code = 0x12, Current = 61, Maximum = 100 } } };
var planned = ProfilePlanner.Create(generic, null);
Check(planned.Vendor == "AMD" && planned.Gaming.Backlight == 80 && planned.Evening.Backlight == 30, "Unknown AMD monitor gets its own hardware brightness profiles");
Check(planned.Gaming.Contrast == null && planned.Gaming.Output == null && planned.Evening.Vibrance == null, "Generic profiles preserve contrast, output format and unsupported GPU color controls");
Check(ProfilePlanner.CanApplySoftware(generic, planned.Evening), "SDR gamma available for generic monitor");
generic.Hdr = true; generic.ActiveColorMode = 2;
Check(!ProfilePlanner.CanApplySoftware(generic, planned.Evening) && !planned.Evening.DisableWcg, "HDR preserved and SDR curve skipped");
generic.Hdr = false; generic.ActiveColorMode = 1;
Check(!ProfilePlanner.CanApplySoftware(generic, planned.Evening), "Generic WCG color management stays enabled");
generic.ActiveColorMode = 0; generic.SoftwareControlFailed = true;
Check(!ProfilePlanner.CanApplySoftware(generic, planned.Evening), "Rejected software control does not disable hardware profile");
var laptop = new Display { Id = "test-laptop", Name = "Internal Panel", AdapterName = "Intel Graphics", AdapterDeviceId = "PCI\\VEN_8086", ColorInfoKnown = true, GammaAvailable = true, WindowsBrightnessInstance = "DISPLAY\\TEST\\INSTANCE_0", WindowsBrightnessPercent = 60 };
var laptopPlan = ProfilePlanner.Create(laptop, null);
Check(laptopPlan.Vendor == "Intel" && laptopPlan.Evening.Backlight == 30 && laptopPlan.Evening.Color.Brightness == 100, "Laptop WMI backlight avoids double dimming");
Check(WindowsBrightness.Identity(@"DISPLAY\TEST\INSTANCE_0") == WindowsBrightness.Identity(@"\\?\DISPLAY#TEST#INSTANCE#{guid}"), "WMI brightness mapped by full monitor instance identity");
var fallback = new Display { Id = "test-fallback", Name = "No DDC panel", ColorInfoKnown = true, GammaAvailable = true };
var fallbackPlan = ProfilePlanner.Create(fallback, null);
Check(fallbackPlan.Evening.Backlight == null && fallbackPlan.Evening.Color.Brightness == 85, "No DDC/WMI uses SDR software dimming");
Check(ProfilePlanner.Valid(fallbackPlan, fallback.Id) && !ProfilePlanner.Valid(fallbackPlan, laptop.Id), "Per-display profile identity prevents applying another screen's profile");
Check(ProfilePlanner.Reconcile(fallbackPlan, fallback, null).Gaming.Backlight == null, "Capability refresh keeps stored baseline");
Check(ProfilePlanner.Vendor("PCI\\VEN_10DE", "Graphics") == "NVIDIA", "GPU vendor identified from PCI vendor ID");
var nvPanel = new Display { Id = "test-nvidia", Name = "Other Panel", AdapterName = "NVIDIA GPU", ColorInfoKnown = true, GammaAvailable = true };
var nvCapabilities = new NvidiaDisplayState(nvPanel.Id, "test", 1, new(70, 0, 100, 50), 0, 0, new(0, 0, 0, 3, 0, 0));
var nvPlan = ProfilePlanner.Create(nvPanel, nvCapabilities);
Check(nvPlan.Gaming.Vibrance == 70 && nvPlan.Evening.Vibrance == 55 && nvPlan.Gaming.Output == null, "NVIDIA colors generated without forcing output depth or range");
nvPanel.Hdr = true;
Check(ProfilePlanner.Create(nvPanel, nvCapabilities).Evening.Vibrance == null, "HDR NVIDIA colors are preserved");
var dimmed = new Display { Id = generic.Id, Name = generic.Name, Features = new Dictionary<byte,HardwareFeature> { [0x10] = new() { Code = 0x10, Current = 30, Maximum = 100 } } };
Check(ProfilePlanner.Reconcile(planned, dimmed, null).Gaming.Backlight == 80, "Refreshing an evening monitor never replaces its original Gaming baseline");
Check(planned.Gaming.Id != laptopPlan.Gaming.Id && laptopPlan.Gaming.Id != nvPlan.Gaming.Id, "Every display receives unique scene IDs");
Check(GameDetectionService.IsInside(@"E:\SteamLibrary\steamapps\common\Game\bin\game.exe", @"E:\SteamLibrary\steamapps\common\Game"), "Game executable inside installation");
Check(!GameDetectionService.IsInside(@"E:\SteamLibrary\steamapps\common\GameOther\game.exe", @"E:\SteamLibrary\steamapps\common\Game"), "Sibling directory is not a game match");
Check(GameDetectionService.IsHelper("UnityCrashHandler64.exe") && GameDetectionService.IsHelper("steamwebhelper.exe") && GameDetectionService.IsHelper("wallpaper64.exe"), "Background tools are not games");
Check(!GameDetectionService.IsHelper("cs2.exe"), "Game executable accepted");
Check(!GameDetectionService.IsHelper("CrashBandicoot.exe"), "Game title containing Crash is not a crash reporter");
Check(GameDetectionService.IsHelper("LeagueClientUx.exe") && GameDetectionService.IsHelper("RiotClientServices.exe"), "Riot lobby/client is not a running match");
Check(GameCatalogScanner.IsAuxiliaryPath(@"E:\Game\sdk\editor\tool.exe", @"E:\Game"), "SDK tools excluded from directory fallback");
Check(!GameCatalogScanner.IsAuxiliaryPath(@"E:\Game\Binaries\Win64\game.exe", @"E:\Game"), "Nested Unreal game is accepted");
Check(GameCatalogScanner.IsSoftware("Wallpaper Engine") && GameCatalogScanner.IsSoftware("3DMark Demo"), "Store software excluded from installed game list");
var fixture = Path.GetFullPath(Path.Combine("artifacts", "catalog-fixtures", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(fixture);
void FakeExe(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var bytes = new byte[128]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; File.WriteAllBytes(path, bytes); }
var library = Path.Combine(fixture, "Steam Library");
var steamGame = Path.Combine(library, "steamapps", "common", "Nested Game");
FakeExe(Path.Combine(steamGame, "Binaries", "Win64", "RealGame-Win64-Shipping.exe"));
FakeExe(Path.Combine(steamGame, "UnityCrashHandler64.exe"));
FakeExe(Path.Combine(steamGame, "sdk", "Tool.exe"));
File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_10.acf"), "\"name\" \"Nested Game\"\n\"installdir\" \"Nested Game\"");
File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_11.acf"), "\"name\" \"Removed Game\"\n\"installdir\" \"Gone\"");
File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_12.acf"), "\"name\" \"Escaped Game\"\n\"installdir\" \"../../Outside\"");
var epicGame = Path.Combine(fixture, "Epic Game"); FakeExe(Path.Combine(epicGame, "Binaries", "Win64", "EpicGame.exe"));
var epicFile = Path.Combine(fixture, "epic.item");
File.WriteAllText(epicFile, JsonSerializer.Serialize(new { DisplayName = "Epic Fixture", InstallLocation = epicGame, LaunchExecutable = "Binaries/Win64/EpicGame.exe", bIsIncompleteInstall = false, AppCategories = new[] { "games" } }));
var riotGame = Path.Combine(fixture, "Riot Games", "VALORANT", "live"); FakeExe(Path.Combine(riotGame, "ShooterGame", "Binaries", "Win64", "VALORANT-Win64-Shipping.exe"));
var riotFile = Path.Combine(fixture, "RiotClientInstalls.json"); File.WriteAllText(riotFile, JsonSerializer.Serialize(new { associated_client = new Dictionary<string,string> { [riotGame] = "RiotClientServices.exe" } }));
var xboxGame = Path.Combine(fixture, "Xbox Game", "Content"); FakeExe(Path.Combine(xboxGame, "XboxGame.exe"));
var xboxFile = Path.Combine(xboxGame, "MicrosoftGame.config"); File.WriteAllText(xboxFile, "<Game><ExecutableList><Executable Name=\"XboxGame.exe\" /></ExecutableList><ShellVisuals DefaultDisplayName=\"Xbox Fixture\" /></Game>");
var scanner = new GameCatalogScanner(); scanner.ReadSteamLibrary(library); scanner.ReadSteamLibrary(library); scanner.ReadEpicManifest(epicFile); scanner.ReadRiotInstalls(riotFile); scanner.ReadXboxConfig(xboxFile);
var scanned = scanner.Complete();
Check(scanned.Games.Length == 4, "Four manifest providers merge without duplicates or removed installations");
Check(scanned.Games.Single(x => x.Source == "Steam").Executables!.Length == 1, "Only real game indexed; crash helper and SDK excluded");
Check(scanned.Games.Any(x => x.Name == "VALORANT") && scanned.Games.Any(x => x.Name == "Xbox Fixture"), "Riot and Xbox display names parsed");
Check(scanned.Games.All(x => x.Executables!.Length > 0), "Every discovered installation has an executable");
var probeDirectory = Path.Combine(library, "steamapps", "common", "Probe Game"); Directory.CreateDirectory(probeDirectory);
foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory, "Luma.Checks.*")) File.Copy(file, Path.Combine(probeDirectory, Path.GetFileName(file)), true);
File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_13.acf"), "\"name\" \"Probe Game\"\n\"installdir\" \"Probe Game\"");
var processScanner = new GameCatalogScanner(); processScanner.ReadSteamLibrary(library);
var processCatalog = processScanner.Complete();
var isolatedDetector = new GameDetectionService(() => processCatalog, Path.Combine(fixture, "isolated-cache.json"));
using (var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(probeDirectory, "Luma.Checks.exe"), "--game-probe --probe-seconds 3") { UseShellExecute = false, CreateNoWindow = true })!)
{
    await isolatedDetector.PollAsync(Array.Empty<string>(), Array.Empty<string>());
    Check(isolatedDetector.Running.Any(x => x.ProcessId == probe.Id && x.Name == "Probe Game"), "Auto-discovered executable recognized without manual EXE selection or a visible window");
    await probe.WaitForExitAsync();
    await isolatedDetector.PollAsync(Array.Empty<string>(), Array.Empty<string>());
    Check(!isolatedDetector.Running.Any(x => x.ProcessId == probe.Id), "Auto-discovered game exits cleanly");
}
Check(Marshal.SizeOf<Native.DevMode>() == 220, "DEVMODEW interop layout");
Check(Marshal.SizeOf<Native.DisplayPath>() == 72, "DISPLAYCONFIG_PATH_INFO interop layout");
Check(Marshal.SizeOf<Native.SourceName>() == 84, "DISPLAYCONFIG_SOURCE_DEVICE_NAME interop layout");
Check(Marshal.SizeOf<Native.TargetName>() == 420, "DISPLAYCONFIG_TARGET_DEVICE_NAME interop layout");
Check(Marshal.SizeOf<Native.AdvancedColorInfo>() == 32, "DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO interop layout");
Check(Marshal.SizeOf<Native.AdvancedColorInfo2>() == 36, "Win11 HDR / WCG distinct color mode layout");
Check(Marshal.SizeOf<Native.ColorState>() == 24, "Windows per-display WCG state layout");
Console.WriteLine($"PASS: {assertions:N0} assertions (color safety, calibration, transitions, overnight schedule, interop).");
Check(Marshal.SizeOf<NvidiaService.DvcInfo>() == 20, "DVC ABI");
Check(Marshal.SizeOf<NvidiaService.ColorData>() == 24, "NV_COLOR_DATA_V5 ABI");
Check(Marshal.OffsetOf<NvidiaService.ColorData>(nameof(NvidiaService.ColorData.Format)).ToInt32() == 8, "NV_COLOR_DATA nested structure alignment");
Check(Marshal.SizeOf<NvidiaService.DrsSetting>() == 12320, "NVDRS_SETTING_V1 ABI");
Check(Marshal.OffsetOf<NvidiaService.DrsSetting>(nameof(NvidiaService.DrsSetting.Current)).ToInt32() == 8220, "NVDRS_SETTING_V1 union offset");
foreach (var range in new[] { new NvidiaRange(0, -100, 100, 0), new NvidiaRange(50, 0, 100, 50), new NvidiaRange(0, 0, 63, 0) })
{
    Check(range.FromPercent(50) == range.Default, "Neutral vibrance mapping");
    Check(range.FromPercent(0) == range.Minimum && range.FromPercent(100) == range.Maximum, "Vibrance bounds");
}
if (args.Contains("--games"))
{
    var detector = new GameDetectionService(); var settings = AppSettings.Load();
    await detector.PollAsync(settings.GameExecutables, settings.IgnoredGameExecutables);
    string report = JsonSerializer.Serialize(new { detector.InstalledCount, detector.Status, detector.Running, detector.CatalogScannedAt, detector.CatalogDurationMs, detector.CatalogWarnings, detector.Catalog }, AppSettings.JsonOptions);
    Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/game-probe.json", report); Console.WriteLine(report);
}
if (args.Contains("--nvidia") || args.Contains("--nvidia-drstest"))
{
    using var monitors = new DisplayService(); await monitors.RefreshAsync();
    using var nvidia = new NvidiaService(); await nvidia.RefreshAsync(monitors.Monitors);
    if (args.Contains("--nvidia-drstest")) { await nvidia.VerifyNoopDrsRoundtripAsync(); Console.WriteLine("PASS: real NVAPI DRS write/save/reload/restore with identical effective setting."); }
    var report = JsonSerializer.Serialize(new { nvidia.Available, nvidia.Status, nvidia.DriverVersion, nvidia.Displays, nvidia.DriverSettings }, new JsonSerializerOptions { WriteIndented = true });
    Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/nvidia-probe.json", report); Console.WriteLine(report);
}
if (args.Contains("--hardware"))
{
    using var displays = new DisplayService(); await displays.RefreshAsync(true);
    var hardwareReport = JsonSerializer.Serialize(new
    {
        Gpus = displays.Gpus,
        Displays = displays.Monitors.Select(x => new { x.Number, x.Name, x.Detail, x.Primary, x.AdapterName, x.GpuVendor, x.BrightnessBackend, x.Hdr, x.ActiveColorMode, x.AdvancedColorFlags, x.ColorInfoKnown, x.GammaAvailable, x.ColorStatus, x.HardwareStatus, Features = x.Features.Values.Select(f => new { Code = f.Code.ToString("X2"), f.Name, f.Current, f.Maximum }), x.Inputs })
    }, new JsonSerializerOptions { WriteIndented = true });
    Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/hardware-probe.json", hardwareReport); Console.WriteLine(hardwareReport);
}
