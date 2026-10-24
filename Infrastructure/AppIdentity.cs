using System.IO;

namespace ClipAssist.Infrastructure;

/// <summary>程序运行模式。安装版和便携版共享同一套二进制，只在身份和默认路径上不同。</summary>
public enum AppInstallMode
{
    Installed,
    Portable,
}

/// <summary>
/// 安装版/便携版的运行身份。
///
/// 便携版通过程序目录中的 ClipAssist.portable 标记文件识别，因此无论用户
/// 直接双击 ClipAssist.exe，还是通过便携启动脚本启动，都不依赖环境变量。
/// 安装版不包含该标记文件，继续使用原有的身份和数据路径。
/// </summary>
public sealed record AppIdentity(
    AppInstallMode Mode,
    string ModeName,
    string DataFolderName,
    string PointerFolderName,
    string StartupValueName,
    string InstanceName,
    string MutexName,
    string ShowEventName)
{
    public const string PortableMarkerFileName = "ClipAssist.portable";
    public const string InstalledModeName = "ClipAssist";
    public const string PortableModeName = "ClipAssistPortable";
    public const string InstalledDataFolderName = "ClipAssist";
    public const string PortableDataFolderName = "Data";

    public bool IsPortable => Mode == AppInstallMode.Portable;

    /// <summary>
    /// 当前进程的身份。标记文件只在程序目录中查找，不读取用户配置，
    /// 避免安装版被旧的便携配置误判。
    /// </summary>
    public static AppIdentity Current { get; } = Resolve(AppContext.BaseDirectory);

    /// <summary>根据指定程序目录解析身份；测试使用临时目录验证两种模式。</summary>
    public static AppIdentity Resolve(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("程序目录不能为空。", nameof(baseDirectory));
        }

        var normalizedBase = Path.GetFullPath(baseDirectory);
        var isPortable = File.Exists(
            Path.Combine(normalizedBase, PortableMarkerFileName));

        return isPortable
            ? CreatePortable()
            : CreateInstalled();
    }

    private static AppIdentity CreateInstalled() => new(
        AppInstallMode.Installed,
        InstalledModeName,
        InstalledDataFolderName,
        InstalledModeName,
        InstalledModeName,
        InstalledModeName,
        @"Local\" + InstalledModeName + ".SingleInstance",
        @"Local\" + InstalledModeName + ".ShowWindow");

    private static AppIdentity CreatePortable() => new(
        AppInstallMode.Portable,
        PortableModeName,
        PortableDataFolderName,
        PortableModeName,
        PortableModeName,
        PortableModeName,
        @"Local\" + PortableModeName + ".SingleInstance",
        @"Local\" + PortableModeName + ".ShowWindow");
}