using ClipAssist.Models;

namespace ClipAssist.Services;

/// <summary>
/// 主窗口和浮窗共用的搜索规则：忽略大小写，先去掉查询首尾空格。
/// 只负责判断文本是否匹配，不持有搜索状态或历史数据。
/// </summary>
internal static class ClipboardSearchMatcher
{
    public static bool Matches(ClipboardEntry entry, string? searchText)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return entry.Text.Contains(searchText.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}