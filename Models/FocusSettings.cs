namespace ClipAssist.Models;

/// <summary>
/// 窗口行为设置。
/// 当前只有“呼出时是否全选搜索框已有文本”，默认开启，保持原有手感。
/// </summary>
public sealed class FocusSettings
{
    public bool SelectSearchTextOnActivate { get; set; } = true;
}