using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WinMusicXmlPlayer.Services;

namespace WinMusicXmlPlayer
{
    public partial class PreferencesWindow : Window
    {
        private bool _isSyncingUi;
        private readonly List<Border> _presetRingBorders = new();
        private readonly List<TextBlock> _presetCheckmarks = new();

        public PreferencesWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, _) => ThemeManager.ApplyWindowDwmStyle(this);
            BuildPresetSwatches();
            SyncUiFromThemeManager();
        }

        private void BuildPresetSwatches()
        {
            PresetSwatchesPanel.Children.Clear();
            _presetRingBorders.Clear();
            _presetCheckmarks.Clear();

            for (int i = 0; i < ThemeManager.Presets.Count; i++)
            {
                var preset = ThemeManager.Presets[i];
                int capturedIndex = i;

                var ringBorder = new Border
                {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(14),
                    BorderThickness = new Thickness(2),
                    BorderBrush = Brushes.Transparent,
                    Padding = new Thickness(2),
                    Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = $"{preset.Name} (Hue {preset.Hue:0}°)"
                };

                var innerGrid = new Grid();
                var circle = new Ellipse
                {
                    Fill = preset.Brush
                };

                var checkText = new TextBlock
                {
                    Text = "✓",
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed
                };

                innerGrid.Children.Add(circle);
                innerGrid.Children.Add(checkText);
                ringBorder.Child = innerGrid;

                ringBorder.MouseLeftButtonDown += (_, _) =>
                {
                    OnPresetSwatchClicked(capturedIndex);
                };

                _presetRingBorders.Add(ringBorder);
                _presetCheckmarks.Add(checkText);
                PresetSwatchesPanel.Children.Add(ringBorder);
            }
        }

        private void SyncUiFromThemeManager()
        {
            _isSyncingUi = true;
            try
            {
                UpdateDisplayModeButtonsVisual(ThemeManager.DisplayMode);
                UpdateWindowStyleButtonsVisual(ThemeManager.WindowStyle);
                SliderHue.Value = ThemeManager.CurrentHue;
                UpdateColorVisualIndicators();
            }
            finally
            {
                _isSyncingUi = false;
            }
        }

        private void UpdateDisplayModeButtonsVisual(ThemeDisplayMode activeMode)
        {
            ApplyOptionButtonState(BtnModeSystem, activeMode == ThemeDisplayMode.System, 11, 5, 12.5);
            ApplyOptionButtonState(BtnModeLight, activeMode == ThemeDisplayMode.Light, 11, 5, 12.5);
            ApplyOptionButtonState(BtnModeDark, activeMode == ThemeDisplayMode.Dark, 11, 5, 12.5);
        }

        private void UpdateWindowStyleButtonsVisual(WindowStyleTheme activeStyle)
        {
            ApplyOptionButtonState(BtnWinStyleModern, activeStyle == WindowStyleTheme.ModernFluent, 8, 6, 12);
            ApplyOptionButtonState(BtnWinStyleStudio, activeStyle == WindowStyleTheme.StudioSharp, 8, 6, 12);
            ApplyOptionButtonState(BtnWinStyleTinted, activeStyle == WindowStyleTheme.ThemeTinted, 8, 6, 12);
            ApplyOptionButtonState(BtnWinStyleConcert, activeStyle == WindowStyleTheme.ConcertHall, 8, 6, 12);
        }

        private static void ApplyOptionButtonState(Button btn, bool isActive, double padX, double padY, double fontSize)
        {
            btn.Style = (Style)Application.Current.FindResource(isActive ? "PlayButtonStyle" : "ToolbarButtonStyle");
            btn.Padding = new Thickness(padX, padY, padX, padY);
            btn.FontSize = fontSize;
        }

        private void UpdateColorVisualIndicators()
        {
            Color c = ThemeManager.CurrentAccentColor;
            string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            int presetIdx = ThemeManager.SelectedPresetIndex;

            // 更新滑桿左方的小型圓形自訂色塊
            CustomColorSwatchEllipse.Fill = new SolidColorBrush(c);
            TxtHueDegree.Text = $"{Math.Round(ThemeManager.CurrentHue):0}°";

            var activeRingBrush = (Brush)Application.Current.FindResource("TextPrimaryBrush");
            var subtleRingBrush = (Brush)Application.Current.FindResource("BorderSubtleBrush");

            // 更新 6 個預設小型圓形色塊的選中狀態
            for (int i = 0; i < _presetRingBorders.Count; i++)
            {
                bool isSelected = (i == presetIdx);
                _presetRingBorders[i].BorderBrush = isSelected ? activeRingBrush : Brushes.Transparent;
                _presetCheckmarks[i].Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            }

            if (presetIdx >= 0 && presetIdx < ThemeManager.Presets.Count)
            {
                string presetName = ThemeManager.Presets[presetIdx].Name;
                TxtActiveColorLabel.Text = $"{presetName} ({hex})";
                CustomColorOuterRing.BorderBrush = subtleRingBrush;
            }
            else
            {
                TxtActiveColorLabel.Text = $"自訂主題色彩 ({hex})";
                CustomColorOuterRing.BorderBrush = activeRingBrush;
            }
        }

        private void OnPresetSwatchClicked(int presetIndex)
        {
            if (_isSyncingUi) return;
            _isSyncingUi = true;
            try
            {
                ThemeManager.SetPresetColor(presetIndex);
                SliderHue.Value = ThemeManager.CurrentHue;
                UpdateDisplayModeButtonsVisual(ThemeManager.DisplayMode);
                UpdateWindowStyleButtonsVisual(ThemeManager.WindowStyle);
                UpdateColorVisualIndicators();
            }
            finally
            {
                _isSyncingUi = false;
            }
        }

        private void SliderHue_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSyncingUi || !IsLoaded) return;
            _isSyncingUi = true;
            try
            {
                ThemeManager.SetCustomHue(e.NewValue);
                UpdateDisplayModeButtonsVisual(ThemeManager.DisplayMode);
                UpdateWindowStyleButtonsVisual(ThemeManager.WindowStyle);
                UpdateColorVisualIndicators();
            }
            finally
            {
                _isSyncingUi = false;
            }
        }

        private void BtnModeSystem_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.SetDisplayMode(ThemeDisplayMode.System);
            UpdateDisplayModeButtonsVisual(ThemeManager.DisplayMode);
            UpdateColorVisualIndicators();
        }

        private void BtnModeLight_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.SetDisplayMode(ThemeDisplayMode.Light);
            UpdateDisplayModeButtonsVisual(ThemeManager.DisplayMode);
            UpdateColorVisualIndicators();
        }

        private void BtnModeDark_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.SetDisplayMode(ThemeDisplayMode.Dark);
            UpdateDisplayModeButtonsVisual(ThemeManager.DisplayMode);
            UpdateColorVisualIndicators();
        }

        private void BtnWinStyleModern_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.SetWindowStyle(WindowStyleTheme.ModernFluent);
            UpdateWindowStyleButtonsVisual(ThemeManager.WindowStyle);
        }

        private void BtnWinStyleStudio_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.SetWindowStyle(WindowStyleTheme.StudioSharp);
            UpdateWindowStyleButtonsVisual(ThemeManager.WindowStyle);
        }

        private void BtnWinStyleTinted_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.SetWindowStyle(WindowStyleTheme.ThemeTinted);
            UpdateWindowStyleButtonsVisual(ThemeManager.WindowStyle);
        }

        private void BtnWinStyleConcert_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.SetWindowStyle(WindowStyleTheme.ConcertHall);
            UpdateWindowStyleButtonsVisual(ThemeManager.WindowStyle);
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (CmbLanguage.SelectedItem is ComboBoxItem item && item.Tag is string lang)
            {
                ThemeManager.SetLanguage(lang);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
