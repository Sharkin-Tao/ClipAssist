using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ClipAssist.Infrastructure;
using ClipAssist.Models;
using ClipAssist.Services;
using ClipAssist.Views;

namespace ClipAssist.ViewModels;

/// <summary>
/// 主窗口 ViewModel：只负责“展示与交互”，数据从 ClipboardHistory 来、
/// 剪贴板读写通过 ClipboardService 完成，自身不直接调用 Win32。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ClipboardHistory _history;
    private readonly ClipboardService _clipboardService;
    private readonly Func<string, string, bool> _confirmDelete;
    private readonly Func<Action<bool>?, Task<HistorySaveResult>> _saveHistory;
    private readonly ObservableCollection<ClipboardEntry> _selectedEntries = new();

    private string _searchText = string.Empty;
    private ClipboardEntry? _selectedItem;
    private string _statusText = string.Empty;
    private string _emptyHintText = string.Empty;
    private bool _isListEmpty;
    private bool _isSelectionMode;
    private bool _deleteInProgress;

    public MainViewModel(
        ClipboardHistory history,
        ClipboardService clipboardService,
        string? initialNotice = null,
        Func<string, string, bool>? confirmDelete = null,
        Func<Action<bool>?, Task<HistorySaveResult>>? saveHistory = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _confirmDelete = confirmDelete ?? ConfirmDeleteWithDialog;
        _saveHistory = saveHistory ?? SaveHistoryInMemory;

        _history.Changed += OnHistoryChanged;
        _selectedEntries.CollectionChanged += OnSelectedEntriesChanged;

        // 默认视图 + 过滤谓词：搜索只影响显示，不修改历史数据。
        Items = CollectionViewSource.GetDefaultView(_history.Entries);
        Items.Filter = MatchesSearch;

        CopyCommand = new RelayCommand(CopySelected, () => SelectedItem is not null);
        ClearCommand = new RelayCommand(
            ClearAll,
            () => !_deleteInProgress && _history.Entries.Count > 0);
        DeleteEntryCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is ClipboardEntry entry)
                {
                    _ = DeleteEntryAsync(entry);
                }
            },
            parameter => parameter is ClipboardEntry && !_deleteInProgress);
        BatchDeleteCommand = new RelayCommand(
            () => _ = DeleteSelectedAsync(),
            () => !_deleteInProgress && IsSelectionMode && SelectedCount > 0);

        RefreshView();

        // 文件损坏、读取失败等启动提示，放在统计信息之后展示。
        if (!string.IsNullOrWhiteSpace(initialNotice))
        {
            StatusText = initialNotice;
        }
    }

    /// <summary>请求界面清空 ListBox 的多选集合（例如搜索条件改变或退出多选）。</summary>
    public event EventHandler? SelectionResetRequested;

    private static bool ConfirmDeleteWithDialog(string title, string message)
    {
        var owner = Application.Current?.Windows
            .Cast<Window>()
            .FirstOrDefault(window => window.IsActive)
            ?? Application.Current?.MainWindow;

        // 标题已经区分操作类型；按钮文案只在这里映射，不改动确认委托签名。
        var confirmText = title.StartsWith("清空", StringComparison.Ordinal)
            ? "清空历史"
            : title.Contains("选中", StringComparison.Ordinal)
                ? "删除选中"
                : "删除";

        return ConfirmDialog.Show(owner, title, message, confirmText);
    }

    private static Task<HistorySaveResult> SaveHistoryInMemory(Action<bool>? afterSave)
    {
        // 单元测试/无持久化调用时的默认实现：内存删除视为成功。
        afterSave?.Invoke(true);
        return Task.FromResult(new HistorySaveResult(true, null));
    }

    /// <summary>在状态栏显示一条临时提示（保存失败、文件损坏等）。</summary>
    public void ShowNotice(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText = message;
        }
    }

    /// <summary>经过搜索过滤后的视图，绑定给 ListBox。</summary>
    public ICollectionView Items { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                // 搜索条件变化后旧选择可能被隐藏；无论单选还是多选都清空，
                // 防止多选里残留看不见的旧记录被批量删除。
                ClearSelection();
                RefreshView();
            }
        }
    }

    public ClipboardEntry? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        private set
        {
            if (!SetProperty(ref _isSelectionMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ListSelectionMode));
            OnPropertyChanged(nameof(SelectionModeButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>ListBox 的 SelectionMode；单条复制时仍保持单选。</summary>
    public SelectionMode ListSelectionMode =>
        IsSelectionMode ? SelectionMode.Multiple : SelectionMode.Single;

    public string SelectionModeButtonText => IsSelectionMode ? "退出多选" : "多选";

    public int SelectedCount => _selectedEntries.Count;

    public bool HasSelection => SelectedCount > 0;

    public string SelectionCountText => $"已选 {SelectedCount} 条 · 仅当前显示结果";

    /// <summary>当前过滤后的可见记录；不包含被搜索隐藏的记录。</summary>
    public IReadOnlyList<ClipboardEntry> VisibleEntries =>
        Items.Cast<ClipboardEntry>().ToList();

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string EmptyHintText
    {
        get => _emptyHintText;
        private set => SetProperty(ref _emptyHintText, value);
    }

    public bool IsListEmpty
    {
        get => _isListEmpty;
        private set => SetProperty(ref _isListEmpty, value);
    }

    public ICommand CopyCommand { get; }

    public ICommand ClearCommand { get; }

    public ICommand DeleteEntryCommand { get; }

    public ICommand BatchDeleteCommand { get; }

    /// <summary>搜索谓词：忽略大小写，先去掉首尾空格。</summary>
    private bool MatchesSearch(object item) =>
        item is ClipboardEntry entry && ClipboardSearchMatcher.Matches(entry, _searchText);

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        // 新记录可能影响“清空”按钮是否可用；删除/恢复也可能让已选 ID 失效。
        PruneSelection();
        CommandManager.InvalidateRequerySuggested();
        RefreshView();
    }

    private void OnSelectedEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionCountText));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>由主窗口在 ListBox.SelectionChanged 后把选中项同步进来。</summary>
    public void UpdateSelectedEntries(IEnumerable<ClipboardEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var current = entries
            .Where(entry => _history.Entries.Contains(entry))
            .Distinct()
            .ToList();

        if (_selectedEntries.SequenceEqual(current))
        {
            return;
        }

        _selectedEntries.Clear();
        foreach (var entry in current)
        {
            _selectedEntries.Add(entry);
        }
    }

    public void ToggleSelectionMode() => SetSelectionMode(!IsSelectionMode);

    public void SetSelectionMode(bool enabled)
    {
        if (IsSelectionMode == enabled)
        {
            return;
        }

        if (!enabled)
        {
            // 在切回单选模式前清空原生 SelectedItems，否则 ListBox 会拒绝在单选模式中改多选集合。
            ClearSelection();
        }

        IsSelectionMode = enabled;
    }

    /// <summary>清空多选并通知主窗口清除 ListBox 的原生选中集合。</summary>
    public void ClearSelection()
    {
        if (_selectedEntries.Count > 0)
        {
            _selectedEntries.Clear();
        }

        if (SelectedItem is not null)
        {
            SelectedItem = null;
        }

        SelectionResetRequested?.Invoke(this, EventArgs.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    private void PruneSelection()
    {
        var stale = _selectedEntries
            .Where(entry => !_history.Entries.Contains(entry))
            .ToList();

        foreach (var entry in stale)
        {
            _selectedEntries.Remove(entry);
        }
    }

    private void RefreshView()
    {
        Items.Refresh();

        var total = _history.Entries.Count;
        var visible = _history.Entries.Count(entry => MatchesSearch(entry));

        IsListEmpty = visible == 0;
        EmptyHintText = total == 0
            ? "还没有记录。复制一段文字试试。"
            : "没有匹配的历史记录";

        if (total == 0)
        {
            StatusText = "暂无记录";
            return;
        }

        StatusText = string.IsNullOrWhiteSpace(_searchText)
            ? $"共 {total} 条记录 · 上限 {_history.MaxEntries}"
            : $"匹配 {visible} / {total} 条 · 上限 {_history.MaxEntries}";
    }

    public async Task DeleteEntryAsync(ClipboardEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        await DeleteEntriesAsync(new[] { entry }, batch: false);
    }

    public async Task DeleteSelectedAsync()
    {
        var targets = _selectedEntries.ToList();
        if (targets.Count == 0)
        {
            return;
        }

        await DeleteEntriesAsync(targets, batch: true);
    }

    private async Task DeleteEntriesAsync(
        IReadOnlyList<ClipboardEntry> targets,
        bool batch,
        bool clearAll = false)
    {
        if (_deleteInProgress || targets.Count == 0)
        {
            return;
        }

        var ids = targets
            .Select(entry => entry.Id)
            .Where(id => _history.Entries.Any(entry => entry.Id == id))
            .ToHashSet();

        if (ids.Count == 0)
        {
            StatusText = "要删除的记录已不存在。";
            return;
        }

        var count = ids.Count;
        bool confirmed;

        try
        {
            confirmed = clearAll
                ? _confirmDelete(
                    "清空剪贴板历史？",
                    "此操作将删除全部剪贴板历史记录，请确认是否继续。\n\n删除后无法恢复。")
                : batch
                    ? _confirmDelete(
                        "删除选中的记录？",
                        $"确定删除选中的 {count} 条记录吗？\n\n" +
                        "只删除当前选中的记录，不会影响未选中的记录。\n删除后无法恢复。")
                    : _confirmDelete(
                        "删除这条记录？",
                        "确认后将删除这条剪贴板历史记录。\n\n" +
                        $"{PreviewForDialog(targets[0].Text)}\n\n删除后无法恢复。");
        }
        catch (Exception ex)
        {
            StatusText = $"删除确认失败：{ex.Message}";
            return;
        }

        if (!confirmed)
        {
            return;
        }

        _deleteInProgress = true;
        CommandManager.InvalidateRequerySuggested();

        var removed = _history.RemoveByIds(ids);
        if (removed.Count == 0)
        {
            _deleteInProgress = false;
            StatusText = "要删除的记录已不存在。";
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        var restored = false;
        var committed = false;

        try
        {
            var saveResult = await _saveHistory(success =>
            {
                if (success)
                {
                    return;
                }

                // 在保存锁释放前恢复，防止排队中的后台保存把失败状态写进文件。
                _history.Restore(removed);
                restored = true;
            });

            if (!saveResult.Success)
            {
                if (!restored)
                {
                    _history.Restore(removed);
                }

                StatusText = saveResult.Error ?? "删除失败：历史记录保存失败，已恢复记录。";
                return;
            }

            // 到这里文件已经写入成功，后续界面清理即使异常也不能回滚，
            // 否则会出现“界面有记录、文件已删除”的不一致。
            committed = true;

            if (batch || clearAll)
            {
                if (IsSelectionMode)
                {
                    SetSelectionMode(false);
                }
                else
                {
                    ClearSelection();
                }
            }
            else if (SelectedItem is not null &&
                     removed.Any(item => ReferenceEquals(item.Entry, SelectedItem)))
            {
                SelectedItem = null;
            }

            StatusText = clearAll
                ? $"已清空 {count} 条历史记录"
                : count == 1 ? "已删除 1 条记录" : $"已删除 {count} 条记录";
        }
        catch (Exception ex)
        {
            if (!committed && !restored)
            {
                _history.Restore(removed);
            }

            StatusText = committed
                ? $"已删除 {count} 条记录，但界面选择状态更新失败：{ex.Message}"
                : $"删除失败：{ex.Message}";
        }
        finally
        {
            _deleteInProgress = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static string PreviewForDialog(string text)
    {
        var preview = text
            .Replace("\r\n", " ")
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Replace('\t', ' ');

        return preview.Length <= 100 ? preview : preview[..100] + "…";
    }

    private void CopySelected() => CopyEntry(SelectedItem);

    /// <summary>
    /// 复制指定记录。工具栏“复制”按钮和列表双击共用这一个入口：
    /// 复制源永远是完整 Text，不使用列表展示用的 Preview。
    /// </summary>
    public void CopyEntry(ClipboardEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.Text))
        {
            StatusText = "复制失败：这条记录没有可复制的文本。";
            return;
        }

        // 双击时也让选中状态跟上，按钮可用性和视觉选中保持一致。
        SelectedItem = entry;

        try
        {
            // 由 ClipboardService 负责“抑制自己写入造成的重复记录”。
            _clipboardService.CopyToClipboard(entry.Text);
            StatusText = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            // 先给出明确失败提示，再弹出具体原因；绝不显示“已复制”。
            StatusText = "复制失败：剪贴板可能被其他程序占用。";
            MessageBox.Show(
                $"写入剪贴板失败：{ex.Message}",
                "ClipAssist",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ClearAll()
    {
        if (_history.Entries.Count == 0)
        {
            return;
        }

        // 清空也走同一套“先确认、再删除、保存失败回滚”的事务，
        // 避免保存失败时界面显示已清空、重启后记录又出现。
        var targets = _history.Entries.ToList();
        _ = DeleteEntriesAsync(targets, batch: false, clearAll: true);
    }
}