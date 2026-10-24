namespace ClipAssist.Models;

/// <summary>
/// settings.json 里的快捷键设置。
/// 只存规范化后的文本，例如 "Ctrl+Shift+V"；不存 Win32 修饰位，
/// 这样文件可读，也便于未来迁移和在设置界面直接显示。
/// </summary>
public sealed class HotkeySettings
{
    public string Gesture { get; set; } = HotkeyGesture.Default.DisplayText;
}