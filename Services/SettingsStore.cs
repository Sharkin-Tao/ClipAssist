using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using ClipAssist.Models;

namespace ClipAssist.Services;

/// <summary>settings.json 原始读取状态。</summary>
public enum SettingsReadStatus
{
    Missing,
    Loaded,
    Corrupt,
    Unreadable,
}

/// <summary>
/// 读取结果。Root 只在 Loaded 时非空；Corrupt 时 BackupPath 表示是否成功保留现场。
/// </summary>
public sealed record SettingsReadResult(
    SettingsReadStatus Status,
    JsonElement? Root,
    string? Warning,
    string? BackupPath = null);

/// <summary>保存结果：失败时返回原因，不抛异常给界面。</summary>
public sealed record SettingsSaveResult(bool Success, string? Error);

/// <summary>
/// settings.json 的本地读写。
/// 只负责文件层：读取原始 JSON、原子保存、损坏文件改名备份。
/// 版本迁移和数值校验由 SettingsService 编排。
/// </summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 让 Ctrl+Shift+V 这类内容以可读形式落盘，而不是 \u002B。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public SettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        FilePath = filePath;
        DirectoryPath = Path.GetDirectoryName(filePath)
            ?? throw new ArgumentException("设置文件路径不包含目录。", nameof(filePath));
    }

    public string FilePath { get; }

    public string DirectoryPath { get; }

    public SettingsReadResult Read()
    {
        if (!File.Exists(FilePath))
        {
            return new SettingsReadResult(SettingsReadStatus.Missing, null, null);
        }

        try
        {
            using var stream = File.OpenRead(FilePath);
            using var document = JsonDocument.Parse(stream, DocumentOptions);
            return new SettingsReadResult(
                SettingsReadStatus.Loaded,
                document.RootElement.Clone(),
                null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 文件在检查后被删除：按首次启动处理，允许重新创建。
            return new SettingsReadResult(SettingsReadStatus.Missing, null, null);
        }
        catch (JsonException)
        {
            var backupPath = TryBackupCurrentFile();
            var message = backupPath is null
                ? "设置文件格式损坏，本次使用默认设置；原文件未能备份，未自动覆盖。"
                : $"设置文件格式损坏，已备份为 {Path.GetFileName(backupPath)}，本次使用默认设置。";

            return new SettingsReadResult(
                SettingsReadStatus.Corrupt,
                null,
                message,
                backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsReadResult(
                SettingsReadStatus.Unreadable,
                null,
                $"读取设置文件失败：{ex.Message}");
        }
    }

    public SettingsSaveResult Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var tempPath = FilePath + ".tmp";

        try
        {
            Directory.CreateDirectory(DirectoryPath);

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, FilePath, overwrite: true);
            return new SettingsSaveResult(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            TryDeleteTempFile(tempPath);
            return new SettingsSaveResult(false, $"保存设置失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            TryDeleteTempFile(tempPath);
            return new SettingsSaveResult(false, $"保存设置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 保留当前设置文件，供格式损坏或内容无法迁移时排查。
    /// 失败返回 null，调用方应避免覆盖原文件。
    /// </summary>
    public string? TryBackupCurrentFile()
    {
        try
        {
            var suffix = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var backupPath = FilePath + ".bad-" + suffix;

            // 同一秒内连续损坏时，加序号避免覆盖/冲突。
            var index = 1;
            while (File.Exists(backupPath))
            {
                backupPath = FilePath + ".bad-" + suffix + "-" + index.ToString();
                index++;
            }

            File.Move(FilePath, backupPath);
            return backupPath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响用户数据，忽略。
        }
    }
}