using Microsoft.Win32;
using System.IO;
using System.Security;
using ClipAssist.Models;

namespace ClipAssist.Services;

/// <summary>
/// Windows 10/11 的“应用模式”由 HKCU 下的 AppsUseLightTheme 控制：
/// 1 = 浅色，0 = 深色。该值缺失或读取失败时回退为浅色，
/// 但 ThemeService 仍保留用户选择的 System，不会把模式改成 Light。
/// </summary>
public sealed class RegistrySystemThemeSource : ISystemThemeSource
{
    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    public ThemePreference GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            if (key?.GetValue(AppsUseLightThemeValue) is int value)
            {
                return value == 0 ? ThemePreference.Dark : ThemePreference.Light;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
            or SecurityException
            or IOException
            or ObjectDisposedException)
        {
            // 读取失败按浅色处理；用户保存的 System 选择保持不变。
        }

        return ThemePreference.Light;
    }
}