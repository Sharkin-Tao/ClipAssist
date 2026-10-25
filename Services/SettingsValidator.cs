using ClipAssist.Localization;
using ClipAssist.Models;

namespace ClipAssist.Services;

/// <summary>校验结果；Changed 表示内存中的设置需要写回文件。</summary>
public sealed record SettingsValidationResult(
    AppSettings Settings,
    bool Changed,
    string? Warning);

/// <summary>
/// 设置模型的语义校验。
/// 屏幕尺寸/多显示器相关校验仍交给 WindowPlacementService，
/// 这里只负责判断数值是否能安全地交给窗口层。
/// </summary>
public static class SettingsValidator
{
    public static SettingsValidationResult Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var changed = false;
        string? warning = null;

        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
        {
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            changed = true;
        }

        if (settings.Window is not null)
        {
            var window = settings.Window;
            var valid = double.IsFinite(window.Left)
                && double.IsFinite(window.Top)
                && double.IsFinite(window.Width)
                && double.IsFinite(window.Height)
                && window.Width > 0
                && window.Height > 0;

            if (!valid)
            {
                settings.Window = null;
                changed = true;
                warning = AppendWarning(warning, L.Get("Settings.Warning.WindowInvalid"));
            }
        }

        if (settings.Hotkey is null)
        {
            settings.Hotkey = new HotkeySettings();
            changed = true;
            warning = AppendWarning(warning, L.Get("Settings.Warning.HotkeyMissing"));
        }
        else if (!HotkeyGesture.TryParse(settings.Hotkey.Gesture, out var hotkey, out var hotkeyError))
        {
            settings.Hotkey = new HotkeySettings();
            changed = true;
            warning = AppendWarning(
                warning,
                L.Format("Settings.Warning.HotkeyInvalid", hotkeyError));
        }
        else if (!string.Equals(settings.Hotkey.Gesture, hotkey.DisplayText, StringComparison.Ordinal))
        {
            settings.Hotkey.Gesture = hotkey.DisplayText;
            changed = true;
        }

        if (settings.Focus is null)
        {
            settings.Focus = new FocusSettings();
            changed = true;
            warning = AppendWarning(warning, L.Get("Settings.Warning.FocusMissing"));
        }

        if (settings.Theme is null)
        {
            settings.Theme = new ThemeSettings();
            changed = true;
            warning = AppendWarning(warning, L.Get("Settings.Warning.ThemeMissing"));
        }
        else if (!ThemePreferenceExtensions.TryParse(settings.Theme.Mode, out var theme))
        {
            settings.Theme.Mode = ThemePreference.System.ToStorageValue();
            changed = true;
            warning = AppendWarning(warning, L.Get("Settings.Warning.ThemeInvalid"));
        }
        else if (!string.Equals(settings.Theme.Mode, theme.ToStorageValue(), StringComparison.Ordinal))
        {
            settings.Theme.Mode = theme.ToStorageValue();
            changed = true;
        }

        if (settings.Language is null)
        {
            settings.Language = new LanguageSettings();
            changed = true;
        }
        else if (!string.IsNullOrWhiteSpace(settings.Language.Mode))
        {
            if (!LanguagePreferenceExtensions.TryParse(settings.Language.Mode, out var language))
            {
                settings.Language.Mode = null;
                changed = true;
                warning = AppendWarning(warning, L.Get("Settings.Warning.LanguageInvalid"));
            }
            else if (!string.Equals(settings.Language.Mode, language.ToStorageValue(), StringComparison.Ordinal))
            {
                settings.Language.Mode = language.ToStorageValue();
                changed = true;
            }
        }
        else if (settings.Language.Mode is not null)
        {
            settings.Language.Mode = null;
            changed = true;
        }

        return new SettingsValidationResult(settings, changed, warning);
    }

    private static string? AppendWarning(string? existing, string message) =>
        string.IsNullOrWhiteSpace(existing) ? message : existing + " " + message;
}