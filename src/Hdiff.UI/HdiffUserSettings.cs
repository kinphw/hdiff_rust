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
        try
        {
            if (!File.Exists(SettingsPath)) return DefaultFontSizeKey;
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            return string.IsNullOrWhiteSpace(settings?.DiffFontSize) ? DefaultFontSizeKey : settings.DiffFontSize;
        }
        catch
        {
            return DefaultFontSizeKey;
        }
    }

    public static void SaveDiffFontSizeKey(string key)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new Settings(key)));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch
        {
            // Preferences must never prevent a document comparison.
        }
    }

    private sealed record Settings(string DiffFontSize);
}
