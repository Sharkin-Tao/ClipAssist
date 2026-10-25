using System.Windows.Input;
using ClipAssist.Localization;
using ClipAssist.Models;

namespace ClipAssist.Services;

/// <summary>启动注册结果。Warning 为 null 表示“已保存的组合就是当前生效组合”。</summary>
public sealed record HotkeyInitializeResult(
    HotkeyGesture? EffectiveGesture,
    string? Warning);

/// <summary>更换快捷键结果；失败时旧快捷键保持有效。</summary>
public sealed record HotkeyApplyResult(
    bool Success,
    string? Error,
    HotkeyGesture? EffectiveGesture);

/// <summary>
/// 快捷键编排层：
/// - 启动时注册保存的组合；失败时临时回退，但不覆盖保存值
/// - 更换时先注册新组合，再保存配置，最后才注销旧组合
/// - 对外暴露“已保存”和“当前生效”两个状态，界面可以如实展示
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HotkeyService _service;
    private readonly SettingsService _settings;
    private HotkeyGesture _saved;
    private HotkeyGesture? _effective;
    private bool _initialized;
    private bool _disposed;

    public HotkeyManager(HotkeyService service, SettingsService settings)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _saved = ReadSavedGesture();
        _service.Pressed += OnServicePressed;
    }

    public event EventHandler? Pressed;

    /// <summary>用户保存在配置里的快捷键。</summary>
    public HotkeyGesture SavedGesture => _saved;

    /// <summary>当前真正注册成功的快捷键；null 表示没有可用的全局快捷键。</summary>
    public HotkeyGesture? EffectiveGesture => _effective;

    public string? StatusMessage { get; private set; }

    public HotkeyInitializeResult Initialize()
    {
        if (_initialized)
        {
            return new HotkeyInitializeResult(_effective, StatusMessage);
        }

        _initialized = true;

        var defaultGesture = HotkeyGesture.Default;
        var altFallback = new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Alt, Key.V);

        var candidates = new List<HotkeyGesture> { _saved, defaultGesture, altFallback };
        var errors = new List<string>();

        foreach (var candidate in candidates.Distinct())
        {
            if (!TryActivate(candidate))
            {
                errors.Add(_service.LastError ?? L.Format("Hotkey.RegistrationFailedDetail", candidate.DisplayText));
                continue;
            }

            if (candidate == _saved)
            {
                StatusMessage = L.Format("Hotkey.StatusActive", candidate.DisplayText);
                return new HotkeyInitializeResult(_effective, Warning: null);
            }

            StatusMessage = L.Format(
                "Hotkey.StatusFallback",
                _saved.DisplayText,
                candidate.DisplayText);
            return new HotkeyInitializeResult(_effective, StatusMessage);
        }

        StatusMessage = L.Format("Hotkey.StatusUnavailable", string.Join(" ", errors));
        return new HotkeyInitializeResult(EffectiveGesture: null, StatusMessage);
    }

    /// <summary>
    /// 尝试把用户选择的新快捷键设为当前值。
    /// 注册失败或配置保存失败都会回滚，旧快捷键保持有效。
    /// selectSearchTextOnActivate 为 null 时沿用当前设置。
    /// </summary>
    public HotkeyApplyResult Apply(
        HotkeyGesture gesture,
        bool? selectSearchTextOnActivate = null)
    {
        if (_disposed)
        {
            return new HotkeyApplyResult(false, L.Get("Hotkey.Disposed"), _effective);
        }

        if (!gesture.IsValid)
        {
            return new HotkeyApplyResult(false, L.Get("Hotkey.Invalid"), _effective);
        }

        var previousSelect = _settings.Current.Focus?.SelectSearchTextOnActivate ?? true;
        var select = selectSearchTextOnActivate ?? previousSelect;

        if (_effective == gesture && _saved == gesture)
        {
            if (previousSelect == select)
            {
                return new HotkeyApplyResult(true, null, _effective);
            }

            var saveFocus = _settings.SaveFocus(select);
            if (!saveFocus.Success)
            {
                return new HotkeyApplyResult(
                    false,
                    saveFocus.Error ?? L.Get("Hotkey.SaveFocusFailed"),
                    _effective);
            }

            return new HotkeyApplyResult(true, null, _effective);
        }

        // 当前已经生效（例如启动回退值），只需要把它持久化为用户选择。
        if (_effective == gesture)
        {
            var saveOnly = _settings.SaveHotkeyAndFocus(gesture, select);
            if (!saveOnly.Success)
            {
                return new HotkeyApplyResult(
                    false,
                    saveOnly.Error ?? L.Get("Hotkey.SaveFailed"),
                    _effective);
            }

            _saved = gesture;
            StatusMessage = L.Format("Hotkey.StatusActive", gesture.DisplayText);
            return new HotkeyApplyResult(true, null, _effective);
        }

        var registration = _service.TryBeginRegistration(gesture);
        if (registration is null)
        {
            return new HotkeyApplyResult(
                false,
                _service.LastError ?? L.Get("Hotkey.RegisterFailed"),
                _effective);
        }

        var save = _settings.SaveHotkeyAndFocus(gesture, select);
        if (!save.Success)
        {
            _service.CancelRegistration(registration);
            return new HotkeyApplyResult(
                false,
                save.Error ?? L.Get("Hotkey.SaveFailedKeep"),
                _effective);
        }

        if (!_service.CommitRegistration(registration))
        {
            var message = _service.LastError ?? L.Get("Hotkey.ReplaceFailed");
            var rollback = _settings.SaveHotkeyAndFocus(_saved, previousSelect);
            if (!rollback.Success)
            {
                message += L.Format("Hotkey.RollbackFailedDetail", rollback.Error);
            }

            return new HotkeyApplyResult(false, message, _effective);
        }

        _saved = gesture;
        _effective = gesture;
        StatusMessage = L.Format("Hotkey.StatusActive", gesture.DisplayText);
        return new HotkeyApplyResult(true, null, _effective);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.Pressed -= OnServicePressed;
        _service.Dispose();
    }

    private bool TryActivate(HotkeyGesture gesture)
    {
        var registration = _service.TryBeginRegistration(gesture);
        if (registration is null)
        {
            return false;
        }

        if (!_service.CommitRegistration(registration))
        {
            return false;
        }

        _effective = gesture;
        return true;
    }

    private HotkeyGesture ReadSavedGesture()
    {
        var configured = _settings.Current.Hotkey?.Gesture;

        return HotkeyGesture.TryParse(configured, out var gesture, out _)
            ? gesture
            : HotkeyGesture.Default;
    }

    private void OnServicePressed(object? sender, EventArgs e) =>
        Pressed?.Invoke(this, EventArgs.Empty);
}