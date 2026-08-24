using System.Text;

namespace Hdiff.UI;

internal enum HdiffThemeKind
{
    Light,
    RustDark,
}

/// <summary>Shared colours for every custom-drawn Hdiff surface.</summary>
internal sealed record HdiffThemePalette(
    Color AppBack,
    Color SurfaceBack,
    Color AttachedSurfaceBack,
    Color DragSurfaceBack,
    Color HeaderBack,
    Color CanvasBack,
    Color Text,
    Color MutedText,
    Color Border,
    Color AttachedBorder,
    Color GutterBack,
    Color GutterText,
    Color EmptyLineBack,
    Color DeletedLineBack,
    Color InsertedLineBack,
    Color RemovedText,
    Color RemovedInlineBack,
    Color AddedText,
    Color AddedInlineBack,
    Color OverviewDocument,
    Color OverviewViewportFill,
    Color OverviewViewportBorder,
    Color ButtonBack,
    Color ButtonText,
    Color ButtonBorder,
    Color PrimaryActionBack,
    Color PrimaryActionHover,
    Color PrimaryActionText,
    Color BadgeBack,
    Color MemoAccent,
    Color MemoFlagText,
    Color MemoSurfaceBack);

internal static class HdiffThemes
{
    private static readonly Color[] LightMemoAuthors =
    {
        Color.FromArgb(180,83,9), Color.FromArgb(29,78,216), Color.FromArgb(185,28,28), Color.FromArgb(4,120,87),
        Color.FromArgb(109,40,217), Color.FromArgb(190,24,93), Color.FromArgb(3,105,161), Color.FromArgb(77,124,15),
    };
    private static readonly Color[] DarkMemoAuthors =
    {
        Color.FromArgb(251,191,36), Color.FromArgb(147,197,253), Color.FromArgb(252,165,165), Color.FromArgb(110,231,183),
        Color.FromArgb(196,181,253), Color.FromArgb(249,168,212), Color.FromArgb(125,211,252), Color.FromArgb(190,242,100),
    };
    public static readonly HdiffThemePalette Light = new(
        AppBack: Color.FromArgb(220, 224, 230),
        SurfaceBack: Color.White,
        AttachedSurfaceBack: Color.FromArgb(247, 252, 249),
        DragSurfaceBack: Color.FromArgb(239, 247, 255),
        HeaderBack: Color.FromArgb(245, 247, 250),
        CanvasBack: Color.White,
        Text: Color.FromArgb(35, 39, 47),
        MutedText: Color.FromArgb(95, 104, 116),
        Border: Color.FromArgb(194, 202, 213),
        AttachedBorder: Color.FromArgb(150, 195, 172),
        GutterBack: Color.FromArgb(248, 249, 250),
        GutterText: Color.FromArgb(105, 112, 122),
        EmptyLineBack: Color.FromArgb(248, 249, 250),
        DeletedLineBack: Color.FromArgb(255, 242, 242),
        InsertedLineBack: Color.FromArgb(239, 252, 245),
        RemovedText: Color.FromArgb(154, 31, 35),
        RemovedInlineBack: Color.FromArgb(255, 199, 206),
        AddedText: Color.FromArgb(0, 104, 50),
        AddedInlineBack: Color.FromArgb(198, 239, 206),
        OverviewDocument: Color.FromArgb(118, 126, 140),
        OverviewViewportFill: Color.FromArgb(38, 55, 65, 81),
        OverviewViewportBorder: Color.FromArgb(125, 89, 98, 112),
        ButtonBack: Color.FromArgb(250, 250, 250),
        ButtonText: Color.FromArgb(35, 39, 47),
        ButtonBorder: Color.FromArgb(180, 188, 199),
        PrimaryActionBack: Color.FromArgb(32, 103, 178),
        PrimaryActionHover: Color.FromArgb(23, 83, 150),
        PrimaryActionText: Color.White,
        BadgeBack: Color.FromArgb(238, 246, 255),
        MemoAccent: Color.FromArgb(217, 119, 6),
        MemoFlagText: Color.White,
        MemoSurfaceBack: Color.FromArgb(255, 251, 235));

    public static readonly HdiffThemePalette RustDark = new(
        AppBack: Color.FromArgb(15, 23, 42),
        SurfaceBack: Color.FromArgb(30, 41, 59),
        AttachedSurfaceBack: Color.FromArgb(20, 49, 47),
        DragSurfaceBack: Color.FromArgb(25, 49, 82),
        HeaderBack: Color.FromArgb(17, 24, 39),
        CanvasBack: Color.FromArgb(15, 23, 42),
        Text: Color.FromArgb(248, 250, 252),
        MutedText: Color.FromArgb(148, 163, 184),
        Border: Color.FromArgb(51, 65, 85),
        AttachedBorder: Color.FromArgb(52, 112, 99),
        GutterBack: Color.FromArgb(15, 23, 42),
        GutterText: Color.FromArgb(100, 116, 139),
        EmptyLineBack: Color.FromArgb(20, 30, 48),
        DeletedLineBack: Color.FromArgb(69, 27, 35),
        InsertedLineBack: Color.FromArgb(20, 66, 50),
        RemovedText: Color.FromArgb(252, 165, 165),
        RemovedInlineBack: Color.FromArgb(127, 29, 29),
        AddedText: Color.FromArgb(134, 239, 172),
        AddedInlineBack: Color.FromArgb(20, 83, 45),
        OverviewDocument: Color.FromArgb(100, 116, 139),
        OverviewViewportFill: Color.FromArgb(70, 148, 163, 184),
        OverviewViewportBorder: Color.FromArgb(190, 148, 163, 184),
        ButtonBack: Color.FromArgb(30, 41, 59),
        ButtonText: Color.FromArgb(248, 250, 252),
        ButtonBorder: Color.FromArgb(71, 85, 105),
        PrimaryActionBack: Color.FromArgb(37, 99, 235),
        PrimaryActionHover: Color.FromArgb(29, 78, 216),
        PrimaryActionText: Color.FromArgb(248, 250, 252),
        BadgeBack: Color.FromArgb(25, 49, 82),
        MemoAccent: Color.FromArgb(245, 158, 11),
        MemoFlagText: Color.FromArgb(31, 41, 55),
        MemoSurfaceBack: Color.FromArgb(38, 33, 26));

    public static HdiffThemePalette Get(HdiffThemeKind theme) => theme == HdiffThemeKind.RustDark ? RustDark : Light;

    public static Color MemoAuthorColor(HdiffThemePalette theme, string author)
    {
        var sum = 0;
        foreach (var rune in author.EnumerateRunes()) sum = (sum + rune.Value) % 4096;
        var colors = theme == RustDark ? DarkMemoAuthors : LightMemoAuthors;
        return colors[sum % colors.Length];
    }
}
