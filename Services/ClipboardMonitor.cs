using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClipAssist.Services;

/// <summary>
/// 剪贴板变化监听器。
///
/// 原理：调用 Win32 的 AddClipboardFormatListener 注册一个“消息专用窗口”，
/// 系统在任何程序修改剪贴板后向该窗口投递 WM_CLIPBOARDUPDATE 消息。
/// 这是事件通知，不是轮询——程序空闲时不会反复读取剪贴板。
///
/// 注意：该窗口必须在有消息循环的 UI 线程（STA）上创建，
/// 因此 Start/Dispose 都只能在 WPF 主线程调用。
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    // HWND_MESSAGE：创建一个只收消息、不显示、不出现在任务栏的窗口。
    private static readonly IntPtr HwndMessage = new(-3);

    private HwndSource? _source;
    private IntPtr _handle;
    private bool _disposed;

    /// <summary>剪贴板内容发生变化（系统通知已到达）。事件在 UI 线程触发。</summary>
    public event EventHandler? ClipboardUpdated;

    public bool IsRunning => _handle != IntPtr.Zero;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            return;
        }

        var parameters = new HwndSourceParameters("ClipAssist.ClipboardMonitor")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = HwndMessage,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _handle = _source.Handle;

        if (!AddClipboardFormatListener(_handle))
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error, "注册剪贴板监听失败。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_handle);
            _handle = IntPtr.Zero;
        }

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            handled = true;
            ClipboardUpdated?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}