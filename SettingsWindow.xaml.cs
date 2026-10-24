using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ClipAssist.Models;
using ClipAssist.Services;
using Microsoft.Win32;

namespace ClipAssist;

/// <summary>
/// 设置窗口：开机启动 + 快捷键录入 + 呼出焦点行为 + 主题选择。
/// 快捷键的“注册 + 保存 + 回滚”由 App 注入的回调交给 HotkeyManager；
/// 主题和开机启动项都即时生效，不会提交尚未点“保存”的快捷键/焦点改动。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Func<HotkeyGesture, bool, HotkeyApplyResult> _apply;
    private readonly Func<ThemePreference, ThemeApplyResult> _applyTheme;
    private readonly Func<string, Task<StorageMigrationResult>> _applyStorage;
    private readonly StartupService _startupService;
    private readonly HotkeyGesture _saved;
    private readonly HotkeyGesture? _effective;
    private HotkeyGesture _pending;
    private ThemePreference _appliedTheme;
    private string _currentDataDirectory;
    private readonly bool _storageChangeAllowed;
    private StartupStatus _startupStatus;
    private bool _recording;
    private bool _busy;
    private bool _storageBusy;
    private bool _initializingTheme;
    private bool _initializingStartup;
    private bool _startupBusy;

    public bool StorageChanged { get; private set; }

    public SettingsWindow(
        HotkeyGesture saved,
        HotkeyGesture? effective,
        string? statusMessage,
        bool selectSearchTextOnActivate,
        ThemePreference currentTheme,
        string dataDirectory,
        bool storageChangeAllowed,
        StartupService startupService,
        Func<string, Task<StorageMigrationResult>> applyStorage,
        Func<HotkeyGesture, bool, HotkeyApplyResult> apply,
        Func<ThemePreference, ThemeApplyResult> applyTheme)
    {
        InitializeComponent();

        _saved = saved;
        _effective = effective;
        _pending = saved;
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _applyTheme = applyTheme ?? throw new ArgumentNullException(nameof(applyTheme));
        _applyStorage = applyStorage ?? throw new ArgumentNullException(nameof(applyStorage));
        _startupService = startupService ?? throw new ArgumentNullException(nameof(startupService));
        _appliedTheme = currentTheme;
        _currentDataDirectory = dataDirectory;
        _storageChangeAllowed = storageChangeAllowed;
        _startupStatus = _startupService.GetStatus();

        ManualGestureBox.Text = saved.DisplayText;
        SelectSearchTextCheckBox.IsChecked = selectSearchTextOnActivate;

        StoragePathBox.Text = dataDirectory;
        StoragePathBox.ToolTip = dataDirectory;
        StorageChangeButton.IsEnabled = storageChangeAllowed;
        StorageHintText.Text = storageChangeAllowed
            ? "更改后会复制 settings.json 和 history.json 到新目录；旧目录保留，完成后应用会重启。"
            : "当前数据目录由环境变量固定，或设置文件不可写，无法在界面修改。";

        _initializingTheme = true;
        ThemeSelector.ItemsSource = new[]
        {
            new ThemeOption(ThemePreference.Light),
            new ThemeOption(ThemePreference.Dark),
            new ThemeOption(ThemePreference.System),
        };
        ThemeSelector.SelectedItem = ThemeSelector.Items
            .OfType<ThemeOption>()
            .FirstOrDefault(option => option.Mode == currentTheme);
        _initializingTheme = false;

        UpdateHeader(statusMessage);
        UpdateCaptureDisplay();
        UpdateStartupPresentation();
    }

    /// <summary>同步复选框和启动项说明；IsChecked 只由真实注册表状态驱动。</summary>
    private void UpdateStartupPresentation()
    {
        _initializingStartup = true;
        StartupCheckBox.IsChecked = _startupStatus.IsEnabled;
        _initializingStartup = false;

        StartupStatusText.Text = _startupStatus.Message ?? string.Empty;
        StartupStatusText.Visibility = string.IsNullOrWhiteSpace(_startupStatus.Message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnStartupOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_initializingStartup || _busy || _storageBusy || _startupBusy)
        {
            return;
        }

        var requestedEnabled = StartupCheckBox.IsChecked == true;
        if (requestedEnabled == _startupStatus.IsEnabled)
        {
            return;
        }

        _startupBusy = true;
        StartupCheckBox.IsEnabled = false;
        ClearError();

        try
        {
            var result = requestedEnabled
                ? _startupService.Enable()
                : _startupService.Disable();

            _startupStatus = result.Status;

            if (!result.Success)
            {
                SetError(result.Error ?? "更新开机启动项失败，已恢复实际状态。");
            }
        }
        catch (Exception ex)
        {
            _startupStatus = _startupService.GetStatus();
            SetError($"更新开机启动项失败，已恢复实际状态：{ex.Message}");
        }
        finally
        {
            _startupBusy = false;
            UpdateStartupPresentation();
            StartupCheckBox.IsEnabled = !_busy && !_storageBusy;
        }
    }

    private void UpdateHeader(string? statusMessage)
    {
        EffectiveText.Text = _effective is null
            ? "当前生效：未注册（仍可从托盘打开窗口）"
            : $"当前生效：{_effective.Value.DisplayText}";

        if (_effective is not null && _effective.Value != _saved)
        {
            SavedText.Text = $"配置中保存：{_saved.DisplayText}（启动时不可用，未覆盖配置）";
            SavedText.Visibility = Visibility.Visible;
        }
        else
        {
            SavedText.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            StatusText.Text = statusMessage;
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _storageBusy)
        {
            return;
        }

        if (_recording)
        {
            CancelRecording();
            return;
        }

        _recording = true;
        ClearError();
        UpdateCaptureDisplay();
        CaptureButton.Focus();
    }

    private void OnManualKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplyManualInput();
        }
    }

    private void OnApplyManualClick(object sender, RoutedEventArgs e) => ApplyManualInput();

    private void ApplyManualInput()
    {
        if (_busy || _storageBusy)
        {
            return;
        }

        var text = ManualGestureBox.Text.Trim();

        if (text.Length == 0)
        {
            SetError("请输入快捷键组合，例如 Ctrl+Alt+K。");
            return;
        }

        if (!HotkeyGesture.TryParse(text, out var gesture, out var error))
        {
            SetError(error ?? "快捷键组合不合法。");
            return;
        }

        _recording = false;
        _pending = gesture;
        ManualGestureBox.Text = gesture.DisplayText;
        ClearError();
        UpdateCaptureDisplay();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording || _busy || _storageBusy)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            e.Handled = true;
            CancelRecording();
            return;
        }

        if (key is Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            e.Handled = true;
            return;
        }

        if (HotkeyGesture.IsModifierKey(key))
        {
            e.Handled = true;
            CaptureText.Text = HotkeyGesture.FormatModifiers(Keyboard.Modifiers);
            return;
        }

        if (!HotkeyGesture.TryCreate(Keyboard.Modifiers, key, out var gesture, out var error))
        {
            e.Handled = true;
            SetError(error ?? "这个组合不能用作全局快捷键。");
            CaptureText.Text = "按下新的组合键…（Esc 取消）";
            return;
        }

        e.Handled = true;
        _pending = gesture;
        _recording = false;
        ManualGestureBox.Text = gesture.DisplayText;
        ClearError();
        UpdateCaptureDisplay();
    }

    private void OnRestoreDefaultClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _storageBusy)
        {
            return;
        }

        _recording = false;
        _pending = HotkeyGesture.Default;
        ManualGestureBox.Text = HotkeyGesture.Default.DisplayText;
        SelectSearchTextCheckBox.IsChecked = true;
        ClearError();
        UpdateCaptureDisplay();
    }

    private void OnThemeSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializingTheme || _busy || _storageBusy || ThemeSelector.SelectedItem is not ThemeOption option)
        {
            return;
        }

        if (option.Mode == _appliedTheme)
        {
            return;
        }

        var previous = _appliedTheme;
        var result = _applyTheme(option.Mode);

        if (!result.Success)
        {
            _initializingTheme = true;
            ThemeSelector.SelectedItem = ThemeSelector.Items
                .OfType<ThemeOption>()
                .FirstOrDefault(item => item.Mode == previous);
            _initializingTheme = false;

            SetError(result.Error ?? "切换主题失败，已恢复原主题。");
            return;
        }

        _appliedTheme = option.Mode;
    }

    private async void OnStorageClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _storageBusy || !_storageChangeAllowed)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "选择 ClipAssist 数据目录",
            Multiselect = false,
        };

        if (Directory.Exists(_currentDataDirectory))
        {
            dialog.InitialDirectory = _currentDataDirectory;
        }

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        var target = dialog.FolderName;
        if (SameDirectory(target, _currentDataDirectory))
        {
            SetError("目标目录与当前数据目录相同，无需迁移。");
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"将把 settings.json 和 history.json 复制到：\n{target}\n\n" +
            "旧目录不会删除。完成后应用会自动重启；当前窗口中尚未保存的快捷键或焦点修改不会保留。\n\n是否继续？",
            "迁移数据目录",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _storageBusy = true;
        SetButtonsEnabled(false);
        ClearError();

        try
        {
            var result = await _applyStorage(target);
            if (!result.Success)
            {
                SetError(result.Error ?? "迁移数据目录失败，已保留原目录。");
                return;
            }

            StorageChanged = true;
            _currentDataDirectory = target;
            StoragePathBox.Text = target;
            StoragePathBox.ToolTip = target;
            StorageHintText.Text = "数据目录已迁移，应用即将重启。旧目录保留在原位置。";

            MessageBox.Show(
                this,
                "数据目录迁移完成，应用即将重启。\n\n旧目录中的文件没有被删除。",
                "迁移完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            CloseWithResult(true);
        }
        catch (Exception ex)
        {
            SetError($"迁移数据目录失败，已保留原目录：{ex.Message}");
        }
        finally
        {
            if (!StorageChanged)
            {
                _storageBusy = false;
                SetButtonsEnabled(true);
            }
        }
    }

    private static bool SameDirectory(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _storageBusy)
        {
            return;
        }

        if (_recording)
        {
            SetError("请先按下一个完整的快捷键组合，或按 Esc 取消录入。");
            return;
        }

        // 手动输入框里如果还有未点“应用”的内容，保存时一并校验，
        // 避免用户以为它生效了、实际却保存了旧组合。
        var manualText = ManualGestureBox.Text.Trim();
        if (manualText.Length > 0)
        {
            if (!HotkeyGesture.TryParse(manualText, out var manualGesture, out var manualError))
            {
                SetError($"手动输入的快捷键无效：{manualError}");
                return;
            }

            _pending = manualGesture;
        }

        _busy = true;
        SetButtonsEnabled(false);

        HotkeyApplyResult result;
        try
        {
            result = _apply(_pending, SelectSearchTextCheckBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            result = new HotkeyApplyResult(false, $"保存设置失败：{ex.Message}", _effective);
        }

        if (!result.Success)
        {
            _busy = false;
            SetButtonsEnabled(true);
            SetError(result.Error ?? "注册快捷键失败，原快捷键仍然有效。");
            return;
        }

        CloseWithResult(true);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _storageBusy)
        {
            return;
        }

        CloseWithResult(false);
    }

    private void CancelRecording()
    {
        _recording = false;
        ClearError();
        UpdateCaptureDisplay();
    }

    private void UpdateCaptureDisplay()
    {
        CaptureText.Text = _recording
            ? "按下新的组合键…（Esc 取消）"
            : _pending.DisplayText;
    }

    private void SetError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        CaptureButton.IsEnabled = enabled;
        ManualGestureBox.IsEnabled = enabled;
        ApplyManualButton.IsEnabled = enabled;
        RestoreDefaultButton.IsEnabled = enabled;
        SelectSearchTextCheckBox.IsEnabled = enabled;
        StartupCheckBox.IsEnabled = enabled && !_startupBusy;
        ThemeSelector.IsEnabled = enabled;
        StorageChangeButton.IsEnabled = enabled && _storageChangeAllowed;
        SaveButton.IsEnabled = enabled;
        CancelButton.IsEnabled = enabled;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 迁移期间禁止关闭窗口，否则回调还在运行而 App 已经离开 ShowDialog。
        if (_storageBusy && !StorageChanged)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private void CloseWithResult(bool result)
    {
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            // 非 ShowDialog 调用时没有 DialogResult，直接关闭即可。
            Close();
        }
    }

    private sealed record ThemeOption(ThemePreference Mode)
    {
        public override string ToString() => Mode.ToDisplayName();
    }
}