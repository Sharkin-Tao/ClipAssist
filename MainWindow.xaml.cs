using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClipAssist.Models;
using ClipAssist.ViewModels;

namespace ClipAssist;

/// <summary>
/// 主窗口。业务逻辑都在 MainViewModel 中，这里只处理“窗口怎么显示”这一件事。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 请求真正退出程序（由 Ctrl+Q 触发）。
    /// 关闭按钮仍然只是隐藏到托盘，两者含义不同。
    /// </summary>
    public event EventHandler? ExitRequested;

    /// <summary>请求打开设置窗口（主窗口只负责发信号，不直接写配置）。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>请求打开或激活精简浮窗（窗口生命周期由 App 管理）。</summary>
    public event EventHandler? FloatingModeRequested;

    private bool _pendingSearchFocus;
    private bool _selectSearchTextOnFocus = true;
    private bool _syncingSelection;
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.SelectionResetRequested -= OnSelectionResetRequested;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.SelectionResetRequested += OnSelectionResetRequested;
        }
    }

    private void OnSelectionResetRequested(object? sender, EventArgs e)
    {
        ClearListSelection();
    }

    /// <summary>
    /// 双击历史记录：复制被双击的那一条。
    /// 只处理左键；单击选中仍交给 ListBox 默认行为，不在这里处理。
    /// 用 sender 的 DataContext 而不是 SelectedItem，避免快速连续双击时
    /// 选中项尚未同步而复制到上一条。
    /// </summary>
    private void OnHistoryItemMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (DataContext is MainViewModel { IsSelectionMode: true })
        {
            // 多选状态下的双击容易误触复制；选择由单击/勾选框负责。
            return;
        }

        if (sender is not ListBoxItem { DataContext: ClipboardEntry entry })
        {
            return;
        }

        if (DataContext is MainViewModel viewModel)
        {
            // 一次双击只走一个入口；标记已处理，避免同一次手势再触发其他双击逻辑。
            viewModel.CopyEntry(entry);
            e.Handled = true;
        }
    }

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.UpdateSelectedEntries(HistoryList.SelectedItems.OfType<ClipboardEntry>());
    }

    private void OnToggleSelectionModeClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.ToggleSelectionMode();

        if (viewModel.IsSelectionMode)
        {
            viewModel.UpdateSelectedEntries(HistoryList.SelectedItems.OfType<ClipboardEntry>());
        }
        else
        {
            ClearListSelection();
        }
    }

    private void OnSelectAllVisibleClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.IsSelectionMode)
        {
            return;
        }

        // 绑定刚切换时 ListBox 可能还没进入 Multiple；此时不能改 SelectedItems。
        if (HistoryList.SelectionMode != SelectionMode.Multiple)
        {
            return;
        }

        var entries = viewModel.VisibleEntries;
        var wasSyncing = _syncingSelection;
        _syncingSelection = true;

        try
        {
            HistoryList.SelectedItems.Clear();
            foreach (var entry in entries)
            {
                HistoryList.SelectedItems.Add(entry);
            }
        }
        finally
        {
            _syncingSelection = wasSyncing;
        }

        viewModel.UpdateSelectedEntries(HistoryList.SelectedItems.OfType<ClipboardEntry>());
    }

    private void OnClearSelectionClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        ClearListSelection();
        viewModel.ClearSelection();
    }

    private void ClearListSelection()
    {
        var wasSyncing = _syncingSelection;
        _syncingSelection = true;

        try
        {
            if (HistoryList.SelectionMode == SelectionMode.Multiple)
            {
                HistoryList.SelectedItems.Clear();
            }
            else
            {
                // 单选模式不能修改 SelectedItems；清空 SelectedItem 即可。
                HistoryList.SelectedItem = null;
            }
        }
        finally
        {
            _syncingSelection = wasSyncing;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnFloatingModeClick(object sender, RoutedEventArgs e)
    {
        FloatingModeRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 从托盘、浮窗的展开按钮或第二实例恢复窗口：显示、取消最小化、抢到前台，并把焦点放进搜索框，
    /// 让用户不用再点一下就能直接输入。
    ///
    /// 焦点不在 Show()/Activate() 之后立刻设置：窗口可能还没完成显示或激活，
    /// 这时 TextBox.Focus() 会失败。这里先登记“待聚焦”，由 Activated 或
    /// Dispatcher(Input) 在窗口真正可用后执行；启动时不会调用本方法。
    /// </summary>
    public void ShowAndActivate(bool selectSearchTextOnActivate)
    {
        _pendingSearchFocus = true;
        _selectSearchTextOnFocus = selectSearchTextOnActivate;

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        // 先置顶再取消：用于绕过 Windows 对后台程序抢前台的限制。
        Topmost = true;
        Topmost = false;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (_pendingSearchFocus && IsVisible && IsActive)
                {
                    ApplySearchFocus();
                }
            }));
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        if (_pendingSearchFocus && IsLoaded)
        {
            ApplySearchFocus();
        }
    }

    /// <summary>
    /// 真正把键盘焦点交给搜索框。只改变焦点/选区，不改 SearchBox.Text，
    /// 因此不会触发布局过滤或破坏当前搜索状态。
    /// </summary>
    private void ApplySearchFocus()
    {
        if (!_pendingSearchFocus || !IsLoaded || !IsVisible)
        {
            return;
        }

        SearchBox.Focus();
        Keyboard.Focus(SearchBox);

        if (!SearchBox.IsKeyboardFocused)
        {
            // 窗口还没真正拿到前台时不要硬抢；保留待聚焦状态，等 Activated 再试。
            return;
        }

        _pendingSearchFocus = false;

        if (_selectSearchTextOnFocus)
        {
            SearchBox.SelectAll();
        }
        else
        {
            SearchBox.Select(SearchBox.Text.Length, 0);
        }
    }
}