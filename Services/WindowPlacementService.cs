using System.Windows;
using ClipAssist.Models;

namespace ClipAssist.Services;

/// <summary>
/// 窗口位置/大小的校验与恢复。
///
/// 保存的是普通状态下的外框（DIP），不保存最大化状态：
/// 最大化或最小化时取 RestoreBounds，下次启动仍以普通窗口打开。
///
/// 恢复时的安全策略：
/// 1. 数值必须是有限数，否则视为无效（居中）；
/// 2. 尺寸不小于窗口最小尺寸，不大于当前虚拟桌面；
/// 3. 与虚拟桌面交集不足 80×40 视为“看不见”（如显示器被拔掉），改为居中；
/// 4. 其余情况把窗口整体收回虚拟桌面内，避免标题栏或大部分内容留在屏幕外。
/// </summary>
public static class WindowPlacementService
{
    public const double DefaultWidth = 720;
    public const double DefaultHeight = 520;
    public const double MinVisibleWidth = 80;
    public const double MinVisibleHeight = 40;

    /// <summary>当前虚拟桌面（所有显示器合起来）的 DIP 边界。</summary>
    public static Rect VirtualScreen => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    /// <summary>主显示器工作区（扣除任务栏）的 DIP 边界。</summary>
    public static Rect PrimaryWorkArea => SystemParameters.WorkArea;

    /// <summary>
    /// 读取窗口当前应保存的位置。
    /// 窗口还没真正显示或数值不可用时返回 null，避免把无效数据写进设置。
    /// </summary>
    public static WindowPlacement? Capture(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var bounds = window.RestoreBounds;

        if (!IsUsable(bounds))
        {
            // RestoreBounds 在窗口尚未显示时可能是空矩形；退回当前属性值。
            var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

            if (!double.IsFinite(window.Left) || !double.IsFinite(window.Top) ||
                !double.IsFinite(width) || !double.IsFinite(height))
            {
                return null;
            }

            bounds = new Rect(window.Left, window.Top, width, height);
        }

        if (!IsUsable(bounds))
        {
            return null;
        }

        return new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = Math.Max(window.MinWidth, bounds.Width),
            Height = Math.Max(window.MinHeight, bounds.Height),
        };
    }

    /// <summary>
    /// 校验保存过的位置。返回 null 表示没有可用位置，调用方应让窗口居中。
    /// 传入的矩形用 DIP；virtualScreen 是整个虚拟桌面，workArea 是主显示器工作区。
    /// </summary>
    public static WindowPlacement? Sanitize(
        WindowPlacement? saved,
        double minWidth,
        double minHeight,
        Rect virtualScreen,
        Rect workArea)
    {
        if (saved is null)
        {
            return null;
        }

        if (!double.IsFinite(saved.Left) || !double.IsFinite(saved.Top) ||
            !double.IsFinite(saved.Width) || !double.IsFinite(saved.Height))
        {
            return null;
        }

        var maxWidth = Math.Max(minWidth, virtualScreen.Width);
        var maxHeight = Math.Max(minHeight, virtualScreen.Height);

        var width = Clamp(saved.Width, minWidth, maxWidth);
        var height = Clamp(saved.Height, minHeight, maxHeight);

        var left = saved.Left;
        var top = saved.Top;

        var visible = Rect.Intersect(new Rect(left, top, width, height), virtualScreen);

        if (visible.IsEmpty || visible.Width < MinVisibleWidth || visible.Height < MinVisibleHeight)
        {
            // 完全或几乎不可见：回到主显示器工作区中央。
            var centerArea = workArea.Width > 0 && workArea.Height > 0
                ? workArea
                : virtualScreen;

            left = centerArea.Left + ((centerArea.Width - width) / 2);
            top = centerArea.Top + ((centerArea.Height - height) / 2);
        }

        // 最终保证整个窗口都在虚拟桌面内（部分越界也直接收回）。
        left = Math.Clamp(
            left,
            virtualScreen.Left,
            Math.Max(virtualScreen.Left, virtualScreen.Right - width));

        top = Math.Clamp(
            top,
            virtualScreen.Top,
            Math.Max(virtualScreen.Top, virtualScreen.Bottom - height));

        return new WindowPlacement
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
        };
    }

    /// <summary>把校验后的位置应用到窗口；null 表示让窗口居中。</summary>
    public static void Apply(Window window, WindowPlacement? placement)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (placement is null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.WindowState = WindowState.Normal;
        window.Width = placement.Width;
        window.Height = placement.Height;
        window.Left = placement.Left;
        window.Top = placement.Top;
    }

    public static bool IsUsable(Rect rect) =>
        !rect.IsEmpty
        && double.IsFinite(rect.Left)
        && double.IsFinite(rect.Top)
        && double.IsFinite(rect.Width)
        && double.IsFinite(rect.Height)
        && rect.Width >= 1
        && rect.Height >= 1;

    private static double Clamp(double value, double min, double max)
    {
        if (max < min)
        {
            max = min;
        }

        return Math.Clamp(value, min, max);
    }
}