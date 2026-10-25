namespace ClipAssist.Models;

/// <summary>
/// 写入 settings.json 的应用设置。
///
/// 版本 3 增加全局快捷键；窗口字段继续保持兼容。
/// 呼出焦点行为和主题是同一版本内的可选字段，缺失时使用默认值。
/// 旧版本缺失的字段使用默认值，未知字段会被 JSON 读取器忽略。
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// 上次正常使用时的窗口位置。
    /// null 表示从未保存过（首次启动），此时窗口居中显示。
    /// </summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>
    /// 用户偏好的全局快捷键。即使启动时该组合暂时被占用，
    /// 也保留这里的值，不会用临时回退值覆盖。
    /// </summary>
    public HotkeySettings Hotkey { get; set; } = new();

    /// <summary>
    /// 窗口呼出时的焦点行为。缺失时默认“全选搜索框文本”。
    /// </summary>
    public FocusSettings Focus { get; set; } = new();

    /// <summary>
    /// 外观主题。缺失时默认跟随 Windows 应用主题。
    /// </summary>
    public ThemeSettings Theme { get; set; } = new();

    /// <summary>
    /// 界面语言。Mode 为空表示用户尚未选择，首次启动时显示语言选择窗口。
    /// </summary>
    public LanguageSettings Language { get; set; } = new();
}