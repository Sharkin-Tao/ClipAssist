namespace ClipAssist.Services;

/// <summary>
/// 单实例守卫：保证同一用户会话里同一模式只运行一个 ClipAssist。
///
/// 安装版和便携版使用不同的实例名，因此各自拥有独立的互斥量和唤醒事件。
/// 两个模式之间的互斥由 CrossModeGuard 负责，避免它们同时争用全局快捷键。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultInstanceName = "ClipAssist";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread? _listener;
    private bool _disposed;

    public bool IsFirstInstance { get; }

    /// <summary>另一个实例请求显示窗口。事件在后台线程触发，订阅者需自行切回 UI 线程。</summary>
    public event EventHandler? ShowRequested;

    public SingleInstanceGuard(string? instanceName = null)
    {
        var name = string.IsNullOrWhiteSpace(instanceName)
            ? DefaultInstanceName
            : instanceName.Trim();

        var mutexName = @"Local\" + name + ".SingleInstance";
        var showEventName = @"Local\" + name + ".ShowWindow";

        _mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        IsFirstInstance = createdNew;

        _showEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, showEventName);

        if (createdNew)
        {
            _listener = new Thread(ListenForShowRequests)
            {
                IsBackground = true,
                Name = name + ".SingleInstance",
            };
            _listener.Start();
        }
    }

    /// <summary>第二个实例调用：请求已有实例显示窗口。</summary>
    public void SignalShow()
    {
        if (IsFirstInstance)
        {
            return;
        }

        try
        {
            _showEvent.Set();
        }
        catch (ObjectDisposedException)
        {
            // 已有实例正在退出，忽略即可。
        }
    }

    private void ListenForShowRequests()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            if (!_showEvent.WaitOne(200))
            {
                continue;
            }

            if (_cancellation.IsCancellationRequested)
            {
                break;
            }

            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _cancellation.Cancel();

        if (_listener is not null)
        {
            try
            {
                _showEvent.Set();
            }
            catch (ObjectDisposedException)
            {
            }

            _listener.Join(1000);
        }

        _cancellation.Dispose();

        if (IsFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 没有持有互斥量（例如初始化异常），忽略。
            }
        }

        _mutex.Dispose();
        _showEvent.Dispose();
    }
}

/// <summary>
/// 跨模式运行守卫：安装版和便携版不能同时运行。
///
/// 两种模式仍有各自的 SingleInstanceGuard；本守卫使用一个共享的命名信号量，
/// 只允许一个模式持有。检测到另一模式正在运行时，App 必须提示用户并退出，
/// 不能静默启动后争用全局快捷键。
/// </summary>
public sealed class CrossModeGuard : IDisposable
{
    public const string DefaultName = @"Local\ClipAssist.ActiveMode";

    private readonly Semaphore? _semaphore;
    private bool _disposed;

    public bool IsFirstInstance { get; }

    public CrossModeGuard(string? name = null)
    {
        var actualName = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
        var semaphore = new Semaphore(1, 1, actualName);

        if (!semaphore.WaitOne(0))
        {
            semaphore.Dispose();
            IsFirstInstance = false;
            return;
        }

        _semaphore = semaphore;
        IsFirstInstance = true;
    }

    public void Dispose()
    {
        if (_disposed || _semaphore is null)
        {
            return;
        }

        _disposed = true;

        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已经释放过时忽略。
        }

        _semaphore.Dispose();
    }
}