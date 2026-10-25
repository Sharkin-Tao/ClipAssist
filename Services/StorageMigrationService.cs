using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ClipAssist.Infrastructure;
using ClipAssist.Localization;

namespace ClipAssist.Services;

public sealed record StorageTargetValidation(
    bool IsValid,
    string? Error,
    bool HasSettings,
    bool HasHistory);

public sealed record StorageMigrationResult(bool Success, string? Error, string? Warning);

/// <summary>
/// 只负责“复制 + 校验 + 提交 + 指针”的数据目录迁移。
/// 不移动、不删除旧目录；任何失败都保留旧目录和旧指针。
/// 剪贴板/历史写入的暂停由 App 在调用前负责。
/// </summary>
public sealed class StorageMigrationService
{
    private static readonly JsonSerializerOptions HistoryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly DataLocationPointer _pointer;

    public StorageMigrationService(DataLocationPointer pointer)
    {
        _pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
    }

    /// <summary>测试注入：在提交最终文件之前执行，用于验证异常回滚。</summary>
    internal Action<string>? BeforeCommitForTests { get; set; }

    /// <summary>测试注入：模拟目标目录不可写。</summary>
    internal Func<string, bool>? WritableProbeForTests { get; set; }

    public StorageTargetValidation Validate(string sourceDirectory, string targetDirectory)
    {
        if (!TryNormalize(sourceDirectory, out var source, out var sourceError))
        {
            return new StorageTargetValidation(false, sourceError, false, false);
        }

        if (!TryNormalize(targetDirectory, out var target, out var targetError))
        {
            return new StorageTargetValidation(false, targetError, false, false);
        }

        if (!Directory.Exists(source))
        {
            return new StorageTargetValidation(false, L.Get("Storage.SourceMissing"), false, false);
        }

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return new StorageTargetValidation(false, L.Get("Storage.SameDirectory"), false, false);
        }

        if (IsSubPath(target, source))
        {
            return new StorageTargetValidation(
                false,
                L.Get("Storage.Subdirectory"),
                false,
                false);
        }

        if (IsSubPath(source, target))
        {
            return new StorageTargetValidation(
                false,
                L.Get("Storage.ParentDirectory"),
                false,
                false);
        }

        if (!Directory.Exists(target))
        {
            return new StorageTargetValidation(false, L.Get("Storage.TargetMissing"), false, false);
        }

        if (!(WritableProbeForTests?.Invoke(target) ?? AppPaths.IsWritableDirectory(target)))
        {
            return new StorageTargetValidation(false, L.Get("Storage.TargetNotWritable"), false, false);
        }

        var pointerDirectory = Path.GetDirectoryName(_pointer.FilePath);
        if (pointerDirectory is not null &&
            IsSameOrAncestor(target, pointerDirectory))
        {
            return new StorageTargetValidation(
                false,
                L.Get("Storage.ContainsPointer"),
                false,
                false);
        }

        var settingsPath = Path.Combine(source, AppPaths.SettingsFileName);
        if (!File.Exists(settingsPath))
        {
            return new StorageTargetValidation(
                false,
                L.Get("Storage.NoSettings"),
                false,
                false);
        }

        if (!ValidateSettingsFile(settingsPath, out var settingsError))
        {
            return new StorageTargetValidation(false, settingsError, true, false);
        }

        var historyPath = Path.Combine(source, AppPaths.HistoryFileName);
        var hasHistory = File.Exists(historyPath);
        if (hasHistory && !ValidateHistoryFile(historyPath, out var historyError))
        {
            return new StorageTargetValidation(false, historyError, true, true);
        }

        foreach (var fileName in new[]
                 {
                     AppPaths.SettingsFileName,
                     AppPaths.HistoryFileName,
                     AppPaths.SettingsFileName + ".tmp",
                     AppPaths.HistoryFileName + ".tmp",
                 })
        {
            var conflictPath = Path.Combine(target, fileName);
            if (File.Exists(conflictPath) || Directory.Exists(conflictPath))
            {
                return new StorageTargetValidation(
                    false,
                    L.Format("Storage.Conflict", fileName),
                    true,
                    hasHistory);
            }
        }

        return new StorageTargetValidation(true, null, true, hasHistory);
    }

    public StorageMigrationResult Migrate(string sourceDirectory, string targetDirectory)
    {
        var validation = Validate(sourceDirectory, targetDirectory);
        if (!validation.IsValid)
        {
            return new StorageMigrationResult(false, validation.Error, null);
        }

        TryNormalize(sourceDirectory, out var source, out _);
        TryNormalize(targetDirectory, out var target, out _);

        var settingsSource = Path.Combine(source, AppPaths.SettingsFileName);
        var historySource = Path.Combine(source, AppPaths.HistoryFileName);
        var staging = Path.Combine(target, ".clipassist-migration-" + Guid.NewGuid().ToString("N"));
        var createdFinals = new List<string>();
        var createdHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Directory.CreateDirectory(staging);

            var stagedSettings = Path.Combine(staging, AppPaths.SettingsFileName);
            var settingsHash = CopyAndHash(settingsSource, stagedSettings);

            string? historyHash = null;
            string? stagedHistory = null;
            if (File.Exists(historySource))
            {
                stagedHistory = Path.Combine(staging, AppPaths.HistoryFileName);
                historyHash = CopyAndHash(historySource, stagedHistory);
            }

            BeforeCommitForTests?.Invoke(target);

            MoveToFinal(stagedSettings, Path.Combine(target, AppPaths.SettingsFileName), createdFinals, createdHashes, settingsHash);
            if (stagedHistory is not null)
            {
                MoveToFinal(stagedHistory, Path.Combine(target, AppPaths.HistoryFileName), createdFinals, createdHashes, historyHash!);
            }

            if (!_pointer.TrySave(target, out var pointerError))
            {
                CleanupCreatedFinals(createdFinals, createdHashes);
                return new StorageMigrationResult(
                    false,
                    pointerError ?? L.Get("Storage.PointerSaveFailed"),
                    null);
            }

            return new StorageMigrationResult(true, null, null);
        }
        catch (Exception ex)
        {
            CleanupCreatedFinals(createdFinals, createdHashes);
            return new StorageMigrationResult(
                false,
                L.Format("Storage.Failed", ex.Message),
                null);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static void MoveToFinal(
        string stagedPath,
        string finalPath,
        List<string> createdFinals,
        Dictionary<string, string> createdHashes,
        string expectedHash)
    {
        File.Move(stagedPath, finalPath, overwrite: false);
        createdFinals.Add(finalPath);
        createdHashes[finalPath] = expectedHash;
    }

    private static void CleanupCreatedFinals(
        IReadOnlyList<string> createdFinals,
        IReadOnlyDictionary<string, string> createdHashes)
    {
        foreach (var path in createdFinals)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                // 只删除本次迁移写入且内容完全一致的文件，绝不误删后来出现的用户文件。
                if (createdHashes.TryGetValue(path, out var expected) &&
                    string.Equals(ComputeSha256(path), expected, StringComparison.Ordinal))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 清理失败不影响旧目录；目标目录可能残留本次文件，下次迁移会检测到冲突。
            }
        }
    }

    private static string CopyAndHash(string sourcePath, string destinationPath)
    {
        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var destination = new FileStream(
                   destinationPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            source.CopyTo(destination);
            destination.Flush(flushToDisk: true);
        }

        var sourceHash = ComputeSha256(sourcePath);
        var destinationHash = ComputeSha256(destinationPath);
        if (!string.Equals(sourceHash, destinationHash, StringComparison.Ordinal))
        {
            throw new IOException(L.Format("Storage.CopyVerifyFailed", Path.GetFileName(sourcePath)));
        }

        return sourceHash;
    }

    private static bool ValidateSettingsFile(string path, out string? error)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = L.Get("Storage.SettingsRootInvalid");
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = L.Format("Storage.SettingsInvalid", ex.Message);
            return false;
        }
    }

    private static bool ValidateHistoryFile(string path, out string? error)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var entries = JsonSerializer.Deserialize<List<StoredEntry>>(stream, HistoryJsonOptions);
            if (entries is null)
            {
                error = L.Get("Storage.HistoryEmpty");
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = L.Format("Storage.HistoryInvalid", ex.Message);
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool TryNormalize(string? directory, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(directory))
        {
            error = L.Get("Storage.Empty");
            return false;
        }

        if (!Path.IsPathFullyQualified(directory))
        {
            error = L.Get("Storage.NotAbsolute");
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = L.Format("Storage.InvalidPath", ex.Message);
            return false;
        }
    }

    private static bool IsSubPath(string candidate, string parent)
    {
        var prefix = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrAncestor(string candidate, string descendant)
    {
        return string.Equals(candidate, descendant, StringComparison.OrdinalIgnoreCase) ||
               IsSubPath(descendant, candidate);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }
}