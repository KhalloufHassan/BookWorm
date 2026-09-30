using MudBlazor;

namespace BookWorm.UI.Theme;

/// <summary>
/// BookWorm's warm look: a "paper" light theme (cream, brown ink, terracotta) and a "hearth"
/// dark theme (deep brown, apricot and amber). Headings use a bookish serif from the device.
/// </summary>
public static class BookWormTheme
{
    public const string SerifFonts = "\"Iowan Old Style\", \"Palatino Linotype\", Palatino, \"Book Antiqua\", Georgia, serif";
    public const string SansFonts = "system-ui, -apple-system, \"Segoe UI\", Roboto, \"Noto Sans\", \"Helvetica Neue\", Arial, sans-serif";

    public static MudTheme Create() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#b5563a",
            PrimaryContrastText = "#fffbf4",
            Secondary = "#6f7f5b",
            SecondaryContrastText = "#fffbf4",
            Tertiary = "#b8862f",
            TertiaryContrastText = "#fffbf4",
            Info = "#4f6d7a",
            Success = "#4f7a4a",
            Warning = "#b8862f",
            Error = "#b3261e",
            Background = "#f7f1e6",
            BackgroundGray = "#efe6d6",
            Surface = "#fffbf4",
            AppbarBackground = "#fffbf4",
            AppbarText = "#3b2f25",
            DrawerBackground = "#f3ebdd",
            DrawerText = "#3b2f25",
            DrawerIcon = "#7a6a5b",
            TextPrimary = "#3b2f25",
            TextSecondary = "#7a6a5b",
            TextDisabled = "#b3a595",
            ActionDefault = "#7a6a5b",
            LinesDefault = "#e6dccb",
            LinesInputs = "#cdbfa9",
            TableLines = "#ece3d4",
            TableHover = "#f5ecdd",
            Divider = "#e6dccb",
            Skeleton = "#eee4d3",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#e0915f",
            PrimaryContrastText = "#1e1814",
            Secondary = "#a3b18a",
            SecondaryContrastText = "#1e1814",
            Tertiary = "#e3b45a",
            TertiaryContrastText = "#1e1814",
            Info = "#9bbccb",
            Success = "#9cc190",
            Warning = "#e3b45a",
            Error = "#f2877e",
            Background = "#1e1814",
            BackgroundGray = "#261f1a",
            Surface = "#2a221c",
            AppbarBackground = "#2a221c",
            AppbarText = "#f1e6d6",
            DrawerBackground = "#241d18",
            DrawerText = "#f1e6d6",
            DrawerIcon = "#bfae99",
            TextPrimary = "#f1e6d6",
            TextSecondary = "#bfae99",
            TextDisabled = "#7d6f60",
            ActionDefault = "#bfae99",
            LinesDefault = "#3d3229",
            LinesInputs = "#5a4b3e",
            TableLines = "#3a3029",
            TableHover = "#332a23",
            Divider = "#3d3229",
            Skeleton = "#3a3029",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
            DrawerWidthLeft = "232px",
        },
    };
}
