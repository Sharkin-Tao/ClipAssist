namespace ClipAssist.Models;

/// <summary>
/// 主题设置。Mode 缺失或非法时由 SettingsValidator 回退到 System。
/// </summary>
public sealed class ThemeSettings
{
    public string Mode { get; set; } = ThemePreference.System.ToStorageValue();
}