using System.Windows;
using WinMusicXmlPlayer.Services;

namespace WinMusicXmlPlayer
{
    public partial class AboutWindow : Window
    {
        public AboutWindow(string currentSoundFontName, string currentSoundFontPath)
        {
            InitializeComponent();
            SourceInitialized += (_, _) => ThemeManager.ApplyWindowDwmStyle(this);

            if (!string.IsNullOrWhiteSpace(currentSoundFontPath))
            {
                TxtCurrentSoundFontInfo.Text = $"• 目前使用中音源：{currentSoundFontName}（路徑：{currentSoundFontPath}）";
            }
            else
            {
                TxtCurrentSoundFontInfo.Text = $"• 目前使用中音源：{currentSoundFontName}";
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
