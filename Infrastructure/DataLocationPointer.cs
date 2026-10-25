using System.IO;
using System.Text;
using System.Text.Json;
using ClipAssist.Localization;

namespace ClipAssist.Infrastructure;

public enum DataLocationPointerStatus
{
    Missing,
    Loaded,
    Invalid,
    Unreadable,
}

/// <summary>指针读取结果。Directory 只在 Loaded 时非空。</summary>
public sealed record DataLocationPointerReadResult(
    DataLocationPointerStatus Status,
    string? Directory,
    string? Warning);

/// <summary>
/// 固定位置的“数据目录指针”，用于在 settings.json 被迁移后仍能发现新目录。
/// 文件本身永远不迁移，也不能被放进自定义数据目录。
/// </summary>
public sealed class DataLocationPointer
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DataLocationPointer(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public DataLocationPointerReadResult Read()
    {
        if (!File.Exists(FilePath))
        {
            return new DataLocationPointerReadResult(DataLocationPointerStatus.Missing, null, null);
        }

        try
        {
            using var stream = File.OpenRead(FilePath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("dataDirectory", out var directoryElement) ||
                directoryElement.ValueKind != JsonValueKind.String)
            {
                return Invalid(L.Get("Data.PointerInvalid"));
            }

            var raw = directoryElement.GetString();
            if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathFullyQualified(raw))
            {
                return Invalid(L.Get("Data.PointerNotAbsolute"));
            }

            var directory = Path.GetFullPath(raw.Trim());
            return new DataLocationPointerReadResult(
                DataLocationPointerStatus.Loaded,
                Path.TrimEndingDirectorySeparator(directory),
                null);
        }
        catch (JsonException)
        {
            return Invalid(L.Get("Data.PointerCorrupt"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DataLocationPointerReadResult(
                DataLocationPointerStatus.Unreadable,
                null,
                L.Format("Data.PointerReadFailed", ex.Message));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid(L.Format("Data.PointerPathInvalid", ex.Message));
        }
    }

    /// <summary>
    /// 原子更新指针。写入前保留旧内容；写入后重新读取校验，失败时恢复旧指针。
    /// </summary>
    public bool TrySave(string directory, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
        {
            error = L.Get("Data.MustBeAbsolute");
            return false;
        }

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = L.Format("Data.InvalidPath", ex.Message);
            return false;
        }

        var pointerDirectory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(pointerDirectory))
        {
            error = L.Get("Data.PointerPathInvalidNoDetail");
            return false;
        }

        var tempPath = FilePath + ".tmp";
        string? previous = null;

        try
        {
            Directory.CreateDirectory(pointerDirectory);
            if (File.Exists(FilePath))
            {
                previous = File.ReadAllText(FilePath, Encoding.UTF8);
            }

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(
                    stream,
                    new PointerDocument
                    {
                        Version = CurrentVersion,
                        DataDirectory = normalized,
                    },
                    JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, FilePath, overwrite: true);

            var verify = Read();
            if (verify.Status != DataLocationPointerStatus.Loaded ||
                !string.Equals(verify.Directory, normalized, StringComparison.OrdinalIgnoreCase))
            {
                Restore(previous);
                error = L.Get("Data.PointerVerifyFailed");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or ArgumentException)
        {
            TryDelete(tempPath);
            Restore(previous);
            error = L.Format("Data.PointerSaveFailed", ex.Message);
            return false;
        }
    }

    private void Restore(string? previous)
    {
        try
        {
            if (previous is null)
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }

                return;
            }

            var restorePath = FilePath + ".restore";
            File.WriteAllText(restorePath, previous, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(restorePath, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // 恢复失败时不覆盖调用方更具体的错误；指针校验/保存错误仍会告知用户。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    private DataLocationPointerReadResult Invalid(string warning) =>
        new(DataLocationPointerStatus.Invalid, null, warning);

    private sealed class PointerDocument
    {
        public int Version { get; set; }

        public string DataDirectory { get; set; } = string.Empty;
    }
}