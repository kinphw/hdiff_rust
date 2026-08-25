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

    public static bool LoadWrapLongLines() => Load().WrapLongLines ?? true;

    public static void SaveWrapLongLines(bool wrap)
        => Save(Load() with { WrapLongLines = wrap });

    public static bool LoadIgnoreWhitespaceChanges() => Load().IgnoreWhitespaceChanges ?? true;

    public static void SaveIgnoreWhitespaceChanges(bool ignore)
        => Save(Load() with { IgnoreWhitespaceChanges = ignore });

    public static bool LoadIgnoreBlankLines() => Load().IgnoreBlankLines ?? true;

    public static void SaveIgnoreBlankLines(bool ignore)
        => Save(Load() with { IgnoreBlankLines = ignore });

    public static bool LoadTextSelectionEnabled() => Load().TextSelectionEnabled ?? true;

    public static void SaveTextSelectionEnabled(bool enabled)
        => Save(Load() with { TextSelectionEnabled = enabled });

    public static bool LoadIncludeMemos() => Load().IncludeMemos ?? false;

    public static void SaveIncludeMemos(bool include)
        => Save(Load() with { IncludeMemos = include });

    public static bool LoadReflowPdfParagraphs() => Load().ReflowPdfParagraphs ?? true;

    public static void SaveReflowPdfParagraphs(bool reflow)
        => Save(Load() with { ReflowPdfParagraphs = reflow });

    private static Settings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return EmptySettings();
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? EmptySettings();
        }
        catch
        {
            return EmptySettings();
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

    private static Settings EmptySettings() => new(null, null, null, null, null, null, null, null, null);

    private sealed record Settings(
        string? DiffFontSize,
        string? Theme,
        bool? ShowRowSeparators,
        bool? WrapLongLines,
        bool? IgnoreWhitespaceChanges,
        bool? IgnoreBlankLines,
        bool? TextSelectionEnabled,
        bool? IncludeMemos,
        bool? ReflowPdfParagraphs);
}
