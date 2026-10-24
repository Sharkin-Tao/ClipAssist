using System.Collections.ObjectModel;
using ClipAssist.Models;

namespace ClipAssist.Services;

/// <summary>
/// 删除记录时保留原始位置，保存失败后可以原位恢复。
/// </summary>
public sealed record RemovedHistoryEntry(int Index, ClipboardEntry Entry);

/// <summary>
/// 剪贴板历史的数据管理者，只负责“存哪些、怎么排序、怎么去重、怎么裁剪”，
/// 不碰剪贴板 API，也不碰界面。这样以后做持久化时只需要替换这一层的数据来源。
/// </summary>
public sealed class ClipboardHistory
{
    /// <summary>默认上限：最多保留 500 条，超出时删除最旧的记录。</summary>
    public const int DefaultMaxEntries = 500;

    private readonly ObservableCollection<ClipboardEntry> _entries = new();

    public ClipboardHistory(int maxEntries = DefaultMaxEntries)
    {
        MaxEntries = Math.Max(1, maxEntries);
        Entries = new ReadOnlyObservableCollection<ClipboardEntry>(_entries);
    }

    /// <summary>只读视图，界面绑定它，外部无法直接增删。</summary>
    public ReadOnlyObservableCollection<ClipboardEntry> Entries { get; }

    public int MaxEntries { get; }

    /// <summary>集合内容发生变化时触发，供 ViewModel 刷新统计信息。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 加入一段文本：已存在则更新时间并移到顶部，不存在则新建。
    /// 空白文本会被忽略并返回 null。
    /// </summary>
    public ClipboardEntry? AddOrPromote(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var existing = _entries.FirstOrDefault(
            entry => string.Equals(entry.Text, text, StringComparison.Ordinal));

        if (existing is not null)
        {
            _entries.Remove(existing);
            existing.UpdatedAt = DateTime.Now;
            _entries.Insert(0, existing);

            Changed?.Invoke(this, EventArgs.Empty);
            return existing;
        }

        var now = DateTime.Now;
        var created = new ClipboardEntry
        {
            Text = text,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _entries.Insert(0, created);
        TrimToLimit();

        Changed?.Invoke(this, EventArgs.Empty);
        return created;
    }

    public void Clear()
    {
        if (_entries.Count == 0)
        {
            return;
        }

        _entries.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 用加载到的记录整体替换内容（启动时调用）。
    /// 保留重复文本：它们可能拥有不同的 ID、时间和状态，不能用文本代替 ID。
    /// 空白记录会被过滤，超过上限时只保留前面的记录。
    /// </summary>
    public void ReplaceAll(IEnumerable<ClipboardEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _entries.Clear();

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Text))
            {
                continue;
            }

            _entries.Add(entry);

            if (_entries.Count >= MaxEntries)
            {
                break;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 按稳定 ID 删除记录，返回被删除记录及其原始索引。
    /// 不按文本匹配，因此重复文本中的其他记录不会被误删。
    /// </summary>
    public IReadOnlyList<RemovedHistoryEntry> RemoveByIds(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var idSet = ids.ToHashSet();
        if (idSet.Count == 0)
        {
            return Array.Empty<RemovedHistoryEntry>();
        }

        var removed = new List<RemovedHistoryEntry>();

        // 从后往前删除，前面的索引在删除过程中保持有效。
        for (var index = _entries.Count - 1; index >= 0; index--)
        {
            var entry = _entries[index];
            if (!idSet.Contains(entry.Id))
            {
                continue;
            }

            removed.Add(new RemovedHistoryEntry(index, entry));
            _entries.RemoveAt(index);
        }

        if (removed.Count == 0)
        {
            return Array.Empty<RemovedHistoryEntry>();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return removed.OrderBy(item => item.Index).ToList();
    }

    /// <summary>保存失败时按原索引把记录放回，内容、时间和对象状态保持不变。</summary>
    public void Restore(IReadOnlyList<RemovedHistoryEntry> removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        if (removed.Count == 0)
        {
            return;
        }

        foreach (var item in removed.OrderBy(entry => entry.Index))
        {
            var index = Math.Clamp(item.Index, 0, _entries.Count);
            _entries.Insert(index, item.Entry);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>超出上限时从尾部（最旧）开始删除。</summary>
    private void TrimToLimit()
    {
        while (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(_entries.Count - 1);
        }
    }
}