using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Microsoft.Win32;

namespace DesktopTodo
{
    // The five theme families. Persisted codes are lower-case English words so
    // tasks.json stays readable ("sage", "graphite", ...).
    public enum ThemeFamily { Sage = 0, Graphite = 1, Midnight = 2, Catppuccin = 3, LiquidGlass = 4 }

    // How the light/dark variant is chosen: follow Windows, or lock to one side.
    public enum LightDarkMode { FollowSystem = 0, Light = 1, Dark = 2 }

    // One palette: every colour the card can paint, as hex strings ("RRGGBB" or
    // "AARRGGBB") so this file stays free of WPF references and the storage test
    // suite can compile and validate it. App.cs turns the tokens into brushes.
    //
    // Token names map 1:1 to resource keys in MainWindow.xaml ("Theme" + name).
    public sealed class ThemePalette
    {
        public ThemeFamily Family;
        public bool Dark;
        // Liquid Glass fakes frosted glass: semi-transparent layered surfaces,
        // a top specular highlight and a CardBg gradient. Other themes leave
        // CardBgEnd/GlassHighlight empty and get plain solids.
        public bool IsGlass;

        public string CardBg;
        public string CardBgEnd;       // glass only: bottom of the card gradient
        public string CardBorder;
        public string CardShadow;      // stored as a Color resource, not a brush
        public string GlassHighlight;  // glass only: top specular line

        public string TextPrimary;
        public string TextSoft;
        public string TextSecondary;
        public string TextMuted;

        public string Accent;
        public string AccentHover;
        public string AccentFocus;
        public string AccentForeground; // glyph colour on accent-filled buttons
        public string AccentSoft;
        public string AccentSoftBorder;
        public string AccentHoverBorder;
        public string AccentText;

        public string ButtonFg;
        public string IconMuted;
        public string HoverSurface;
        public string FocusSurface;

        public string InputBg;
        public string InputBorder;
        public string Separator;
        public string ToastBg;
        public string ProgressBg;
        public string ProgressFg;

        public string CircleStroke;
        public string DoneGray;
        // Semantic colours keep their meaning in every theme (red = overdue,
        // green = due today, grey = done); the exact values are tuned per theme.
        public string Overdue;
        public string DueToday;
        public string DueFuture;
        public string MarkerRed;        // holiday/weekend dot in the calendar

        public string PinBg;
        public string PinBorder;
        public string DisabledGray;

        public string EmptyArtBg;
        public string EmptyArtBorder;
        public string EmptyArtCheck;
        public string EmptyArtLine;

        public string FillerText;       // out-of-month calendar days, resize grip
        public string SelectedCell;     // selected calendar day background

        // Heat scale from lightest to deepest, exactly five stops.
        public string[] Heat;
    }

    public static class Themes
    {
        public const int FamilyCount = 5;
        public const int ModeCount = 3;

        private static readonly string[] FamilyCodes = { "sage", "graphite", "midnight", "catppuccin", "liquidglass" };
        private static readonly string[] ModeCodes = { "followSystem", "light", "dark" };
        private static readonly ThemePalette[] All = BuildAll();

        // Test hook: the integration suite pins the system appearance so theme
        // resolution is deterministic. Production reads the registry.
        internal static Func<bool> SystemLightProbe = ReadSystemLight;

        public static ThemePalette[] Registered { get { return All; } }

        public static string CodeOf(ThemeFamily family) { return FamilyCodes[(int)family]; }
        public static string CodeOf(LightDarkMode mode) { return ModeCodes[(int)mode]; }

        public static ThemeFamily ParseFamily(string stored)
        {
            if (!String.IsNullOrWhiteSpace(stored))
                for (int i = 0; i < FamilyCodes.Length; i++)
                    if (String.Equals(FamilyCodes[i], stored.Trim(), StringComparison.OrdinalIgnoreCase))
                        return (ThemeFamily)i;
            return ThemeFamily.Sage;
        }

        public static LightDarkMode ParseMode(string stored)
        {
            if (!String.IsNullOrWhiteSpace(stored))
                for (int i = 0; i < ModeCodes.Length; i++)
                    if (String.Equals(ModeCodes[i], stored.Trim(), StringComparison.OrdinalIgnoreCase))
                        return (LightDarkMode)i;
            return LightDarkMode.FollowSystem;
        }

        public static ThemePalette Get(ThemeFamily family, bool dark)
        {
            foreach (ThemePalette palette in All)
                if (palette.Family == family && palette.Dark == dark) return palette;
            return All[0];
        }

        // The palette for a persisted (family, mode) pair given the current OS
        // appearance. FollowSystem asks the probe; the locked modes ignore it.
        public static ThemePalette Resolve(ThemeFamily family, LightDarkMode mode, bool systemLight)
        {
            bool dark = mode == LightDarkMode.Dark
                || (mode == LightDarkMode.FollowSystem && !systemLight);
            return Get(family, dark);
        }

        public static bool SystemIsLight()
        {
            try { return SystemLightProbe(); }
            catch (InvalidOperationException) { return true; }
            catch (System.Security.SecurityException) { return true; }
            catch (System.IO.IOException) { return true; }
        }

        private static bool ReadSystemLight()
        {
            // Windows personalisation key: 1 = apps use a light theme.
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
            {
                object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                return !(value is int) || (int)value != 0;
            }
        }

        // Structural validation used by the storage tests: every token filled,
        // every value a valid 6/8-digit hex colour, heat scale exactly five stops.
        public static List<string> Validate(ThemePalette palette)
        {
            List<string> problems = new List<string>();
            foreach (FieldInfo field in typeof(ThemePalette).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.FieldType == typeof(string))
                {
                    string value = (string)field.GetValue(palette);
                    bool optional = field.Name == "CardBgEnd" || field.Name == "GlassHighlight";
                    if (String.IsNullOrEmpty(value))
                    {
                        if (!optional) problems.Add(field.Name + " is empty");
                    }
                    else if (!IsHexColor(value)) problems.Add(field.Name + " is not a hex colour: " + value);
                }
            }
            if (palette.Heat == null || palette.Heat.Length != 5)
                problems.Add("Heat must hold exactly five stops");
            else
                for (int i = 0; i < palette.Heat.Length; i++)
                    if (!IsHexColor(palette.Heat[i])) problems.Add("Heat[" + i + "] is not a hex colour");
            if (palette.IsGlass && (String.IsNullOrEmpty(palette.CardBgEnd) || String.IsNullOrEmpty(palette.GlassHighlight)))
                problems.Add("a glass palette needs CardBgEnd and GlassHighlight");
            return problems;
        }

        private static bool IsHexColor(string value)
        {
            if (value == null) return false;
            if (value.Length != 6 && value.Length != 8) return false;
            foreach (char c in value)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        private static ThemePalette[] BuildAll()
        {
            return new ThemePalette[]
            {
                // ---- Sage: the classic look. Light values are byte-identical to
                // the original hard-coded card, which the UI tests assert on. ----
                new ThemePalette
                {
                    Family = ThemeFamily.Sage, Dark = false,
                    CardBg = "FFFEFB", CardBorder = "E2E8E0", CardShadow = "354A3D",
                    TextPrimary = "303A36", TextSoft = "6F7D72", TextSecondary = "8A958D", TextMuted = "9BA595",
                    Accent = "3C7865", AccentHover = "2D6351", AccentFocus = "214F40", AccentForeground = "FFFFFF",
                    AccentSoft = "EAF2EC", AccentSoftBorder = "D5E5D9", AccentHoverBorder = "87A991", AccentText = "56755E",
                    ButtonFg = "66736C", IconMuted = "8C9890", HoverSurface = "EDF2EE", FocusSurface = "DFEBE3",
                    InputBg = "F4F6F0", InputBorder = "E1E7DC", Separator = "EAF0E6", ToastBg = "EEF3EB",
                    ProgressBg = "EDF1E8", ProgressFg = "9EB796",
                    CircleStroke = "ABB8AF", DoneGray = "8D9891",
                    Overdue = "C0504D", DueToday = "3C7865", DueFuture = "8A9A90", MarkerRed = "C0504D",
                    PinBg = "FFFFFF", PinBorder = "E2E7E1", DisabledGray = "A5AEA7",
                    EmptyArtBg = "F0F4EB", EmptyArtBorder = "BBCBB8", EmptyArtCheck = "70966D", EmptyArtLine = "D2DFCB",
                    FillerText = "B8C0B6", SelectedCell = "D5E8DA",
                    Heat = new[] { "EDF1E8", "D5E8DA", "A8D3B4", "6FB386", "3C7865" }
                },
                new ThemePalette
                {
                    Family = ThemeFamily.Sage, Dark = true,
                    CardBg = "1E2622", CardBorder = "33413A", CardShadow = "000000",
                    TextPrimary = "DDE5DF", TextSoft = "93A49A", TextSecondary = "8A9890", TextMuted = "6E7D74",
                    Accent = "6FB89B", AccentHover = "8AC7AE", AccentFocus = "A3D6C1", AccentForeground = "1E2622",
                    AccentSoft = "2A3C34", AccentSoftBorder = "365246", AccentHoverBorder = "466F5E", AccentText = "8AC7AE",
                    ButtonFg = "9DB0A6", IconMuted = "7A8B81", HoverSurface = "28322D", FocusSurface = "30403A",
                    InputBg = "252E29", InputBorder = "36433C", Separator = "2B3831", ToastBg = "28342E",
                    ProgressBg = "2A3530", ProgressFg = "5F8F77",
                    CircleStroke = "5A6B62", DoneGray = "6B7A70",
                    Overdue = "E07870", DueToday = "7FC98F", DueFuture = "75857B", MarkerRed = "E07870",
                    PinBg = "28322D", PinBorder = "3A4A42", DisabledGray = "55645C",
                    EmptyArtBg = "28322D", EmptyArtBorder = "46574E", EmptyArtCheck = "6FA98A", EmptyArtLine = "3D4E45",
                    FillerText = "5C6C63", SelectedCell = "365246",
                    Heat = new[] { "2A3530", "3A5A48", "4F7A60", "699B7C", "8FD3A8" }
                },
                // ---- Graphite: Notion / Things style neutral grey. ----
                new ThemePalette
                {
                    Family = ThemeFamily.Graphite, Dark = false,
                    CardBg = "FFFFFF", CardBorder = "E5E5E3", CardShadow = "4A4A48",
                    TextPrimary = "1F2328", TextSoft = "6B6F75", TextSecondary = "8B8F94", TextMuted = "A3A6AA",
                    Accent = "37352F", AccentHover = "24231F", AccentFocus = "12110E", AccentForeground = "FFFFFF",
                    AccentSoft = "EFEFEE", AccentSoftBorder = "D7D6D5", AccentHoverBorder = "B9B8B6", AccentText = "4A4842",
                    ButtonFg = "5F6368", IconMuted = "8C9095", HoverSurface = "F5F5F4", FocusSurface = "EBEBEA",
                    InputBg = "F7F7F5", InputBorder = "E3E3E1", Separator = "EBEBE9", ToastBg = "F0F0EE",
                    ProgressBg = "EFEFED", ProgressFg = "A9A7A2",
                    CircleStroke = "B5B8BB", DoneGray = "9B9E9F",
                    Overdue = "C4554D", DueToday = "4C8A58", DueFuture = "93969B", MarkerRed = "C4554D",
                    PinBg = "FFFFFF", PinBorder = "E2E2E0", DisabledGray = "ADAFB2",
                    EmptyArtBg = "F2F2F0", EmptyArtBorder = "C9C9C6", EmptyArtCheck = "8F8D87", EmptyArtLine = "D8D7D4",
                    FillerText = "B9BCBF", SelectedCell = "E2E2E0",
                    Heat = new[] { "EFEFED", "D8D7D3", "B5B3AD", "85837E", "55534E" }
                },
                new ThemePalette
                {
                    Family = ThemeFamily.Graphite, Dark = true,
                    CardBg = "191919", CardBorder = "2C2C2C", CardShadow = "000000",
                    TextPrimary = "E8E8E6", TextSoft = "9B9B98", TextSecondary = "8B8B88", TextMuted = "6E6E6B",
                    Accent = "ECECEA", AccentHover = "FFFFFF", AccentFocus = "D6D6D3", AccentForeground = "191919",
                    AccentSoft = "2E2E2D", AccentSoftBorder = "474745", AccentHoverBorder = "6D6D6B", AccentText = "D4D4D1",
                    ButtonFg = "A0A09D", IconMuted = "7E7E7B", HoverSurface = "252525", FocusSurface = "2F2F2E",
                    InputBg = "222222", InputBorder = "333332", Separator = "282828", ToastBg = "242423",
                    ProgressBg = "2A2A29", ProgressFg = "8A8A86",
                    CircleStroke = "555554", DoneGray = "6E7377",
                    Overdue = "E06C65", DueToday = "6FBF82", DueFuture = "767875", MarkerRed = "E06C65",
                    PinBg = "252525", PinBorder = "383837", DisabledGray = "4C4C4A",
                    EmptyArtBg = "222221", EmptyArtBorder = "3A3A38", EmptyArtCheck = "9B9B95", EmptyArtLine = "333331",
                    FillerText = "555553", SelectedCell = "3A3A38",
                    Heat = new[] { "2A2A2A", "555553", "82827F", "B0B0AD", "D8D8D5" }
                },
                // ---- Midnight: Linear / Raycast style indigo. ----
                new ThemePalette
                {
                    Family = ThemeFamily.Midnight, Dark = false,
                    CardBg = "F7F8F9", CardBorder = "E3E5E8", CardShadow = "3A4058",
                    TextPrimary = "22262B", TextSoft = "5C6470", TextSecondary = "6E7480", TextMuted = "9AA0AB",
                    Accent = "5E6AD2", AccentHover = "4E57B8", AccentFocus = "3F4799", AccentForeground = "FFFFFF",
                    AccentSoft = "E7EAF5", AccentSoftBorder = "D1D4EF", AccentHoverBorder = "B2B8E7", AccentText = "4E57B8",
                    ButtonFg = "5C6470", IconMuted = "878D99", HoverSurface = "EEF0F4", FocusSurface = "E2E6F0",
                    InputBg = "FFFFFF", InputBorder = "DFE2E8", Separator = "E7E9ED", ToastBg = "EEF0F6",
                    ProgressBg = "E8EAF6", ProgressFg = "9AA3E0",
                    CircleStroke = "AEB4BF", DoneGray = "9AA0AB",
                    Overdue = "D95757", DueToday = "3D9A63", DueFuture = "8B91A0", MarkerRed = "D95757",
                    PinBg = "FFFFFF", PinBorder = "DFE2E8", DisabledGray = "B3B8C2",
                    EmptyArtBg = "EBEEF8", EmptyArtBorder = "BEC5E6", EmptyArtCheck = "7C89DB", EmptyArtLine = "D6DBF2",
                    FillerText = "B6BBC6", SelectedCell = "D1D4EF",
                    Heat = new[] { "E8EAF6", "C9CEEF", "A5ADEC", "828BE8", "5E6AD2" }
                },
                new ThemePalette
                {
                    Family = ThemeFamily.Midnight, Dark = true,
                    CardBg = "101216", CardBorder = "23262E", CardShadow = "000000",
                    TextPrimary = "E6E8EE", TextSoft = "8A91A0", TextSecondary = "7C8290", TextMuted = "5E6470",
                    Accent = "8A97FF", AccentHover = "A3AEFF", AccentFocus = "BCC5FF", AccentForeground = "101216",
                    AccentSoft = "212537", AccentSoftBorder = "353A5C", AccentHoverBorder = "4D558B", AccentText = "A8B2FF",
                    ButtonFg = "98A0B0", IconMuted = "6E7684", HoverSurface = "1B1E26", FocusSurface = "232738",
                    InputBg = "171A20", InputBorder = "262A34", Separator = "1D212A", ToastBg = "1A1E2A",
                    ProgressBg = "262B4D", ProgressFg = "5E6AD2",
                    CircleStroke = "454B58", DoneGray = "5C6370",
                    Overdue = "F07070", DueToday = "5FC98A", DueFuture = "6A7180", MarkerRed = "F07070",
                    PinBg = "1B1E26", PinBorder = "2C303C", DisabledGray = "3E434E",
                    EmptyArtBg = "1A1D28", EmptyArtBorder = "343A52", EmptyArtCheck = "6E7BE0", EmptyArtLine = "2A2F45",
                    FillerText = "474D5C", SelectedCell = "353A5C",
                    Heat = new[] { "262B4D", "3D4480", "565EB8", "707BE8", "8A97FF" }
                },
                // ---- Catppuccin: the community palettes, Latte and Mocha, with
                // mauve as the accent. ----
                new ThemePalette
                {
                    Family = ThemeFamily.Catppuccin, Dark = false,
                    CardBg = "EFF1F5", CardBorder = "CCD0DA", CardShadow = "4C4F69",
                    TextPrimary = "4C4F69", TextSoft = "5C5F77", TextSecondary = "6C6F85", TextMuted = "9CA0B0",
                    Accent = "8839EF", AccentHover = "7A3DD6", AccentFocus = "6A32BD", AccentForeground = "FFFFFF",
                    AccentSoft = "E4DEF4", AccentSoftBorder = "D5C3F3", AccentHoverBorder = "C09EF2", AccentText = "7A3DD6",
                    ButtonFg = "5C5F77", IconMuted = "8C8FA1", HoverSurface = "DCE0E8", FocusSurface = "CCD0DA",
                    InputBg = "E6E9EF", InputBorder = "CCD0DA", Separator = "DCE0E8", ToastBg = "E6E9EF",
                    ProgressBg = "DCE0E8", ProgressFg = "B18EF5",
                    CircleStroke = "ACB0BE", DoneGray = "9CA0B0",
                    Overdue = "D20F39", DueToday = "40A02B", DueFuture = "8C8FA1", MarkerRed = "D20F39",
                    PinBg = "EFF1F5", PinBorder = "CCD0DA", DisabledGray = "ACB0BE",
                    EmptyArtBg = "E6E9EF", EmptyArtBorder = "BCC0CC", EmptyArtCheck = "9D6CE0", EmptyArtLine = "CCD0DA",
                    FillerText = "ACB0BE", SelectedCell = "D5C3F3",
                    Heat = new[] { "DCE0E8", "D0C3F0", "BD9DF3", "A571F2", "8839EF" }
                },
                new ThemePalette
                {
                    Family = ThemeFamily.Catppuccin, Dark = true,
                    CardBg = "1E1E2E", CardBorder = "313244", CardShadow = "11111B",
                    TextPrimary = "CDD6F4", TextSoft = "A6ADC8", TextSecondary = "9399B2", TextMuted = "7F849C",
                    Accent = "CBA6F7", AccentHover = "D8B9FA", AccentFocus = "E3CCFB", AccentForeground = "1E1E2E",
                    AccentSoft = "36314A", AccentSoftBorder = "52476A", AccentHoverBorder = "756293", AccentText = "D8B9FA",
                    ButtonFg = "A6ADC8", IconMuted = "7F849C", HoverSurface = "292C3C", FocusSurface = "32324A",
                    InputBg = "181825", InputBorder = "313244", Separator = "292C3C", ToastBg = "242437",
                    ProgressBg = "313244", ProgressFg = "9D7FD6",
                    CircleStroke = "585B70", DoneGray = "6C7086",
                    Overdue = "F38BA8", DueToday = "A6E3A1", DueFuture = "7F849C", MarkerRed = "F38BA8",
                    PinBg = "242437", PinBorder = "3B3D52", DisabledGray = "45475A",
                    EmptyArtBg = "242437", EmptyArtBorder = "45475A", EmptyArtCheck = "B18EF5", EmptyArtLine = "3A3C50",
                    FillerText = "585B70", SelectedCell = "52476A",
                    Heat = new[] { "313244", "6B5290", "8A68B8", "AB86D8", "CBA6F7" }
                },
                // ---- Liquid Glass: the iOS 26 look, faked with layered translucency
                // (no real backdrop blur; see docs/theme-design.md). Light variant
                // pushed much more transparent 2026-09-20 at Leo's request: card
                // fill 65/55% so the wallpaper genuinely shows through. ----
                new ThemePalette
                {
                    Family = ThemeFamily.LiquidGlass, Dark = false, IsGlass = true,
                    CardBg = "A6FFFFFF", CardBgEnd = "8CF5FAFF", CardBorder = "A6FFFFFF", CardShadow = "3A4A6A",
                    GlassHighlight = "80FFFFFF",
                    TextPrimary = "1C1C1E", TextSoft = "5A5A5E", TextSecondary = "6E6E73", TextMuted = "98989D",
                    Accent = "007AFF", AccentHover = "0062CC", AccentFocus = "004A99", AccentForeground = "FFFFFF",
                    AccentSoft = "E5F1FF", AccentSoftBorder = "BFDDFF", AccentHoverBorder = "8CC3FF", AccentText = "0062CC",
                    ButtonFg = "5A5A60", IconMuted = "8E8E93", HoverSurface = "73FFFFFF", FocusSurface = "59FFFFFF",
                    InputBg = "B3FFFFFF", InputBorder = "80FFFFFF", Separator = "1A000000", ToastBg = "C8F5F9FF",
                    ProgressBg = "26000000", ProgressFg = "80007AFF",
                    CircleStroke = "AEAEB2", DoneGray = "8E8E93",
                    Overdue = "FF3B30", DueToday = "248A3D", DueFuture = "8E8E93", MarkerRed = "FF3B30",
                    PinBg = "99FFFFFF", PinBorder = "80FFFFFF", DisabledGray = "C7C7CC",
                    EmptyArtBg = "99F0F8FF", EmptyArtBorder = "80FFFFFF", EmptyArtCheck = "5AB8E6", EmptyArtLine = "B3E0F0",
                    FillerText = "B0B0B5", SelectedCell = "BFDDFF",
                    Heat = new[] { "E3F0FF", "B8D9FF", "85BEFF", "4D9FFF", "007AFF" }
                },
                new ThemePalette
                {
                    Family = ThemeFamily.LiquidGlass, Dark = true, IsGlass = true,
                    CardBg = "C21C1C1E", CardBgEnd = "A8282830", CardBorder = "33FFFFFF", CardShadow = "000000",
                    GlassHighlight = "2EFFFFFF",
                    TextPrimary = "F2F2F7", TextSoft = "A8A8AD", TextSecondary = "98989D", TextMuted = "6E6E73",
                    Accent = "0A84FF", AccentHover = "3B9BFF", AccentFocus = "66B2FF", AccentForeground = "FFFFFF",
                    AccentSoft = "18314B", AccentSoftBorder = "16406D", AccentHoverBorder = "12559A", AccentText = "66B2FF",
                    ButtonFg = "B8B8BD", IconMuted = "8E8E93", HoverSurface = "26FFFFFF", FocusSurface = "33FFFFFF",
                    InputBg = "1FFFFFFF", InputBorder = "2EFFFFFF", Separator = "26FFFFFF", ToastBg = "E02C2C34",
                    ProgressBg = "33FFFFFF", ProgressFg = "660A84FF",
                    CircleStroke = "5A5A5E", DoneGray = "636366",
                    Overdue = "FF453A", DueToday = "30D158", DueFuture = "78787D", MarkerRed = "FF453A",
                    PinBg = "2EFFFFFF", PinBorder = "40FFFFFF", DisabledGray = "48484C",
                    EmptyArtBg = "1FFFFFFF", EmptyArtBorder = "33FFFFFF", EmptyArtCheck = "5EB2F0", EmptyArtLine = "26FFFFFF",
                    FillerText = "55555A", SelectedCell = "16406D",
                    Heat = new[] { "1C3A5E", "155094", "0F6BC9", "2C90FF", "66B2FF" }
                }
            };
        }
    }
}
