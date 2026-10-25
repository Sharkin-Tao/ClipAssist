using System.IO;
using System.Text.Json;
using ClipAssist.Infrastructure;
using ClipAssist.Localization;

namespace ClipAssist.Services;

/// <summary>
/// 写进 JSON 文件的一条记录。
/// 单独定义而不直接序列化 ClipboardEntry，是为了不把 Preview、TimeText
/// 这类“界面用的计算属性”也写进文件。
/// </summary>
public sealed record StoredEntry(Guid Id, string Text, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>读取结果：记录 + 需要提示用户的问题（正常时为 null）。</summary>
public sealed record LoadResult(IReadOnlyList<StoredEntry> Entries, string? Warning);

/// <summary>
/// 历史记录的本地 JSON 读写。
/// 默认位置：%APPDATA%\ClipAssist\history.json（纯本地，不发起任何网络请求）。
///
/// 防损坏策略：
/// 1. 先写 history.json.tmp，再原子替换为 history.json，避免中途退出留下半个文件；
/// 2. 文件损坏时改名成 history.json.bad-时间戳 保留现场，然后从空历史开始；
/// 3. 读取失败只返回提示，不删除用户数据。
/// </summary>
public sealed class ClipboardStorage
{
    public const string DefaultFolderName = AppPaths.AppFolderName;
    public const string FileName = AppPaths.HistoryFileName;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public ClipboardStorage(string? directoryPath = null)
    {
        DirectoryPath = string.IsNullOrWhiteSpace(directoryPath)
            ? AppPaths.GetDataDirectory()
            : directoryPath;

        FilePath = Path.Combine(DirectoryPath, FileName);
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    public LoadResult Load()
    {
        if (!File.Exists(FilePath))
        {
            return new LoadResult(Array.Empty<StoredEntry>(), null);
        }

        try
        {
            using var stream = File.OpenRead(FilePath);
            var entries = JsonSerializer.Deserialize<List<StoredEntry>>(stream, JsonOptions);

            if (entries is null)
            {
                return new LoadResult(Array.Empty<StoredEntry>(), L.Get("Storage.HistoryEmptyWarning"));
            }

            return new LoadResult(entries, null);
        }
        catch (JsonException)
        {
            return HandleCorruptFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LoadResult(Array.Empty<StoredEntry>(), L.Format("Storage.HistoryReadFailed", ex.Message));
        }
    }

    /// <summary>
    /// 保存全部记录。调用方保证 entries 已经是固定快照，
    /// 这样后台线程序列化时不会被界面线程同时修改。
    /// </summary>
    public void Save(IReadOnlyList<StoredEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Directory.CreateDirectory(DirectoryPath);

        var tempPath = FilePath + ".tmp";

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, entries, JsonOptions);
            // 把缓冲区真正刷到磁盘，降低断电导致文件不完整的概率。
            stream.Flush(flushToDisk: true);
        }

        // 同卷内“重命名覆盖”是原子操作：读者要么看到旧文件，要么看到新文件，
        // 不会看到写了一半的内容。这里不用 File.Replace，因为它在部分环境
        // （受控目录、某些同步盘）会因权限被拒绝而失败。
        File.Move(tempPath, FilePath, overwrite: true);
    }

    private LoadResult HandleCorruptFile()
    {
        var backupPath = TryBackupCorruptFile();

        var message = backupPath is null
            ? L.Get("Storage.HistoryCorrupt")
            : L.Format("Storage.HistoryCorruptBackup", Path.GetFileName(backupPath));

        return new LoadResult(Array.Empty<StoredEntry>(), message);
    }

    /// <summary>把损坏的文件改名保留，方便以后排查；改名失败也不影响程序继续运行。</summary>
    private string? TryBackupCorruptFile()
    {
        try
        {
            var backupPath = FilePath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(FilePath, backupPath);
            return backupPath;
        }
        catch (Exception)
        {
            return null;
        }
    }
}