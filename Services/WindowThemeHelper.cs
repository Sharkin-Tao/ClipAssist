using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipAssist.Services;

/// <summary>
/// 尽力把 Windows 11 的标题栏切到深色。DWM 属性不支持或调用失败时静默忽略，
/// 窗口内容主题仍然正常，不影响应用启动。
/// </summary>
public static class WindowThemeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    public static void Apply(Window window, bool dark)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var value = dark ? 1 : 0;

        // Windows 11 使用属性 20；更早的 Windows 10 使用 19。
        // 这里只做 best-effort，不把未验证的系统称为已支持。
        if (TrySet(handle, DwmwaUseImmersiveDarkMode, value))
        {
            return;
        }

        TrySet(handle, DwmwaUseImmersiveDarkModeBefore20H1, value);
    }

    private static bool TrySet(IntPtr handle, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException
            or EntryPointNotFoundException
            or ExternalException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);
}