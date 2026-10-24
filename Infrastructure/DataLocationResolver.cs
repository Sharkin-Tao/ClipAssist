using System.IO;

namespace ClipAssist.Infrastructure;

public enum DataLocationSource
{
    EnvironmentOverride,
    CustomPointer,
    Default,
    Fallback,
}

/// <summary>启动时解析出的数据目录及来源。</summary>
public sealed record DataLocationResolution(
    string Directory,
    DataLocationSource Source,
    string? Warning);

/// <summary>
/// 解析启动数据目录。优先级：
/// CLIPASSIST_DATA_DIR → 固定指针 → 原有默认/回退目录。
/// 自定义指针存在但目录不可用时，不静默写入其他目录。
/// </summary>
public sealed class DataLocationResolver
{
    private readonly DataLocationPointer _pointer;

    public DataLocationResolver(string pointerFilePath)
    {
        _pointer = new DataLocationPointer(pointerFilePath);
    }

    public DataLocationResolution Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new DataLocationResolution(
                overridden.Trim(),
                DataLocationSource.EnvironmentOverride,
                null);
        }

        var pointer = _pointer.Read();
        if (pointer.Status == DataLocationPointerStatus.Loaded && pointer.Directory is not null)
        {
            string? warning = null;
            if (!Directory.Exists(pointer.Directory))
            {
                warning =
                    $"自定义数据目录不存在：{pointer.Directory}。应用未切换到默认目录，请修复该路径后重启应用。";
            }
            else if (!AppPaths.IsWritableDirectory(pointer.Directory))
            {
                warning =
                    $"自定义数据目录不可写：{pointer.Directory}。应用未切换到默认目录，请检查权限后重启应用。";
            }

            return new DataLocationResolution(
                pointer.Directory,
                DataLocationSource.CustomPointer,
                warning);
        }

        var defaultDirectory = AppPaths.GetDataDirectory();
        var isFallback = !string.Equals(
            defaultDirectory,
            AppPaths.PreferredDataDirectory,
            StringComparison.OrdinalIgnoreCase);

        return new DataLocationResolution(
            defaultDirectory,
            isFallback ? DataLocationSource.Fallback : DataLocationSource.Default,
            pointer.Warning);
    }
}