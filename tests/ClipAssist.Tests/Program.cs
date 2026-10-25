using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using ClipAssist.Controls;
using ClipAssist.Infrastructure;
using ClipAssist.Localization;
using ClipAssist.Models;
using ClipAssist.Services;
using ClipAssist.Tests;
using ClipAssist.ViewModels;
using Microsoft.Win32;

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok)
    {
        pass++;
        Console.WriteLine($"PASS  {name}" + (detail.Length > 0 ? $": {detail}" : string.Empty));
    }
    else
    {
        fail++;
        Console.WriteLine($"FAIL  {name}" + (detail.Length > 0 ? $": {detail}" : string.Empty));
    }
}

var root = Path.Combine(Path.GetTempPath(), "clipassist-window-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    var thread = new Thread(RunTests);
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();

    if (!thread.Join(TimeSpan.FromSeconds(60)))
    {
        Check("测试线程", false, "超时未结束");
    }
}
catch (Exception ex)
{
    Check("测试线程", false, ex.ToString());
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch (Exception)
    {
        // 清理失败不影响测试结论。
    }
}

Console.WriteLine();
Console.WriteLine($"RESULT {pass} passed, {fail} failed");
Environment.ExitCode = fail == 0 ? 0 : 1;

void RunTests()
{
    // 本任务只跑删除专项时可设置 CLIPASSIST_TEST_FILTER=deletion，
    // 避免重复执行已通过的主题/窗口测试。
    if (Environment.GetEnvironmentVariable("CLIPASSIST_TEST_FILTER") == "startup")
    {
        TestStartupRegistration();
        TestStartupModeIsolation();
        return;
    }

    if (Environment.GetEnvironmentVariable("CLIPASSIST_TEST_FILTER") == "deletion")
    {
        TestHistoryDeletionCore();
        TestHistoryDeletionPersistence();
        TestHistoryDeletionViewModel();
        TestHistoryDeletionBatchScope();
        TestHistoryDeletionSaveFailureReal();
        TestClearAllDeletion();
        return;
    }

    TestAppPaths();
    TestAppIdentityAndPortablePaths();
    TestSingleInstanceModes();
    TestCrossModeGuard();
    TestDataLocation();
    TestStorageMigration();
    TestSettingsStore();
    TestLocalization();
    TestThemeSettings();
    TestSystemThemeWatcher();
    TestPlacement();
    TestCaptureAndApply();
    TestPreviewAndTextSeparation();
    TestTextHighlighter();
    TestHighlightTextBlock();
    TestHighlightPerformance();
    TestCopyEmptyFeedback();
    TestHistoryDeletionCore();
    TestHistoryDeletionPersistence();
    TestHistoryDeletionViewModel();
    TestHistoryDeletionBatchScope();
    TestHistoryDeletionSaveFailureReal();
    TestClearAllDeletion();
    TestHistoryBackwardCompatibility();
    TestStartupModeIsolation();
    TestHotkeyGesture();
    TestHotkeyPersistenceAndAtomicRegistration();
    TestHotkeyManagerFallbackAndRollback();
}

void TestAppPaths()
{
    var previous = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable);
    var custom = Path.Combine(root, "custom-data");

    try
    {
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable, custom);
        Check("AppPaths 环境变量覆盖", AppPaths.GetDataDirectory() == custom, AppPaths.GetDataDirectory());
        Check(
            "设置文件与历史文件同目录",
            Path.GetDirectoryName(AppPaths.GetSettingsFilePath()) == custom
                && Path.GetDirectoryName(AppPaths.GetHistoryFilePath()) == custom,
            AppPaths.GetSettingsFilePath());

        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable, null);
        var fallback = AppPaths.GetDataDirectory();
        Check(
            "默认目录以 ClipAssist 结尾",
            fallback.EndsWith(Path.DirectorySeparatorChar + AppPaths.AppFolderName, StringComparison.Ordinal),
            fallback);
    }
    finally
    {
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable, previous);
    }
}

void TestAppIdentityAndPortablePaths()
{
    var installedBase = Path.Combine(root, "identity-installed");
    var portableBase = Path.Combine(root, "identity-portable");
    var localRoot = Path.Combine(root, "identity-local");
    Directory.CreateDirectory(installedBase);
    Directory.CreateDirectory(portableBase);
    File.WriteAllText(
        Path.Combine(portableBase, AppIdentity.PortableMarkerFileName),
        "portable");

    var installed = AppIdentity.Resolve(installedBase);
    var portable = AppIdentity.Resolve(portableBase);

    Check(
        "无便携标记时识别为安装版",
        !installed.IsPortable
            && installed.ModeName == AppIdentity.InstalledModeName
            && installed.StartupValueName == AppIdentity.InstalledModeName,
        installed.ModeName);
    Check(
        "有便携标记时识别为便携版",
        portable.IsPortable
            && portable.ModeName == AppIdentity.PortableModeName
            && portable.StartupValueName == AppIdentity.PortableModeName,
        portable.ModeName);
    Check(
        "两种模式的实例标识不同",
        installed.MutexName != portable.MutexName
            && installed.ShowEventName != portable.ShowEventName,
        $"{installed.MutexName} / {portable.MutexName}");

    var installedPreferred = AppPaths.ResolvePreferredDataDirectory(installed, installedBase);
    var portablePreferred = AppPaths.ResolvePreferredDataDirectory(portable, portableBase);
    Check(
        "安装版首选数据目录保持 %APPDATA%\\ClipAssist",
        installedPreferred == Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppPaths.AppFolderName),
        installedPreferred);
    Check(
        "便携版首选数据目录为程序目录旁 Data",
        portablePreferred == Path.Combine(portableBase, AppIdentity.PortableDataFolderName),
        portablePreferred);

    var installedCandidates = AppPaths.GetDataDirectoryCandidates(
        installed,
        installedBase,
        localRoot);
    var portableCandidates = AppPaths.GetDataDirectoryCandidates(
        portable,
        portableBase,
        localRoot);

    Check(
        "安装版候选顺序仍以默认目录开头",
        installedCandidates.Count > 0 && installedCandidates[0] == installedPreferred,
        installedCandidates.FirstOrDefault() ?? "(empty)");
    Check(
        "便携版候选顺序为 Data 后接本地回退目录",
        portableCandidates.Count == 2
            && portableCandidates[0] == portablePreferred
            && portableCandidates[1] == Path.Combine(localRoot, AppIdentity.PortableModeName),
        string.Join(" | ", portableCandidates));
    Check(
        "便携版候选不包含安装版数据目录",
        !portableCandidates.Any(path => string.Equals(
            path,
            installedPreferred,
            StringComparison.OrdinalIgnoreCase)),
        string.Join(" | ", portableCandidates));

    var installedPointer = AppPaths.ResolveDataLocationFilePath(installed, localRoot);
    var portablePointer = AppPaths.ResolveDataLocationFilePath(portable, localRoot);
    Check(
        "两种模式的数据位置指针隔离",
        installedPointer == Path.Combine(
            localRoot,
            AppIdentity.InstalledModeName,
            AppPaths.DataLocationFileName)
            && portablePointer == Path.Combine(
                localRoot,
                AppIdentity.PortableModeName,
                AppPaths.DataLocationFileName)
            && installedPointer != portablePointer,
        $"{installedPointer} / {portablePointer}");
}

void TestSingleInstanceModes()
{
    var instanceName = "ClipAssist.Tests.Single." + Guid.NewGuid().ToString("N");

    using (var first = new SingleInstanceGuard(instanceName))
    {
        using var second = new SingleInstanceGuard(instanceName);
        Check(
            "同一模式重复启动只有第一个实例获得所有权",
            first.IsFirstInstance && !second.IsFirstInstance);
    }

    using (var afterRelease = new SingleInstanceGuard(instanceName))
    {
        Check(
            "同一模式释放后可以重新启动",
            afterRelease.IsFirstInstance);
    }
}

void TestCrossModeGuard()
{
    var name = @"Local\ClipAssist.Tests.CrossMode." + Guid.NewGuid().ToString("N");

    using (var first = new CrossModeGuard(name))
    {
        using var second = new CrossModeGuard(name);
        Check(
            "跨模式共享守卫只允许一个模式进入",
            first.IsFirstInstance && !second.IsFirstInstance);
    }

    using (var afterRelease = new CrossModeGuard(name))
    {
        Check(
            "跨模式守卫释放后可以重新进入",
            afterRelease.IsFirstInstance);
    }
}

void TestDataLocation()
{
    var previousOverride = Environment.GetEnvironmentVariable(
        AppPaths.DataDirectoryEnvironmentVariable);
    var pointerPath = Path.Combine(root, "pointer-tests", "data-location.json");
    var customDirectory = Path.Combine(root, "存储 目录");
    Directory.CreateDirectory(customDirectory);

    try
    {
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable, null);

        var pointer = new DataLocationPointer(pointerPath);
        Check("数据目录指针保存成功", pointer.TrySave(customDirectory, out var saveError), saveError ?? string.Empty);

        var read = pointer.Read();
        Check(
            "数据目录指针可读且路径规范化",
            read.Status == DataLocationPointerStatus.Loaded &&
            string.Equals(read.Directory, Path.TrimEndingDirectorySeparator(customDirectory), StringComparison.OrdinalIgnoreCase),
            read.Directory ?? read.Warning ?? string.Empty);

        var resolved = new DataLocationResolver(pointerPath).Resolve();
        Check(
            "有效指针覆盖默认目录",
            resolved.Source == DataLocationSource.CustomPointer &&
            string.Equals(resolved.Directory, customDirectory, StringComparison.OrdinalIgnoreCase) &&
            resolved.Warning is null,
            resolved.Directory);

        var restarted = new DataLocationResolver(pointerPath).Resolve();
        Check(
            "重启后继续解析到自定义目录",
            string.Equals(restarted.Directory, customDirectory, StringComparison.OrdinalIgnoreCase),
            restarted.Directory);

        var environmentDirectory = Path.Combine(root, "env-priority");
        Environment.SetEnvironmentVariable(
            AppPaths.DataDirectoryEnvironmentVariable,
            environmentDirectory);
        var environmentResolved = new DataLocationResolver(pointerPath).Resolve();
        Check(
            "环境变量优先级高于指针",
            environmentResolved.Source == DataLocationSource.EnvironmentOverride &&
            environmentResolved.Directory == environmentDirectory,
            environmentResolved.Directory);
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable, null);

        File.WriteAllText(pointerPath, "{ this is not json");
        var invalid = new DataLocationResolver(pointerPath).Resolve();
        Check(
            "损坏指针回退默认目录且保留指针",
            invalid.Source != DataLocationSource.CustomPointer &&
            invalid.Warning is not null &&
            File.ReadAllText(pointerPath) == "{ this is not json",
            invalid.Warning ?? string.Empty);

        File.WriteAllText(pointerPath, "{\"dataDirectory\":\"relative-path\"}");
        var relative = new DataLocationResolver(pointerPath).Resolve();
        Check(
            "相对路径指针被拒绝",
            relative.Source != DataLocationSource.CustomPointer &&
            relative.Warning is not null,
            relative.Warning ?? string.Empty);

        var missingDirectory = Path.Combine(root, "not-created-data");
        File.WriteAllText(
            pointerPath,
            "{\"dataDirectory\":" + JsonSerializer.Serialize(missingDirectory) + "}");
        var missing = new DataLocationResolver(pointerPath).Resolve();
        Check(
            "自定义目录不可用时保留指针并警告",
            missing.Source == DataLocationSource.CustomPointer &&
            missing.Warning is not null &&
            File.Exists(pointerPath),
            missing.Warning ?? string.Empty);

        File.Delete(pointerPath);
        var defaultLocation = new DataLocationResolver(pointerPath).Resolve();
        Check(
            "无指针时使用默认或回退目录",
            defaultLocation.Source is DataLocationSource.Default or DataLocationSource.Fallback &&
            defaultLocation.Directory.EndsWith(
                Path.DirectorySeparatorChar + AppPaths.AppFolderName,
                StringComparison.Ordinal),
            defaultLocation.Directory);
    }
    finally
    {
        Environment.SetEnvironmentVariable(
            AppPaths.DataDirectoryEnvironmentVariable,
            previousOverride);
    }
}

void TestStorageMigration()
{
    static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    static void WriteSettings(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, AppPaths.SettingsFileName),
            "{\"schemaVersion\":3,\"window\":null," +
            "\"hotkey\":{\"gesture\":\"Ctrl+Shift+V\"}," +
            "\"focus\":{\"selectSearchTextOnActivate\":true}," +
            "\"theme\":{\"mode\":\"system\"}}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    static void WriteHistory(string directory, string text = "first")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, AppPaths.HistoryFileName),
            "[{\"id\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"text\":" + JsonSerializer.Serialize(text) + "," +
            "\"createdAt\":\"2026-01-01T00:00:00\"," +
            "\"updatedAt\":\"2026-01-01T00:00:00\"}]",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    var successSource = Path.Combine(root, "migration-success-source");
    var successTarget = Path.Combine(root, "迁移 目标");
    WriteSettings(successSource);
    WriteHistory(successSource);
    Directory.CreateDirectory(successTarget);

    var successPointerPath = Path.Combine(root, "migration-success-pointer", "data-location.json");
    var successService = new StorageMigrationService(new DataLocationPointer(successPointerPath));
    var sourceSettingsPath = Path.Combine(successSource, AppPaths.SettingsFileName);
    var sourceHistoryPath = Path.Combine(successSource, AppPaths.HistoryFileName);
    var sourceSettingsHash = HashFile(sourceSettingsPath);
    var sourceHistoryHash = HashFile(sourceHistoryPath);

    var successValidation = successService.Validate(successSource, successTarget);
    Check("迁移预检查通过", successValidation.IsValid, successValidation.Error ?? string.Empty);

    var success = successService.Migrate(successSource, successTarget);
    Check("复制设置和历史到新目录", success.Success, success.Error ?? string.Empty);

    var targetSettingsPath = Path.Combine(successTarget, AppPaths.SettingsFileName);
    var targetHistoryPath = Path.Combine(successTarget, AppPaths.HistoryFileName);
    Check(
        "新目录文件内容与旧目录一致",
        File.Exists(targetSettingsPath) &&
        File.Exists(targetHistoryPath) &&
        HashFile(targetSettingsPath) == sourceSettingsHash &&
        HashFile(targetHistoryPath) == sourceHistoryHash,
        "哈希检查");

    Check(
        "旧目录文件未被修改或删除",
        File.Exists(sourceSettingsPath) &&
        File.Exists(sourceHistoryPath) &&
        HashFile(sourceSettingsPath) == sourceSettingsHash &&
        HashFile(sourceHistoryPath) == sourceHistoryHash,
        "旧目录保留");

    var pointer = new DataLocationPointer(successPointerPath);
    var pointerRead = pointer.Read();
    Check(
        "迁移成功后指针指向新目录",
        pointerRead.Status == DataLocationPointerStatus.Loaded &&
        string.Equals(pointerRead.Directory, successTarget, StringComparison.OrdinalIgnoreCase),
        pointerRead.Directory ?? pointerRead.Warning ?? string.Empty);

    var restarted = new DataLocationResolver(successPointerPath).Resolve();
    Check(
        "重启解析继续使用新目录",
        string.Equals(restarted.Directory, successTarget, StringComparison.OrdinalIgnoreCase),
        restarted.Directory);

    Check(
        "迁移暂存目录已清理",
        !Directory.EnumerateFileSystemEntries(successTarget, ".clipassist-migration-*").Any(),
        successTarget);

    // 模拟迁移后只写新目录：旧历史必须保持不变，用于检测分叉。
    WriteHistory(successTarget, "after-migration");
    Check(
        "迁移后新目录写入不修改旧目录",
        HashFile(sourceHistoryPath) == sourceHistoryHash,
        "旧历史哈希不变");

    // 冲突：不得覆盖目标已有设置。
    var conflictSource = Path.Combine(root, "migration-conflict-source");
    var conflictTarget = Path.Combine(root, "migration-conflict-target");
    WriteSettings(conflictSource);
    WriteHistory(conflictSource);
    Directory.CreateDirectory(conflictTarget);
    var conflictSettings = Path.Combine(conflictTarget, AppPaths.SettingsFileName);
    File.WriteAllText(conflictSettings, "target-sentinel");
    var conflictPointerPath = Path.Combine(root, "migration-conflict-pointer", "data-location.json");
    var conflictService = new StorageMigrationService(new DataLocationPointer(conflictPointerPath));
    var conflictValidation = conflictService.Validate(conflictSource, conflictTarget);
    var conflictResult = conflictService.Migrate(conflictSource, conflictTarget);
    Check(
        "目标已有同名设置时拒绝迁移",
        !conflictValidation.IsValid &&
        !conflictResult.Success &&
        File.ReadAllText(conflictSettings) == "target-sentinel" &&
        !File.Exists(conflictPointerPath),
        conflictResult.Error ?? string.Empty);

    var historyConflictTarget = Path.Combine(root, "migration-history-conflict-target");
    Directory.CreateDirectory(historyConflictTarget);
    var historyConflict = Path.Combine(historyConflictTarget, AppPaths.HistoryFileName);
    File.WriteAllText(historyConflict, "history-sentinel");
    var historyConflictValidation = new StorageMigrationService(
        new DataLocationPointer(Path.Combine(root, "migration-history-conflict-pointer", "data-location.json")))
        .Validate(conflictSource, historyConflictTarget);
    Check(
        "目标已有同名历史时拒绝迁移且不覆盖",
        !historyConflictValidation.IsValid &&
        File.ReadAllText(historyConflict) == "history-sentinel",
        historyConflictValidation.Error ?? string.Empty);

    // 目标不可写/不是目录。
    var fileTarget = Path.Combine(root, "migration-target-is-file");
    File.WriteAllText(fileTarget, "not-a-directory");
    var invalidTargetPointerPath = Path.Combine(root, "migration-invalid-target-pointer", "data-location.json");
    var invalidTargetService = new StorageMigrationService(new DataLocationPointer(invalidTargetPointerPath));
    var invalidTarget = invalidTargetService.Validate(successSource, fileTarget);
    Check(
        "目标不是目录时拒绝迁移",
        !invalidTarget.IsValid &&
        !File.Exists(invalidTargetPointerPath),
        invalidTarget.Error ?? string.Empty);

    // 目标不可写（通过注入模拟 ACL/权限拒绝）。
    var unwritableTarget = Path.Combine(root, "migration-unwritable-target");
    Directory.CreateDirectory(unwritableTarget);
    var unwritablePointerPath = Path.Combine(root, "migration-unwritable-pointer", "data-location.json");
    var unwritableService = new StorageMigrationService(
        new DataLocationPointer(unwritablePointerPath))
    {
        WritableProbeForTests = _ => false,
    };
    var unwritableValidation = unwritableService.Validate(successSource, unwritableTarget);
    var unwritableResult = unwritableService.Migrate(successSource, unwritableTarget);
    Check(
        "目标目录不可写时拒绝迁移",
        !unwritableValidation.IsValid &&
        !unwritableResult.Success &&
        !File.Exists(unwritablePointerPath),
        unwritableResult.Error ?? string.Empty);

    // 迁移中途异常：旧目录和旧指针都不能改变。
    var faultSource = Path.Combine(root, "migration-fault-source");
    var faultTarget = Path.Combine(root, "migration-fault-target");
    var oldTarget = Path.Combine(root, "migration-old-target");
    WriteSettings(faultSource);
    WriteHistory(faultSource);
    Directory.CreateDirectory(faultTarget);
    Directory.CreateDirectory(oldTarget);
    var faultPointerPath = Path.Combine(root, "migration-fault-pointer", "data-location.json");
    var faultPointer = new DataLocationPointer(faultPointerPath);
    Check("旧指针建立成功", faultPointer.TrySave(oldTarget, out var oldPointerError), oldPointerError ?? string.Empty);
    var faultService = new StorageMigrationService(faultPointer)
    {
        BeforeCommitForTests = _ => throw new IOException("注入的迁移异常"),
    };
    var faultSettingsHash = HashFile(Path.Combine(faultSource, AppPaths.SettingsFileName));
    var faultHistoryHash = HashFile(Path.Combine(faultSource, AppPaths.HistoryFileName));
    var faultResult = faultService.Migrate(faultSource, faultTarget);
    Check(
        "迁移中途异常时保留旧目录和旧指针",
        !faultResult.Success &&
        HashFile(Path.Combine(faultSource, AppPaths.SettingsFileName)) == faultSettingsHash &&
        HashFile(Path.Combine(faultSource, AppPaths.HistoryFileName)) == faultHistoryHash &&
        string.Equals(faultPointer.Read().Directory, oldTarget, StringComparison.OrdinalIgnoreCase) &&
        !File.Exists(Path.Combine(faultTarget, AppPaths.SettingsFileName)) &&
        !File.Exists(Path.Combine(faultTarget, AppPaths.HistoryFileName)),
        faultResult.Error ?? string.Empty);

    // 无历史文件：只迁移设置。
    var noHistorySource = Path.Combine(root, "migration-no-history-source");
    var noHistoryTarget = Path.Combine(root, "migration-no-history-target");
    WriteSettings(noHistorySource);
    Directory.CreateDirectory(noHistoryTarget);
    var noHistoryPointerPath = Path.Combine(root, "migration-no-history-pointer", "data-location.json");
    var noHistoryResult = new StorageMigrationService(
        new DataLocationPointer(noHistoryPointerPath)).Migrate(noHistorySource, noHistoryTarget);
    Check(
        "没有历史文件时只迁移设置",
        noHistoryResult.Success &&
        File.Exists(Path.Combine(noHistoryTarget, AppPaths.SettingsFileName)) &&
        !File.Exists(Path.Combine(noHistoryTarget, AppPaths.HistoryFileName)),
        noHistoryResult.Error ?? string.Empty);

    // 历史损坏或设置损坏：拒绝迁移。
    var corruptHistorySource = Path.Combine(root, "migration-corrupt-history-source");
    var corruptHistoryTarget = Path.Combine(root, "migration-corrupt-history-target");
    WriteSettings(corruptHistorySource);
    File.WriteAllText(Path.Combine(corruptHistorySource, AppPaths.HistoryFileName), "{ not json");
    Directory.CreateDirectory(corruptHistoryTarget);
    var corruptHistoryValidation = new StorageMigrationService(
        new DataLocationPointer(Path.Combine(root, "migration-corrupt-history-pointer", "data-location.json")))
        .Validate(corruptHistorySource, corruptHistoryTarget);
    Check(
        "历史文件损坏时拒绝迁移",
        !corruptHistoryValidation.IsValid,
        corruptHistoryValidation.Error ?? string.Empty);

    var corruptSettingsSource = Path.Combine(root, "migration-corrupt-settings-source");
    var corruptSettingsTarget = Path.Combine(root, "migration-corrupt-settings-target");
    Directory.CreateDirectory(corruptSettingsSource);
    Directory.CreateDirectory(corruptSettingsTarget);
    File.WriteAllText(Path.Combine(corruptSettingsSource, AppPaths.SettingsFileName), "{ not json");
    var corruptSettingsValidation = new StorageMigrationService(
        new DataLocationPointer(Path.Combine(root, "migration-corrupt-settings-pointer", "data-location.json")))
        .Validate(corruptSettingsSource, corruptSettingsTarget);
    Check(
        "设置文件损坏时拒绝迁移",
        !corruptSettingsValidation.IsValid,
        corruptSettingsValidation.Error ?? string.Empty);

    // 目标位于当前目录内部，或目标包含启动指针文件：拒绝。
    var nestedTarget = Path.Combine(successSource, "nested-target");
    Directory.CreateDirectory(nestedTarget);
    var nestedValidation = successService.Validate(successSource, nestedTarget);
    Check("拒绝当前目录内部的目标目录", !nestedValidation.IsValid, nestedValidation.Error ?? string.Empty);

    var parentValidation = successService.Validate(
        successSource,
        Path.GetDirectoryName(successSource)!);
    Check("拒绝当前目录的上级目标目录", !parentValidation.IsValid, parentValidation.Error ?? string.Empty);

    var pointerDirectory = Path.Combine(root, "migration-pointer-directory");
    Directory.CreateDirectory(pointerDirectory);
    var pointerDirectoryService = new StorageMigrationService(
        new DataLocationPointer(Path.Combine(pointerDirectory, AppPaths.DataLocationFileName)));
    var pointerDirectoryValidation = pointerDirectoryService.Validate(successSource, pointerDirectory);
    Check("拒绝包含启动指针的目标目录", !pointerDirectoryValidation.IsValid, pointerDirectoryValidation.Error ?? string.Empty);

    // 目标目录中只有无关文件时允许迁移，且不删除无关文件。
    var unrelatedTarget = Path.Combine(root, "migration-unrelated-target");
    Directory.CreateDirectory(unrelatedTarget);
    var unrelatedFile = Path.Combine(unrelatedTarget, "unrelated.txt");
    File.WriteAllText(unrelatedFile, "keep-me");
    var unrelatedResult = new StorageMigrationService(
        new DataLocationPointer(Path.Combine(root, "migration-unrelated-pointer", "data-location.json")))
        .Migrate(successSource, unrelatedTarget);
    Check(
        "目标无关文件不阻止迁移且保留",
        unrelatedResult.Success &&
        File.Exists(unrelatedFile) &&
        File.ReadAllText(unrelatedFile) == "keep-me",
        unrelatedResult.Error ?? string.Empty);
}

void TestLocalization()
{
    var service = LocalizationService.Instance;

    try
    {
        service.ResetForTests(LanguagePreference.ChineseSimplified);

        var chinese = service.Get("Main.Subtitle");
        var english = service.Get(LanguagePreference.English, "Main.Subtitle");
        Check(
            "中英文文案不同且非空",
            chinese.Length > 0 && english.Length > 0 && chinese != english,
            $"{chinese} / {english}");

        var incomplete = service.Keys
            .Where(key =>
                service.Get(LanguagePreference.ChineseSimplified, key) == key
                || service.Get(LanguagePreference.English, key) == key)
            .ToArray();
        Check(
            "所有本地化键都有中英文",
            service.Keys.Count > 150 && incomplete.Length == 0,
            $"keys={service.Keys.Count}, missing={string.Join(",", incomplete.Take(5))}");

        var itemChanged = 0;
        var languageChanged = 0;
        PropertyChangedEventHandler onPropertyChanged = (_, args) =>
        {
            if (args.PropertyName == "Item[]")
            {
                itemChanged++;
            }
        };
        EventHandler onLanguageChanged = (_, _) => languageChanged++;
        service.PropertyChanged += onPropertyChanged;
        service.LanguageChanged += onLanguageChanged;

        try
        {
            service.SetLanguage(LanguagePreference.English);
            Check(
                "切换语言触发 XAML 索引器刷新",
                service.CurrentLanguage == LanguagePreference.English
                    && itemChanged == 1
                    && languageChanged == 1
                    && service.Get("Main.Subtitle") == english,
                $"item={itemChanged}, language={languageChanged}");
        }
        finally
        {
            service.PropertyChanged -= onPropertyChanged;
            service.LanguageChanged -= onLanguageChanged;
        }

        Check(
            "系统语言映射",
            LanguagePreferenceExtensions.FromSystemCulture(new System.Globalization.CultureInfo("zh-CN"))
                == LanguagePreference.ChineseSimplified
                && LanguagePreferenceExtensions.FromSystemCulture(new System.Globalization.CultureInfo("en-US"))
                == LanguagePreference.English,
            "zh-CN/en-US");
        Check(
            "语言存储值解析大小写不敏感",
            LanguagePreferenceExtensions.TryParse("ZH-cn", out var parsedChinese)
                && parsedChinese == LanguagePreference.ChineseSimplified
                && LanguagePreferenceExtensions.TryParse("en-US", out var parsedEnglish)
                && parsedEnglish == LanguagePreference.English,
            "zh-CN/en-US");

        var directory = Path.Combine(root, "localization");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");

        var settings = new SettingsService(new SettingsStore(path));
        var first = settings.Load();
        Check(
            "首次启动没有已选语言",
            first.Settings.Language is not null
                && first.Settings.Language.Mode is null,
            $"mode={first.Settings.Language?.Mode ?? "(null)"}");

        var save = settings.SaveLanguage(LanguagePreference.English);
        Check("保存语言偏好成功", save.Success, save.Error ?? string.Empty);
        Check(
            "语言偏好写入存储值",
            File.ReadAllText(path).Contains("\"mode\": \"en-US\"", StringComparison.Ordinal),
            "settings.json");

        var reloaded = new SettingsService(new SettingsStore(path)).Load();
        Check(
            "语言偏好重新加载",
            LanguagePreferenceExtensions.TryParse(
                reloaded.Settings.Language?.Mode,
                out var reloadedLanguage)
                && reloadedLanguage == LanguagePreference.English,
            $"mode={reloaded.Settings.Language?.Mode ?? "(null)"}");

        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 3,
              "language": { "mode": "fr-FR" },
              "window": null
            }
            """);

        var invalid = new SettingsService(new SettingsStore(path)).Load();
        Check(
            "非法语言回退为未选择并提示",
            invalid.Settings.Language?.Mode is null
                && !string.IsNullOrWhiteSpace(invalid.Warning),
            invalid.Warning ?? "(null)");
    }
    finally
    {
        service.ResetForTests(LanguagePreference.ChineseSimplified);
    }
}

void TestSettingsStore()
{
    var directory = Path.Combine(root, "settings");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "settings.json");

    var service = new SettingsService(new SettingsStore(path));
    var missing = service.Load();
    Check(
        "无设置文件时使用默认值",
        missing.Warning is null
            && missing.FileCreated
            && missing.Settings.Window is null
            && missing.Settings.SchemaVersion == AppSettings.CurrentSchemaVersion,
        $"warning={missing.Warning ?? "(null)"}");
    Check("首次启动自动创建默认配置", File.Exists(path), path);

    var defaultJson = File.ReadAllText(path);
    Check(
        "默认配置是 schema 3 且没有窗口位置",
        defaultJson.Contains("\"schemaVersion\": 3", StringComparison.Ordinal)
            && defaultJson.Contains("\"window\": null", StringComparison.Ordinal)
            && defaultJson.Contains("\"hotkey\"", StringComparison.Ordinal)
            && defaultJson.Contains("\"gesture\": \"Ctrl+Shift+V\"", StringComparison.Ordinal)
            && defaultJson.Contains("\"focus\"", StringComparison.Ordinal)
            && defaultJson.Contains("\"selectSearchTextOnActivate\": true", StringComparison.Ordinal)
            && defaultJson.Contains("\"theme\"", StringComparison.Ordinal)
            && defaultJson.Contains("\"mode\": \"system\"", StringComparison.Ordinal),
        "默认配置内容检查");

    var expected = new WindowPlacement { Left = 100.5, Top = 80.25, Width = 900, Height = 650 };
    var saveResult = service.SaveWindowPlacement(expected);
    Check("保存窗口设置成功", saveResult.Success, saveResult.Error ?? string.Empty);

    var focusSave = service.SaveFocus(false);
    Check("保存呼出焦点设置成功", focusSave.Success, focusSave.Error ?? string.Empty);
    var focusReload = new SettingsService(new SettingsStore(path)).Load();
    Check(
        "呼出焦点设置持久化",
        !focusReload.Settings.Focus.SelectSearchTextOnActivate
            && focusReload.Settings.Window is not null,
        $"warning={focusReload.Warning ?? "(null)"}");

    var json = File.ReadAllText(path);
    Check(
        "设置使用 camelCase 字段",
        json.Contains("\"schemaVersion\"", StringComparison.Ordinal)
            && json.Contains("\"window\"", StringComparison.Ordinal)
            && json.Contains("\"left\"", StringComparison.Ordinal),
        "字段名检查");

    var loadedService = new SettingsService(new SettingsStore(path));
    var loaded = loadedService.Load();
    var actual = loaded.Settings.Window;
    Check(
        "设置往返一致",
        loaded.Warning is null
            && actual is not null
            && Math.Abs(actual.Left - expected.Left) < 0.001
            && Math.Abs(actual.Top - expected.Top) < 0.001
            && Math.Abs(actual.Width - expected.Width) < 0.001
            && Math.Abs(actual.Height - expected.Height) < 0.001,
        $"warning={loaded.Warning ?? "(null)"}");

    // v1 → v2：窗口字段不变，只升级版本号并写回。
    File.WriteAllText(
        path,
        """
        {
          "schemaVersion": 1,
          "window": { "left": 11, "top": 22, "width": 810, "height": 610 }
        }
        """);

    var migratedService = new SettingsService(new SettingsStore(path));
    var migrated = migratedService.Load();
    Check(
        "v1 设置迁移后保留窗口",
        migrated.Warning is null
            && migrated.Migrated
            && migrated.Settings.SchemaVersion == AppSettings.CurrentSchemaVersion
            && migrated.Settings.Window is not null
            && Math.Abs(migrated.Settings.Window.Width - 810) < 0.001,
        $"warning={migrated.Warning ?? "(null)"}");
    Check(
        "迁移后文件已升级为 v3",
        File.ReadAllText(path).Contains("\"schemaVersion\": 3", StringComparison.Ordinal),
        "迁移写回检查");

    // 没有 schemaVersion 的旧文件按 v1 处理。
    File.WriteAllText(
        path,
        """
        {
          "window": { "left": 12, "top": 23, "width": 820, "height": 620 }
        }
        """);

    var legacy = new SettingsService(new SettingsStore(path)).Load();
    Check(
        "没有版本号的旧设置按 v1 迁移",
        legacy.Warning is null
            && legacy.Migrated
            && legacy.Settings.Window is not null
            && Math.Abs(legacy.Settings.Window.Width - 820) < 0.001,
        $"warning={legacy.Warning ?? "(null)"}");

    // 非法 hotkey 现在是已知字段：回退默认，但不能阻止启动。
    File.WriteAllText(
        path,
        """
        {
          "schemaVersion": 2,
          "hotkey": { "gesture": "Not+A+Real+Shortcut" },
          "theme": "neon",
          "window": { "left": 13, "top": 24, "width": 830, "height": 630 }
        }
        """);

    var unknown = new SettingsService(new SettingsStore(path)).Load();
    Check(
        "非法 hotkey 回退默认且窗口仍可读取",
        unknown.Warning is not null
            && unknown.Settings.Hotkey.Gesture == HotkeyGesture.Default.DisplayText
            && unknown.Settings.Window is not null
            && Math.Abs(unknown.Settings.Window.Width - 830) < 0.001,
        $"warning={unknown.Warning ?? "(null)"}");

    // focus 字段类型错误时只重置焦点设置，窗口仍然保留。
    File.WriteAllText(
        path,
        """
        {
          "schemaVersion": 2,
          "focus": "always",
          "window": { "left": 16, "top": 27, "width": 860, "height": 660 }
        }
        """);

    var badFocus = new SettingsService(new SettingsStore(path)).Load();
    Check(
        "非法 focus 回退默认且窗口仍可读取",
        badFocus.Warning is not null
            && badFocus.Settings.Focus.SelectSearchTextOnActivate
            && badFocus.Settings.Window is not null
            && Math.Abs(badFocus.Settings.Window.Width - 860) < 0.001,
        $"warning={badFocus.Warning ?? "(null)"}");

    // Windows 记事本等工具可能写入 UTF-8 BOM；设置文件应能正常读取。
    File.WriteAllText(
        path,
        """
        {
          "schemaVersion": 2,
          "window": { "left": 15, "top": 26, "width": 850, "height": 650 }
        }
        """,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    var withBom = new SettingsService(new SettingsStore(path)).Load();
    Check(
        "带 BOM 的设置文件也能读取",
        withBom.Warning is null
            && withBom.Settings.Window is not null
            && Math.Abs(withBom.Settings.Window.Width - 850) < 0.001,
        $"warning={withBom.Warning ?? "(null)"}");

    // 损坏 JSON：备份 + 默认值 + 新的默认文件。
    File.WriteAllText(path, "{ this is not json");
    var corruptService = new SettingsService(new SettingsStore(path));
    var corrupt = corruptService.Load();
    var backups = Directory.GetFiles(directory, "settings.json.bad-*");
    Check(
        "损坏设置被备份并回退默认值",
        corrupt.Warning is not null
            && corrupt.Settings.Window is null
            && backups.Length == 1,
        $"warning={corrupt.Warning ?? "(null)"} backups={backups.Length}");
    Check(
        "损坏设置恢复后重新创建默认文件",
        File.Exists(path) && File.ReadAllText(path).Contains("\"schemaVersion\": 3", StringComparison.Ordinal),
        path);

    // 语义无效（字段类型错误）：也应备份、回退并继续运行。
    File.WriteAllText(
        path,
        """
        {
          "schemaVersion": 2,
          "window": { "left": 0, "top": 0, "width": "wide", "height": 600 }
        }
        """);

    var semanticInvalid = new SettingsService(new SettingsStore(path)).Load();
    var semanticBackups = Directory.GetFiles(directory, "settings.json.bad-*");
    Check(
        "字段类型错误的设置回退默认值",
        semanticInvalid.Warning is not null
            && semanticInvalid.Settings.Window is null
            && semanticBackups.Length >= 2,
        $"warning={semanticInvalid.Warning ?? "(null)"} backups={semanticBackups.Length}");

    // 未来版本：保留原文件，只读运行，不自动覆盖。
    var futureJson =
        """
        {
          "schemaVersion": 99,
          "window": { "left": 14, "top": 25, "width": 840, "height": 640 }
        }
        """;
    File.WriteAllText(path, futureJson);
    var future = new SettingsService(new SettingsStore(path)).Load();
    Check(
        "高于当前版本的设置只读运行",
        future.ReadOnly
            && future.Warning is not null
            && future.Settings.Window is null
            && File.ReadAllText(path) == futureJson,
        $"warning={future.Warning ?? "(null)"}");

    // 无效数值由校验器处理。
    var invalidWindow = SettingsValidator.Normalize(
        new AppSettings
        {
            Window = new WindowPlacement
            {
                Left = double.NaN,
                Top = 0,
                Width = 600,
                Height = 400,
            },
        });
    Check(
        "非有限窗口数值被清空并提示",
        invalidWindow.Changed
            && invalidWindow.Settings.Window is null
            && invalidWindow.Warning is not null,
        invalidWindow.Warning ?? "(no warning)");

    var negativeWindow = SettingsValidator.Normalize(
        new AppSettings
        {
            Window = new WindowPlacement { Left = 0, Top = 0, Width = -1, Height = 0 },
        });
    Check(
        "非正窗口尺寸被清空",
        negativeWindow.Changed && negativeWindow.Settings.Window is null);

    // 相同位置重复保存不应再次写盘。
    var duplicatePath = Path.Combine(directory, "duplicate.json");
    var duplicateService = new SettingsService(new SettingsStore(duplicatePath));
    duplicateService.Load();
    var duplicatePlacement = new WindowPlacement
    {
        Left = 50,
        Top = 60,
        Width = 700,
        Height = 500,
    };
    Check("首次保存窗口", duplicateService.SaveWindowPlacement(duplicatePlacement).Success);

    var lastWrite = File.GetLastWriteTimeUtc(duplicatePath);
    Thread.Sleep(100);
    var duplicateSave = duplicateService.SaveWindowPlacement(
        new WindowPlacement { Left = 50, Top = 60, Width = 700, Height = 500 });
    Check(
        "相同窗口位置不重复写盘",
        duplicateSave.Success && File.GetLastWriteTimeUtc(duplicatePath) == lastWrite,
        $"before={lastWrite:O} after={File.GetLastWriteTimeUtc(duplicatePath):O}");

    // 删除设置文件后应重新生成默认值。
    File.Delete(duplicatePath);
    var afterDelete = new SettingsService(new SettingsStore(duplicatePath)).Load();
    Check(
        "删除设置文件后恢复默认值并重新创建",
        afterDelete.FileCreated
            && afterDelete.Warning is null
            && afterDelete.Settings.Window is null
            && File.Exists(duplicatePath),
        afterDelete.Warning ?? "(null)");

    // 设置保存失败返回错误而不抛异常。
    var blockedDirectory = Path.Combine(root, "blocked");
    Directory.CreateDirectory(blockedDirectory);
    var blockedPath = Path.Combine(blockedDirectory, "settings.json");
    Directory.CreateDirectory(blockedPath);

    var blockedService = new SettingsService(new SettingsStore(blockedPath));
    var blockedLoad = blockedService.Load();
    Check(
        "默认设置创建失败只返回提示",
        blockedLoad.Warning is not null && blockedLoad.Settings.SchemaVersion == AppSettings.CurrentSchemaVersion,
        blockedLoad.Warning ?? "(no warning)");

    Check(
        "保存失败不残留临时文件",
        !File.Exists(blockedPath + ".tmp"),
        blockedPath + ".tmp");

    // 设置操作不碰历史文件。
    var historyPath = Path.Combine(directory, "history.json");
    const string historySentinel = "SETTINGS-MUST-NOT-TOUCH-HISTORY";
    File.WriteAllText(historyPath, historySentinel);
    var isolatedService = new SettingsService(new SettingsStore(path));
    isolatedService.Load();
    isolatedService.SaveWindowPlacement(
        new WindowPlacement { Left = 70, Top = 80, Width = 720, Height = 520 });
    Check(
        "设置更新不影响历史文件",
        File.ReadAllText(historyPath) == historySentinel,
        "history.json 内容保持不变");
    Check(
        "设置文件不包含剪贴板正文",
        !File.ReadAllText(path).Contains(historySentinel, StringComparison.Ordinal),
        "settings.json 不包含历史文本");
}

void TestThemeSettings()
{
    var directory = Path.Combine(root, "theme-settings");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "settings.json");

    var service = new SettingsService(new SettingsStore(path));
    var defaults = service.Load();
    Check(
        "默认主题跟随系统",
        defaults.Settings.Theme.Mode == ThemePreference.System.ToStorageValue(),
        defaults.Settings.Theme.Mode);
    Check(
        "主题枚举使用稳定字符串",
        ThemePreference.Light.ToStorageValue() == "light"
            && ThemePreference.Dark.ToStorageValue() == "dark"
            && ThemePreference.System.ToStorageValue() == "system"
            && ThemePreferenceExtensions.TryParse("DARK", out var parsed)
            && parsed == ThemePreference.Dark
            && !ThemePreferenceExtensions.TryParse("neon", out var invalid)
            && invalid == ThemePreference.System,
        "light/dark/system 解析检查");

    // 旧版 v3 配置没有 theme 字段：默认 System，且不产生错误提示。
    File.WriteAllText(
        path,
        """
        {
          "schemaVersion": 3,
          "window": { "left": 21, "top": 22, "width": 721, "height": 521 },
          "hotkey": { "gesture": "Ctrl+Shift+V" },
          "focus": { "selectSearchTextOnActivate": false }
        }
        """);

    var legacy = new SettingsService(new SettingsStore(path)).Load();
    Check(
        "旧配置缺少主题时默认跟随系统",
        legacy.Warning is null
            && legacy.Settings.Theme.Mode == ThemePreference.System.ToStorageValue()
            && legacy.Settings.Window is not null
            && Math.Abs(legacy.Settings.Window.Width - 721) < 0.001
            && !legacy.Settings.Focus.SelectSearchTextOnActivate,
        $"warning={legacy.Warning ?? "(null)"}");

    // 未知字符串只重置主题，不丢窗口/快捷键/焦点。
    File.WriteAllText(
        path,
        """
        {
          "schemaVersion": 3,
          "theme": { "mode": "neon" },
          "window": { "left": 23, "top": 24, "width": 731, "height": 531 },
          "hotkey": { "gesture": "Ctrl+Alt+K" },
          "focus": { "selectSearchTextOnActivate": false }
        }
        """);

    var invalidMode = new SettingsService(new SettingsStore(path)).Load();
    Check(
        "非法主题值回退系统且保留其他设置",
        invalidMode.Warning is not null
            && invalidMode.Settings.Theme.Mode == ThemePreference.System.ToStorageValue()
            && invalidMode.Settings.Window is not null
            && Math.Abs(invalidMode.Settings.Window.Width - 731) < 0.001
            && invalidMode.Settings.Hotkey.Gesture == "Ctrl+Alt+K"
            && !invalidMode.Settings.Focus.SelectSearchTextOnActivate,
        invalidMode.Warning ?? "(no warning)");

    // theme 不是对象或 mode 不是字符串时由迁移层清理，不能让整个配置失效。
    File.WriteAllText(
        path,
        """
        {
          "schemaVersion": 3,
          "theme": { "mode": 123 },
          "window": { "left": 25, "top": 26, "width": 741, "height": 541 },
          "hotkey": { "gesture": "Ctrl+Alt+K" },
          "focus": { "selectSearchTextOnActivate": false }
        }
        """);

    var malformedTheme = new SettingsService(new SettingsStore(path)).Load();
    Check(
        "错误主题类型只重置主题字段",
        malformedTheme.Warning is not null
            && malformedTheme.Settings.Theme.Mode == ThemePreference.System.ToStorageValue()
            && malformedTheme.Settings.Window is not null
            && Math.Abs(malformedTheme.Settings.Window.Width - 741) < 0.001
            && malformedTheme.Settings.Hotkey.Gesture == "Ctrl+Alt+K"
            && !malformedTheme.Settings.Focus.SelectSearchTextOnActivate,
        $"warning={malformedTheme.Warning ?? "(null)"}");

    // 保存主题只改 theme，不得提交其他字段。
    var isolationPath = Path.Combine(directory, "isolation.json");
    var isolationService = new SettingsService(new SettingsStore(isolationPath));
    isolationService.Load();
    var customHotkey = new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Alt, Key.K);
    Check(
        "主题测试准备自定义快捷键",
        isolationService.SaveHotkeyAndFocus(customHotkey, false).Success,
        "保存快捷键与焦点");

    var themeSave = isolationService.SaveTheme(ThemePreference.Dark);
    Check("保存深色主题成功", themeSave.Success, themeSave.Error ?? string.Empty);

    var themeReload = new SettingsService(new SettingsStore(isolationPath)).Load();
    Check(
        "主题持久化且不改变快捷键和焦点",
        themeReload.Settings.Theme.Mode == ThemePreference.Dark.ToStorageValue()
            && themeReload.Settings.Hotkey.Gesture == customHotkey.DisplayText
            && !themeReload.Settings.Focus.SelectSearchTextOnActivate,
        $"mode={themeReload.Settings.Theme.Mode} hotkey={themeReload.Settings.Hotkey.Gesture} focus={themeReload.Settings.Focus.SelectSearchTextOnActivate}");

    // 写盘失败必须回滚到原主题，并且不抛异常。
    var blockedParent = Path.Combine(root, "theme-blocked-parent");
    File.WriteAllText(blockedParent, "this is a file, not a directory");
    var blockedPath = Path.Combine(blockedParent, "settings.json");
    var blockedService = new SettingsService(new SettingsStore(blockedPath));
    blockedService.Load();
    var beforeTheme = blockedService.Current.Theme.Mode;
    var blockedSave = blockedService.SaveTheme(ThemePreference.Dark);
    Check(
        "主题写盘失败回滚原选择",
        !blockedSave.Success
            && blockedSave.Error is not null
            && blockedService.Current.Theme.Mode == beforeTheme,
        blockedSave.Error ?? "(no error)");

    CheckThemeContrast("Light");
    CheckThemeContrast("Dark");

    // 可注入系统主题源：System 模式随来源变化，显式 Light 不受来源影响。
    var source = new FakeSystemThemeSource(ThemePreference.Dark);
    var themeService = new ThemeService(source);
    themeService.Initialize(ThemePreference.System);
    Check(
        "System 模式解析当前系统深色",
        themeService.SelectedMode == ThemePreference.System
            && themeService.EffectiveTheme == ThemePreference.Dark,
        themeService.EffectiveTheme.ToString());

    source.Current = ThemePreference.Light;
    Check(
        "System 模式响应系统主题变化",
        themeService.RefreshSystemTheme()
            && themeService.EffectiveTheme == ThemePreference.Light,
        themeService.EffectiveTheme.ToString());

    themeService.Apply(ThemePreference.Dark);
    source.Current = ThemePreference.Light;
    Check(
        "显式主题不受系统主题变化影响",
        !themeService.RefreshSystemTheme()
            && themeService.SelectedMode == ThemePreference.Dark
            && themeService.EffectiveTheme == ThemePreference.Dark,
        $"selected={themeService.SelectedMode} effective={themeService.EffectiveTheme}");
}

void TestSystemThemeWatcher()
{
    // 只向消息窗口发送 WM_SETTINGCHANGE，不读取或修改真实系统主题。
    using var watcher = new SystemThemeWatcher();
    Check("主题消息窗口创建", watcher.Handle != IntPtr.Zero, watcher.Handle.ToString());
    Check(
        "主题监听窗口是隐藏顶层窗口",
        watcher.Handle != IntPtr.Zero
            && TestNativeMethods.GetAncestor(watcher.Handle, 2) == watcher.Handle
            && !TestNativeMethods.IsWindowVisible(watcher.Handle),
        watcher.Handle.ToString());

    var notified = false;
    watcher.Changed += (_, _) => notified = true;
    TestNativeMethods.SendMessage(watcher.Handle, 0x001A, IntPtr.Zero, IntPtr.Zero);

    var frame = new DispatcherFrame();
    var timer = new DispatcherTimer
    {
        Interval = TimeSpan.FromMilliseconds(800),
    };
    timer.Tick += (_, _) =>
    {
        timer.Stop();
        frame.Continue = false;
    };
    timer.Start();
    Dispatcher.PushFrame(frame);

    Check("WM_SETTINGCHANGE 触发主题刷新通知", notified);
}

void CheckThemeContrast(string themeName)
{
    var path = Path.Combine(AppContext.BaseDirectory, "Styles", $"ThemeColors.{themeName}.xaml");
    var document = XDocument.Load(path);
    var xNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
    var brushes = document.Descendants()
        .Where(element => element.Name.LocalName == "SolidColorBrush")
        .Select(element => new
        {
            Key = (string?)element.Attribute(xNamespace + "Key"),
            Color = (string?)element.Attribute("Color"),
        })
        .Where(item => item.Key is not null && item.Color is not null)
        .ToDictionary(
            item => item.Key!,
            item => (Color)ColorConverter.ConvertFromString(item.Color!)!);

    (string Foreground, string Background)[] pairs =
    [
        ("TextPrimaryBrush", "WindowBackgroundBrush"),
        ("TextPrimaryBrush", "SurfaceBrush"),
        ("TextSecondaryBrush", "SurfaceBrush"),
        ("TextPrimaryBrush", "ItemSelectedBrush"),
        ("TextPrimaryBrush", "SearchHighlightBrush"),
        ("DangerBrush", "WindowBackgroundBrush"),
        ("AccentTextBrush", "WindowBackgroundBrush"),
        ("AccentForegroundBrush", "AccentBrush"),
    ];

    foreach (var (foreground, background) in pairs)
    {
        var ratio = ContrastRatio(brushes[foreground], brushes[background]);
        Check(
            $"{themeName} 主题对比度 {foreground}/{background}",
            ratio >= 4.5,
            ratio.ToString("F2"));
    }
}

static double ContrastRatio(Color first, Color second)
{
    var lighter = Math.Max(Luminance(first), Luminance(second));
    var darker = Math.Min(Luminance(first), Luminance(second));
    return (lighter + 0.05) / (darker + 0.05);
}

static double Luminance(Color color)
{
    static double Channel(byte value)
    {
        var normalized = value / 255.0;
        return normalized <= 0.04045
            ? normalized / 12.92
            : Math.Pow((normalized + 0.055) / 1.055, 2.4);
    }

    return (0.2126 * Channel(color.R))
        + (0.7152 * Channel(color.G))
        + (0.0722 * Channel(color.B));
}

void TestPlacement()
{
    var virtualScreen = new Rect(0, 0, 1920, 1040);
    var workArea = new Rect(0, 0, 1920, 1000);

    Check("空位置返回 null", WindowPlacementService.Sanitize(null, 520, 380, virtualScreen, workArea) is null);

    var normal = WindowPlacementService.Sanitize(
        new WindowPlacement { Left = 100, Top = 120, Width = 800, Height = 600 },
        520,
        380,
        virtualScreen,
        workArea);

    Check(
        "正常位置保持不变",
        normal is not null && normal.Left == 100 && normal.Top == 120 && normal.Width == 800 && normal.Height == 600,
        normal is null ? "(null)" : $"{normal.Left},{normal.Top} {normal.Width}x{normal.Height}");

    var offscreen = WindowPlacementService.Sanitize(
        new WindowPlacement { Left = -5000, Top = -5000, Width = 800, Height = 600 },
        520,
        380,
        virtualScreen,
        workArea);

    Check(
        "屏幕外位置回退到工作区中央",
        offscreen is not null && offscreen.Left == 560 && offscreen.Top == 200,
        offscreen is null ? "(null)" : $"{offscreen.Left},{offscreen.Top}");

    var farRight = WindowPlacementService.Sanitize(
        new WindowPlacement { Left = 5000, Top = 3000, Width = 800, Height = 600 },
        520,
        380,
        virtualScreen,
        workArea);

    Check(
        "右下角屏幕外同样回退中央",
        farRight is not null && farRight.Left == 560 && farRight.Top == 200,
        farRight is null ? "(null)" : $"{farRight.Left},{farRight.Top}");

    var tooSmall = WindowPlacementService.Sanitize(
        new WindowPlacement { Left = 100, Top = 100, Width = 100, Height = 100 },
        520,
        380,
        virtualScreen,
        workArea);

    Check(
        "过小尺寸提升到最小尺寸",
        tooSmall is not null && tooSmall.Width == 520 && tooSmall.Height == 380,
        tooSmall is null ? "(null)" : $"{tooSmall.Width}x{tooSmall.Height}");

    var tooLarge = WindowPlacementService.Sanitize(
        new WindowPlacement { Left = 0, Top = 0, Width = 99999, Height = 99999 },
        520,
        380,
        virtualScreen,
        workArea);

    Check(
        "过大尺寸限制在虚拟桌面内",
        tooLarge is not null && tooLarge.Width == 1920 && tooLarge.Height == 1040,
        tooLarge is null ? "(null)" : $"{tooLarge.Width}x{tooLarge.Height}");

    var invalid = WindowPlacementService.Sanitize(
        new WindowPlacement { Left = double.NaN, Top = 0, Width = 800, Height = 600 },
        520,
        380,
        virtualScreen,
        workArea);

    Check("NaN 坐标视为无效", invalid is null);

    var partialTop = WindowPlacementService.Sanitize(
        new WindowPlacement { Left = 1500, Top = -100, Width = 800, Height = 600 },
        520,
        380,
        virtualScreen,
        workArea);

    Check(
        "顶部越界时整体拉回虚拟桌面内",
        partialTop is not null && partialTop.Top == 0 && partialTop.Left == 1120,
        partialTop is null ? "(null)" : $"{partialTop.Left},{partialTop.Top}");

    var partialBottom = WindowPlacementService.Sanitize(
        new WindowPlacement { Left = 1500, Top = 980, Width = 800, Height = 600 },
        520,
        380,
        virtualScreen,
        workArea);

    Check(
        "底部越界时整体拉回虚拟桌面内",
        partialBottom is not null && partialBottom.Top == 440 && partialBottom.Left == 1120,
        partialBottom is null ? "(null)" : $"{partialBottom.Left},{partialBottom.Top}");

    var secondaryWorkArea = new Rect(1920, 0, 1920, 1000);
    var secondMonitor = WindowPlacementService.Sanitize(
        null,
        520,
        380,
        new Rect(0, 0, 3840, 1040),
        secondaryWorkArea);

    Check("空位置不生成坐标", secondMonitor is null);

    var onSecondMonitor = WindowPlacementService.Sanitize(
        new WindowPlacement { Left = 2500, Top = 300, Width = 800, Height = 600 },
        520,
        380,
        new Rect(0, 0, 3840, 1040),
        secondaryWorkArea);

    Check(
        "第二显示器坐标保持有效",
        onSecondMonitor is not null && onSecondMonitor.Left == 2500 && onSecondMonitor.Top == 300,
        onSecondMonitor is null ? "(null)" : $"{onSecondMonitor.Left},{onSecondMonitor.Top}");
}

void TestCaptureAndApply()
{
    var window = new Window
    {
        Width = 900,
        Height = 650,
        MinWidth = 520,
        MinHeight = 380,
        Left = 120,
        Top = 80,
    };

    var captured = WindowPlacementService.Capture(window);
    Check(
        "未显示窗口也能按属性值捕获",
        captured is not null
            && Math.Abs(captured.Left - 120) < 0.001
            && Math.Abs(captured.Top - 80) < 0.001
            && Math.Abs(captured.Width - 900) < 0.001
            && Math.Abs(captured.Height - 650) < 0.001,
        captured is null ? "(null)" : $"{captured.Left},{captured.Top} {captured.Width}x{captured.Height}");

    var applied = new Window { MinWidth = 520, MinHeight = 380 };
    WindowPlacementService.Apply(
        applied,
        new WindowPlacement { Left = 200, Top = 150, Width = 880, Height = 620 });

    Check(
        "应用位置到窗口",
        applied.WindowStartupLocation == WindowStartupLocation.Manual
            && applied.WindowState == WindowState.Normal
            && Math.Abs(applied.Left - 200) < 0.001
            && Math.Abs(applied.Top - 150) < 0.001
            && Math.Abs(applied.Width - 880) < 0.001
            && Math.Abs(applied.Height - 620) < 0.001,
        $"startup={applied.WindowStartupLocation} state={applied.WindowState}");

    var centered = new Window();
    WindowPlacementService.Apply(centered, null);
    Check(
        "没有位置时使用居中启动",
        centered.WindowStartupLocation == WindowStartupLocation.CenterScreen,
        centered.WindowStartupLocation.ToString());
}

void TestPreviewAndTextSeparation()
{
    var longText = "START\r\n" + new string('A', 5000) + "\r\n第二行\tTAB\r\nEND";
    var entry = new ClipboardEntry { Text = longText };

    var preview = entry.Preview;
    Check(
        "预览把换行和制表符折叠成空格",
        !preview.Contains('\r') && !preview.Contains('\n') && !preview.Contains('\t')
            && preview.Contains("START", StringComparison.Ordinal)
            && preview.Contains("END", StringComparison.Ordinal),
        $"previewLength={preview.Length}");

    Check(
        "长文本的完整 Text 未被预览截断",
        entry.Text.Length == longText.Length && string.Equals(entry.Text, longText, StringComparison.Ordinal),
        $"textLength={entry.Text.Length}");
}

void TestTextHighlighter()
{
    var mixed = TextHighlighter.Split("abc ABC abc", "abc");
    Check(
        "高亮分段：同一文本三个匹配",
        mixed.Count(segment => segment.IsMatch) == 3,
        $"matches={mixed.Count(segment => segment.IsMatch)}");
    Check(
        "高亮分段：拼接后与原文完全一致",
        string.Concat(mixed.Select(segment => segment.Text)) == "abc ABC abc");
    Check(
        "高亮分段：保留原文大小写",
        mixed.Where(segment => segment.IsMatch).Select(segment => segment.Text)
            .SequenceEqual(new[] { "abc", "ABC", "abc" }));

    var chinese = TextHighlighter.Split("搜索 ClipAssist 搜索", "搜索");
    Check(
        "高亮分段：中文和英文混合",
        chinese.Count(segment => segment.IsMatch) == 2
            && string.Concat(chinese.Select(segment => segment.Text)) == "搜索 ClipAssist 搜索");

    var special = TextHighlighter.Split("括号(a.b)[c]*+? 点号.", ".");
    Check(
        "高亮分段：点号按字面量匹配",
        special.Count(segment => segment.IsMatch) == 2,
        $"matches={special.Count(segment => segment.IsMatch)}");

    var brackets = TextHighlighter.Split("x(a.b)y", "(a.b)");
    Check(
        "高亮分段：括号和特殊字符不抛异常",
        brackets.Count(segment => segment.IsMatch) == 1
            && brackets.Single(segment => segment.IsMatch).Text == "(a.b)");

    var emptySearch = TextHighlighter.Split("abc", "   ");
    Check(
        "高亮分段：空白搜索词不产生高亮",
        emptySearch.Count == 1 && !emptySearch[0].IsMatch);

    var emptySource = TextHighlighter.Split("", "abc");
    Check("高亮分段：空文本不产生分段", emptySource.Count == 0);

    var noMatch = TextHighlighter.Split("abc", "zzz");
    Check(
        "高亮分段：没有匹配时保留普通文本",
        noMatch.Count == 1 && !noMatch[0].IsMatch && noMatch[0].Text == "abc");

    var repeated = string.Concat(Enumerable.Repeat("关键词abc", 1000));
    var repeatedSegments = TextHighlighter.Split(repeated, "关键词");
    Check(
        "高亮分段：长文本的所有匹配都被处理",
        repeatedSegments.Count(segment => segment.IsMatch) == 1000
            && string.Concat(repeatedSegments.Select(segment => segment.Text)) == repeated,
        $"matches={repeatedSegments.Count(segment => segment.IsMatch)}");

    var overlaps = TextHighlighter.Split("aaaa", "aa");
    Check(
        "高亮分段：重叠查找不会死循环",
        overlaps.Count(segment => segment.IsMatch) == 2,
        $"matches={overlaps.Count(segment => segment.IsMatch)}");
}

void TestHighlightTextBlock()
{
    var block = new HighlightTextBlock
    {
        SourceText = "alpha ALPHA beta",
        SearchText = "alpha",
        HighlightBrush = Brushes.Yellow
    };

    var runs = block.Inlines.OfType<Run>().ToList();
    Check(
        "高亮控件：两个匹配段都有背景",
        runs.Count(run => run.Background is not null) == 2,
        $"highlighted={runs.Count(run => run.Background is not null)}");
    Check(
        "高亮控件：普通段没有背景",
        runs.Where(run => run.Background is null).All(run => run.Text.Length > 0));
    Check(
        "高亮控件：所有 Run 拼接后仍是原文",
        string.Concat(runs.Select(run => run.Text)) == block.SourceText);
    Check(
        "高亮控件：高亮段保留原文大小写",
        runs.Where(run => run.Background is not null).Select(run => run.Text)
            .SequenceEqual(new[] { "alpha", "ALPHA" }));

    block.SearchText = "beta";
    runs = block.Inlines.OfType<Run>().ToList();
    Check(
        "高亮控件：搜索词变化后立即更新",
        runs.Count(run => run.Background is not null) == 1
            && runs.Single(run => run.Background is not null).Text == "beta");

    block.SearchText = "";
    runs = block.Inlines.OfType<Run>().ToList();
    Check(
        "高亮控件：清空搜索后恢复普通文本",
        runs.Count == 1 && runs[0].Background is null && runs[0].Text == block.SourceText);

    block.SearchText = "   ";
    Check(
        "高亮控件：纯空格不产生高亮",
        block.Inlines.OfType<Run>().All(run => run.Background is null));

    block.SourceText = "中文搜索和中文搜索";
    block.SearchText = "搜索";
    Check(
        "高亮控件：修改原文后重建全部匹配",
        block.Inlines.OfType<Run>().Count(run => run.Background is not null) == 2);

    var emptyBlock = new HighlightTextBlock { SourceText = "", SearchText = "x" };
    Check("高亮控件：空文本不产生 Run", emptyBlock.Inlines.Count == 0);

    block.SourceText = "a.b(c)[d]*+?";
    block.SearchText = "(c)[d]*+?";
    var specialRuns = block.Inlines.OfType<Run>().ToList();
    Check(
        "高亮控件：特殊字符搜索不抛异常",
        specialRuns.Count(run => run.Background is not null) == 1
            && specialRuns.Single(run => run.Background is not null).Text == "(c)[d]*+?");
}

void TestHighlightPerformance()
{
    var source = string.Concat(Enumerable.Repeat("ClipAssist 搜索 文本 ", 240));
    var stopwatch = Stopwatch.StartNew();
    var totalSegments = 0;

    for (var i = 0; i < 500; i++)
    {
        totalSegments += TextHighlighter.Split(source, "搜索").Count;
    }

    stopwatch.Stop();
    Check(
        "大量记录高亮分段不抛异常",
        totalSegments > 0,
        $"segments={totalSegments}");
    Console.WriteLine(
        $"INFO  500 条×{source.Length} 字符高亮分段耗时: {stopwatch.ElapsedMilliseconds} ms");
}

void TestCopyEmptyFeedback()
{
    var history = new ClipboardHistory();
    using var service = new ClipboardService(history);
    var viewModel = new MainViewModel(history, service);

    viewModel.CopyEntry(new ClipboardEntry { Text = "   " });
    Check(
        "空文本复制不会假装成功",
        viewModel.StatusText.Contains("复制失败", StringComparison.Ordinal)
            && !viewModel.StatusText.Contains("已复制", StringComparison.Ordinal),
        viewModel.StatusText);
}

void TestHistoryDeletionCore()
{
    var history = new ClipboardHistory();
    var firstTime = new DateTime(2026, 10, 1, 9, 0, 0);
    var secondTime = firstTime.AddMinutes(5);
    var first = new ClipboardEntry
    {
        Id = Guid.NewGuid(),
        Text = "重复文本",
        CreatedAt = firstTime,
        UpdatedAt = firstTime,
    };
    var second = new ClipboardEntry
    {
        Id = Guid.NewGuid(),
        Text = "重复文本",
        CreatedAt = secondTime,
        UpdatedAt = secondTime,
    };
    var other = new ClipboardEntry
    {
        Id = Guid.NewGuid(),
        Text = "其他记录",
        CreatedAt = firstTime,
        UpdatedAt = firstTime,
    };

    history.ReplaceAll(new[] { first, second, other });
    Check("重复文本记录可以同时加载", history.Entries.Count == 3, $"count={history.Entries.Count}");

    var removed = history.RemoveByIds(new[] { first.Id });
    Check(
        "按 ID 只删除指定重复文本记录",
        removed.Count == 1 &&
        history.Entries.Count == 2 &&
        ReferenceEquals(history.Entries[0], second) &&
        ReferenceEquals(history.Entries[1], other) &&
        history.Entries[0].UpdatedAt == secondTime,
        $"count={history.Entries.Count}");

    history.Restore(removed);
    Check(
        "删除失败可按原索引恢复且不改内容时间",
        history.Entries.Count == 3 &&
        ReferenceEquals(history.Entries[0], first) &&
        history.Entries[0].Text == "重复文本" &&
        history.Entries[0].UpdatedAt == firstTime,
        $"count={history.Entries.Count}");
}

void TestHistoryDeletionPersistence()
{
    static StoredEntry ToStored(ClipboardEntry entry) =>
        new(entry.Id, entry.Text, entry.CreatedAt, entry.UpdatedAt);

    var directory = Path.Combine(root, "history-delete-persist");
    Directory.CreateDirectory(directory);
    var storage = new ClipboardStorage(directory);
    var history = new ClipboardHistory();
    var target = new ClipboardEntry
    {
        Id = Guid.NewGuid(),
        Text = "要删除的记录",
        CreatedAt = DateTime.Now,
        UpdatedAt = DateTime.Now,
    };
    var keeper = new ClipboardEntry
    {
        Id = Guid.NewGuid(),
        Text = "必须保留的记录",
        CreatedAt = DateTime.Now.AddMinutes(-1),
        UpdatedAt = DateTime.Now.AddMinutes(-1),
    };
    history.ReplaceAll(new[] { target, keeper });

    Func<Action<bool>?, Task<HistorySaveResult>> save = afterSave =>
    {
        try
        {
            storage.Save(history.Entries.Select(ToStored).ToList());
            afterSave?.Invoke(true);
            return Task.FromResult(new HistorySaveResult(true, null));
        }
        catch (Exception ex)
        {
            afterSave?.Invoke(false);
            return Task.FromResult(new HistorySaveResult(false, ex.Message));
        }
    };

    using var service = new ClipboardService(history);
    var viewModel = new MainViewModel(
        history,
        service,
        confirmDelete: (_, _) => true,
        saveHistory: save);

    viewModel.DeleteEntryAsync(target).GetAwaiter().GetResult();

    var loaded = storage.Load();
    Check(
        "删除后持久化文件同步移除指定 ID",
        loaded.Warning is null &&
        loaded.Entries.Count == 1 &&
        loaded.Entries[0].Id == keeper.Id &&
        loaded.Entries.All(entry => entry.Id != target.Id),
        $"count={loaded.Entries.Count} warning={loaded.Warning ?? "(null)"}");

    var restartedHistory = new ClipboardHistory();
    restartedHistory.ReplaceAll(loaded.Entries.Select(entry => new ClipboardEntry
    {
        Id = entry.Id,
        Text = entry.Text,
        CreatedAt = entry.CreatedAt,
        UpdatedAt = entry.UpdatedAt,
    }));
    Check(
        "删除后重启只恢复未删除记录",
        restartedHistory.Entries.Count == 1 &&
        restartedHistory.Entries[0].Id == keeper.Id &&
        restartedHistory.Entries[0].Text == keeper.Text,
        $"count={restartedHistory.Entries.Count}");

    // 再走一次真实 HistoryPersistence.SaveCurrentAsync，验证生产保存路径。
    var productionDirectory = Path.Combine(directory, "production");
    var productionStorage = new ClipboardStorage(productionDirectory);
    var productionHistory = new ClipboardHistory();
    var productionTarget = new ClipboardEntry
    {
        Id = Guid.NewGuid(),
        Text = "真实持久化删除",
        CreatedAt = DateTime.Now,
        UpdatedAt = DateTime.Now,
    };
    var productionKeeper = new ClipboardEntry
    {
        Id = Guid.NewGuid(),
        Text = "真实持久化保留",
        CreatedAt = DateTime.Now.AddMinutes(-1),
        UpdatedAt = DateTime.Now.AddMinutes(-1),
    };
    productionHistory.ReplaceAll(new[] { productionTarget, productionKeeper });

    using (var persistence = new HistoryPersistence(productionStorage, productionHistory))
    using (var productionService = new ClipboardService(productionHistory))
    {
        var productionViewModel = new MainViewModel(
            productionHistory,
            productionService,
            confirmDelete: (_, _) => true,
            saveHistory: persistence.SaveCurrentAsync);

        productionViewModel.DeleteEntryAsync(productionTarget).GetAwaiter().GetResult();

        var productionLoaded = productionStorage.Load();
        Check(
            "真实保存服务删除后写入正确记录",
            productionLoaded.Warning is null &&
            productionLoaded.Entries.Count == 1 &&
            productionLoaded.Entries[0].Id == productionKeeper.Id,
            $"count={productionLoaded.Entries.Count} warning={productionLoaded.Warning ?? "(null)"}");
    }
}

void TestHistoryDeletionViewModel()
{
    var history = new ClipboardHistory();
    var first = new ClipboardEntry { Text = "alpha", Id = Guid.NewGuid() };
    var second = new ClipboardEntry { Text = "alpine", Id = Guid.NewGuid() };
    var third = new ClipboardEntry { Text = "beta", Id = Guid.NewGuid() };
    var fourth = new ClipboardEntry { Text = "gamma", Id = Guid.NewGuid() };
    history.ReplaceAll(new[] { first, second, third, fourth });

    var saveCalls = 0;
    var confirmCalls = 0;
    var lastConfirmMessage = string.Empty;
    var confirmResult = false;

    Func<string, string, bool> confirm = (_, message) =>
    {
        confirmCalls++;
        lastConfirmMessage = message;
        return confirmResult;
    };

    Func<Action<bool>?, Task<HistorySaveResult>> save = afterSave =>
    {
        saveCalls++;
        afterSave?.Invoke(true);
        return Task.FromResult(new HistorySaveResult(true, null));
    };

    using var service = new ClipboardService(history);
    var viewModel = new MainViewModel(history, service, confirmDelete: confirm, saveHistory: save);

    // 取消单条删除：不能修改数据或触发保存。
    viewModel.DeleteEntryAsync(third).GetAwaiter().GetResult();
    Check(
        "取消单条删除保留记录且不保存",
        history.Entries.Count == 4 &&
        history.Entries.Contains(third) &&
        saveCalls == 0 &&
        confirmCalls == 1 &&
        lastConfirmMessage.Contains(third.Text, StringComparison.Ordinal),
        $"count={history.Entries.Count} save={saveCalls}");

    // 确认单条删除：只移除指定 ID。
    confirmResult = true;
    viewModel.DeleteEntryAsync(third).GetAwaiter().GetResult();
    Check(
        "确认单条删除只移除目标记录",
        history.Entries.Count == 3 &&
        history.Entries.All(entry => entry.Id != third.Id) &&
        history.Entries.Contains(first) &&
        saveCalls == 1 &&
        viewModel.StatusText.Contains("已删除", StringComparison.Ordinal),
        viewModel.StatusText);

    // 搜索结果中单条删除。
    viewModel.SearchText = "alp";
    var searched = viewModel.VisibleEntries.Single(entry => entry.Id == first.Id);
    viewModel.DeleteEntryAsync(searched).GetAwaiter().GetResult();
    Check(
        "搜索结果中单条删除不影响未显示记录",
        viewModel.VisibleEntries.Count == 1 &&
        viewModel.VisibleEntries[0].Id == second.Id &&
        history.Entries.Any(entry => entry.Id == fourth.Id),
        $"visible={viewModel.VisibleEntries.Count}");

    // 进入多选，选择当前显示结果并批量删除。
    viewModel.SetSelectionMode(true);
    viewModel.UpdateSelectedEntries(viewModel.VisibleEntries);
    Check(
        "多选数量与当前可见结果一致",
        viewModel.SelectedCount == 1 && viewModel.SelectionCountText.Contains("已选 1", StringComparison.Ordinal),
        viewModel.SelectionCountText);

    confirmResult = false;
    viewModel.DeleteSelectedAsync().GetAwaiter().GetResult();
    Check(
        "取消批量删除保留全部选中记录",
        history.Entries.Count == 2 &&
        history.Entries.Any(entry => entry.Id == second.Id) &&
        saveCalls == 2,
        $"count={history.Entries.Count} save={saveCalls}");

    // 搜索结果改变时清空旧选择，避免删除隐藏记录。
    viewModel.SearchText = "beta";
    Check(
        "搜索变化清空多选",
        viewModel.SelectedCount == 0 && !viewModel.HasSelection,
        $"selected={viewModel.SelectedCount}");

    // 当前显示全选：只选择过滤结果，保留未显示的 beta/gamma。
    viewModel.SearchText = "alp";
    viewModel.SetSelectionMode(true);
    var visibleBefore = viewModel.VisibleEntries.ToList();
    viewModel.UpdateSelectedEntries(visibleBefore);
    var expectedIds = visibleBefore.Select(entry => entry.Id).ToHashSet();
    confirmResult = true;
    viewModel.DeleteSelectedAsync().GetAwaiter().GetResult();
    Check(
        "批量删除当前显示记录且保留未显示记录",
        history.Entries.Count == 1 &&
        history.Entries[0].Id == fourth.Id &&
        history.Entries.All(entry => !expectedIds.Contains(entry.Id)),
        $"count={history.Entries.Count} visible={visibleBefore.Count}");

    // 失败保存必须回滚，不能把界面留在“已删除”状态。
    var rollbackHistory = new ClipboardHistory();
    var retained = new ClipboardEntry { Text = "保留", Id = Guid.NewGuid() };
    var doomed = new ClipboardEntry { Text = "删除失败", Id = Guid.NewGuid() };
    rollbackHistory.ReplaceAll(new[] { retained, doomed });
    using var rollbackService = new ClipboardService(rollbackHistory);
    var rollbackViewModel = new MainViewModel(
        rollbackHistory,
        rollbackService,
        confirmDelete: (_, _) => true,
        saveHistory: afterSave =>
        {
            afterSave?.Invoke(false);
            return Task.FromResult(new HistorySaveResult(false, "模拟磁盘不可写"));
        });

    rollbackViewModel.DeleteEntryAsync(doomed).GetAwaiter().GetResult();
    Check(
        "保存失败恢复记录且不报告成功",
        rollbackHistory.Entries.Count == 2 &&
        rollbackHistory.Entries.Contains(doomed) &&
        rollbackViewModel.StatusText.Contains("模拟磁盘不可写", StringComparison.Ordinal) &&
        !rollbackViewModel.StatusText.Contains("已删除", StringComparison.Ordinal),
        rollbackViewModel.StatusText);
}

void TestHistoryDeletionBatchScope()
{
    var history = new ClipboardHistory();
    var alpha = new ClipboardEntry { Text = "alpha", Id = Guid.NewGuid() };
    var alpine = new ClipboardEntry { Text = "alpine", Id = Guid.NewGuid() };
    var beta = new ClipboardEntry { Text = "beta", Id = Guid.NewGuid() };
    var gamma = new ClipboardEntry { Text = "gamma", Id = Guid.NewGuid() };
    history.ReplaceAll(new[] { alpha, alpine, beta, gamma });

    var confirmCalls = 0;
    var lastMessage = string.Empty;
    using var service = new ClipboardService(history);
    var viewModel = new MainViewModel(
        history,
        service,
        confirmDelete: (_, message) =>
        {
            confirmCalls++;
            lastMessage = message;
            return true;
        },
        saveHistory: afterSave =>
        {
            afterSave?.Invoke(true);
            return Task.FromResult(new HistorySaveResult(true, null));
        });

    viewModel.SearchText = "alp";
    viewModel.SetSelectionMode(true);
    viewModel.UpdateSelectedEntries(viewModel.VisibleEntries);
    Check(
        "批量选择当前可见的两条记录",
        viewModel.SelectedCount == 2,
        viewModel.SelectionCountText);

    viewModel.DeleteSelectedAsync().GetAwaiter().GetResult();
    Check(
        "批量删除确认提示数量和影响范围",
        confirmCalls == 1 &&
        lastMessage.Contains("2 条", StringComparison.Ordinal) &&
        lastMessage.Contains("未选中", StringComparison.Ordinal),
        lastMessage);

    Check(
        "批量删除后仅保留未显示记录",
        history.Entries.Count == 2 &&
        history.Entries.Any(entry => entry.Id == beta.Id) &&
        history.Entries.Any(entry => entry.Id == gamma.Id) &&
        !viewModel.IsSelectionMode &&
        viewModel.SelectedCount == 0,
        $"count={history.Entries.Count}");

    // 取消批量删除时，两条都不删除。
    var cancelHistory = new ClipboardHistory();
    var cancelA = new ClipboardEntry { Text = "a", Id = Guid.NewGuid() };
    var cancelB = new ClipboardEntry { Text = "b", Id = Guid.NewGuid() };
    var cancelC = new ClipboardEntry { Text = "c", Id = Guid.NewGuid() };
    cancelHistory.ReplaceAll(new[] { cancelA, cancelB, cancelC });
    using var cancelService = new ClipboardService(cancelHistory);
    var cancelViewModel = new MainViewModel(
        cancelHistory,
        cancelService,
        confirmDelete: (_, _) => false);

    cancelViewModel.SetSelectionMode(true);
    cancelViewModel.UpdateSelectedEntries(new[] { cancelA, cancelB });
    cancelViewModel.DeleteSelectedAsync().GetAwaiter().GetResult();
    Check(
        "取消批量删除两条记录均保留",
        cancelHistory.Entries.Count == 3 &&
        cancelHistory.Entries.Contains(cancelA) &&
        cancelHistory.Entries.Contains(cancelB) &&
        cancelHistory.Entries.Contains(cancelC),
        $"count={cancelHistory.Entries.Count}");
}

void TestHistoryDeletionSaveFailureReal()
{
    var blockedPath = Path.Combine(root, "blocked-history-directory");
    File.WriteAllText(blockedPath, "this file intentionally blocks the directory");

    var storage = new ClipboardStorage(blockedPath);
    var history = new ClipboardHistory();
    var retained = new ClipboardEntry { Text = "保留", Id = Guid.NewGuid() };
    var doomed = new ClipboardEntry { Text = "删除失败", Id = Guid.NewGuid() };
    history.ReplaceAll(new[] { retained, doomed });

    using var persistence = new HistoryPersistence(storage, history);
    var result = persistence.SaveCurrentAsync().GetAwaiter().GetResult();

    Check(
        "真实保存服务失败时返回失败结果",
        !result.Success &&
        !string.IsNullOrWhiteSpace(result.Error) &&
        result.Error.Contains("保存历史失败", StringComparison.Ordinal),
        result.Error ?? "(null)");
    Check(
        "真实保存失败不修改内存记录",
        history.Entries.Count == 2 &&
        history.Entries.Contains(doomed) &&
        history.Entries.Contains(retained),
        $"count={history.Entries.Count}");
}

void TestClearAllDeletion()
{
    var history = new ClipboardHistory();
    var first = new ClipboardEntry { Text = "清空一", Id = Guid.NewGuid() };
    var second = new ClipboardEntry { Text = "清空二", Id = Guid.NewGuid() };
    var third = new ClipboardEntry { Text = "清空三", Id = Guid.NewGuid() };
    history.ReplaceAll(new[] { first, second, third });

    var saveCalls = 0;
    using var service = new ClipboardService(history);
    var viewModel = new MainViewModel(
        history,
        service,
        confirmDelete: (_, _) => true,
        saveHistory: afterSave =>
        {
            saveCalls++;
            afterSave?.Invoke(true);
            return Task.FromResult(new HistorySaveResult(true, null));
        });

    viewModel.ClearCommand.Execute(null);
    Check(
        "确认清空全部历史并保存",
        history.Entries.Count == 0 &&
        saveCalls == 1 &&
        viewModel.StatusText.Contains("已清空", StringComparison.Ordinal),
        viewModel.StatusText);

    var failureHistory = new ClipboardHistory();
    var failureFirst = new ClipboardEntry { Text = "失败一", Id = Guid.NewGuid() };
    var failureSecond = new ClipboardEntry { Text = "失败二", Id = Guid.NewGuid() };
    failureHistory.ReplaceAll(new[] { failureFirst, failureSecond });
    using var failureService = new ClipboardService(failureHistory);
    var failureViewModel = new MainViewModel(
        failureHistory,
        failureService,
        confirmDelete: (_, _) => true,
        saveHistory: afterSave =>
        {
            afterSave?.Invoke(false);
            return Task.FromResult(new HistorySaveResult(false, "清空保存失败"));
        });

    failureViewModel.ClearCommand.Execute(null);
    Check(
        "清空保存失败时恢复全部记录并报告失败",
        failureHistory.Entries.Count == 2 &&
        failureHistory.Entries.Contains(failureFirst) &&
        failureHistory.Entries.Contains(failureSecond) &&
        failureViewModel.StatusText.Contains("清空保存失败", StringComparison.Ordinal) &&
        !failureViewModel.StatusText.Contains("已清空", StringComparison.Ordinal),
        failureViewModel.StatusText);

    var cancelHistory = new ClipboardHistory();
    var cancelEntry = new ClipboardEntry { Text = "不清空", Id = Guid.NewGuid() };
    cancelHistory.ReplaceAll(new[] { cancelEntry });
    using var cancelService = new ClipboardService(cancelHistory);
    var cancelViewModel = new MainViewModel(
        cancelHistory,
        cancelService,
        confirmDelete: (_, _) => false);

    cancelViewModel.ClearCommand.Execute(null);
    Check(
        "取消清空保留全部记录",
        cancelHistory.Entries.Count == 1 && cancelHistory.Entries.Contains(cancelEntry),
        $"count={cancelHistory.Entries.Count}");
}

void TestHistoryBackwardCompatibility()
{
    var directory = Path.Combine(root, "history");
    Directory.CreateDirectory(directory);

    File.WriteAllText(
        Path.Combine(directory, "history.json"),
        """
        [
          {
            "id": "11111111-1111-1111-1111-111111111111",
            "text": "V0.1 旧数据",
            "createdAt": "2026-10-01T09:00:00+08:00",
            "updatedAt": "2026-10-01T09:00:00+08:00"
          }
        ]
        """);

    var result = new ClipboardStorage(directory).Load();
    Check(
        "V0.1 历史文件仍可读取",
        result.Warning is null && result.Entries.Count == 1 && result.Entries[0].Text == "V0.1 旧数据",
        $"count={result.Entries.Count} warning={result.Warning ?? "(null)"}");
}

void TestStartupRegistration()
{
    var startupDirectory = Path.Combine(root, "Clip Assist 测试");
    Directory.CreateDirectory(startupDirectory);

    var currentExe = Path.Combine(startupDirectory, "ClipAssist.exe");
    var staleExe = Path.Combine(startupDirectory, "old", "ClipAssist.exe");
    var missingExe = Path.Combine(startupDirectory, "missing", "ClipAssist.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(staleExe)!);
    File.WriteAllText(currentExe, "test");
    File.WriteAllText(staleExe, "old");
    File.WriteAllText(Path.Combine(startupDirectory, "settings.json"), "settings-sentinel");
    File.WriteAllText(Path.Combine(startupDirectory, "history.json"), "history-sentinel");

    const string valueName = "ClipAssist";
    var expectedCommand = "\"" + Path.GetFullPath(currentExe) + "\" "
        + StartupService.StartupArgument;
    // 不写入真实 HKCU Run；测试在内存注册表实现上验证同一套业务逻辑。
    var registry = new InMemoryStartupRegistry();
    var service = new StartupService(currentExe, valueName, registry);

    Check("启动项不存在时状态为关闭", service.GetStatus().State == StartupState.Disabled);

    var enabled = service.Enable();
    Check(
        "开启后创建当前用户启动项",
        enabled.Success && enabled.Status.IsEnabled,
        enabled.Error ?? enabled.Status.Message ?? "(null)");
    Check(
        "启动项路径带引号且适用于空格/中文路径",
        registry.ReadString(valueName) == expectedCommand,
        registry.ReadString(valueName) ?? "(null)");
    Check(
        "启动项只有一个 ClipAssist 值",
        registry.ValueCount == 1 && registry.ReadString(valueName) is not null);

    var reopened = new StartupService(currentExe, valueName, registry);
    Check("重新打开设置时读取真实开启状态", reopened.GetStatus().IsEnabled);

    var repeated = service.Enable();
    Check(
        "重复开启成功且不创建重复值",
        repeated.Success && registry.ValueCount == 1);
    Check(
        "启动命令包含静默启动参数",
        expectedCommand.EndsWith(" " + StartupService.StartupArgument, StringComparison.Ordinal));

    registry.Write(
        valueName,
        "\"" + Path.GetFullPath(currentExe) + "\"",
        RegistryValueKind.String);
    var legacyStatus = service.GetStatus();
    Check(
        "旧启动命令仍识别为开启并提示静默参数",
        legacyStatus.IsEnabled
            && legacyStatus.Message?.Contains("静默", StringComparison.Ordinal) == true,
        legacyStatus.Message ?? "(null)");

    var legacyRepair = service.Enable();
    Check(
        "重新开启会把旧命令更新为静默启动命令",
        legacyRepair.Success && registry.ReadString(valueName) == expectedCommand,
        legacyRepair.Error ?? "(null)");

    registry.Write("OtherApp", "\"other.exe\"", RegistryValueKind.String);
    var disabled = service.Disable();
    Check(
        "关闭只移除 ClipAssist 自己的启动项",
        disabled.Success
            && !disabled.Status.IsEnabled
            && registry.ReadString(valueName) is null
            && registry.ReadString("OtherApp") == "\"other.exe\"");
    Check("重复关闭不抛异常", service.Disable().Success);

    registry.Write(valueName, "\"" + Path.GetFullPath(staleExe) + "\"", RegistryValueKind.String);
    var staleStatus = service.GetStatus();
    Check(
        "旧路径启动项显示开启并提示更新",
        staleStatus.IsEnabled && !string.IsNullOrWhiteSpace(staleStatus.Message),
        staleStatus.Message ?? "(null)");

    var repaired = service.Enable();
    Check(
        "重新开启会修复旧路径",
        repaired.Success && registry.ReadString(valueName) == expectedCommand,
        repaired.Error ?? "(null)");

    registry.Write(valueName, "\"" + Path.GetFullPath(missingExe) + "\"", RegistryValueKind.String);
    var invalidStatus = service.GetStatus();
    Check(
        "失效路径不显示为开启且给出提示",
        !invalidStatus.IsEnabled && !string.IsNullOrWhiteSpace(invalidStatus.Message),
        invalidStatus.Message ?? "(null)");

    var invalidService = new StartupService(missingExe, valueName, registry);
    var invalidEnable = invalidService.Enable();
    Check(
        "程序路径无效时开启失败，不虚报成功",
        !invalidEnable.Success && !invalidEnable.Status.IsEnabled,
        invalidEnable.Error ?? "(null)");

    var repairedAgain = service.Enable();
    Check(
        "失效项可由当前有效路径重新修复",
        repairedAgain.Success && registry.ReadString(valueName) == expectedCommand);

    var writeFailureRegistry = new InMemoryStartupRegistry { FailWrites = true };
    var writeFailure = new StartupService(currentExe, valueName, writeFailureRegistry).Enable();
    Check(
        "注册表写入失败时开关不虚报开启",
        !writeFailure.Success
            && !writeFailure.Status.IsEnabled
            && writeFailureRegistry.ReadString(valueName) is null
            && writeFailure.Error?.Contains("受限", StringComparison.Ordinal) == true,
        writeFailure.Error ?? "(null)");

    var readFailureRegistry = new InMemoryStartupRegistry { FailReads = true };
    var readFailure = new StartupService(currentExe, valueName, readFailureRegistry).GetStatus();
    Check(
        "注册表读取失败时不谎报关闭状态",
        readFailure.State == StartupState.Unknown
            && !readFailure.IsEnabled
            && readFailure.Message?.Contains("受限", StringComparison.Ordinal) == true,
        readFailure.Message ?? "(null)");

    var enableReadFailureRegistry = new InMemoryStartupRegistry { FailReads = true };
    var enableReadFailure = new StartupService(currentExe, valueName, enableReadFailureRegistry).Enable();
    Check(
        "开启时读取失败不误报为写入失败",
        !enableReadFailure.Success
            && enableReadFailure.Error?.Contains("读取", StringComparison.Ordinal) == true,
        enableReadFailure.Error ?? "(null)");

    Check(
        "设置和历史文件未被启动项操作修改",
        File.ReadAllText(Path.Combine(startupDirectory, "settings.json")) == "settings-sentinel"
            && File.ReadAllText(Path.Combine(startupDirectory, "history.json")) == "history-sentinel");

    service.Disable();
}

void TestStartupModeIsolation()
{
    var directory = Path.Combine(root, "startup-mode-isolation");
    Directory.CreateDirectory(directory);

    var executable = Path.Combine(directory, "ClipAssist.exe");
    File.WriteAllText(executable, "test");

    var registry = new InMemoryStartupRegistry();
    var installed = new StartupService(
        executable,
        AppIdentity.InstalledModeName,
        registry);
    var portable = new StartupService(
        executable,
        AppIdentity.PortableModeName,
        registry);

    Check(
        "两种模式初始都没有启动项",
        !installed.GetStatus().IsEnabled && !portable.GetStatus().IsEnabled);

    var installedEnable = installed.Enable();
    var portableEnable = portable.Enable();
    Check(
        "两种模式可以分别拥有启动项",
        installedEnable.Success
            && portableEnable.Success
            && registry.ValueCount == 2
            && registry.ReadString(AppIdentity.InstalledModeName) is not null
            && registry.ReadString(AppIdentity.PortableModeName) is not null,
        installedEnable.Error ?? portableEnable.Error ?? "(null)");

    var portableDisable = portable.Disable();
    Check(
        "关闭便携版启动项不影响安装版",
        portableDisable.Success
            && registry.ReadString(AppIdentity.PortableModeName) is null
            && registry.ReadString(AppIdentity.InstalledModeName) is not null,
        portableDisable.Error ?? "(null)");

    var installedDisable = installed.Disable();
    Check(
        "关闭安装版启动项不残留便携版值",
        installedDisable.Success
            && registry.ReadString(AppIdentity.InstalledModeName) is null
            && registry.ReadString(AppIdentity.PortableModeName) is null
            && registry.ValueCount == 0,
        installedDisable.Error ?? "(null)");
}

void TestHotkeyGesture()
{
    Check(
        "默认快捷键是 Ctrl+Shift+V",
        HotkeyGesture.Default.DisplayText == "Ctrl+Shift+V");

    Check(
        "解析标准快捷键",
        HotkeyGesture.TryParse("Ctrl+Shift+V", out var standard, out _)
            && standard == HotkeyGesture.Default
            && standard.DisplayText == "Ctrl+Shift+V");

    Check(
        "解析大小写与别名",
        HotkeyGesture.TryParse(" control + alt + k ", out var aliased, out _)
            && aliased.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)
            && aliased.Key == Key.K
            && aliased.DisplayText == "Ctrl+Alt+K");

    Check(
        "功能键允许一个修饰键",
        HotkeyGesture.TryParse("Ctrl+F1", out var function, out _)
            && function.DisplayText == "Ctrl+F1");

    Check(
        "导航键允许一个修饰键",
        HotkeyGesture.TryParse("Alt+Left", out var navigation, out _)
            && navigation.DisplayText == "Alt+Left");

    Check(
        "数字键规范化显示",
        HotkeyGesture.TryParse("Ctrl+Shift+0", out var digit, out _)
            && digit.DisplayText == "Ctrl+Shift+0");

    var invalidCombinations = new[]
    {
        "A",
        "Shift+A",
        "Ctrl+A",
        "Ctrl+Shift",
        "Ctrl+Alt+Delete",
        "Ctrl+Shift+Esc",
        "Ctrl+Esc",
        "Alt+Tab",
        "Alt+F4",
        "Win+L",
        "Win+V",
        "Ctrl+Shift+Mouse",
    };

    foreach (var text in invalidCombinations)
    {
        Check(
            $"拒绝非法组合 {text}",
            !HotkeyGesture.TryParse(text, out _, out var error) && !string.IsNullOrWhiteSpace(error),
            error ?? "(no error)");
    }

    Check(
        "直接构造时也拒绝 Shift+字母",
        !HotkeyGesture.TryCreate(ModifierKeys.Shift, Key.A, out _, out _));

    Check(
        "直接构造时拒绝单修饰键+字母",
        !HotkeyGesture.TryCreate(ModifierKeys.Control, Key.A, out _, out _));

    Check(
        "直接构造时接受双修饰键+字母",
        HotkeyGesture.TryCreate(
            ModifierKeys.Control | ModifierKeys.Alt,
            Key.K,
            out var constructed,
            out _)
            && constructed.DisplayText == "Ctrl+Alt+K");
}

void TestHotkeyPersistenceAndAtomicRegistration()
{
    var directory = Path.Combine(root, "hotkey");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "settings.json");

    var settings = new SettingsService(new SettingsStore(path));
    settings.Load();
    Check(
        "默认设置包含默认快捷键",
        settings.Current.Hotkey.Gesture == HotkeyGesture.Default.DisplayText,
        settings.Current.Hotkey.Gesture);

    var custom = new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Alt, Key.K);
    var save = settings.SaveHotkey(custom);
    Check("保存自定义快捷键", save.Success, save.Error ?? string.Empty);

    var combined = settings.SaveHotkeyAndFocus(custom, false);
    Check("快捷键与焦点设置一次保存", combined.Success, combined.Error ?? string.Empty);
    var combinedReload = new SettingsService(new SettingsStore(path)).Load();
    Check(
        "一次保存同时持久化快捷键和焦点设置",
        combinedReload.Settings.Hotkey.Gesture == "Ctrl+Alt+K"
            && !combinedReload.Settings.Focus.SelectSearchTextOnActivate,
        $"warning={combinedReload.Warning ?? "(null)"}");

    var focusOnly = settings.SaveFocus(true);
    Check("单独保存焦点设置", focusOnly.Success, focusOnly.Error ?? string.Empty);

    var reloaded = new SettingsService(new SettingsStore(path)).Load();
    Check(
        "重启后读取自定义快捷键",
        reloaded.Warning is null
            && reloaded.Settings.Hotkey.Gesture == "Ctrl+Alt+K",
        reloaded.Settings.Hotkey.Gesture);

    Check(
        "非法快捷键不会写入配置",
        !settings.SaveHotkey(new HotkeyGesture(ModifierKeys.Shift, Key.A)).Success
            && settings.Current.Hotkey.Gesture == "Ctrl+Alt+K",
        settings.Current.Hotkey.Gesture);

    // v2 文件没有 hotkey 字段，迁移时应补默认值并保留窗口。
    var migrationPath = Path.Combine(directory, "migration.json");
    File.WriteAllText(
        migrationPath,
        """
        {
          "schemaVersion": 2,
          "window": { "left": 10, "top": 20, "width": 800, "height": 600 }
        }
        """);

    var migrated = new SettingsService(new SettingsStore(migrationPath)).Load();
    Check(
        "v2 迁移到 v3 时补默认快捷键",
        migrated.Warning is null
            && migrated.Settings.SchemaVersion == 3
            && migrated.Settings.Hotkey.Gesture == "Ctrl+Shift+V"
            && migrated.Settings.Window is not null,
        migrated.Warning ?? "(null)");

    // V0.2-04 把 hotkey 当未知字段，旧文件里可能是字符串；不能因此丢掉窗口。
    var legacyStringPath = Path.Combine(directory, "legacy-string.json");
    File.WriteAllText(
        legacyStringPath,
        """
        {
          "schemaVersion": 3,
          "hotkey": "Ctrl+Shift+V",
          "window": { "left": 11, "top": 22, "width": 640, "height": 480 }
        }
        """);

    var legacyString = new SettingsService(new SettingsStore(legacyStringPath)).Load();
    Check(
        "字符串形式的旧 hotkey 只重置快捷键并保留窗口",
        legacyString.Warning is not null
            && legacyString.Settings.Hotkey.Gesture == "Ctrl+Shift+V"
            && legacyString.Settings.Window is not null
            && Math.Abs(legacyString.Settings.Window.Width - 640) < 0.001
            && File.ReadAllText(legacyStringPath).Contains("\"gesture\"", StringComparison.Ordinal),
        $"warning={legacyString.Warning ?? "(null)"}");

    // 原子注册：占用 B 时尝试切换到 B，必须保留 A。
    var oldGesture = new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Alt, Key.F13);
    var occupiedGesture = new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Alt, Key.F14);

    var blocker = new HotkeyService();
    var blockerRegistration = blocker.TryBeginRegistration(occupiedGesture);
    Check(
        "测试进程成功占用 F14 组合",
        blockerRegistration is not null,
        blocker.LastError ?? "(null)");

    if (blockerRegistration is null)
    {
        blocker.Dispose();
        return;
    }

    blocker.CommitRegistration(blockerRegistration);

    var primary = new HotkeyService();
    var oldRegistration = primary.TryBeginRegistration(oldGesture);
    Check(
        "测试进程成功注册原快捷键",
        oldRegistration is not null,
        primary.LastError ?? "(null)");

    if (oldRegistration is null)
    {
        primary.Dispose();
        blocker.Dispose();
        return;
    }

    primary.CommitRegistration(oldRegistration);

    var duplicate = primary.TryBeginRegistration(oldGesture);
    Check(
        "相同快捷键不会重复注册",
        duplicate is null && primary.CurrentGesture == oldGesture,
        primary.LastError ?? "(null)");

    var conflict = primary.TryBeginRegistration(occupiedGesture);
    Check(
        "被其他进程占用时新注册失败",
        conflict is null && !string.IsNullOrWhiteSpace(primary.LastError),
        primary.LastError ?? "(null)");

    Check(
        "注册失败后原快捷键仍然有效",
        primary.IsRegistered && primary.CurrentGesture == oldGesture);

    blocker.Dispose();

    var replacement = primary.TryBeginRegistration(occupiedGesture);
    Check(
        "占用释放后可以注册新快捷键",
        replacement is not null,
        primary.LastError ?? "(null)");

    if (replacement is not null)
    {
        primary.CommitRegistration(replacement);
    }

    Check(
        "提交后当前快捷键已替换",
        primary.CurrentGesture == occupiedGesture);

    primary.Dispose();

    var fresh = new HotkeyService();
    var reusable = fresh.TryBeginRegistration(oldGesture);
    Check(
        "释放服务后原组合可被重新注册",
        reusable is not null,
        fresh.LastError ?? "(null)");

    if (reusable is not null)
    {
        fresh.CommitRegistration(reusable);
    }

    fresh.Dispose();
    blocker.Dispose();
}

void TestHotkeyManagerFallbackAndRollback()
{
    var directory = Path.Combine(root, "hotkey-manager");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "settings.json");

    var saved = new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Alt, Key.F13);
    File.WriteAllText(
        path,
        $$"""
        {
          "schemaVersion": 3,
          "hotkey": { "gesture": "{{saved.DisplayText}}" }
        }
        """);

    var settings = new SettingsService(new SettingsStore(path));
    settings.Load();

    var blocker = new HotkeyService();
    var blockerRegistration = blocker.TryBeginRegistration(saved);
    Check(
        "启动回退测试成功占用已保存组合",
        blockerRegistration is not null,
        blocker.LastError ?? "(null)");

    if (blockerRegistration is null)
    {
        blocker.Dispose();
        return;
    }

    blocker.CommitRegistration(blockerRegistration);

    var manager = new HotkeyManager(new HotkeyService(), settings);
    var initialized = manager.Initialize();
    Check(
        "已保存组合被占用时启动回退到默认组合",
        initialized.EffectiveGesture == HotkeyGesture.Default
            && manager.SavedGesture == saved,
        initialized.Warning ?? "(null)");
    Check(
        "启动回退不覆盖已保存配置",
        settings.Current.Hotkey.Gesture == saved.DisplayText,
        settings.Current.Hotkey.Gesture);

    blocker.Dispose();

    var occupied = new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Alt, Key.F14);
    var secondBlocker = new HotkeyService();
    var secondRegistration = secondBlocker.TryBeginRegistration(occupied);
    Check(
        "更换失败测试成功占用目标组合",
        secondRegistration is not null,
        secondBlocker.LastError ?? "(null)");

    if (secondRegistration is not null)
    {
        secondBlocker.CommitRegistration(secondRegistration);

        var failed = manager.Apply(occupied);
        Check(
            "更换到被占用组合失败且保留当前快捷键",
            !failed.Success && manager.EffectiveGesture == HotkeyGesture.Default,
            failed.Error ?? "(null)");
        Check(
            "更换失败不覆盖已保存配置",
            settings.Current.Hotkey.Gesture == saved.DisplayText,
            settings.Current.Hotkey.Gesture);

        secondBlocker.Dispose();

        var applied = manager.Apply(occupied);
        Check(
            "占用释放后更换成功并写入配置",
            applied.Success
                && manager.EffectiveGesture == occupied
                && settings.Current.Hotkey.Gesture == occupied.DisplayText,
            applied.Error ?? "(null)");
    }

    manager.Dispose();
    secondBlocker.Dispose();

    var fresh = new HotkeyService();
    var reusable = fresh.TryBeginRegistration(occupied);
    Check(
        "Manager 释放后快捷键被注销",
        reusable is not null,
        fresh.LastError ?? "(null)");
    if (reusable is not null)
    {
        fresh.CommitRegistration(reusable);
    }

    fresh.Dispose();
    blocker.Dispose();
}

internal sealed class InMemoryStartupRegistry : IStartupRegistry
{
    private readonly Dictionary<string, StartupRegistryValue> _values =
        new(StringComparer.OrdinalIgnoreCase);

    public bool FailReads { get; init; }

    public bool FailWrites { get; init; }

    public int ValueCount => _values.Count;

    public StartupRegistryValue? Read(string valueName)
    {
        if (FailReads)
        {
            throw new UnauthorizedAccessException("模拟读取启动项失败");
        }

        return _values.TryGetValue(valueName, out var value) ? value : null;
    }

    public void Write(string valueName, object value, RegistryValueKind kind)
    {
        if (FailWrites)
        {
            throw new UnauthorizedAccessException("模拟写入启动项失败");
        }

        _values[valueName] = new StartupRegistryValue(value, kind);
    }

    public void Delete(string valueName) => _values.Remove(valueName);

    public string? ReadString(string valueName) => Read(valueName)?.Value as string;
}