using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClipAssist.Models;

/// <summary>
/// 一条剪贴板文本记录。
/// CreatedAt 是第一次出现的时间，UpdatedAt 是最近一次出现的时间：
/// 同一段文本再次被复制时，只更新 UpdatedAt 并把记录移到顶部，不新建记录。
/// </summary>
public sealed class ClipboardEntry : INotifyPropertyChanged
{
    private DateTime _updatedAt = DateTime.Now;

    /// <summary>唯一标识。M3 持久化后用它来识别同一条记录。</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>完整文本。搜索、复制、持久化都基于这个字段。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>第一次记录的时间。</summary>
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    /// <summary>
    /// 最近一次复制的时间。重复文本被提升到顶部时会修改它，
    /// 因此这里要触发属性通知，让列表上的时间跟着刷新。
    /// </summary>
    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set
        {
            if (_updatedAt == value)
            {
                return;
            }

            _updatedAt = value;
            OnPropertyChanged(nameof(UpdatedAt));
            OnPropertyChanged(nameof(TimeText));
        }
    }

    /// <summary>列表只显示一行：把换行/制表符折叠成空格。</summary>
    public string Preview => Text
        .Replace("\r\n", " ")
        .Replace('\n', ' ')
        .Replace('\r', ' ')
        .Replace('\t', ' ');

    /// <summary>列表右侧的时间文本（最近一次复制时间）。</summary>
    public string TimeText => UpdatedAt.ToString("MM-dd HH:mm");

    /// <summary>UI 自动化/读屏软件读取列表项时使用的内容。</summary>
    public override string ToString() => Preview;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}