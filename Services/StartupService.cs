using System.IO;
using System.Security;
using Microsoft.Win32;

namespace ClipAssist.Services;

/// <summary>开机启动项的实际状态。</summary>
public enum StartupState
{
    Disabled,
    Enabled,
    Invalid,
    Unknown,
}

/// <summary>
/// 从 Windows 启动项读取到的状态。Message 用于向用户解释失效或过期路径。
/// </summary>
public sealed record StartupStatus(StartupState State, string? Message = null)
{
    public bool IsEnabled => State == StartupState.Enabled;
}

/// <summary>开启或关闭启动项的结果；失败时保留真实状态。</summary>
public sealed record StartupChangeResult(bool Success, StartupStatus Status, string? Error = null);

/// <summary>测试用注册表抽象；生产实现直接读写 HKCU Run。</summary>
internal sealed record StartupRegistryValue(object Value, RegistryValueKind Kind);

internal interface IStartupRegistry
{
    StartupRegistryValue? Read(string valueName);

    void Write(string valueName, object value, RegistryValueKind kind);

    void Delete(string valueName);
}

/// <summary>
/// 管理当前用户的 ClipAssist 开机启动项。
///
/// 使用 HKCU\Software\Microsoft\Windows\CurrentVersion\Run：
/// - 无需管理员权限，不创建服务；
/// - 只维护名为 ClipAssist 的一个值；
/// - 每次读取真实注册表值，不依赖 settings.json 中的布尔值；
/// - 写入后回读校验，失败时恢复原值。
/// </summary>
public sealed class StartupService
{
    internal const string DefaultValueName = "ClipAssist";
    internal const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// 开机启动参数。由 Run 项追加在可执行文件后，用于区分“用户手动启动”和
    /// “登录时静默启动”，同时保持原有 HKCU Run 注册方式不变。
    /// </summary>
    public const string StartupArgument = "--startup";

    private readonly string _valueName;
    private readonly string? _executablePathOverride;
    private readonly IStartupRegistry _registry;

    public StartupService()
        : this(null, null, null)
    {
    }

    /// <summary>供测试注入临时注册表实现和可执行文件路径。</summary>
    internal StartupService(
        string? executablePath,
        string? valueName = null,
        IStartupRegistry? registry = null)
    {
        _executablePathOverride = executablePath;
        _valueName = string.IsNullOrWhiteSpace(valueName) ? DefaultValueName : valueName;
        _registry = registry ?? new WindowsStartupRegistry();
    }

    /// <summary>读取 Windows 实际启动项，不修改注册表。</summary>
    public StartupStatus GetStatus()
    {
        StartupRegistryValue? entry;

        try
        {
            entry = _registry.Read(_valueName);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return new StartupStatus(
                StartupState.Unknown,
                DescribeReadFailure(ex));
        }

        if (entry is null)
        {
            return new StartupStatus(StartupState.Disabled);
        }

        if (entry.Value is not string command || string.IsNullOrWhiteSpace(command))
        {
            return new StartupStatus(
                StartupState.Invalid,
                "开机启动项格式无效，关闭后重新开启可修复。");
        }

        var registeredPath = ExtractExecutablePath(command);
        if (registeredPath is null)
        {
            return new StartupStatus(
                StartupState.Invalid,
                "开机启动项未指向可识别的程序路径，关闭后重新开启可修复。");
        }

        if (!File.Exists(registeredPath))
        {
            return new StartupStatus(
                StartupState.Invalid,
                $"开机启动项指向的文件不存在：{registeredPath}。关闭后重新开启可修复。");
        }

        var currentPath = ResolveExecutablePath(out var pathError);
        if (currentPath is null)
        {
            return new StartupStatus(
                StartupState.Enabled,
                $"启动项已存在，但无法确认当前程序路径：{pathError}");
        }

        if (PathsEqual(registeredPath, currentPath))
        {
            return HasStartupArgument(command)
                ? new StartupStatus(StartupState.Enabled)
                : new StartupStatus(
                    StartupState.Enabled,
                    "开机启动项已开启，但仍是旧命令，缺少静默启动参数。关闭后重新开启可更新为后台启动。");
        }

        return new StartupStatus(
            StartupState.Enabled,
            $"启动项仍指向旧位置：{registeredPath}。关闭后重新开启可更新为当前程序。");
    }

    /// <summary>
    /// 创建或更新 ClipAssist 启动项。重复开启只覆盖同一个值，不创建重复项。
    /// </summary>
    public StartupChangeResult Enable()
    {
        var executablePath = ResolveExecutablePath(out var pathError);
        if (executablePath is null)
        {
            return new StartupChangeResult(false, GetStatus(), pathError);
        }

        var command = BuildCommand(executablePath);
        StartupRegistryValue? previous = null;
        var writeAttempted = false;

        try
        {
            previous = _registry.Read(_valueName);

            if (previous?.Value is string previousCommand &&
                string.Equals(previousCommand, command, StringComparison.Ordinal))
            {
                var existingStatus = GetStatus();
                return existingStatus.IsEnabled
                    ? new StartupChangeResult(true, existingStatus)
                    : new StartupChangeResult(
                        false,
                        existingStatus,
                        "启动项已存在，但回读状态不一致。");
            }

            writeAttempted = true;
            _registry.Write(_valueName, command, RegistryValueKind.String);

            var written = _registry.Read(_valueName);
            if (written?.Value is not string writtenCommand ||
                !string.Equals(writtenCommand, command, StringComparison.Ordinal))
            {
                RestorePreviousValue(previous);
                return new StartupChangeResult(
                    false,
                    GetStatus(),
                    "写入开机启动项后回读不一致，已恢复原状态。");
            }

            var status = GetStatus();
            if (!status.IsEnabled)
            {
                return new StartupChangeResult(
                    false,
                    status,
                    "启动项已写入，但读取验证未通过。");
            }

            return new StartupChangeResult(true, status);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            if (writeAttempted)
            {
                RestorePreviousValue(previous);
            }

            return new StartupChangeResult(
                false,
                GetStatus(),
                DescribeChangeFailure("开启开机自启动", ex));
        }
    }

    /// <summary>
    /// 只删除 ClipAssist 自己的启动项值，不触碰 Run 键中的其他应用。
    /// </summary>
    public StartupChangeResult Disable()
    {
        try
        {
            var existing = _registry.Read(_valueName);
            if (existing is null)
            {
                return new StartupChangeResult(true, new StartupStatus(StartupState.Disabled));
            }

            _registry.Delete(_valueName);

            var remaining = _registry.Read(_valueName);
            if (remaining is not null)
            {
                return new StartupChangeResult(
                    false,
                    GetStatus(),
                    "关闭开机自启动后，启动项仍然存在。");
            }

            return new StartupChangeResult(true, new StartupStatus(StartupState.Disabled));
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return new StartupChangeResult(
                false,
                GetStatus(),
                DescribeChangeFailure("关闭开机自启动", ex));
        }
    }

    private void RestorePreviousValue(StartupRegistryValue? previous)
    {
        try
        {
            if (previous is null)
            {
                _registry.Delete(_valueName);
            }
            else
            {
                _registry.Write(_valueName, previous.Value, previous.Kind);
            }
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            // 回滚失败时由调用方展示原始错误；这里不再抛异常覆盖它。
        }
    }

    private string? ResolveExecutablePath(out string? error)
    {
        error = null;

        try
        {
            var path = _executablePathOverride;

            if (string.IsNullOrWhiteSpace(path))
            {
                var processPath = Environment.ProcessPath;

                // dotnet run 时 ProcessPath 是 dotnet.exe，不能把它写进启动项。
                if (!string.IsNullOrWhiteSpace(processPath) &&
                    !string.Equals(
                        Path.GetFileNameWithoutExtension(processPath),
                        "dotnet",
                        StringComparison.OrdinalIgnoreCase))
                {
                    path = processPath;
                }
                else
                {
                    var appHost = Path.Combine(AppContext.BaseDirectory, "ClipAssist.exe");
                    if (File.Exists(appHost))
                    {
                        path = appHost;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "无法确定 ClipAssist 的可执行文件路径。";
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                error = $"ClipAssist 可执行文件不存在：{fullPath}";
                return null;
            }

            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException
                                       or SecurityException
                                       or IOException)
        {
            error = $"无法验证 ClipAssist 可执行文件路径：{ex.Message}";
            return null;
        }
    }

    private static string? ExtractExecutablePath(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.Length == 0)
        {
            return null;
        }

        string candidate;

        if (text[0] == '"')
        {
            var closingQuote = text.IndexOf('"', 1);
            if (closingQuote <= 1)
            {
                return null;
            }

            candidate = text[1..closingQuote];
        }
        else
        {
            candidate = text;

            // 兼容旧启动项可能没有给带空格的路径加引号的情况。
            if (!File.Exists(candidate))
            {
                var space = candidate.IndexOf(' ');
                if (space > 0)
                {
                    candidate = candidate[..space];
                }
            }
        }

        candidate = candidate.Trim();
        if (candidate.Length == 0)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException
                                       or SecurityException)
        {
            return null;
        }
    }

    private static string BuildCommand(string path) =>
        QuotePath(path) + " " + StartupArgument;

    private static bool HasStartupArgument(string command)
    {
        var text = command.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var remainder = text;
        if (text[0] == '"')
        {
            var closingQuote = text.IndexOf('"', 1);
            if (closingQuote < 0)
            {
                return false;
            }

            remainder = text[(closingQuote + 1)..];
        }

        return remainder
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(token => string.Equals(
                token,
                StartupArgument,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string QuotePath(string path) => "\"" + path + "\"";

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException)
        {
            return false;
        }
    }

    private static string DescribeReadFailure(Exception exception) =>
        exception is UnauthorizedAccessException or SecurityException
            ? "无法读取开机启动项：Windows 拒绝访问。当前程序可能运行在受限终端、沙箱或低权限环境中；请从桌面或开始菜单以普通用户身份启动 ClipAssist。"
            : $"无法读取开机启动项：{exception.Message}";

    private static string DescribeChangeFailure(string action, Exception exception) =>
        exception is UnauthorizedAccessException or SecurityException
            ? $"{action}失败：Windows 拒绝写入当前用户的启动项。当前程序可能运行在受限终端、沙箱或低权限环境中；请关闭 ClipAssist，从桌面或开始菜单以普通用户身份重新启动后再试。"
            : $"{action}失败：{exception.Message}";

    private static bool IsRegistryFailure(Exception exception) =>
        exception is SecurityException
            or UnauthorizedAccessException
            or ObjectDisposedException
            or ArgumentException
            or NotSupportedException
            or InvalidOperationException
            or IOException;

    private sealed class WindowsStartupRegistry : IStartupRegistry
    {
        public StartupRegistryValue? Read(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(DefaultRunKeyPath, writable: false);
            if (key is null)
            {
                return null;
            }

            var value = key.GetValue(
                valueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);

            return value is null
                ? null
                : new StartupRegistryValue(value, key.GetValueKind(valueName));
        }

        public void Write(string valueName, object value, RegistryValueKind kind)
        {
            using var key = Registry.CurrentUser.CreateSubKey(DefaultRunKeyPath, writable: true)
                ?? throw new InvalidOperationException("无法打开当前用户的开机启动项注册表位置。");

            key.SetValue(valueName, value, kind);
        }

        public void Delete(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(DefaultRunKeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}