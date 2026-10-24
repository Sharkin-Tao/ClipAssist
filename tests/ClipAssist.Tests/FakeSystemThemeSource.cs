using ClipAssist.Models;
using ClipAssist.Services;

namespace ClipAssist.Tests;

/// <summary>单元测试用的系统主题源；不读写真实注册表。</summary>
public sealed class FakeSystemThemeSource : ISystemThemeSource
{
    public FakeSystemThemeSource(ThemePreference current)
    {
        Current = current;
    }

    public ThemePreference Current { get; set; }

    public ThemePreference GetSystemTheme() => Current;
}