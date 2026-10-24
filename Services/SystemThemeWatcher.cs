using System.Windows.Interop;
using System.Windows.Threading;

namespace ClipAssist.Services;

/// <summary>
/// 监听 Windows 主题变化消息。使用隐藏的顶层窗口（不是 HWND_MESSAGE），
/// 因为 WM_SETTINGCHANGE 的系统广播只发送给顶层窗口；不依赖轮询。
/// 事件通过 Dispatcher 回到 UI 线程，只负责通知，是否应用由 ThemeService 决定。
/// </summary>
public sealed class SystemThemeWatcher : IDisposable
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_THEMECHANGED = 0x031A;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;

    private HwndSource? _source;
    private Dispatcher? _dispatcher;
    private bool _refreshScheduled;
    private bool _disposed;

    public event EventHandler? Changed;

    internal IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    public SystemThemeWatcher()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            var parameters = new HwndSourceParameters("ClipAssist.ThemeWatcher")
            {
                Width = 0,
                Height = 0,
                WindowStyle = WsPopup,
                ExtendedWindowStyle = WsExToolWindow,
                ParentWindow = IntPtr.Zero,
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or ArgumentException)
        {
            // 监听窗口创建失败时应用仍可启动；此时系统主题变化不会自动刷新。
            _source = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is not WM_SETTINGCHANGE and not WM_THEMECHANGED)
        {
            return IntPtr.Zero;
        }

        if (_refreshScheduled || _dispatcher is null)
        {
            return IntPtr.Zero;
        }

        _refreshScheduled = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshScheduled = false;
            if (!_disposed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }));

        return IntPtr.Zero;
    }
}