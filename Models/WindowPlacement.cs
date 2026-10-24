namespace ClipAssist.Models;

/// <summary>
/// 窗口在“普通状态”下的外框位置与大小，单位是 WPF 逻辑像素（DIP）。
/// 不保存最大化状态：最大化时记录的是 RestoreBounds，下次仍以普通窗口打开。
/// </summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; } = 720;

    public double Height { get; set; } = 520;
}