using System.Globalization;

namespace ClipAssist.Localization;

/// <summary>
/// The two interface languages supported by ClipAssist.
/// The enum values are stable; the values persisted in settings are in
/// <see cref="LanguagePreferenceExtensions.ToStorageValue"/>.
/// </summary>
public enum LanguagePreference
{
    ChineseSimplified,
    English,
}

public static class LanguagePreferenceExtensions
{
    public const string ChineseSimplifiedStorageValue = "zh-CN";
    public const string EnglishStorageValue = "en-US";

    public static string ToStorageValue(this LanguagePreference language) =>
        language == LanguagePreference.English
            ? EnglishStorageValue
            : ChineseSimplifiedStorageValue;

    /// <summary>
    /// Parses persisted or system language names. Unknown values are rejected so
    /// callers can treat them as "not chosen yet" and show the language picker.
    /// </summary>
    public static bool TryParse(string? value, out LanguagePreference language)
    {
        language = LanguagePreference.ChineseSimplified;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "zh":
            case "zh-cn":
            case "zh-hans":
            case "zh-hans-cn":
            case "chinesesimplified":
                language = LanguagePreference.ChineseSimplified;
                return true;
            case "en":
            case "en-us":
            case "en-gb":
            case "english":
                language = LanguagePreference.English;
                return true;
            default:
                return false;
        }
    }

    public static CultureInfo ToCultureInfo(this LanguagePreference language) =>
        language == LanguagePreference.English
            ? CultureInfo.GetCultureInfo("en-US")
            : CultureInfo.GetCultureInfo("zh-CN");

    /// <summary>
    /// Autonyms are intentionally shown in their own language in the picker,
    /// e.g. "简体中文" and "English", regardless of the current UI language.
    /// </summary>
    public static string ToDisplayName(this LanguagePreference language) =>
        language == LanguagePreference.English ? "English" : "简体中文";

    public static LanguagePreference FromSystemCulture(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        return string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase)
            ? LanguagePreference.ChineseSimplified
            : LanguagePreference.English;
    }
}