using System.Windows;
using ClipAssist.Models;

namespace ClipAssist.Services;

/// <summary>主题切换 + 写盘结果；失败时调用方应保留/恢复原选择。</summary>
public sealed record ThemeApplyResult(bool Success, string? Error);

/// <summary>
/// 主题编排：决定实际浅/深色，替换颜色资源字典，并通知窗口更新标题栏。
/// 用户选择的 System 不会被注册表读取结果覆盖；注册表失败时仅实际主题回退浅色。
/// </summary>
public sealed class ThemeService
{
    private readonly ISystemThemeSource _systemThemeSource;
    private readonly Application? _application;
    private ResourceDictionary? _currentColors;
    private ThemePreference _selectedMode = ThemePreference.System;
    private ThemePreference _effectiveTheme = ThemePreference.Light;

    public ThemeService(ISystemThemeSource? systemThemeSource = null, Application? application = null)
    {
        _systemThemeSource = systemThemeSource ?? new RegistrySystemThemeSource();
        _application = application ?? Application.Current;
    }

    public ThemePreference SelectedMode => _selectedMode;

    public ThemePreference EffectiveTheme => _effectiveTheme;

    public bool IsDark => _effectiveTheme == ThemePreference.Dark;

    /// <summary>应用启动时调用：加载保存的模式并立即替换资源字典。</summary>
    public void Initialize(ThemePreference selectedMode)
    {
        _selectedMode = selectedMode;
        _effectiveTheme = ResolveEffective(selectedMode);
        ReplaceColorDictionary(_effectiveTheme);
        ApplyTitleBars();
    }

    /// <summary>应用设置窗口里的选择；只改内存和资源，不负责写盘。</summary>
    public void Apply(ThemePreference selectedMode)
    {
        _selectedMode = selectedMode;
        var effective = ResolveEffective(selectedMode);

        if (effective != _effectiveTheme || _currentColors is null)
        {
            _effectiveTheme = effective;
            ReplaceColorDictionary(effective);
            ApplyTitleBars();
        }
    }

    /// <summary>
    /// System 模式下收到 Windows 主题变化后重新读取并应用。
    /// 返回 true 表示实际浅/深色发生变化。
    /// </summary>
    public bool RefreshSystemTheme()
    {
        if (_selectedMode != ThemePreference.System)
        {
            return false;
        }

        var effective = _systemThemeSource.GetSystemTheme();
        if (effective == _effectiveTheme && _currentColors is not null)
        {
            return false;
        }

        _effectiveTheme = effective;
        ReplaceColorDictionary(effective);
        ApplyTitleBars();
        return true;
    }

    /// <summary>窗口创建后补一次标题栏主题（此时才拿到 HWND）。</summary>
    public void ApplyWindowChrome(Window window)
    {
        WindowThemeHelper.Apply(window, IsDark);
    }

    private ThemePreference ResolveEffective(ThemePreference selectedMode) =>
        selectedMode == ThemePreference.System
            ? _systemThemeSource.GetSystemTheme()
            : selectedMode;

    private void ReplaceColorDictionary(ThemePreference effective)
    {
        if (_application is null)
        {
            return;
        }

        var dictionary = new ResourceDictionary
        {
            Source = CreateColorDictionaryUri(effective),
        };

        var dictionaries = _application.Resources.MergedDictionaries;
        if (_currentColors is not null)
        {
            var index = dictionaries.IndexOf(_currentColors);
            if (index >= 0)
            {
                dictionaries[index] = dictionary;
                _currentColors = dictionary;
                return;
            }
        }

        // App.xaml 中颜色字典固定在最前面；这里再按 Source 查找一次，避免依赖硬编码索引。
        var existingIndex = -1;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is not null &&
                source.Contains("ThemeColors.", StringComparison.OrdinalIgnoreCase))
            {
                existingIndex = i;
                break;
            }
        }

        if (existingIndex >= 0)
        {
            dictionaries[existingIndex] = dictionary;
        }
        else
        {
            dictionaries.Insert(0, dictionary);
        }

        _currentColors = dictionary;
    }

    private static Uri CreateColorDictionaryUri(ThemePreference effective)
    {
        var name = effective == ThemePreference.Dark ? "Dark" : "Light";
        var assembly = typeof(ThemeService).Assembly.GetName().Name;
        return new Uri(
            $"pack://application:,,,/{assembly};component/Styles/ThemeColors.{name}.xaml",
            UriKind.Absolute);
    }

    private void ApplyTitleBars()
    {
        if (_application is null)
        {
            return;
        }

        foreach (Window window in _application.Windows)
        {
            WindowThemeHelper.Apply(window, IsDark);
        }
    }
}