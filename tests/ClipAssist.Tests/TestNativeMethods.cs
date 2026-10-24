using System.Runtime.InteropServices;

namespace ClipAssist.Tests;

internal static class TestNativeMethods
{
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
}