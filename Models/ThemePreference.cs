namespace ClipAssist.Models;

/// <summary>
/// 用户选择的主题模式。System 表示跟随 Windows 的应用主题。
/// 存储时使用稳定的小写字符串，不把枚举序号写进配置文件。
/// </summary>
public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public static class ThemePreferenceExtensions
{
    public const string SystemStorageValue = "system";
    public const string LightStorageValue = "light";
    public const string DarkStorageValue = "dark";

    public static string ToStorageValue(this ThemePreference mode) => mode switch
    {
        ThemePreference.Light => LightStorageValue,
        ThemePreference.Dark => DarkStorageValue,
        _ => SystemStorageValue,
    };

    public static bool TryParse(string? value, out ThemePreference mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case LightStorageValue:
                mode = ThemePreference.Light;
                return true;
            case DarkStorageValue:
                mode = ThemePreference.Dark;
                return true;
            case SystemStorageValue:
                mode = ThemePreference.System;
                return true;
            default:
                mode = ThemePreference.System;
                return false;
        }
    }

    public static string ToDisplayName(this ThemePreference mode) => mode switch
    {
        ThemePreference.Light => "浅色",
        ThemePreference.Dark => "深色",
        _ => "跟随系统",
    };
}