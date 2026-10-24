using System.Runtime.InteropServices;
using System.Windows;

namespace ClipAssist.Services;

/// <summary>
/// 把“系统通知”变成“可用的文本”：负责读取剪贴板、重试、过滤空白、
/// 以及抑制“程序自己写剪贴板”造成的重复记录。
///
/// 线程说明：所有逻辑都在 WPF UI 线程执行。
/// WPF 的 Clipboard API 要求 STA 线程；WM_CLIPBOARDUPDATE 本来就在 UI 线程到达，
/// 读取失败时用 await Task.Delay 让出线程再重试，不会阻塞界面。
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private const int MaxReadAttempts = 4;
    private const int RetryDelayMilliseconds = 50;

    private readonly ClipboardMonitor _monitor;
    private readonly ClipboardHistory _history;

    /// <summary>
    /// 刚由本程序写回剪贴板的文本。
    /// 注意：一次 Clipboard.SetText 可能触发不止一条 WM_CLIPBOARDUPDATE
    /// （OLE 写入 + flush），所以不能只忽略“下一条”，而要在时间窗内忽略相同文本。
    /// </summary>
    private string? _selfWrittenText;

    private DateTime _selfWriteExpiresAt;

    /// <summary>自己写入剪贴板后的抑制时长，足够覆盖紧随其后的重复通知。</summary>
    private static readonly TimeSpan SelfWriteSuppressionWindow = TimeSpan.FromSeconds(1.5);

    private bool _isCapturing;
    private bool _pendingCapture;
    private bool _captureSuspended;
    private bool _disposed;

    public ClipboardService(ClipboardHistory history)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));

        _monitor = new ClipboardMonitor();
        _monitor.ClipboardUpdated += OnClipboardUpdated;
        _monitor.Start();
    }

    /// <summary>
    /// 把历史记录写回剪贴板。
    /// 写入前记下文本，监听器下次收到通知时如果内容相同就跳过，避免“自己记录自己”。
    /// </summary>
    public void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _selfWrittenText = text;
        _selfWriteExpiresAt = DateTime.UtcNow + SelfWriteSuppressionWindow;

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // 写入失败时清掉标记，否则会误吞下一次真实复制。
            _selfWrittenText = null;
            throw;
        }
    }

    private void OnClipboardUpdated(object? sender, EventArgs e)
    {
        if (_disposed || _captureSuspended)
        {
            return;
        }

        // 上一次读取还没结束时只记一个标记，等它循环回来再读一次，
        // 避免多个 WM_CLIPBOARDUPDATE 同时进入导致重复处理。
        if (_isCapturing)
        {
            _pendingCapture = true;
            return;
        }

        _ = CaptureAsync();
    }

    /// <summary>暂停监听并把正在读取的剪贴板操作等到结束，供数据目录迁移使用。</summary>
    public async Task SuspendCaptureAsync()
    {
        if (_disposed)
        {
            return;
        }

        _captureSuspended = true;
        while (_isCapturing && !_disposed)
        {
            await Task.Delay(25);
        }
    }

    public void ResumeCapture()
    {
        if (!_disposed)
        {
            _captureSuspended = false;
        }
    }

    private async Task CaptureAsync()
    {
        _isCapturing = true;

        try
        {
            do
            {
                _pendingCapture = false;

                var text = await ReadTextWithRetryAsync();

                if (_captureSuspended)
                {
                    break;
                }

                if (text is null)
                {
                    continue;
                }

                if (IsSelfWritten(text))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                _history.AddOrPromote(text);
            }
            while (_pendingCapture && !_disposed && !_captureSuspended);
        }
        finally
        {
            _isCapturing = false;
        }
    }

    /// <summary>
    /// 判断这段文本是不是本程序刚刚写进去的。
    /// 时间窗过期后自动失效，避免永久吞掉用户后来复制的相同内容。
    /// </summary>
    private bool IsSelfWritten(string text)
    {
        if (_selfWrittenText is null)
        {
            return false;
        }

        if (DateTime.UtcNow > _selfWriteExpiresAt)
        {
            _selfWrittenText = null;
            return false;
        }

        return string.Equals(text, _selfWrittenText, StringComparison.Ordinal);
    }

    /// <summary>
    /// 读取剪贴板文本。剪贴板可能正被其他程序占用（COM 错误），
    /// 因此最多重试 4 次、每次间隔 50ms；全部失败则安静放弃这一条。
    /// </summary>
    private static async Task<string?> ReadTextWithRetryAsync()
    {
        for (var attempt = 0; attempt < MaxReadAttempts; attempt++)
        {
            try
            {
                if (!Clipboard.ContainsText())
                {
                    return null;
                }

                var text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (string.IsNullOrEmpty(text))
                {
                    text = Clipboard.GetText(TextDataFormat.Text);
                }

                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (COMException)
            {
                // 剪贴板被占用，稍后重试。
            }
            catch (ExternalException)
            {
                // 其他 OLE 剪贴板错误，同样重试。
            }

            await Task.Delay(RetryDelayMilliseconds);
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _monitor.ClipboardUpdated -= OnClipboardUpdated;
        _monitor.Dispose();
    }
}