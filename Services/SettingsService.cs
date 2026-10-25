using System.IO;
using ClipAssist.Localization;
using ClipAssist.Models;

namespace ClipAssist.Services;

/// <summary>设置加载结果：统一携带设置、提示和是否只读。</summary>
public sealed record SettingsLoadResult(
    AppSettings Settings,
    string? Warning,
    bool FileCreated,
    bool Migrated,
    bool ReadOnly);

/// <summary>
/// 设置系统的编排层：
/// 读取 → 迁移 → 校验 → 首次创建/损坏恢复 → 需要时保存。
/// 不依赖 WPF；窗口几何校验仍由 WindowPlacementService 负责。
/// </summary>
public sealed class SettingsService
{
    private readonly SettingsStore _store;
    private AppSettings _settings = new();
    private bool _canPersist = true;
    private bool _needsSave;

    public SettingsService(SettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public AppSettings Current => _settings;

    /// <summary>false 表示文件不可读或来自更高版本，本次不应覆盖。</summary>
    public bool CanPersist => _canPersist;

    public SettingsLoadResult Load()
    {
        var read = _store.Read();

        switch (read.Status)
        {
            case SettingsReadStatus.Missing:
                return CreateDefault(created: true);

            case SettingsReadStatus.Unreadable:
                _settings = new AppSettings();
                _canPersist = false;
                _needsSave = false;
                return new SettingsLoadResult(
                    _settings,
                    read.Warning,
                    FileCreated: false,
                    Migrated: false,
                    ReadOnly: true);

            case SettingsReadStatus.Corrupt:
                return RecoverCorrupt(read);

            default:
                return LoadDocument(read);
        }
    }

    /// <summary>
    /// 保存窗口位置。位置没有变化时跳过写盘；失败时恢复旧值，保证下次仍能重试。
    /// </summary>
    public SettingsSaveResult SaveWindowPlacement(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        if (!IsValid(placement))
        {
            return new SettingsSaveResult(false, L.Get("Settings.Warning.InvalidPlacement"));
        }

        if (!_canPersist)
        {
            return new SettingsSaveResult(false, L.Get("Settings.Warning.ReadOnly"));
        }

        if (!_needsSave && SameWindow(_settings.Window, placement))
        {
            return new SettingsSaveResult(true, null);
        }

        var previous = _settings.Window;
        _settings.Window = Clone(placement);

        var result = _store.Save(_settings);
        if (!result.Success)
        {
            _settings.Window = previous;
            _needsSave = true;
            return result;
        }

        _needsSave = false;
        return result;
    }

    /// <summary>
    /// 保存用户选择的全局快捷键。保存失败时恢复旧值，避免“注册成功但配置没存上”。
    /// 保留当前焦点设置，二者由同一个文件承载。
    /// </summary>
    public SettingsSaveResult SaveHotkey(HotkeyGesture gesture)
    {
        return SaveHotkeyAndFocus(
            gesture,
            _settings.Focus?.SelectSearchTextOnActivate ?? true);
    }

    /// <summary>
    /// 一次性保存快捷键和呼出焦点行为，避免两次写盘出现半套配置。
    /// 保存失败时两项都恢复旧值。
    /// </summary>
    public SettingsSaveResult SaveHotkeyAndFocus(
        HotkeyGesture gesture,
        bool selectSearchTextOnActivate)
    {
        if (!gesture.IsValid)
        {
            return new SettingsSaveResult(false, L.Get("Settings.Warning.HotkeyInvalidValue"));
        }

        if (!_canPersist)
        {
            return new SettingsSaveResult(false, L.Get("Settings.Warning.ReadOnly"));
        }

        var gestureText = gesture.DisplayText;
        var focus = _settings.Focus ??= new FocusSettings();

        if (!_needsSave &&
            string.Equals(_settings.Hotkey?.Gesture, gestureText, StringComparison.Ordinal) &&
            focus.SelectSearchTextOnActivate == selectSearchTextOnActivate)
        {
            return new SettingsSaveResult(true, null);
        }

        var previousHotkey = _settings.Hotkey;
        var previousFocus = _settings.Focus;
        var previousSelect = focus.SelectSearchTextOnActivate;

        _settings.Hotkey = new HotkeySettings { Gesture = gestureText };
        _settings.Focus = new FocusSettings
        {
            SelectSearchTextOnActivate = selectSearchTextOnActivate,
        };

        var result = _store.Save(_settings);
        if (!result.Success)
        {
            _settings.Hotkey = previousHotkey ?? new HotkeySettings();
            _settings.Focus = previousFocus ?? new FocusSettings { SelectSearchTextOnActivate = previousSelect };
            _needsSave = true;
            return result;
        }

        _needsSave = false;
        return result;
    }

    /// <summary>保存“呼出时全选搜索框文本”行为；失败时恢复旧值。</summary>
    public SettingsSaveResult SaveFocus(bool selectSearchTextOnActivate)
    {
        if (!_canPersist)
        {
            return new SettingsSaveResult(false, L.Get("Settings.Warning.ReadOnly"));
        }

        var focus = _settings.Focus ??= new FocusSettings();
        if (!_needsSave && focus.SelectSearchTextOnActivate == selectSearchTextOnActivate)
        {
            return new SettingsSaveResult(true, null);
        }

        var previous = focus.SelectSearchTextOnActivate;
        focus.SelectSearchTextOnActivate = selectSearchTextOnActivate;

        var result = _store.Save(_settings);
        if (!result.Success)
        {
            focus.SelectSearchTextOnActivate = previous;
            _needsSave = true;
            return result;
        }

        _needsSave = false;
        return result;
    }

    /// <summary>
    /// 只保存主题字段，不触碰尚未提交的快捷键或焦点设置。
    /// 设置窗口里这些值仍是局部状态，只有点“保存”才会进入 SettingsService。
    /// </summary>
    public SettingsSaveResult SaveTheme(ThemePreference mode)
    {
        if (!_canPersist)
        {
            return new SettingsSaveResult(false, L.Get("Settings.Warning.ReadOnly"));
        }

        var theme = _settings.Theme ??= new ThemeSettings();
        var storageValue = mode.ToStorageValue();

        if (!_needsSave && string.Equals(theme.Mode, storageValue, StringComparison.Ordinal))
        {
            return new SettingsSaveResult(true, null);
        }

        var previous = theme.Mode;
        theme.Mode = storageValue;

        var result = _store.Save(_settings);
        if (!result.Success)
        {
            theme.Mode = previous;
            _needsSave = true;
            return result;
        }

        _needsSave = false;
        return result;
    }

    /// <summary>
    /// Persists the interface language without touching pending hotkey, focus,
    /// theme, or window state. Failure restores the previous value.
    /// </summary>
    public SettingsSaveResult SaveLanguage(LanguagePreference language)
    {
        if (!_canPersist)
        {
            return new SettingsSaveResult(false, L.Get("Settings.Warning.ReadOnly"));
        }

        var settings = _settings.Language ??= new LanguageSettings();
        var storageValue = language.ToStorageValue();

        if (!_needsSave && string.Equals(settings.Mode, storageValue, StringComparison.Ordinal))
        {
            return new SettingsSaveResult(true, null);
        }

        var previous = settings.Mode;
        settings.Mode = storageValue;

        var result = _store.Save(_settings);
        if (!result.Success)
        {
            settings.Mode = previous;
            _needsSave = true;
            return result;
        }

        _needsSave = false;
        return result;
    }

    private SettingsLoadResult CreateDefault(bool created)
    {
        _settings = new AppSettings();
        _canPersist = true;

        var save = _store.Save(_settings);
        _needsSave = !save.Success;

        return new SettingsLoadResult(
            _settings,
            save.Error,
            FileCreated: created,
            Migrated: false,
            ReadOnly: false);
    }

    private SettingsLoadResult RecoverCorrupt(SettingsReadResult read)
    {
        _settings = new AppSettings();

        if (read.BackupPath is null)
        {
            // 不能覆盖可能仍有价值的损坏文件。
            _canPersist = false;
            _needsSave = false;
            return new SettingsLoadResult(
                _settings,
                read.Warning,
                FileCreated: false,
                Migrated: false,
                ReadOnly: true);
        }

        _canPersist = true;
        var save = _store.Save(_settings);
        _needsSave = !save.Success;

        return new SettingsLoadResult(
            _settings,
            JoinWarnings(read.Warning, save.Error),
            FileCreated: true,
            Migrated: false,
            ReadOnly: false);
    }

    private SettingsLoadResult LoadDocument(SettingsReadResult read)
    {
        var root = read.Root!.Value;
        var migration = SettingsMigrator.Migrate(root);

        if (migration.Settings is null)
        {
            return RecoverInvalid(read.Warning, migration.Warning);
        }

        if (migration.ReadOnly)
        {
            _settings = migration.Settings;
            _canPersist = false;
            _needsSave = false;
            return new SettingsLoadResult(
                _settings,
                JoinWarnings(read.Warning, migration.Warning),
                FileCreated: false,
                Migrated: false,
                ReadOnly: true);
        }

        var validation = SettingsValidator.Normalize(migration.Settings);
        _settings = validation.Settings;
        _canPersist = true;

        var warning = JoinWarnings(read.Warning, migration.Warning, validation.Warning);
        var shouldSave = migration.Migrated || validation.Changed;

        if (shouldSave)
        {
            var save = _store.Save(_settings);
            _needsSave = !save.Success;
            warning = JoinWarnings(warning, save.Error);
        }
        else
        {
            _needsSave = false;
        }

        return new SettingsLoadResult(
            _settings,
            warning,
            FileCreated: false,
            Migrated: migration.Migrated,
            ReadOnly: false);
    }

    private SettingsLoadResult RecoverInvalid(string? readWarning, string? migrationWarning)
    {
        var backupPath = _store.TryBackupCurrentFile();
        _settings = new AppSettings();

        if (backupPath is null)
        {
            _canPersist = false;
            _needsSave = false;
            return new SettingsLoadResult(
                _settings,
                JoinWarnings(
                    readWarning,
                    migrationWarning,
                    L.Get("Settings.Warning.InvalidNoBackup")),
                FileCreated: false,
                Migrated: false,
                ReadOnly: true);
        }

        _canPersist = true;
        var save = _store.Save(_settings);
        _needsSave = !save.Success;

        return new SettingsLoadResult(
            _settings,
            JoinWarnings(
                readWarning,
                migrationWarning,
                L.Format("Settings.Warning.InvalidBackedUp", Path.GetFileName(backupPath)),
                save.Error),
            FileCreated: true,
            Migrated: false,
            ReadOnly: false);
    }

    private static bool IsValid(WindowPlacement placement) =>
        double.IsFinite(placement.Left)
        && double.IsFinite(placement.Top)
        && double.IsFinite(placement.Width)
        && double.IsFinite(placement.Height)
        && placement.Width > 0
        && placement.Height > 0;

    private static bool SameWindow(WindowPlacement? first, WindowPlacement second) =>
        first is not null
        && Math.Abs(first.Left - second.Left) <= 0.01
        && Math.Abs(first.Top - second.Top) <= 0.01
        && Math.Abs(first.Width - second.Width) <= 0.01
        && Math.Abs(first.Height - second.Height) <= 0.01;

    private static WindowPlacement Clone(WindowPlacement placement) => new()
    {
        Left = placement.Left,
        Top = placement.Top,
        Width = placement.Width,
        Height = placement.Height,
    };

    private static string? JoinWarnings(params string?[] messages)
    {
        var parts = messages
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToArray();

        return parts.Length == 0 ? null : string.Join(" ", parts);
    }
}