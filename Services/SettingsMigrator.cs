using System.Text.Json;
using System.Text.Json.Nodes;
using ClipAssist.Localization;
using ClipAssist.Models;

namespace ClipAssist.Services;

/// <summary>
/// 迁移结果。Settings 为 null 表示内容在语义上无效，需要回退默认值。
/// ReadOnly 表示文件来自更高版本，本次只读，不能覆盖。
/// </summary>
public sealed record SettingsMigrationResult(
    AppSettings? Settings,
    bool Migrated,
    bool ReadOnly,
    string? Warning);

/// <summary>
/// 把旧版 settings.json 迁移到当前版本。
/// v1/v2 的窗口字段结构相同；v3 新增 hotkey，缺失时由模型默认值补齐。
/// focus 是 v3 内的可选字段，旧文件缺失时使用默认值。
/// 未来新增字段时在这里按版本逐级追加迁移步骤。
/// </summary>
public static class SettingsMigrator
{
    public static SettingsMigrationResult Migrate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Invalid(L.Get("Settings.Warning.RootInvalid"));
        }

        var version = 1;
        if (root.TryGetProperty("schemaVersion", out var versionElement))
        {
            if (versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out version) ||
                version < 1)
            {
                return Invalid(L.Get("Settings.Warning.VersionInvalid"));
            }
        }

        if (version > AppSettings.CurrentSchemaVersion)
        {
            return new SettingsMigrationResult(
                new AppSettings(),
                Migrated: false,
                ReadOnly: true,
                L.Format("Settings.Warning.VersionNewer", version, AppSettings.CurrentSchemaVersion));
        }

        try
        {
            // V0.2-04 曾把 hotkey 当作未知字段，因此旧文件里可能出现字符串等非对象形式。
            // 这种字段只应重置对应设置，不应连带丢弃仍然有效的窗口位置或其他字段。
            var sanitized = false;
            var warnings = new List<string>();
            JsonElement? rootForDeserialize = root;
            JsonObject? sanitizedNode = null;

            if (root.TryGetProperty("hotkey", out var hotkeyElement) &&
                hotkeyElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
            {
                sanitizedNode ??= JsonNode.Parse(root.GetRawText())!.AsObject();
                sanitizedNode.Remove("hotkey");
                sanitized = true;
                warnings.Add(L.Get("Settings.Warning.HotkeyMissing"));
            }

            if (root.TryGetProperty("focus", out var focusElement) &&
                focusElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
            {
                sanitizedNode ??= JsonNode.Parse(root.GetRawText())!.AsObject();
                sanitizedNode.Remove("focus");
                sanitized = true;
                warnings.Add(L.Get("Settings.Warning.FocusMissing"));
            }

            // 主题只在格式无法反序列化时整段移除；合法对象里的未知 mode 字符串
            // 交给 SettingsValidator 回退，避免牵连窗口、快捷键和焦点设置。
            if (root.TryGetProperty("theme", out var themeElement) &&
                themeElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
            {
                sanitizedNode ??= JsonNode.Parse(root.GetRawText())!.AsObject();
                sanitizedNode.Remove("theme");
                sanitized = true;
                warnings.Add(L.Get("Settings.Warning.ThemeInvalid"));
            }
            else if (themeElement.ValueKind == JsonValueKind.Object &&
                themeElement.TryGetProperty("mode", out var themeModeElement) &&
                themeModeElement.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
            {
                sanitizedNode ??= JsonNode.Parse(root.GetRawText())!.AsObject();
                sanitizedNode.Remove("theme");
                sanitized = true;
                warnings.Add(L.Get("Settings.Warning.ThemeInvalid"));
            }

            if (root.TryGetProperty("language", out var languageElement) &&
                languageElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
            {
                sanitizedNode ??= JsonNode.Parse(root.GetRawText())!.AsObject();
                sanitizedNode.Remove("language");
                sanitized = true;
                warnings.Add(L.Get("Settings.Warning.LanguageFormat"));
            }
            else if (languageElement.ValueKind == JsonValueKind.Object &&
                languageElement.TryGetProperty("mode", out var languageModeElement) &&
                languageModeElement.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
            {
                sanitizedNode ??= JsonNode.Parse(root.GetRawText())!.AsObject();
                sanitizedNode.Remove("language");
                sanitized = true;
                warnings.Add(L.Get("Settings.Warning.LanguageFormat"));
            }

            if (sanitizedNode is not null)
            {
                rootForDeserialize = JsonSerializer.SerializeToElement(
                    sanitizedNode,
                    SettingsStore.JsonOptions);
            }

            var settings = rootForDeserialize.Value.Deserialize<AppSettings>(SettingsStore.JsonOptions);
            if (settings is null)
            {
                return Invalid(L.Get("Settings.Warning.ContentEmpty"));
            }

            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;

            return new SettingsMigrationResult(
                settings,
                Migrated: sanitized || version < AppSettings.CurrentSchemaVersion,
                ReadOnly: false,
                Warning: warnings.Count == 0 ? null : string.Join(" ", warnings));
        }
        catch (JsonException)
        {
            return Invalid(L.Get("Settings.Warning.FormatInvalid"));
        }
        catch (NotSupportedException)
        {
            return Invalid(L.Get("Settings.Warning.Unsupported"));
        }
    }

    private static SettingsMigrationResult Invalid(string message) =>
        new(null, Migrated: false, ReadOnly: false, Warning: message);
}