using System.Windows.Threading;
using ClipAssist.Localization;
using ClipAssist.Models;

namespace ClipAssist.Services;

/// <summary>一次显式保存的结果，供删除操作决定是否回滚 UI 状态。</summary>
public sealed record HistorySaveResult(bool Success, string? Error);

/// <summary>
/// 把 ClipboardHistory 的变化按“防抖”方式写入 JSON：
/// 连续变化时只在最后一次变化 800ms 后保存一次，避免频繁复制时反复写盘。
///
/// 线程模型：
/// - 计时器、快照都在 UI 线程执行；
/// - 真正的文件写入放到线程池（Task.Run），不卡界面；
/// - 用 SemaphoreSlim 保证同一时间只有一次写入，防止两个任务互相覆盖临时文件。
/// </summary>
public sealed class HistoryPersistence : IDisposable
{
    private const int SaveDelayMilliseconds = 800;

    private readonly ClipboardStorage _storage;
    private readonly ClipboardHistory _history;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private bool _disposed;
    private bool _paused;

    /// <summary>保存失败、加载异常等需要展示给用户的提示。</summary>
    public event EventHandler<string>? Notice;

    public HistoryPersistence(ClipboardStorage storage, ClipboardHistory history)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _history = history ?? throw new ArgumentNullException(nameof(history));

        _history.Changed += OnHistoryChanged;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(SaveDelayMilliseconds),
        };
        _timer.Tick += OnTimerTick;
    }

    public string FilePath => _storage.FilePath;

    /// <summary>
    /// 启动时加载文件并填充历史。返回需要提示用户的问题（正常时为 null）。
    /// 必须在创建 ClipboardService 之前调用，避免监听线程与加载并发。
    /// </summary>
    public string? Load()
    {
        var result = _storage.Load();

        if (result.Entries.Count > 0)
        {
            var entries = result.Entries
                .Where(stored => !string.IsNullOrWhiteSpace(stored.Text))
                .Select(stored => new ClipboardEntry
                {
                    Id = stored.Id == Guid.Empty ? Guid.NewGuid() : stored.Id,
                    Text = stored.Text,
                    CreatedAt = stored.CreatedAt,
                    UpdatedAt = stored.UpdatedAt,
                });

            // ReplaceAll 内部会过滤空白、保留重复文本，并截断到上限。
            _history.ReplaceAll(entries);
        }

        return result.Warning;
    }

    /// <summary>退出前同步保存，防止防抖窗口内的最后一笔变更丢失。</summary>
    public void SaveNow()
    {
        try
        {
            _storage.Save(TakeSnapshot());
        }
        catch (Exception ex)
        {
            Notice?.Invoke(this, L.Format("History.SaveFailed", ex.Message));
        }
    }

    /// <summary>
    /// 暂停防抖保存，等待正在进行的写盘，再同步保存一次当前快照。
    /// 成功后保持暂停；调用方在迁移失败时必须调用 ResumeAfterFailedMigration。
    /// </summary>
    public async Task FlushForMigrationAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _paused = true;
        _timer.Stop();

        await _saveGate.WaitAsync().ConfigureAwait(true);
        try
        {
            _storage.Save(TakeSnapshot());
        }
        catch
        {
            _paused = false;
            throw;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void ResumeAfterFailedMigration()
    {
        if (!_disposed)
        {
            _paused = false;
        }
    }

    /// <summary>
    /// 立即保存当前历史快照，并返回成功/失败。
    /// 删除操作必须在文件保存成功后才能保持 UI 的删除结果；失败时调用方的回滚
    /// 会通过 afterSave 在释放写锁前执行，避免排队中的旧保存把删除状态又写回去。
    /// </summary>
    public async Task<HistorySaveResult> SaveCurrentAsync(Action<bool>? afterSave = null)
    {
        if (_disposed)
        {
            return new HistorySaveResult(false, L.Get("History.Disposed"));
        }

        if (_paused)
        {
            return new HistorySaveResult(false, L.Get("History.Paused"));
        }

        _timer.Stop();
        await _saveGate.WaitAsync().ConfigureAwait(true);

        HistorySaveResult result;
        try
        {
            try
            {
                // 快照在拿到写锁后生成，排队中的保存不会使用删除前的旧集合。
                var snapshot = TakeSnapshot();
                await Task.Run(() => _storage.Save(snapshot)).ConfigureAwait(true);
                result = new HistorySaveResult(true, null);
            }
            catch (Exception ex)
            {
                result = new HistorySaveResult(false, L.Format("History.SaveFailed", ex.Message));
                Notice?.Invoke(this, result.Error ?? L.Get("History.SaveFailedGeneric"));
            }

            afterSave?.Invoke(result.Success);
            return result;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (_disposed || _paused)
        {
            return;
        }

        // 每次变化都重新计时，只有安静 800ms 后才真正写盘。
        _timer.Stop();
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_disposed || _paused)
        {
            return;
        }

        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (_disposed)
        {
            return;
        }

        // 在 UI 线程生成不可变快照，后台只负责序列化，避免读取到“半更新”的对象。
        await _saveGate.WaitAsync().ConfigureAwait(true);

        try
        {
            // 必须在拿到写锁后再取快照：否则较早排队的旧快照可能覆盖刚完成的删除。
            var snapshot = TakeSnapshot();
            await Task.Run(() => _storage.Save(snapshot)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Notice?.Invoke(this, L.Format("History.SaveFailed", ex.Message));
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private List<StoredEntry> TakeSnapshot()
        => _history.Entries
            .Select(entry => new StoredEntry(entry.Id, entry.Text, entry.CreatedAt, entry.UpdatedAt))
            .ToList();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _history.Changed -= OnHistoryChanged;

        // 故意不 Dispose _saveGate：退出瞬间可能仍有保存任务在等待/释放它。
    }
}