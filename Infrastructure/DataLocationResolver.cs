using System.IO;
using ClipAssist.Localization;

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
                    L.Format("Data.CustomMissing", pointer.Directory);
            }
            else if (!AppPaths.IsWritableDirectory(pointer.Directory))
            {
                warning =
                    L.Format("Data.CustomNotWritable", pointer.Directory);
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