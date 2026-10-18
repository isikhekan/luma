using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Luma;

public sealed record LanguageOption(string Code, string Name);

/// <summary>Embedded, offline translations. Persist codes, never translated identifiers.</summary>
public static class Loc
{
    public static IReadOnlyList<LanguageOption> Languages { get; } = new[]
    {
        new LanguageOption("auto", "System / Sistem / Sistema / 系统"),
        new LanguageOption("tr", "Türkçe"), new LanguageOption("en", "English"),
        new LanguageOption("es", "Español"), new LanguageOption("zh-Hans", "简体中文")
    };
    private static readonly Dictionary<string, Dictionary<string, string>> Catalogs = Load();
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;
    private static readonly (string Key, Regex Pattern)[] Templates = Catalogs["en"].Keys
        .Where(k => Regex.IsMatch(k, @"\{\d+[^}]*\}"))
        .Select(k => (k, new Regex("^" + Regex.Replace(Regex.Escape(k), @"\\\{(\d+)(?:[^}]*?)\}", "(?<p$1>.+?)") + "$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))))
        .ToArray();
    public static event Action? Changed;
    public static string Language { get; private set; } = "en";
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "zh-Hans" ? "zh-CN" : Language);
    public static string Normalize(string? code) => Languages.Any(x => x.Code == code) ? code! : "auto";
    public static string Resolve(string? code, CultureInfo? system = null)
    {
        code = Normalize(code);
        if (code != "auto") return code;
        return (system ?? SystemCulture).TwoLetterISOLanguageName switch { "tr" => "tr", "es" => "es", "zh" => "zh-Hans", _ => "en" };
    }
    public static void Set(string? preference)
    {
        Language = Resolve(preference);
        CultureInfo.CurrentCulture = Culture; CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = Culture; CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Changed?.Invoke();
    }
    public static string T(string source)
    {
        if (Language == "tr" || string.IsNullOrEmpty(source)) return source;
        var catalog = Catalogs[Language];
        if (catalog.TryGetValue(source, out var text)) return text;
        // Legacy services retain stable source-language status values for comparisons.
        // Match complete messages only; never replace words inside user or device names.
        foreach (var template in Templates)
        {
            var match = template.Pattern.Match(source); if (!match.Success) continue;
            return Regex.Replace(catalog[template.Key], @"\{(\d+)[^}]*\}", m => match.Groups["p" + m.Groups[1].Value].Value);
        }
        return source;
    }
    public static string F(FormattableString message) => string.Format(Culture, T(message.Format), message.GetArguments());
    public static string Percent(double value) => Language == "tr" ? $"%{value:0}" : $"{value:0}%";
    public static string Status(string text) => string.Join("\n", text.Split('\n').Select(line =>
    {
        string translated = T(line);
        return translated != line ? translated : string.Join(" · ", line.Split(" · ").Select(T));
    }));
    public static IReadOnlyDictionary<string, string> Catalog(string language) => Catalogs[language];
    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (string code in new[] { "en", "es", "zh-Hans" })
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Luma.Locales." + code + ".json")
                ?? throw new InvalidDataException("Missing Luma language resource: " + code);
            result[code] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? throw new InvalidDataException(code);
        }
        return result;
    }
}
