namespace ClipAssist.Controls;

/// <summary>
/// 一段预览文本：IsMatch 为 true 时表示这段需要高亮。
/// Text 始终来自原始预览文本，不会改成搜索词本身。
/// </summary>
public readonly record struct HighlightSegment(string Text, bool IsMatch);

/// <summary>
/// 把预览文本按搜索词切成普通段/高亮段。
/// 只使用字面量 IndexOf，不使用正则，所以括号、点号等特殊字符不需要转义。
/// </summary>
public static class TextHighlighter
{
    public static IReadOnlyList<HighlightSegment> Split(string? text, string? searchText)
    {
        var source = text ?? string.Empty;
        var term = searchText?.Trim() ?? string.Empty;

        if (source.Length == 0)
        {
            return Array.Empty<HighlightSegment>();
        }

        if (term.Length == 0)
        {
            return new[] { new HighlightSegment(source, false) };
        }

        var segments = new List<HighlightSegment>();
        var position = 0;

        while (position < source.Length)
        {
            var matchIndex = source.IndexOf(term, position, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                segments.Add(new HighlightSegment(source[position..], false));
                break;
            }

            if (matchIndex > position)
            {
                segments.Add(new HighlightSegment(source[position..matchIndex], false));
            }

            // 高亮段保留原文大小写，长度使用搜索词长度。
            segments.Add(new HighlightSegment(source.Substring(matchIndex, term.Length), true));
            position = matchIndex + term.Length;
        }

        return segments;
    }
}