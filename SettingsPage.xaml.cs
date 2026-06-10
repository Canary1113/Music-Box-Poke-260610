using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MusicBox.Services;
using MusicBox.ViewModels;

namespace MusicBox
{
    public sealed partial class SettingsPage : Page
    {
        private readonly AppSettingsService _settings = AppSettingsService.Instance;
        private bool _syncing;
        private string _lastAppliedLanguageTag = "system";

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += SettingsPage_Loaded;
            Unloaded += SettingsPage_Unloaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MainViewModel vm)
            {
                DataContext = vm;
            }

            SyncControls();
            ApplyLocalizedText();
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
            _settings.SettingsChanged += Settings_SettingsChanged;
            SyncControls();
            ApplyLocalizedText();
        }

        private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
            _settings.SettingsChanged -= Settings_SettingsChanged;
        }

        private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
        {
            ApplyLocalizedText();
        }

        private void Settings_SettingsChanged(object? sender, EventArgs e)
        {
            SyncControls();
            ApplyLocalizedText();
        }

        private void SyncControls()
        {
            _syncing = true;
            try
            {
                AppThemePreference preference = _settings.ThemePreference switch
                {
                    AppThemePreference.Light => AppThemePreference.Light,
                    AppThemePreference.Dark => AppThemePreference.Dark,
                    _ => AppThemePreference.System
                };

                ThemeSystemRadio.IsChecked = preference == AppThemePreference.System;
                ThemeLightRadio.IsChecked = preference == AppThemePreference.Light;
                ThemeDarkRadio.IsChecked = preference == AppThemePreference.Dark;
                ExperimentalFeaturesToggleSwitch.IsOn = _settings.ExperimentalFeaturesEnabled;
                PlaybackAutoScrollToggleSwitch.IsOn = _settings.PlaybackAutoScrollEnabled;

                string targetLang = _settings.LanguageTag;
                foreach (object obj in LanguageComboBox.Items)
                {
                    if (obj is ComboBoxItem item
                        && string.Equals(item.Tag?.ToString(), targetLang, StringComparison.OrdinalIgnoreCase))
                    {
                        LanguageComboBox.SelectedItem = item;
                        _lastAppliedLanguageTag = targetLang;
                        break;
                    }
                }
            }
            finally
            {
                _syncing = false;
            }
        }

        private void ApplyLocalizedText()
        {
            bool isEnglish = IsEnglishUi();
            AppBuildInfo buildInfo = AppBuildInfoService.GetCurrent();

            PageTitleText.Text = isEnglish ? "Settings" : "\u8bbe\u7f6e";
            PersonalizationTitleText.Text = isEnglish ? "Personalization" : "\u4e2a\u6027\u5316";
            ThemeTitleText.Text = isEnglish ? "Theme" : "\u4e3b\u9898";
            ThemeDescText.Text = isEnglish ? "Choose app appearance theme" : "\u9009\u62e9\u5e94\u7528\u7684\u5916\u89c2\u4e3b\u9898";
            ThemeSystemRadio.Content = isEnglish ? "Use system setting" : "\u8ddf\u968f\u7cfb\u7edf";
            ThemeLightRadio.Content = isEnglish ? "Light" : "\u6d45\u8272";
            ThemeDarkRadio.Content = isEnglish ? "Dark" : "\u6df1\u8272";
            UpdateThemeSummaryText(isEnglish);

            LanguageTitleText.Text = isEnglish ? "Language" : "\u8bed\u8a00";
            LanguageDescText.Text = isEnglish ? "Change display language" : "\u5207\u6362\u754c\u9762\u663e\u793a\u8bed\u8a00";
            FeaturesSectionTitleText.Text = isEnglish ? "Software Features" : "\u8f6f\u4ef6\u529f\u80fd";
            AboutSectionTitleText.Text = isEnglish ? "About" : "\u5173\u4e8e";

            LabsTitleText.Text = isEnglish ? "Experimental Features" : "\u5b9e\u9a8c\u5ba4\u529f\u80fd";
            LabsDescText.Text = isEnglish
                ? "Show unfinished preview features when available"
                : "\u663e\u793a\u53ef\u7528\u7684\u9884\u89c8\u529f\u80fd";
            ExperimentalFeaturesToggleSwitch.OnContent = isEnglish ? "On" : "\u5f00";
            ExperimentalFeaturesToggleSwitch.OffContent = isEnglish ? "Off" : "\u5173";

            PlaybackAutoScrollTitleText.Text = isEnglish ? "Auto Scroll During Staff Playback" : "\u4e94\u7ebf\u8c31\u64ad\u653e\u65f6\u81ea\u52a8\u6eda\u52a8";
            PlaybackAutoScrollDescText.Text = isEnglish
                ? "Keep the staff view following the playback cursor automatically"
                : "\u64ad\u653e\u65f6\u8ba9\u4e94\u7ebf\u8c31\u89c6\u56fe\u81ea\u52a8\u8ddf\u968f\u64ad\u653e\u5149\u6807";
            PlaybackAutoScrollToggleSwitch.OnContent = isEnglish ? "On" : "\u5f00";
            PlaybackAutoScrollToggleSwitch.OffContent = isEnglish ? "Off" : "\u5173";

            foreach (object obj in LanguageComboBox.Items)
            {
                if (obj is not ComboBoxItem item)
                {
                    continue;
                }

                string tag = item.Tag?.ToString() ?? string.Empty;
                item.Content = tag switch
                {
                    "system" => isEnglish ? "Use system setting" : "\u8ddf\u968f\u7cfb\u7edf",
                    "zh-Hans" => isEnglish ? "Chinese (Simplified)" : "\u7b80\u4f53\u4e2d\u6587",
                    "en-US" => "English",
                    _ => tag
                };
            }

            AboutHeaderText.Text = isEnglish ? "MusicBox" : "\u97f3\u4e50\u9b54\u76d2\uff08MusicBox\uff09";
            AboutVersionLabelText.Text = isEnglish ? "Version" : "\u7248\u672c\u53f7";
            AboutBuildLabelText.Text = isEnglish ? "Build Number" : "\u6784\u5efa\u53f7";
            AboutAuthorLabelText.Text = isEnglish ? "Author" : "\u4f5c\u8005";
            UpdateTitleText.Text = isEnglish ? "Software Update" : "\u8f6f\u4ef6\u66f4\u65b0";
            if (string.IsNullOrWhiteSpace(UpdateStatusText.Text))
            {
                UpdateStatusText.Text = isEnglish ? "Click the button to check for updates" : "\u70b9\u51fb\u6309\u94ae\u68c0\u67e5\u66f4\u65b0";
            }
            CheckUpdateButton.Content = isEnglish ? "Check for updates" : "\u68c0\u67e5\u66f4\u65b0";
            AboutVersionValueText.Text = buildInfo.VersionDisplay;
            AboutBuildValueText.Text = buildInfo.BuildNumber;
            AboutAuthorValueText.Text = "Rylan";

            WarningTitleText.Text = isEnglish ? "Work in Progress" : "\u5f00\u53d1\u4e2d";
            WarningBodyText.Text = isEnglish
                ? "This project is currently under active development and some features may be incomplete or unstable."
                : "\u8fd9\u4e2a\u9879\u76ee\u76ee\u524d\u4ecd\u5728\u6301\u7eed\u5f00\u53d1\u4e2d\uff0c\u90e8\u5206\u529f\u80fd\u53ef\u80fd\u5c1a\u672a\u5b8c\u6210\u6216\u4e0d\u592a\u7a33\u5b9a\u3002";
        }

        private void UpdateThemeSummaryText(bool isEnglish)
        {
            ThemeSummaryText.Text = _settings.ThemePreference switch
            {
                AppThemePreference.Light => isEnglish ? "Light" : "\u6d45\u8272",
                AppThemePreference.Dark => isEnglish ? "Dark" : "\u6df1\u8272",
                _ => isEnglish ? "Use system setting" : "\u8ddf\u968f\u7cfb\u7edf"
            };
        }

        private bool IsEnglishUi()
        {
            return _settings.ResolveLanguageTag().StartsWith("en", StringComparison.OrdinalIgnoreCase);
        }

        private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncing)
            {
                return;
            }

            if (ThemeLightRadio.IsChecked == true)
            {
                _settings.ThemePreference = AppThemePreference.Light;
            }
            else if (ThemeDarkRadio.IsChecked == true)
            {
                _settings.ThemePreference = AppThemePreference.Dark;
            }
            else
            {
                _settings.ThemePreference = AppThemePreference.System;
            }
        }

        private void ExperimentalFeaturesToggleSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing)
            {
                return;
            }

            _settings.ExperimentalFeaturesEnabled = ExperimentalFeaturesToggleSwitch.IsOn;
        }

        private void PlaybackAutoScrollToggleSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing)
            {
                return;
            }

            _settings.PlaybackAutoScrollEnabled = PlaybackAutoScrollToggleSwitch.IsOn;
        }

        private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing)
            {
                return;
            }

            if (LanguageComboBox.SelectedItem is not ComboBoxItem item)
            {
                return;
            }

            string tag = item.Tag?.ToString() ?? "system";
            if (string.Equals(tag, _settings.LanguageTag, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            bool restartNow = await ConfirmLanguageRestartAsync().ConfigureAwait(true);
            if (!restartNow)
            {
                SelectLanguageItem(_lastAppliedLanguageTag);
                return;
            }

            _settings.LanguageTag = tag;
            _lastAppliedLanguageTag = _settings.LanguageTag;
            RestartApplication();
        }

        private async System.Threading.Tasks.Task<bool> ConfirmLanguageRestartAsync()
        {
            if (XamlRoot == null)
            {
                return true;
            }

            bool isEnglish = IsEnglishUi();
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = isEnglish ? "Restart Required" : "\u9700\u8981\u91cd\u542f",
                Content = isEnglish
                    ? "Changing display language requires a restart. Unsaved changes may be lost. Restart now?"
                    : "\u4fee\u6539\u754c\u9762\u8bed\u8a00\u9700\u8981\u91cd\u542f\u5e94\u7528\uff0c\u672a\u4fdd\u5b58\u5185\u5bb9\u53ef\u80fd\u4e22\u5931\u3002\u73b0\u5728\u91cd\u542f\u5417\uff1f",
                PrimaryButtonText = isEnglish ? "Restart now" : "\u7acb\u5373\u91cd\u542f",
                CloseButtonText = isEnglish ? "Later" : "\u7a0d\u540e",
                DefaultButton = ContentDialogButton.Close
            };

            ContentDialogResult result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        private void SelectLanguageItem(string tag)
        {
            _syncing = true;
            try
            {
                foreach (object obj in LanguageComboBox.Items)
                {
                    if (obj is ComboBoxItem item
                        && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                    {
                        LanguageComboBox.SelectedItem = item;
                        return;
                    }
                }
            }
            finally
            {
                _syncing = false;
            }
        }

        private static void RestartApplication()
        {
            try
            {
                string? executable = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(executable))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = executable,
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
            }

            Application.Current?.Exit();
        }

        private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            bool isEnglish = IsEnglishUi();
            CheckUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = isEnglish ? "Checking GitHub releases..." : "\u6b63\u5728\u68c0\u67e5 GitHub Release...";

            try
            {
                AppUpdateInfo updateInfo = await GitHubUpdateService.Instance.CheckAsync().ConfigureAwait(true);
                await HandleUpdateResultAsync(updateInfo).ConfigureAwait(true);
            }
            finally
            {
                if (Application.Current != null)
                {
                    CheckUpdateButton.IsEnabled = true;
                }
            }
        }

        private async Task HandleUpdateResultAsync(AppUpdateInfo updateInfo)
        {
            bool isEnglish = IsEnglishUi();
            switch (updateInfo.State)
            {
                case AppUpdateState.Current:
                    string latestCurrentVersion = GetDisplayUpdateVersion(updateInfo);
                    UpdateStatusText.Text = isEnglish
                        ? $"Latest version: {latestCurrentVersion}. No update available."
                        : $"\u6700\u65b0\u7248\u672c\uff1a{latestCurrentVersion}\uff0c\u6682\u65e0\u66f4\u65b0\u3002";
                    break;

                case AppUpdateState.UpdateAvailable:
                    string latestRemoteVersion = GetDisplayUpdateVersion(updateInfo);
                    UpdateStatusText.Text = isEnglish
                        ? $"Latest version: {latestRemoteVersion}. Update required."
                        : $"\u6700\u65b0\u7248\u672c\uff1a{latestRemoteVersion}\uff0c\u9700\u8981\u66f4\u65b0\u3002";

                    if (await ConfirmInstallUpdateAsync(updateInfo).ConfigureAwait(true))
                    {
                        await InstallUpdateAsync(updateInfo).ConfigureAwait(true);
                    }
                    break;

                case AppUpdateState.NoReleaseFound:
                    UpdateStatusText.Text = isEnglish
                        ? "No compatible win-x64 update package was found in GitHub releases."
                        : "\u6ca1\u6709\u5728 GitHub Release \u4e2d\u627e\u5230\u53ef\u7528\u7684 win-x64 \u66f4\u65b0\u5305\u3002";
                    break;

                case AppUpdateState.UnsupportedInstall:
                    UpdateStatusText.Text = isEnglish
                        ? "This running copy cannot be updated automatically."
                        : "\u5f53\u524d\u8fd0\u884c\u526f\u672c\u65e0\u6cd5\u81ea\u52a8\u66f4\u65b0\u3002";
                    break;

                default:
                    UpdateStatusText.Text = isEnglish
                        ? $"Update check failed: {updateInfo.ErrorMessage}"
                        : $"\u68c0\u67e5\u66f4\u65b0\u5931\u8d25\uff1a{updateInfo.ErrorMessage}";
                    break;
            }
        }

        private static string GetDisplayUpdateVersion(AppUpdateInfo updateInfo)
        {
            return !string.IsNullOrWhiteSpace(updateInfo.RemoteVersion)
                ? updateInfo.RemoteVersion
                : updateInfo.CurrentVersion;
        }

        private async Task<bool> ConfirmInstallUpdateAsync(AppUpdateInfo updateInfo)
        {
            if (XamlRoot == null)
            {
                return true;
            }

            bool isEnglish = IsEnglishUi();
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = isEnglish ? "Update Available" : "\u53d1\u73b0\u65b0\u7248\u672c",
                Content = isEnglish
                    ? $"GitHub has a newer package ({updateInfo.RemoteVersion}). The app will close, replace files, and restart. Continue?"
                    : $"GitHub \u4e0a\u6709\u66f4\u65b0\u7684\u5305\uff08{updateInfo.RemoteVersion}\uff09\u3002\u5e94\u7528\u5c06\u5173\u95ed\u3001\u8986\u76d6\u6587\u4ef6\u5e76\u91cd\u65b0\u542f\u52a8\u3002\u7ee7\u7eed\u5417\uff1f",
                PrimaryButtonText = isEnglish ? "Update now" : "\u7acb\u5373\u66f4\u65b0",
                CloseButtonText = isEnglish ? "Cancel" : "\u53d6\u6d88",
                DefaultButton = ContentDialogButton.Primary
            };

            ContentDialogResult result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        private async Task InstallUpdateAsync(AppUpdateInfo updateInfo)
        {
            bool isEnglish = IsEnglishUi();
            CheckUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = isEnglish ? "Downloading update package..." : "\u6b63\u5728\u4e0b\u8f7d\u66f4\u65b0\u5305...";

            try
            {
                await GitHubUpdateService.Instance.DownloadAndInstallAsync(updateInfo).ConfigureAwait(true);
                UpdateStatusText.Text = isEnglish
                    ? "Update downloaded. The app will restart to apply it."
                    : "\u66f4\u65b0\u5305\u5df2\u4e0b\u8f7d\uff0c\u5373\u5c06\u91cd\u542f\u5e76\u8986\u76d6\u7a0b\u5e8f\u6587\u4ef6\u3002";
                Application.Current?.Exit();
            }
            catch (Exception ex)
            {
                CheckUpdateButton.IsEnabled = true;
                UpdateStatusText.Text = isEnglish
                    ? $"Update failed: {ex.Message}"
                    : $"\u66f4\u65b0\u5931\u8d25\uff1a{ex.Message}";
            }
        }
    }
}

