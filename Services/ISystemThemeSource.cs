using ClipAssist.Models;

namespace ClipAssist.Services;

/// <summary>读取 Windows 当前应用主题；只在 System 模式下使用。</summary>
public interface ISystemThemeSource
{
    /// <summary>返回 Light 或 Dark；读取失败时返回 Light，不修改用户保存的模式。</summary>
    ThemePreference GetSystemTheme();
}