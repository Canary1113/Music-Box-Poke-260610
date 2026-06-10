using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.Storage;
using Windows.System.UserProfile;
using MusicBox.ViewModels;

namespace MusicBox.Services
{
    public enum AppThemePreference
    {
        System,
        Light,
        Dark
    }

    public sealed class AppSettingsService : ObservableObject
    {
        private const string ThemeKey = "AppThemePreference";
        private const string LanguageKey = "AppLanguageTag";
        private const string ExperimentalFeaturesKey = "ExperimentalFeaturesEnabled";
        private const string PlaybackAutoScrollKey = "PlaybackAutoScrollEnabled";
        private const string SystemLanguage = "system";
        private static readonly Lazy<AppSettingsService> LazyInstance = new(() => new AppSettingsService());
        private readonly ApplicationDataContainer? _settings;
        private readonly string _fallbackPath;
        private readonly Dictionary<string, string> _fallbackValues = new(StringComparer.OrdinalIgnoreCase);

        private AppThemePreference _themePreference;
        private string _languageTag = SystemLanguage;
        private bool _experimentalFeaturesEnabled;
        private bool _playbackAutoScrollEnabled = true;

        public static AppSettingsService Instance => LazyInstance.Value;

        private AppSettingsService()
        {
            _settings = TryGetLocalSettings();
            _fallbackPath = BuildFallbackPath();
            if (_settings == null)
            {
                LoadFallbackValues();
            }

            _themePreference = ParseThemePreference(GetSettingValue(ThemeKey));
            _languageTag = ParseLanguageTag(GetSettingValue(LanguageKey));
            _experimentalFeaturesEnabled = ParseBoolSetting(GetSettingValue(ExperimentalFeaturesKey));
            _playbackAutoScrollEnabled = ParseBoolSetting(GetSettingValue(PlaybackAutoScrollKey), defaultValue: true);
        }

        public event EventHandler? SettingsChanged;

        public AppThemePreference ThemePreference
        {
            get => _themePreference;
            set
            {
                if (SetProperty(ref _themePreference, value))
                {
                    SetSettingValue(ThemeKey, value.ToString());
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string LanguageTag
        {
            get => _languageTag;
            set
            {
                string parsed = ParseLanguageTag(value);
                if (SetProperty(ref _languageTag, parsed))
                {
                    SetSettingValue(LanguageKey, parsed);
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public bool ExperimentalFeaturesEnabled
        {
            get => _experimentalFeaturesEnabled;
            set
            {
                if (SetProperty(ref _experimentalFeaturesEnabled, value))
                {
                    SetSettingValue(ExperimentalFeaturesKey, value.ToString());
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public bool PlaybackAutoScrollEnabled
        {
            get => _playbackAutoScrollEnabled;
            set
            {
                if (SetProperty(ref _playbackAutoScrollEnabled, value))
                {
                    SetSettingValue(PlaybackAutoScrollKey, value.ToString());
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public ElementTheme ResolveElementTheme()
        {
            return ThemePreference switch
            {
                AppThemePreference.Light => ElementTheme.Light,
                AppThemePreference.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }

        public string ResolveLanguageTag()
        {
            if (!string.Equals(LanguageTag, SystemLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeLanguageTag(LanguageTag);
            }

            string? systemTag = GlobalizationPreferences.Languages.FirstOrDefault();
            return NormalizeLanguageTag(systemTag);
        }

        private static AppThemePreference ParseThemePreference(string? value)
        {
            if (Enum.TryParse(value, true, out AppThemePreference parsed))
            {
                return parsed;
            }

            return AppThemePreference.System;
        }

        private static string ParseLanguageTag(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return SystemLanguage;
            }

            string trimmed = value.Trim();
            if (string.Equals(trimmed, SystemLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return SystemLanguage;
            }

            return NormalizeLanguageTag(trimmed);
        }

        private static bool ParseBoolSetting(string? value)
        {
            return bool.TryParse(value, out bool parsed) && parsed;
        }

        private static bool ParseBoolSetting(string? value, bool defaultValue)
        {
            return bool.TryParse(value, out bool parsed) ? parsed : defaultValue;
        }

        private static string NormalizeLanguageTag(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "zh-Hans";
            }

            string normalized = raw.Trim().ToLowerInvariant();
            if (normalized.StartsWith("zh", StringComparison.Ordinal))
            {
                return "zh-Hans";
            }

            if (normalized.StartsWith("en", StringComparison.Ordinal))
            {
                return "en-US";
            }

            return "en-US";
        }

        private static ApplicationDataContainer? TryGetLocalSettings()
        {
            try
            {
                return ApplicationData.Current.LocalSettings;
            }
            catch
            {
                return null;
            }
        }

        private static string BuildFallbackPath()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "MusicBox", "appsettings.json");
        }

        private string? GetSettingValue(string key)
        {
            if (_settings != null)
            {
                try
                {
                    return _settings.Values[key]?.ToString();
                }
                catch
                {
                    return null;
                }
            }

            return _fallbackValues.TryGetValue(key, out string? value) ? value : null;
        }

        private void SetSettingValue(string key, string value)
        {
            if (_settings != null)
            {
                try
                {
                    _settings.Values[key] = value;
                    return;
                }
                catch
                {
                }
            }

            _fallbackValues[key] = value;
            SaveFallbackValues();
        }

        private void LoadFallbackValues()
        {
            try
            {
                if (!File.Exists(_fallbackPath))
                {
                    return;
                }

                string json = File.ReadAllText(_fallbackPath);
                Dictionary<string, string>? data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (data == null)
                {
                    return;
                }

                _fallbackValues.Clear();
                foreach (KeyValuePair<string, string> entry in data)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Key))
                    {
                        _fallbackValues[entry.Key] = entry.Value ?? string.Empty;
                    }
                }
            }
            catch
            {
            }
        }

        private void SaveFallbackValues()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_fallbackPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(_fallbackValues);
                File.WriteAllText(_fallbackPath, json);
            }
            catch
            {
            }
        }
    }
}
