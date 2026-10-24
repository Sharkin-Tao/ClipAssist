using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using ClipAssist.Models;

namespace ClipAssist.Services;

/// <summary>一次待提交的快捷键注册（尚未替换当前生效组合）。</summary>
public sealed record HotkeyRegistration(int Id, HotkeyGesture Gesture);

/// <summary>
/// 全局快捷键服务（Win32 RegisterHotKey）。
///
/// 关键设计：用两个 hotkey id 做“先注册新、成功后再注销旧”的事务。
/// 这样新组合被占用时，旧快捷键完全不受影响，满足“注册失败不覆盖旧值”。
/// 窗口是 HWND_MESSAGE 消息窗口，创建和销毁都必须在 WPF UI 线程。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int FirstHotkeyId = 0x4341;  // 'CA'
    private const int SecondHotkeyId = 0x4342;
    private const int ModNoRepeat = 0x4000;    // 按住不放时不重复触发
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private static readonly IntPtr HwndMessage = new(-3);

    private HwndSource? _source;
    private HotkeyRegistration? _current;
    private HotkeyRegistration? _pending;
    private int _currentId = FirstHotkeyId;
    private int _pendingId = SecondHotkeyId;
    private bool _disposed;

    /// <summary>快捷键被按下。事件在 UI 线程触发。</summary>
    public event EventHandler? Pressed;

    public bool IsRegistered => _current is not null;

    /// <summary>当前真正注册成功的组合键；失败时为 null。</summary>
    public HotkeyGesture? CurrentGesture => _current?.Gesture;

    public string? RegisteredGesture => _current?.Gesture.DisplayText;

    /// <summary>最近一次注册失败的原因；成功时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 尝试注册一个“待提交”组合。此时当前生效组合仍然保留。
    /// 返回 null 表示失败，失败原因见 LastError。
    /// </summary>
    public HotkeyRegistration? TryBeginRegistration(HotkeyGesture gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureWindow();

        if (!gesture.IsValid)
        {
            LastError = "快捷键组合不合法。";
            return null;
        }

        if (_current?.Gesture == gesture)
        {
            LastError = $"快捷键 {gesture.DisplayText} 已经生效，无需重复注册。";
            return null;
        }

        // 同一时间只允许一个待提交注册；上一次未提交的先清理掉。
        if (_pending is not null)
        {
            CancelRegistration(_pending);
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        if (virtualKey == 0)
        {
            LastError = "无法把这个按键转换为系统虚拟键。";
            return null;
        }

        var flags = ToWin32Modifiers(gesture.Modifiers) | ModNoRepeat;

        if (!RegisterHotKey(_source!.Handle, _pendingId, flags, virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            LastError = error == ErrorHotkeyAlreadyRegistered
                ? $"快捷键 {gesture.DisplayText} 已被其他程序占用。"
                : $"注册快捷键 {gesture.DisplayText} 失败（错误 {error}：{new Win32Exception(error).Message}）。";
            return null;
        }

        var registration = new HotkeyRegistration(_pendingId, gesture);
        _pending = registration;
        LastError = null;
        return registration;
    }

    /// <summary>
    /// 提交待注册组合：注销旧组合，把新组合设为当前生效。
    /// 若旧组合注销失败，会取消新组合并保留旧组合，避免同时存在两个全局快捷键。
    /// </summary>
    public bool CommitRegistration(HotkeyRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pending is null || _pending.Id != registration.Id)
        {
            throw new InvalidOperationException("没有匹配的待提交快捷键注册。");
        }

        if (_current is not null &&
            !UnregisterHotKey(_source!.Handle, _currentId))
        {
            var error = Marshal.GetLastWin32Error();
            UnregisterHotKey(_source!.Handle, registration.Id);
            _pending = null;
            LastError = $"注销旧快捷键失败（错误 {error}），已保留旧快捷键。";
            return false;
        }

        _current = registration;
        _currentId = registration.Id;
        _pending = null;
        _pendingId = OtherId(_currentId);
        LastError = null;
        return true;
    }

    /// <summary>放弃待注册组合，当前生效组合保持不变。</summary>
    public void CancelRegistration(HotkeyRegistration registration)
    {
        if (registration is null || _pending is null || _pending.Id != registration.Id)
        {
            return;
        }

        if (_source is not null)
        {
            UnregisterHotKey(_source.Handle, registration.Id);
        }

        _pending = null;
    }

    /// <summary>注销所有快捷键（退出或异常清理）。</summary>
    public void UnregisterAll()
    {
        if (_source is not null)
        {
            if (_current is not null)
            {
                UnregisterHotKey(_source.Handle, _currentId);
            }

            if (_pending is not null)
            {
                UnregisterHotKey(_source.Handle, _pendingId);
            }
        }

        _current = null;
        _pending = null;
        _currentId = FirstHotkeyId;
        _pendingId = SecondHotkeyId;
    }

    private void EnsureWindow()
    {
        if (_source is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("ClipAssist.HotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = HwndMessage,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            handled = true;

            // 只有当前生效 id 才触发；待提交 id 在替换窗口期即使被按下也不重复触发。
            if (_current is not null && wParam.ToInt32() == _currentId)
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    private static int OtherId(int id) =>
        id == FirstHotkeyId ? SecondHotkeyId : FirstHotkeyId;

    private static int ToWin32Modifiers(ModifierKeys modifiers)
    {
        var result = 0;

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= 0x0001;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= 0x0002;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= 0x0004;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            result |= 0x0008;
        }

        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}