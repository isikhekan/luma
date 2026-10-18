using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Luma;

public sealed record GameInstall(string Name, string Directory, string? Executable = null, string Source = "Otomatik", string[]? Executables = null);
public sealed record GameCatalog(DateTimeOffset ScannedAt, GameInstall[] Games, string[] Warnings, long DurationMs);

public sealed class GameCatalogScanner
{
    private readonly List<GameInstall> _found = new();
    private readonly List<string> _warnings = new();
    private int _visited;
    private static readonly string[] SkipDirectories = { ".git", "node_modules", "redist", "_commonredist", "directx", "vcredist", "__installer", "installer", "support", "easyanticheat", "battleye", ".egstore", "cache", "logs", "screenshots", "extras", "tools", "thirdparty", "dotnet", "sdk", "server", "editor" };
    public static bool IsAuxiliaryPath(string executable, string root) => Path.GetRelativePath(root, executable).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).SkipLast(1).Any(x => SkipDirectories.Contains(x, StringComparer.OrdinalIgnoreCase));
    public static bool IsSoftware(string name) => new[] { "wallpaper engine", "3dmark", "benchmark", "redistributable", "steamvr", "soundpad", "lossless scaling", "fps monitor", "borderless gaming", "dedicated server", "sdk", "development kit", "epic games launcher", "riot client", "riot vanguard", "ubisoft connect", "ea app", "ea desktop", "epic online services" }.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase)) || name.Equals("Launcher", StringComparison.OrdinalIgnoreCase);
    private void Safe(string source, Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException or ArgumentException or XmlException or InvalidOperationException or NotSupportedException)
        { if (_warnings.Count < 30) _warnings.Add(source + ": " + e.Message); }
    }
    private static string? FullDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!System.IO.Directory.Exists(full) || full.Equals(Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)) return null;
        return full;
    }
    private void Add(string name, string? directory, string source, string? executable = null)
    {
        if (string.IsNullOrWhiteSpace(name) || IsSoftware(name)) return;
        string? full = FullDirectory(directory); if (full == null) return;
        if (_found.Any(x => x.Directory.Equals(full, StringComparison.OrdinalIgnoreCase))) return;
        if (executable != null)
        {
            executable = Path.GetFullPath(Path.Combine(full, executable));
            if (!GameDetectionService.IsInside(executable, full) || !File.Exists(executable) || GameDetectionService.IsHelper(executable)) executable = null;
        }
        _found.Add(new(name, full, executable, source));
    }
    private static string VdfValue(string text, string key) => Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]+)\"", RegexOptions.CultureInvariant).Groups[1].Value.Replace(@"\\", @"\");
    public void ReadSteamLibrary(string library)
    {
        string apps = Path.Combine(library, "steamapps"); if (!System.IO.Directory.Exists(apps)) return;
        foreach (string file in System.IO.Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
        Safe("Steam", () =>
        {
            string text = File.ReadAllText(file); string directory = VdfValue(text, "installdir");
            if (string.IsNullOrWhiteSpace(directory)) return;
            string full = Path.GetFullPath(Path.Combine(apps, "common", directory));
            if (GameDetectionService.IsInside(full, Path.Combine(apps, "common"))) Add(VdfValue(text, "name"), full, "Steam");
        });
    }
    public void ReadEpicManifest(string file)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(file)); var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) return;
        if (!root.TryGetProperty("InstallLocation", out var location) || !root.TryGetProperty("DisplayName", out var name)) return;
        if (root.TryGetProperty("AppCategories", out var categories) && categories.ValueKind == JsonValueKind.Array && !categories.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "games")) return;
        Add(name.GetString() ?? "Epic oyunu", location.GetString(), "Epic Games", root.TryGetProperty("LaunchExecutable", out var launch) ? launch.GetString() : null);
    }
    public void ReadRiotInstalls(string file)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        if (json.RootElement.ValueKind != JsonValueKind.Object) return;
        if (!json.RootElement.TryGetProperty("associated_client", out var installs) || installs.ValueKind != JsonValueKind.Object) return;
        foreach (var install in installs.EnumerateObject())
        {
            string? path = FullDirectory(install.Name); if (path == null) continue;
            string name = path.Contains("VALORANT", StringComparison.OrdinalIgnoreCase) ? "VALORANT" : path.Contains("League of Legends", StringComparison.OrdinalIgnoreCase) ? "League of Legends / TFT" : Path.GetFileName(path);
            if (!name.Contains("Riot Client", StringComparison.OrdinalIgnoreCase)) Add(name, path, "Riot Games");
        }
    }
    public void ReadXboxConfig(string file)
    {
        using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
        var xml = XDocument.Load(reader); string directory = Path.GetDirectoryName(file)!;
        string fallback = new DirectoryInfo(directory).Parent?.Name ?? new DirectoryInfo(directory).Name;
        string name = xml.Descendants().FirstOrDefault(x => x.Name.LocalName == "ShellVisuals")?.Attribute("DefaultDisplayName")?.Value ?? fallback;
        if (name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) name = fallback;
        string? executable = xml.Descendants().FirstOrDefault(x => x.Name.LocalName == "Executable")?.Attribute("Name")?.Value;
        Add(name, directory, "Xbox / Microsoft Store", executable);
    }
    private void ReadStoreRegistry()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (string prefix in new[] { @"SOFTWARE\", @"SOFTWARE\WOW6432Node\" })
        {
            foreach (string provider in new[] { "GOG.com\\Games", "Ubisoft\\Launcher\\Installs" })
            Safe(provider, () =>
            {
                using var store = hive.OpenSubKey(prefix + provider); if (store == null) return;
                foreach (string sub in store.GetSubKeyNames())
                Safe(provider, () =>
                {
                    using var entry = store.OpenSubKey(sub); if (entry == null) return;
                    string? path = entry.GetValue("path") as string ?? entry.GetValue("InstallDir") as string;
                    string? name = entry.GetValue("gameName") as string;
                    if (string.IsNullOrWhiteSpace(name) && path != null) name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                    Add(name ?? "Oyun", path, provider.StartsWith("GOG") ? "GOG" : "Ubisoft Connect", entry.GetValue("exe") as string);
                });
            });
            Safe("Windows kurulum kayıtları", () =>
            {
                using var uninstall = hive.OpenSubKey(prefix + @"Microsoft\Windows\CurrentVersion\Uninstall"); if (uninstall == null) return;
                foreach (string sub in uninstall.GetSubKeyNames())
                Safe("Windows kurulum kaydı", () =>
                {
                    using var entry = uninstall.OpenSubKey(sub); if (entry == null) return;
                    string name = entry.GetValue("DisplayName") as string ?? "";
                    string publisher = entry.GetValue("Publisher") as string ?? "";
                    if (string.IsNullOrWhiteSpace(name) || IsSoftware(name) || GameDetectionService.IsHelper(name + ".exe") || name.Contains("Riot Client", StringComparison.OrdinalIgnoreCase) || name.Contains("Vanguard", StringComparison.OrdinalIgnoreCase)) return;
                    if (!Regex.IsMatch(publisher, @"Riot Games|Electronic Arts|Ubisoft|Blizzard|GOG\.com|Rockstar Games|Bethesda|Activision|2K Games|CD PROJEKT|FromSoftware|Larian|SEGA|BANDAI|CAPCOM|Square Enix", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return;
                    string? path = FullDirectory(entry.GetValue("InstallLocation") as string);
                    if (path == null) return;
                    Add(name, path, publisher.Contains("Riot", StringComparison.OrdinalIgnoreCase) ? "Riot Games" : publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) ? "Battle.net" : publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) ? "EA app" : "Windows oyun kaydı");
                });
            });
        }
    }
    private static bool IsGameFolder(string path)
    {
        if (File.Exists(Path.Combine(path, "UnityPlayer.dll")) || File.Exists(Path.Combine(path, "GameAssembly.dll")) || System.IO.Directory.Exists(Path.Combine(path, "Content", "Paks"))) return true;
        if (System.IO.Directory.Exists(Path.Combine(path, "Engine")) && System.IO.Directory.Exists(Path.Combine(path, "Binaries"))) return true;
        return System.IO.Directory.EnumerateDirectories(path, "*_Data", SearchOption.TopDirectoryOnly).Any(x => File.Exists(Path.Combine(x, "globalgamemanagers")));
    }
    public void ReadGameFolder(string path, bool trustedLibrary = false)
    {
        if (trustedLibrary || IsGameFolder(path)) Add(Path.GetFileName(path), path, "Oyun klasörü");
    }
    private void ReadGameDirectories()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
        Safe("Oyun diskleri", () =>
        {
            string root = drive.RootDirectory.FullName;
            foreach (string folder in new[] { "Games", "Oyunlar", "Epic Games", "GOG Games", "EA Games", "XboxGames", "Ubisoft Games", "Riot Games", "Battle.net" })
            {
                string library = Path.Combine(root, folder); if (!System.IO.Directory.Exists(library)) continue;
                foreach (string path in System.IO.Directory.EnumerateDirectories(library))
                Safe("Oyun klasörü", () =>
                {
                    if (Path.GetFileName(path).Equals("Riot Client", StringComparison.OrdinalIgnoreCase)) return;
                    string config = Path.Combine(path, "Content", "MicrosoftGame.config");
                    if (File.Exists(config)) { ReadXboxConfig(config); return; }
                    // Riot's VALORANT/live is more specific than the parent folder.
                    if (_found.Any(x => GameDetectionService.IsInside(x.Directory, path) || x.Directory.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
                    ReadGameFolder(path, true);
                });
            }
            foreach (string folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            foreach (string libraryName in new[] { "Epic Games", "GOG Galaxy\\Games", "EA Games", "Ubisoft\\Ubisoft Game Launcher\\games" })
            {
                string library = Path.Combine(folder, libraryName); if (!System.IO.Directory.Exists(library)) continue;
                foreach (string path in System.IO.Directory.EnumerateDirectories(library)) Safe("Başlatıcı oyun klasörü", () => ReadGameFolder(path, true));
            }
            // A shallow marker scan finds portable games without walking Windows, documents or videos recursively.
            foreach (var path in drive.RootDirectory.EnumerateDirectories())
            Safe("Taşınabilir oyun klasörü", () =>
            {
                if ((path.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0 || Regex.IsMatch(path.Name, @"^(Windows|Users|Program Files|ProgramData|Recovery|System Volume|PerfLogs|Documents and Settings)$", RegexOptions.IgnoreCase)) return;
                ReadGameFolder(path.FullName);
            });
        });
    }
    public string[] FindExecutables(string directory, int maximumDepth = 8)
    {
        var files = new List<string>(); var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((directory, 0));
        while (pending.Count > 0 && files.Count < 150 && _visited < 16000)
        {
            var item = pending.Dequeue(); _visited++;
            Safe("Oyun uygulamaları", () =>
            {
                foreach (string file in System.IO.Directory.EnumerateFiles(item.Path, "*.exe"))
                {
                    if (GameDetectionService.IsHelper(file)) continue;
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length > 64 && stream.ReadByte() == 'M' && stream.ReadByte() == 'Z') files.Add(file);
                }
                if (item.Depth >= maximumDepth) return;
                foreach (var sub in new DirectoryInfo(item.Path).EnumerateDirectories())
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0 || SkipDirectories.Contains(sub.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    pending.Enqueue((sub.FullName, item.Depth + 1));
                }
            });
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(150).ToArray();
    }
    public GameCatalog Complete()
    {
        var watch = Stopwatch.StartNew(); var games = new List<GameInstall>();
        foreach (var game in _found.OrderByDescending(x => x.Directory.Length))
        {
            // A broad registry/library parent must not absorb another game's directory.
            if (games.Any(x => GameDetectionService.IsInside(x.Directory, game.Directory))) continue;
            string[] executables = FindExecutables(game.Directory);
            if (executables.Length == 0) continue;
            games.Add(game with { Executables = executables });
        }
        return new(DateTimeOffset.Now, games.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), _warnings.ToArray(), watch.ElapsedMilliseconds);
    }
    public GameCatalog Scan()
    {
        var watch = Stopwatch.StartNew();
        Safe("Steam", () =>
        {
            using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? steam = steamKey?.GetValue("SteamPath") as string;
            if (steam == null) return;
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };
            string folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(folders)) foreach (Match match in Regex.Matches(File.ReadAllText(folders), "\"path\"\\s+\"([^\"]+)\"")) libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            foreach (string library in libraries) Safe("Steam kütüphanesi", () => ReadSteamLibrary(library));
        });
        Safe("Epic Games", () =>
        {
            string manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (System.IO.Directory.Exists(manifests)) foreach (string file in System.IO.Directory.EnumerateFiles(manifests, "*.item")) Safe("Epic oyun kaydı", () => ReadEpicManifest(file));
        });
        Safe("Riot Games", () =>
        {
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "RiotClientInstalls.json");
            if (File.Exists(file)) ReadRiotInstalls(file);
        });
        ReadStoreRegistry(); ReadGameDirectories();
        var catalog = Complete(); return catalog with { DurationMs = watch.ElapsedMilliseconds };
    }
}
