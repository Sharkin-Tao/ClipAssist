using System.IO;
using System.Security;

namespace ClipAssist.Infrastructure;

/// <summary>
/// 统一决定 ClipAssist 的本地数据位置。
/// 安装版默认是 %APPDATA%\ClipAssist；便携版默认是程序目录旁的 Data。
/// 测试或绿色版仍可以用 CLIPASSIST_DATA_DIR 覆盖。
/// 历史、设置必须共用同一个目录，避免出现多套配置文件。
///
/// 安装版默认目录不可写时，按原顺序回退到 LocalAppData、程序目录、用户目录和临时目录。
/// 便携版只回退到 %LOCALAPPDATA%\ClipAssistPortable，绝不静默改用安装版数据目录。
/// </summary>
public static class AppPaths
{
    public const string DataDirectoryEnvironmentVariable = "CLIPASSIST_DATA_DIR";
    public const string AppFolderName = "ClipAssist";
    public const string HistoryFileName = "history.json";
    public const string SettingsFileName = "settings.json";
    public const string DataLocationFileName = "data-location.json";

    private static readonly object Gate = new();
    private static string? _resolved;

    public static string GetDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            // 显式覆盖时不做探测：测试/绿色版希望严格使用指定目录。
            return overridden.Trim();
        }

        lock (Gate)
        {
            if (_resolved is not null)
            {
                return _resolved;
            }

            var candidates = GetDataDirectoryCandidates(
                AppIdentity.Current,
                AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

            foreach (var candidate in candidates)
            {
                if (CanWrite(candidate))
                {
                    _resolved = candidate;
                    return candidate;
                }
            }

            // 全部失败时仍返回默认位置，让上层把真实错误展示给用户。
            return PreferredDataDirectory;
        }
    }

    /// <summary>不探测权限时的首选目录，用于判断是否发生了回退。</summary>
    public static string PreferredDataDirectory => ResolvePreferredDataDirectory(
        AppIdentity.Current,
        AppContext.BaseDirectory);

    /// <summary>
/// true 表示当前数据没有存放在当前模式的首选目录：
/// 安装版为 %APPDATA%\ClipAssist，便携版为程序目录旁的 Data。
/// </summary>
    public static bool IsUsingFallbackDataDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable)))
            {
                return false;
            }

            return !string.Equals(
                GetDataDirectory(),
                PreferredDataDirectory,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string GetHistoryFilePath() => Path.Combine(GetDataDirectory(), HistoryFileName);

    public static string GetSettingsFilePath() => Path.Combine(GetDataDirectory(), SettingsFileName);

    /// <summary>固定数据目录指针位置；不放在可迁移的数据目录内。</summary>
    public static string GetDataLocationFilePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        return ResolveDataLocationFilePath(AppIdentity.Current, localAppData);
    }

    /// <summary>不探测权限时的首选数据目录；测试可传入临时程序目录。</summary>
    internal static string ResolvePreferredDataDirectory(
        AppIdentity identity,
        string baseDirectory)
    {
        if (identity.IsPortable)
        {
            return Path.Combine(Path.GetFullPath(baseDirectory), identity.DataFolderName);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppFolderName);
    }

    /// <summary>数据目录候选顺序。便携版绝不包含安装版的 %APPDATA% 路径。</summary>
    internal static IReadOnlyList<string> GetDataDirectoryCandidates(
        AppIdentity identity,
        string baseDirectory,
        string localAppData)
    {
        var candidates = new List<string>
        {
            ResolvePreferredDataDirectory(identity, baseDirectory),
        };

        if (identity.IsPortable)
        {
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, identity.PointerFolderName));
            }

            return candidates;
        }

        candidates.Add(Path.Combine(localAppData, AppFolderName));
        candidates.Add(Path.Combine(Path.GetFullPath(baseDirectory), AppFolderName));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AppFolderName));
        candidates.Add(Path.Combine(Path.GetTempPath(), AppFolderName));

        return candidates;
    }

    /// <summary>按模式解析固定数据目录指针路径。</summary>
    internal static string ResolveDataLocationFilePath(
        AppIdentity identity,
        string localAppData)
    {
        var root = string.IsNullOrWhiteSpace(localAppData)
            ? AppContext.BaseDirectory
            : localAppData;

        return Path.Combine(root, identity.PointerFolderName, DataLocationFileName);
    }

    /// <summary>供数据目录解析/迁移校验复用同一套可写探测。</summary>
    public static bool IsWritableDirectory(string directory) => CanWrite(directory);

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            stream.WriteByte(0);
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or SecurityException
                                       or NotSupportedException
                                       or ArgumentException)
        {
            return false;
        }
    }
}