using System.ComponentModel;
using System.Windows.Data;
using ClipAssist.Infrastructure;
using ClipAssist.Localization;
using ClipAssist.Models;
using ClipAssist.Services;

namespace ClipAssist.ViewModels;

/// <summary>
/// 浮窗专用的轻量视图模型：只负责搜索、过滤和列表空状态。
/// 历史数据仍来自同一个 ClipboardHistory，不复制、不缓存第二份记录。
/// </summary>
public sealed class FloatingViewModel : ObservableObject
{
    private readonly ClipboardHistory _history;
    private readonly CollectionViewSource _viewSource;
    private string _searchText = string.Empty;
    private ClipboardEntry? _selectedItem;
    private bool _isListEmpty;
    private string _emptyHintText = string.Empty;

    public FloatingViewModel(ClipboardHistory history)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));

        // 独立的 CollectionView：与主窗口的筛选互不影响，但底层集合共享。
        _viewSource = new CollectionViewSource
        {
            Source = _history.Entries,
        };
        Items = _viewSource.View;
        Items.Filter = MatchesSearch;

        _history.Changed += OnHistoryChanged;
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
        RefreshView();
    }

    /// <summary>经过浮窗自身搜索条件过滤后的共享历史视图。</summary>
    public ICollectionView Items { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value))
            {
                return;
            }

            // 隐藏当前选择，避免搜索后仍保留一条看不见的旧选择。
            SelectedItem = null;
            RefreshView();
        }
    }

    public ClipboardEntry? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public bool IsListEmpty
    {
        get => _isListEmpty;
        private set => SetProperty(ref _isListEmpty, value);
    }

    public string EmptyHintText
    {
        get => _emptyHintText;
        private set => SetProperty(ref _emptyHintText, value);
    }

    private bool MatchesSearch(object item) =>
        item is ClipboardEntry entry && ClipboardSearchMatcher.Matches(entry, _searchText);

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (SelectedItem is not null && !_history.Entries.Contains(SelectedItem))
        {
            SelectedItem = null;
        }

        RefreshView();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshView();

    private void RefreshView()
    {
        Items.Refresh();

        var total = _history.Entries.Count;
        var visible = Items.Cast<ClipboardEntry>().Count();
        IsListEmpty = visible == 0;
        EmptyHintText = L.Get(total == 0 ? "Main.EmptyAll" : "Main.EmptySearch");
    }
}