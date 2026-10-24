using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows.Threading;

namespace ClipAssist.Services;

/// <summary>
/// 系统托盘图标与右键菜单。
///
/// 使用 WinForms 的 NotifyIcon（.NET Windows Desktop SDK 自带，不需要额外 NuGet），
/// 由 WPF 主线程创建，事件也回到 WPF 主线程，因此可以直接操作窗口。
/// 左键单击打开浮窗，双击打开主窗口，右键显示原生菜单。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly DispatcherTimer _singleClickTimer;
    private readonly Icon _icon;
    private bool _disposed;
    private bool _backgroundHintShown;
    private long _suppressSingleClickUntil;

    /// <summary>
    /// 图标资源加载时的诊断信息。为 null 表示指定图标加载成功；
    /// 非 null 表示已退回程序图标或系统图标，主窗口会显示该提示。
    /// </summary>
    public string? Warning { get; }

    /// <summary>用户要求显示主窗口（菜单项或双击图标）。</summary>
    public event EventHandler? ShowRequested;

    /// <summary>用户要求显示浮动窗口（左键单击或菜单项）。</summary>
    public event EventHandler? FloatingRequested;

    /// <summary>用户要求真正退出程序。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>用户要求打开设置窗口。</summary>
    public event EventHandler? SettingsRequested;

    public TrayService(string? iconPath)
    {
        var openMainItem = new ToolStripMenuItem("打开主窗口");
        openMainItem.Font = new Font(openMainItem.Font, FontStyle.Bold);
        openMainItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        var openFloatingItem = new ToolStripMenuItem("打开浮动窗口");
        openFloatingItem.Click += (_, _) => FloatingRequested?.Invoke(this, EventArgs.Empty);

        var settingsItem = new ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new ToolStripMenuItem("退出 ClipAssist");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenuStrip();
        menu.Items.Add(openMainItem);
        menu.Items.Add(openFloatingItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        var notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = "ClipAssist",
        };

        // 单击和双击都由 NotifyIcon 触发；单击结果延后到系统双击间隔之后再执行。
        // 这样一次双击不会先打开浮窗、再打开主窗口。
        var singleClickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(200, SystemInformation.DoubleClickTime)),
        };

        Icon? icon = null;

        try
        {
            icon = LoadIcon(iconPath, out var warning);
            Warning = warning;

            notifyIcon.Icon = icon;
            notifyIcon.MouseClick += OnMouseClick;
            notifyIcon.DoubleClick += OnDoubleClick;
            singleClickTimer.Tick += OnSingleClickTimerTick;

            // 先准备好图标和事件，再一次性加入通知区域，避免 Visible 先于 Icon 生效。
            notifyIcon.Visible = true;

            if (!notifyIcon.Visible || notifyIcon.Icon is null)
            {
                throw new InvalidOperationException("托盘图标未进入可见状态。");
            }
        }
        catch
        {
            icon?.Dispose();
            singleClickTimer.Stop();
            singleClickTimer.Tick -= OnSingleClickTimerTick;
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            menu.Dispose();
            throw;
        }

        _menu = menu;
        _notifyIcon = notifyIcon;
        _singleClickTimer = singleClickTimer;
        _icon = icon!;
    }

    /// <summary>托盘悬停提示，顺便告知当前快捷键状态。</summary>
    public void SetTooltip(string text)
    {
        // NotifyIcon.Text 有长度限制（约 63 个字符），超出会抛异常。
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    public void ShowBalloon(string title, string text)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(4000);
    }

    /// <summary>第一次关闭到托盘时提醒一次，避免用户以为程序被关掉了。</summary>
    public void ShowBackgroundHintOnce(string text)
    {
        if (_backgroundHintShown)
        {
            return;
        }

        _backgroundHintShown = true;
        ShowBalloon("ClipAssist 仍在运行", text);
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        // 某些事件顺序下，双击的第二次 MouseClick 可能在 DoubleClick 之后到达。
        if (Environment.TickCount64 < _suppressSingleClickUntil)
        {
            return;
        }

        _singleClickTimer.Stop();
        _singleClickTimer.Start();
    }

    private void OnDoubleClick(object? sender, EventArgs e)
    {
        _singleClickTimer.Stop();
        _suppressSingleClickUntil = Environment.TickCount64 + 250;
        ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSingleClickTimerTick(object? sender, EventArgs e)
    {
        _singleClickTimer.Stop();

        if (Environment.TickCount64 < _suppressSingleClickUntil)
        {
            return;
        }

        FloatingRequested?.Invoke(this, EventArgs.Empty);
    }

    private static Icon LoadIcon(string? path, out string? warning)
    {
        warning = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            warning = "未指定托盘图标路径。";
        }
        else if (!File.Exists(path))
        {
            warning = $"未找到托盘图标：{path}";
        }
        else
        {
            try
            {
                return new Icon(path);
            }
            catch (Exception ex)
            {
                warning = $"托盘图标无法加载：{path}（{ex.Message}）";
            }
        }

        // 发布时如果图标文件缺失，优先使用 EXE 自身嵌入的 ApplicationIcon。
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var extracted = Icon.ExtractAssociatedIcon(processPath);
                if (extracted is not null)
                {
                    warning += " 已改用程序自带图标。";
                    return extracted;
                }
            }
        }
        catch (Exception ex)
        {
            warning += $" 读取程序图标失败：{ex.Message}";
        }

        warning += " 已改用系统默认图标。";
        // Clone 后由 TrayService 负责释放，不能直接释放共享的 SystemIcons 对象。
        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _singleClickTimer.Stop();
            _singleClickTimer.Tick -= OnSingleClickTimerTick;

            _notifyIcon.Visible = false;
            _notifyIcon.MouseClick -= OnMouseClick;
            _notifyIcon.DoubleClick -= OnDoubleClick;
            _notifyIcon.Dispose();
        }
        catch (Exception)
        {
            // 退出清理不能因为托盘组件异常而阻断应用其余资源的释放。
        }
        finally
        {
            try
            {
                _icon.Dispose();
            }
            catch (Exception)
            {
            }

            try
            {
                _menu.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}