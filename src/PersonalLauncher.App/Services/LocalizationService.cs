using System.Windows;
using PersonalLauncher.Core.Contracts;

namespace PersonalLauncher.App.Services;

public sealed class LocalizationService : ILocalizationService
{
    private static readonly (string Code, string DisplayName)[] Languages =
    {
        ("vi-VN", "Tiếng Việt"),
        ("en-US", "English"),
        ("ja-JP", "日本語"),
        ("zh-CN", "简体中文")
    };

    private ResourceDictionary? _fallbackDict;
    private ResourceDictionary? _activeDict;

    public string CurrentLanguage { get; private set; } = "vi-VN";
    public IReadOnlyList<(string Code, string DisplayName)> SupportedLanguages => Languages;
    public event EventHandler<string>? LanguageChanged;

    public void SetLanguage(string languageCode)
    {
        string normalized = Languages.Any(l => string.Equals(l.Code, languageCode, StringComparison.OrdinalIgnoreCase))
            ? Languages.First(l => string.Equals(l.Code, languageCode, StringComparison.OrdinalIgnoreCase)).Code
            : "vi-VN";

        CurrentLanguage = normalized;

        Application? app = Application.Current;
        if (app is not null)
        {
            // Ensure English fallback dictionary is loaded first so any missing key falls back to English
            if (_fallbackDict is null)
            {
                _fallbackDict = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/PersonalLauncher;component/Resources/Localization/Strings.en-US.xaml", UriKind.Absolute)
                };
                app.Resources.MergedDictionaries.Add(_fallbackDict);
            }

            if (_activeDict is not null)
            {
                app.Resources.MergedDictionaries.Remove(_activeDict);
            }

            _activeDict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/PersonalLauncher;component/Resources/Localization/Strings.{normalized}.xaml", UriKind.Absolute)
            };
            app.Resources.MergedDictionaries.Add(_activeDict);
        }

        LanguageChanged?.Invoke(this, CurrentLanguage);
    }

    public string GetString(string key)
    {
        if (_activeDict is not null && _activeDict.Contains(key) && _activeDict[key] is string activeVal)
            return activeVal;

        if (_fallbackDict is not null && _fallbackDict.Contains(key) && _fallbackDict[key] is string fallbackVal)
            return fallbackVal;

        if (Application.Current?.TryFindResource(key) is string resVal)
            return resVal;

        return key;
    }

    public string Format(string key, params object[] args)
    {
        string template = GetString(key);
        try
        {
            return string.Format(template, args);
        }
        catch
        {
            return template;
        }
    }
}
