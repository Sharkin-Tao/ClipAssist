using System.Globalization;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClipAssist.Localization;

/// <summary>
/// Single source of truth for all user-visible ClipAssist strings.
/// XAML uses <c>{loc:Loc Key}</c>; code uses <see cref="Get"/> or <see cref="Format"/>.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    private LanguagePreference _currentLanguage;
    private bool _initialized;

    private LocalizationService()
    {
        // Default to Chinese for deterministic, backwards-compatible strings in
        // unit tests and non-UI callers. The app explicitly applies the system
        // language during startup and the first-run picker suggests it.
        _currentLanguage = LanguagePreference.ChineseSimplified;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public LanguagePreference CurrentLanguage => _currentLanguage;

    public CultureInfo Culture => _currentLanguage.ToCultureInfo();

    /// <summary>Every key known to the catalog, for diagnostics and tests.</summary>
    public IReadOnlyCollection<string> Keys => LocalizationCatalog.Keys;

    /// <summary>
    /// Indexer used by WPF bindings produced by <see cref="LocExtension"/>.
    /// Raising PropertyChanged for "Item[]" refreshes every such binding.
    /// </summary>
    public string this[string key] => Get(key);

    public string Get(string key) => Get(_currentLanguage, key);

    /// <summary>
    /// Looks up a string for a specific language without changing the current
    /// UI language. Used by the first-run picker to preview both choices.
    /// </summary>
    public string Get(LanguagePreference language, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var table = LocalizationCatalog.For(language);
        if (table.TryGetValue(key, out var value))
        {
            return value;
        }

        // A missing translation must never produce a blank UI. Fall back to the
        // other language, then to the key so the defect is visible in testing.
        if (LocalizationCatalog.For(Other(language)).TryGetValue(key, out value))
        {
            return value;
        }

        return key;
    }

    public string Format(string key, params object?[] args) =>
        string.Format(Culture, Get(key), args);

    public void SetLanguage(LanguagePreference language)
    {
        if (_initialized && language == _currentLanguage)
        {
            return;
        }

        _currentLanguage = language;
        _initialized = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Used only by tests to exercise a clean first-run state without depending
    /// on the machine's UI culture.
    /// </summary>
    public void ResetForTests(LanguagePreference language)
    {
        _currentLanguage = language;
        _initialized = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static LanguagePreference Other(LanguagePreference language) =>
        language == LanguagePreference.English
            ? LanguagePreference.ChineseSimplified
            : LanguagePreference.English;
}

/// <summary>Short alias for code-behind lookups.</summary>
public static class L
{
    public static string Get(string key) => LocalizationService.Instance.Get(key);

    public static string Format(string key, params object?[] args) =>
        LocalizationService.Instance.Format(key, args);
}