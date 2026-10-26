using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinMusicXmlPlayer.Services
{
    public enum ThemeDisplayMode
    {
        System = 0,
        Light = 1,
        Dark = 2
    }

    public enum WindowStyleTheme
    {
        ModernFluent = 0, // 現代圓潤
        StudioSharp = 1,  // 專業俐落
        ThemeTinted = 2,  // 主題炫彩
        ConcertHall = 3   // 古典音樂廳
    }

    public sealed class ThemeColorPreset
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public double Hue { get; init; }
        public Color Color { get; init; }
        public SolidColorBrush Brush { get; init; } = Brushes.DodgerBlue;
    }

    public sealed class UserPreferencesData
    {
        public int SchemaVersion { get; set; } = 2;
        public string Language { get; set; } = "zh-TW";
        public ThemeDisplayMode DisplayMode { get; set; } = ThemeDisplayMode.System;
        public WindowStyleTheme WindowStyle { get; set; } = WindowStyleTheme.ModernFluent;
        public double ThemeHue { get; set; } = 199.0;
        public int SelectedPresetIndex { get; set; } = 0;
        public string LastOpenedDirectory { get; set; } = string.Empty;
    }

    public static class ThemeManager
    {
        private static readonly string PreferencesFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "user_preferences.json");

        public static readonly IReadOnlyList<ThemeColorPreset> Presets = BuildPresets();

        private static string _language = "zh-TW";
        // 偏好設定的顯示模式預設為系統
        private static ThemeDisplayMode _displayMode = ThemeDisplayMode.System;
        private static WindowStyleTheme _windowStyle = WindowStyleTheme.ModernFluent;
        private static double _currentHue = 199.0;
        private static int _selectedPresetIndex = 0;
        private static string _lastOpenedDirectory = string.Empty;
        private static Color _currentAccentColor = ColorFromHue(199.0);
        private static bool _isDarkEffective = true;
        private static bool _systemHooked;

        public static string Language => _language;
        public static ThemeDisplayMode DisplayMode => _displayMode;
        public static WindowStyleTheme WindowStyle => _windowStyle;
        public static double CurrentHue => _currentHue;
        public static int SelectedPresetIndex => _selectedPresetIndex;
        public static string LastOpenedDirectory => _lastOpenedDirectory;
        public static Color CurrentAccentColor => _currentAccentColor;
        public static bool IsDarkMode => _isDarkEffective;

        public static Color CurrentRollBgColor { get; private set; } = Color.FromRgb(11, 17, 30);
        public static Color CurrentRollBlackLaneColor { get; private set; } = Color.FromRgb(15, 23, 42);
        public static Color CurrentRollRulerColor { get; private set; } = Color.FromRgb(22, 32, 52);

        public static event Action? ThemeChanged;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        private static List<ThemeColorPreset> BuildPresets()
        {
            (string name, double hue)[] defs =
            {
                ("晴空蔚藍", 199.0),
                ("珊瑚粉橘", 14.0),
                ("向日葵金", 42.0),
                ("薄荷翠綠", 156.0),
                ("薰衣草紫", 262.0),
                ("蜜桃玫瑰", 332.0)
            };

            var list = new List<ThemeColorPreset>(defs.Length);
            for (int i = 0; i < defs.Length; i++)
            {
                Color c = ColorFromHue(defs[i].hue);
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                list.Add(new ThemeColorPreset
                {
                    Index = i,
                    Name = defs[i].name,
                    Hue = defs[i].hue,
                    Color = c,
                    Brush = brush
                });
            }
            return list;
        }

        public static void Initialize()
        {
            if (!_systemHooked)
            {
                _systemHooked = true;
                SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
            }

            LoadPreferences();
            ApplyTheme();
        }

        private static void OnSystemUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (_displayMode == ThemeDisplayMode.System)
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    ApplyTheme();
                });
            }
        }

        public static void SetLanguage(string langCode)
        {
            if (string.IsNullOrWhiteSpace(langCode)) return;
            _language = langCode;
            SavePreferences();
        }

        public static string GetInitialMusicXmlDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_lastOpenedDirectory) && Directory.Exists(_lastOpenedDirectory))
            {
                return _lastOpenedDirectory;
            }
            return Directory.GetCurrentDirectory();
        }

        public static void SetLastOpenedDirectory(string? directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) return;
            _lastOpenedDirectory = directoryPath;
            SavePreferences();
        }

        public static void SetDisplayMode(ThemeDisplayMode mode)
        {
            _displayMode = mode;
            ApplyTheme();
            SavePreferences();
        }

        public static void SetWindowStyle(WindowStyleTheme style)
        {
            _windowStyle = style;
            ApplyTheme();
            SavePreferences();
        }

        public static void SetPresetColor(int presetIndex)
        {
            if (presetIndex < 0 || presetIndex >= Presets.Count) return;
            var preset = Presets[presetIndex];
            _selectedPresetIndex = presetIndex;
            _currentHue = preset.Hue;
            _currentAccentColor = preset.Color;
            ApplyTheme();
            SavePreferences();
        }

        public static void SetCustomHue(double hue)
        {
            double normalized = Math.Clamp(hue, 0.0, 360.0);
            _currentHue = normalized;
            _currentAccentColor = ColorFromHue(normalized);

            int matchedPreset = -1;
            for (int i = 0; i < Presets.Count; i++)
            {
                if (Math.Abs(Presets[i].Hue - normalized) <= 1.5)
                {
                    matchedPreset = i;
                    break;
                }
            }
            _selectedPresetIndex = matchedPreset;

            ApplyTheme();
            SavePreferences();
        }

        public static void ApplyTheme()
        {
            _isDarkEffective = ResolveIsDark(_displayMode);
            _currentAccentColor = ColorFromHue(_currentHue);

            var app = Application.Current;
            if (app == null) return;

            var res = app.Resources;

            // 1. 根據視窗樣式主題決定圓角弧度
            switch (_windowStyle)
            {
                case WindowStyleTheme.StudioSharp:
                    res["CardCornerRadius"] = new CornerRadius(2);
                    res["ButtonCornerRadius"] = new CornerRadius(2);
                    res["BadgeCornerRadius"] = new CornerRadius(2);
                    break;
                case WindowStyleTheme.ThemeTinted:
                    res["CardCornerRadius"] = new CornerRadius(12);
                    res["ButtonCornerRadius"] = new CornerRadius(11);
                    res["BadgeCornerRadius"] = new CornerRadius(8);
                    break;
                case WindowStyleTheme.ConcertHall:
                    res["CardCornerRadius"] = new CornerRadius(6);
                    res["ButtonCornerRadius"] = new CornerRadius(5);
                    res["BadgeCornerRadius"] = new CornerRadius(4);
                    break;
                default: // ModernFluent
                    res["CardCornerRadius"] = new CornerRadius(8);
                    res["ButtonCornerRadius"] = new CornerRadius(6);
                    res["BadgeCornerRadius"] = new CornerRadius(5);
                    break;
            }

            // 2. 根據顯示模式 (深色/淺色) 與 視窗樣式主題 計算表面背景與邊框色
            Color bgPrimary, bgSecondary, bgSidebar, bgCard, bgBadge, bgMenu, bgPopup, bgButton, bgButtonHover, borderSubtle, borderStrong;
            Color textPrimary, textSecondary, textMuted;

            if (_isDarkEffective)
            {
                textPrimary = Color.FromRgb(248, 250, 252);
                textSecondary = Color.FromRgb(148, 163, 184);
                textMuted = Color.FromRgb(203, 213, 225);

                switch (_windowStyle)
                {
                    case WindowStyleTheme.StudioSharp:
                        // 專業俐落工作室：深黑高對比
                        bgPrimary = Color.FromRgb(10, 10, 12);
                        bgSecondary = Color.FromRgb(20, 20, 24);
                        bgSidebar = Color.FromRgb(14, 14, 18);
                        bgCard = Color.FromRgb(24, 24, 29);
                        bgBadge = Color.FromRgb(12, 12, 15);
                        bgMenu = Color.FromRgb(10, 10, 12);
                        bgPopup = Color.FromRgb(24, 24, 29);
                        bgButton = Color.FromRgb(28, 28, 34);
                        bgButtonHover = Color.FromRgb(44, 44, 52);
                        borderSubtle = Color.FromRgb(56, 56, 66);
                        borderStrong = Color.FromRgb(82, 82, 96);
                        CurrentRollBgColor = Color.FromRgb(8, 8, 10);
                        CurrentRollBlackLaneColor = Color.FromRgb(14, 14, 18);
                        CurrentRollRulerColor = Color.FromRgb(22, 22, 27);
                        break;

                    case WindowStyleTheme.ThemeTinted:
                        // 主題炫彩：將目前主題色 Hue 融入深色視窗背景與卡片
                        bgPrimary = HslToRgb(_currentHue, 0.38, 0.09);
                        bgSecondary = HslToRgb(_currentHue, 0.36, 0.14);
                        bgSidebar = HslToRgb(_currentHue, 0.36, 0.11);
                        bgCard = HslToRgb(_currentHue, 0.35, 0.16);
                        bgBadge = HslToRgb(_currentHue, 0.40, 0.08);
                        bgMenu = HslToRgb(_currentHue, 0.40, 0.08);
                        bgPopup = HslToRgb(_currentHue, 0.36, 0.15);
                        bgButton = HslToRgb(_currentHue, 0.34, 0.18);
                        bgButtonHover = HslToRgb(_currentHue, 0.40, 0.25);
                        borderSubtle = HslToRgb(_currentHue, 0.38, 0.26);
                        borderStrong = HslToRgb(_currentHue, 0.45, 0.36);
                        CurrentRollBgColor = HslToRgb(_currentHue, 0.42, 0.07);
                        CurrentRollBlackLaneColor = HslToRgb(_currentHue, 0.38, 0.10);
                        CurrentRollRulerColor = HslToRgb(_currentHue, 0.38, 0.15);
                        break;

                    case WindowStyleTheme.ConcertHall:
                        // 古典音樂廳：黑檀木與暖金胡桃木色調
                        bgPrimary = Color.FromRgb(22, 17, 15);
                        bgSecondary = Color.FromRgb(36, 28, 24);
                        bgSidebar = Color.FromRgb(28, 22, 19);
                        bgCard = Color.FromRgb(42, 33, 28);
                        bgBadge = Color.FromRgb(20, 15, 13);
                        bgMenu = Color.FromRgb(22, 17, 15);
                        bgPopup = Color.FromRgb(38, 30, 26);
                        bgButton = Color.FromRgb(44, 34, 29);
                        bgButtonHover = Color.FromRgb(64, 50, 42);
                        borderSubtle = Color.FromRgb(78, 61, 51);
                        borderStrong = Color.FromRgb(110, 86, 72);
                        textPrimary = Color.FromRgb(253, 248, 240);
                        textSecondary = Color.FromRgb(186, 168, 152);
                        textMuted = Color.FromRgb(224, 210, 196);
                        CurrentRollBgColor = Color.FromRgb(18, 14, 12);
                        CurrentRollBlackLaneColor = Color.FromRgb(25, 20, 17);
                        CurrentRollRulerColor = Color.FromRgb(38, 30, 25);
                        break;

                    default: // ModernFluent
                        bgPrimary = Color.FromRgb(15, 23, 42);
                        bgSecondary = Color.FromRgb(30, 41, 59);
                        bgSidebar = Color.FromRgb(21, 31, 50);
                        bgCard = Color.FromRgb(30, 41, 59);
                        bgBadge = Color.FromRgb(15, 23, 42);
                        bgMenu = Color.FromRgb(15, 23, 42);
                        bgPopup = Color.FromRgb(30, 41, 59);
                        bgButton = Color.FromRgb(30, 41, 59);
                        bgButtonHover = Color.FromRgb(51, 65, 85);
                        borderSubtle = Color.FromRgb(51, 65, 85);
                        borderStrong = Color.FromRgb(71, 85, 105);
                        CurrentRollBgColor = Color.FromRgb(11, 17, 30);
                        CurrentRollBlackLaneColor = Color.FromRgb(15, 23, 42);
                        CurrentRollRulerColor = Color.FromRgb(22, 32, 52);
                        break;
                }

                SetBrush(res, "AccentGoldBrush", Color.FromRgb(251, 191, 36));
                SetBrush(res, "AccentGreenBrush", Color.FromRgb(52, 211, 153));
            }
            else
            {
                textPrimary = Color.FromRgb(15, 23, 42);
                textSecondary = Color.FromRgb(71, 85, 105);
                textMuted = Color.FromRgb(51, 65, 85);

                switch (_windowStyle)
                {
                    case WindowStyleTheme.StudioSharp:
                        bgPrimary = Color.FromRgb(238, 240, 244);
                        bgSecondary = Color.FromRgb(252, 252, 253);
                        bgSidebar = Color.FromRgb(244, 245, 248);
                        bgCard = Color.FromRgb(255, 255, 255);
                        bgBadge = Color.FromRgb(224, 227, 233);
                        bgMenu = Color.FromRgb(240, 242, 245);
                        bgPopup = Color.FromRgb(255, 255, 255);
                        bgButton = Color.FromRgb(236, 238, 242);
                        bgButtonHover = Color.FromRgb(218, 222, 230);
                        borderSubtle = Color.FromRgb(180, 186, 198);
                        borderStrong = Color.FromRgb(130, 138, 152);
                        CurrentRollBgColor = Color.FromRgb(248, 249, 251);
                        CurrentRollBlackLaneColor = Color.FromRgb(234, 237, 242);
                        CurrentRollRulerColor = Color.FromRgb(222, 226, 234);
                        break;

                    case WindowStyleTheme.ThemeTinted:
                        bgPrimary = HslToRgb(_currentHue, 0.45, 0.94);
                        bgSecondary = HslToRgb(_currentHue, 0.50, 0.98);
                        bgSidebar = HslToRgb(_currentHue, 0.45, 0.96);
                        bgCard = Color.FromRgb(255, 255, 255);
                        bgBadge = HslToRgb(_currentHue, 0.45, 0.90);
                        bgMenu = HslToRgb(_currentHue, 0.50, 0.95);
                        bgPopup = Color.FromRgb(255, 255, 255);
                        bgButton = HslToRgb(_currentHue, 0.45, 0.93);
                        bgButtonHover = HslToRgb(_currentHue, 0.55, 0.87);
                        borderSubtle = HslToRgb(_currentHue, 0.42, 0.80);
                        borderStrong = HslToRgb(_currentHue, 0.48, 0.65);
                        CurrentRollBgColor = HslToRgb(_currentHue, 0.45, 0.97);
                        CurrentRollBlackLaneColor = HslToRgb(_currentHue, 0.42, 0.92);
                        CurrentRollRulerColor = HslToRgb(_currentHue, 0.48, 0.88);
                        break;

                    case WindowStyleTheme.ConcertHall:
                        // 古典羊皮紙暖調
                        bgPrimary = Color.FromRgb(245, 239, 228);
                        bgSecondary = Color.FromRgb(253, 249, 242);
                        bgSidebar = Color.FromRgb(249, 244, 235);
                        bgCard = Color.FromRgb(255, 252, 247);
                        bgBadge = Color.FromRgb(235, 226, 211);
                        bgMenu = Color.FromRgb(247, 241, 231);
                        bgPopup = Color.FromRgb(255, 252, 247);
                        bgButton = Color.FromRgb(242, 234, 220);
                        bgButtonHover = Color.FromRgb(229, 218, 200);
                        borderSubtle = Color.FromRgb(208, 193, 172);
                        borderStrong = Color.FromRgb(168, 149, 124);
                        textPrimary = Color.FromRgb(42, 31, 24);
                        textSecondary = Color.FromRgb(102, 82, 68);
                        textMuted = Color.FromRgb(74, 58, 46);
                        CurrentRollBgColor = Color.FromRgb(252, 248, 240);
                        CurrentRollBlackLaneColor = Color.FromRgb(240, 232, 218);
                        CurrentRollRulerColor = Color.FromRgb(230, 219, 202);
                        break;

                    default: // ModernFluent
                        bgPrimary = Color.FromRgb(241, 245, 249);
                        bgSecondary = Color.FromRgb(255, 255, 255);
                        bgSidebar = Color.FromRgb(248, 250, 252);
                        bgCard = Color.FromRgb(255, 255, 255);
                        bgBadge = Color.FromRgb(226, 232, 240);
                        bgMenu = Color.FromRgb(248, 250, 252);
                        bgPopup = Color.FromRgb(255, 255, 255);
                        bgButton = Color.FromRgb(241, 245, 249);
                        bgButtonHover = Color.FromRgb(226, 232, 240);
                        borderSubtle = Color.FromRgb(203, 213, 225);
                        borderStrong = Color.FromRgb(148, 163, 184);
                        CurrentRollBgColor = Color.FromRgb(248, 250, 252);
                        CurrentRollBlackLaneColor = Color.FromRgb(236, 242, 249);
                        CurrentRollRulerColor = Color.FromRgb(226, 232, 240);
                        break;
                }

                SetBrush(res, "AccentGoldBrush", Color.FromRgb(217, 119, 6));
                SetBrush(res, "AccentGreenBrush", Color.FromRgb(5, 150, 105));
            }

            SetBrush(res, "BgPrimaryBrush", bgPrimary);
            SetBrush(res, "BgSecondaryBrush", bgSecondary);
            SetBrush(res, "BgSidebarBrush", bgSidebar);
            SetBrush(res, "BgCardBrush", bgCard);
            SetBrush(res, "BgBadgeBrush", bgBadge);
            SetBrush(res, "BgMenuBrush", bgMenu);
            SetBrush(res, "BgPopupBrush", bgPopup);
            SetBrush(res, "BgButtonBrush", bgButton);
            SetBrush(res, "BgButtonHoverBrush", bgButtonHover);
            SetBrush(res, "BgOverlayBrush", Color.FromArgb(220, bgSecondary.R, bgSecondary.G, bgSecondary.B));
            SetBrush(res, "BgSplitterBrush", borderSubtle);

            SetBrush(res, "BorderSubtleBrush", borderSubtle);
            SetBrush(res, "BorderStrongBrush", borderStrong);

            SetBrush(res, "TextPrimaryBrush", textPrimary);
            SetBrush(res, "TextSecondaryBrush", textSecondary);
            SetBrush(res, "TextMutedBrush", textMuted);

            // 計算主題色彩與深淺衍生色
            Color accentDisplay = _isDarkEffective
                ? _currentAccentColor
                : HslToRgb(_currentHue, 0.85, 0.40);

            Color accentButtonBg = HslToRgb(_currentHue, 0.84, 0.44);
            Color accentButtonHover = HslToRgb(_currentHue, 0.88, 0.50);
            Color accentButtonPressed = HslToRgb(_currentHue, 0.86, 0.36);
            Color accentSoftBg = Color.FromArgb(_isDarkEffective ? (byte)52 : (byte)38,
                _currentAccentColor.R, _currentAccentColor.G, _currentAccentColor.B);

            SetBrush(res, "AccentPrimaryBrush", _currentAccentColor);
            SetBrush(res, "AccentBlueBrush", accentDisplay);
            SetBrush(res, "AccentButtonBgBrush", accentButtonBg);
            SetBrush(res, "AccentButtonHoverBrush", accentButtonHover);
            SetBrush(res, "AccentButtonPressedBrush", accentButtonPressed);
            SetBrush(res, "AccentSoftBgBrush", accentSoftBg);

            // 同步更新所有已開啟視窗的原生 DWM 標題列與邊框外觀
            foreach (Window win in app.Windows)
            {
                ApplyWindowDwmStyle(win, bgMenu, textPrimary, borderSubtle);
            }

            ThemeChanged?.Invoke();
        }

        public static void ApplyWindowDwmStyle(Window window)
        {
            var app = Application.Current;
            if (app == null) return;
            var bgMenu = (app.Resources["BgMenuBrush"] as SolidColorBrush)?.Color ?? Color.FromRgb(15, 23, 42);
            var textPrimary = (app.Resources["TextPrimaryBrush"] as SolidColorBrush)?.Color ?? Colors.White;
            var borderSubtle = (app.Resources["BorderSubtleBrush"] as SolidColorBrush)?.Color ?? Colors.Gray;
            ApplyWindowDwmStyle(window, bgMenu, textPrimary, borderSubtle);
        }

        private static void ApplyWindowDwmStyle(Window window, Color captionColor, Color textColor, Color borderColor)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int dark = _isDarkEffective ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

                // Windows 11 圓角屬性 (2 = Round, 1 = DoNotRound, 3 = RoundSmall)
                int cornerPref = _windowStyle switch
                {
                    WindowStyleTheme.StudioSharp => 1,
                    WindowStyleTheme.ConcertHall => 3,
                    _ => 2
                };
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

                int capColorRef = ToColorRef(captionColor);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref capColorRef, sizeof(int));

                int txtColorRef = ToColorRef(textColor);
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref txtColorRef, sizeof(int));

                Color frameBorder = _windowStyle == WindowStyleTheme.ThemeTinted ? _currentAccentColor : borderColor;
                int bdrColorRef = ToColorRef(frameBorder);
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref bdrColorRef, sizeof(int));
            }
            catch
            {
                // 若舊版 Windows 不支援部分 DWM 屬性則安全略過
            }
        }

        private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        private static void SetBrush(ResourceDictionary res, string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            res[key] = brush;
        }

        public static bool ResolveIsDark(ThemeDisplayMode mode)
        {
            if (mode == ThemeDisplayMode.Dark) return true;
            if (mode == ThemeDisplayMode.Light) return false;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int val)
                {
                    return val == 0;
                }
            }
            catch
            {
                // fallback
            }
            return true;
        }

        public static Color ColorFromHue(double hue)
        {
            double h = ((hue % 360.0) + 360.0) % 360.0;
            return HslToRgb(h, 0.88, 0.58);
        }

        public static Color HslToRgb(double h, double s, double l)
        {
            h = ((h % 360.0) + 360.0) % 360.0;
            s = Math.Clamp(s, 0.0, 1.0);
            l = Math.Clamp(l, 0.0, 1.0);

            double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
            double x = c * (1.0 - Math.Abs(((h / 60.0) % 2.0) - 1.0));
            double m = l - c * 0.5;

            double r1, g1, b1;
            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }

            byte r = (byte)Math.Clamp(Math.Round((r1 + m) * 255.0), 0, 255);
            byte g = (byte)Math.Clamp(Math.Round((g1 + m) * 255.0), 0, 255);
            byte b = (byte)Math.Clamp(Math.Round((b1 + m) * 255.0), 0, 255);
            return Color.FromRgb(r, g, b);
        }

        private static void LoadPreferences()
        {
            try
            {
                if (!File.Exists(PreferencesFilePath)) return;
                string json = File.ReadAllText(PreferencesFilePath);
                var data = JsonSerializer.Deserialize<UserPreferencesData>(json);
                if (data != null && data.SchemaVersion >= 2)
                {
                    _language = string.IsNullOrWhiteSpace(data.Language) ? "zh-TW" : data.Language;
                    _displayMode = data.DisplayMode;
                    _windowStyle = data.WindowStyle;
                    _currentHue = Math.Clamp(data.ThemeHue, 0.0, 360.0);
                    _selectedPresetIndex = data.SelectedPresetIndex;
                    if (!string.IsNullOrWhiteSpace(data.LastOpenedDirectory) && Directory.Exists(data.LastOpenedDirectory))
                    {
                        _lastOpenedDirectory = data.LastOpenedDirectory;
                    }
                }
            }
            catch
            {
                // ignore broken config
            }
        }

        private static void SavePreferences()
        {
            try
            {
                var data = new UserPreferencesData
                {
                    SchemaVersion = 2,
                    Language = _language,
                    DisplayMode = _displayMode,
                    WindowStyle = _windowStyle,
                    ThemeHue = _currentHue,
                    SelectedPresetIndex = _selectedPresetIndex,
                    LastOpenedDirectory = _lastOpenedDirectory
                };
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(PreferencesFilePath, json);
            }
            catch
            {
                // ignore write failure
            }
        }
    }
}
