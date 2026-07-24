using System.Text.Json;

namespace Hdiff.UI;

/// <summary>
/// Small, user-scoped preferences that survive replacing an FDD or
/// self-contained package. The executable directory is intentionally never
/// written to because transferred packages are often read-only.
/// </summary>
internal static class HdiffUserSettings
{
    private const string DefaultFontSizeKey = "medium";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hdiff",
        "settings.json");

    public static string LoadDiffFontSizeKey()
    {
        var settings = Load();
        return string.IsNullOrWhiteSpace(settings.DiffFontSize) ? DefaultFontSizeKey : settings.DiffFontSize;
    }

    public static void SaveDiffFontSizeKey(string key)
        => Save(Load() with { DiffFontSize = key });

    public static string LoadThemeKey()
    {
        var settings = Load();
        return string.IsNullOrWhiteSpace(settings.Theme) ? "light" : settings.Theme;
    }

    public static void SaveThemeKey(string key)
        => Save(Load() with { Theme = key });

    public static bool LoadShowRowSeparators() => Load().ShowRowSeparators ?? false;

    public static void SaveShowRowSeparators(bool show)
        => Save(Load() with { ShowRowSeparators = show });

    private static Settings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new Settings(null, null, null);
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings(null, null, null);
        }
        catch
        {
            return new Settings(null, null, null);
        }
    }

    private static void Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch
        {
            // Preferences must never prevent a document comparison.
        }
    }

    private sealed record Settings(string? DiffFontSize, string? Theme, bool? ShowRowSeparators);
}
