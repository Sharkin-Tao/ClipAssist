using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using ClipAssist.Services;

namespace ClipAssist.Views;

/// <summary>
/// 与主界面共用主题资源的轻量确认窗口。
/// 只负责收集用户确认结果；删除、清空和持久化仍由 MainViewModel 执行。
/// </summary>
public partial class ConfirmDialog : Window
{
    private bool _confirmed;

    public ConfirmDialog(string title, string message, string confirmText)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("确认窗口标题不能为空。", nameof(title));
        }

        if (string.IsNullOrWhiteSpace(confirmText))
        {
            throw new ArgumentException("确认按钮文字不能为空。", nameof(confirmText));
        }

        InitializeComponent();

        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message ?? string.Empty;
        ConfirmButton.Content = confirmText;
        AutomationProperties.SetName(ConfirmButton, confirmText);

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// 以主窗口为 Owner 居中显示；没有可用 Owner 时退回屏幕中央。
    /// 只有用户点击确认按钮才返回 true，关闭、Esc、取消都返回 false。
    /// </summary>
    public static bool Show(Window? owner, string title, string message, string confirmText)
    {
        var dialog = new ConfirmDialog(title, message, confirmText);

        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 默认焦点放在取消上，避免 Enter 误删。
        CancelButton.Focus();
        Keyboard.Focus(CancelButton);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 复用应用当前的实际颜色，仅用于让原生标题栏与浅/深色内容协调。
        if (TryFindResource("WindowBackgroundBrush") is not SolidColorBrush background)
        {
            return;
        }

        var luminance =
            (0.2126 * background.Color.R) +
            (0.7152 * background.Color.G) +
            (0.0722 * background.Color.B);

        WindowThemeHelper.Apply(this, luminance < 128);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (_confirmed)
        {
            return;
        }

        _confirmed = true;
        ConfirmButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        DialogResult = true;
    }
}