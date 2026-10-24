using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClipAssist.Models;

namespace ClipAssist;

/// <summary>
/// 独立的精简浮窗：始终置顶，只负责搜索和浏览共享的剪贴板历史。
/// 不复制、删除、清空历史，也不创建第二份数据。
/// </summary>
public partial class FloatingWindow : Window
{
    /// <summary>请求打开或唤起原有的完整主窗口，由 App 统一处理窗口生命周期。</summary>
    public event EventHandler? ExpandRequested;

    private Point _dragStartPoint;
    private ClipboardEntry? _dragEntry;
    private ListBoxItem? _dragSourceItem;

    public FloatingWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 显示、恢复并激活浮窗，然后把焦点放到搜索框。
    /// 从主窗口再次点击浮窗按钮时也调用这里，避免重复创建窗口。
    /// </summary>
    public void ShowAndFocus()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        // 临时取消置顶再激活，随后恢复置顶；用于把已有浮窗可靠地切到前台。
        Topmost = false;
        Activate();
        Topmost = true;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(FocusSearchBox));
    }

    private void OnHistoryListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ResetDragState();

        if (e.ChangedButton != MouseButton.Left ||
            e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(HistoryList, source) is not ListBoxItem item ||
            item.DataContext is not ClipboardEntry entry)
        {
            return;
        }

        // 只记录候选，不在这里启动拖动；普通点击仍由 ListBox 完成选择。
        _dragStartPoint = e.GetPosition(HistoryList);
        _dragEntry = entry;
        _dragSourceItem = item;
    }

    private void OnHistoryListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragEntry is null || _dragSourceItem is null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetDragState();
            return;
        }

        var current = e.GetPosition(HistoryList);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // 先把候选状态取出并清空，避免 DoDragDrop 返回后再次进入拖动。
        var entry = _dragEntry;
        var source = _dragSourceItem;
        ResetDragState();

        if (string.IsNullOrEmpty(entry.Text))
        {
            return;
        }

        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, entry.Text);

        // 使用标准 OLE 拖放。拖动期间不要改变浮窗的置顶状态或 z-order：
        // 源窗口保持可见、激活和置顶，由 Windows 负责目标窗口的命中和效果协商。
        // 只传递当前记录的完整 Text，不写入系统剪贴板。
        try
        {
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            // 目标应用拒绝或 OLE 拖放失败时保持应用可用，原有功能不受影响。
        }

        e.Handled = true;
    }

    private void OnHistoryListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ResetDragState();
    }

    private void ResetDragState()
    {
        _dragEntry = null;
        _dragSourceItem = null;
    }

    private void OnExpandClick(object sender, RoutedEventArgs e)
    {
        ExpandRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FocusSearchBox();
    }

    private void FocusSearchBox()
    {
        if (!SearchBox.IsVisible)
        {
            return;
        }

        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }
}