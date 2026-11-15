using MudBlazor;

namespace CivicOps.Web.Theme;

public static class AppTheme
{
    public const string CookieName = "theme";

    private static readonly string[] Font =
        { "Inter", "Segoe UI", "system-ui", "-apple-system", "Roboto", "sans-serif" };

    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#4F46E5", Secondary = "#0E7490", Tertiary = "#7C3AED",
            Background = "#F8FAFC", BackgroundGray = "#F1F5F9", Surface = "#FFFFFF",
            AppbarBackground = "rgba(255,255,255,0.85)", AppbarText = "#0F172A",
            DrawerBackground = "#FFFFFF", DrawerText = "#0F172A",
            TextPrimary = "#0F172A", TextSecondary = "#475569",
            Divider = "#E2E8F0", DividerLight = "#F1F5F9", LinesDefault = "#E2E8F0", LinesInputs = "#CBD5E1",
            ActionDefault = "#475569", TableLines = "#E2E8F0",
            Success = "#15803D", Warning = "#B45309", Error = "#B91C1C", Info = "#0369A1"
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#6366F1", Secondary = "#22D3EE", Tertiary = "#A78BFA",
            Background = "#0B1120", BackgroundGray = "#0F172A", Surface = "#111827",
            AppbarBackground = "rgba(11,17,32,0.80)", AppbarText = "#E2E8F0",
            DrawerBackground = "#111827", DrawerText = "#E2E8F0",
            TextPrimary = "#F1F5F9", TextSecondary = "#94A3B8",
            Divider = "#1E293B", DividerLight = "#162033", LinesDefault = "#1E293B", LinesInputs = "#334155",
            ActionDefault = "#CBD5E1", TableLines = "#1E293B",
            Success = "#34D399", Warning = "#FBBF24", Error = "#F87171", Info = "#38BDF8"
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "14px", AppbarHeight = "68px" },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Font, FontSize = "1rem", LineHeight = "1.65", LetterSpacing = "normal" },
            H1 = new H1Typography { FontFamily = Font, FontSize = "clamp(2.5rem, 6vw, 4rem)", FontWeight = "800", LineHeight = "1.05", LetterSpacing = "-0.035em" },
            H2 = new H2Typography { FontFamily = Font, FontSize = "clamp(2rem, 4vw, 2.75rem)", FontWeight = "800", LineHeight = "1.1", LetterSpacing = "-0.03em" },
            H3 = new H3Typography { FontFamily = Font, FontSize = "clamp(1.5rem, 3vw, 2rem)", FontWeight = "700", LineHeight = "1.2", LetterSpacing = "-0.025em" },
            H4 = new H4Typography { FontFamily = Font, FontSize = "1.5rem", FontWeight = "700", LineHeight = "1.25", LetterSpacing = "-0.02em" },
            H5 = new H5Typography { FontFamily = Font, FontSize = "1.25rem", FontWeight = "700", LineHeight = "1.3", LetterSpacing = "-0.015em" },
            H6 = new H6Typography { FontFamily = Font, FontSize = "1.05rem", FontWeight = "600", LineHeight = "1.4", LetterSpacing = "-0.01em" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Font, FontWeight = "500", LetterSpacing = "-0.005em" },
            Button = new ButtonTypography { FontFamily = Font, FontWeight = "600", TextTransform = "none", LetterSpacing = "-0.005em" }
        }
    };
}
