using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ClipAssist.Infrastructure;
using ClipAssist.Models;
using ClipAssist.Services;
using ClipAssist.ViewModels;

namespace ClipAssist;

/// <summary>
/// 应用入口：单实例、托盘、全局快捷键、剪贴板服务、持久化的装配与生命周期管理。
/// 任何资源的释放都集中在 OnExit，保证退出后不残留快捷键、监听或托盘图标。
/// </summary>
public partial class App : Application
{
    private ClipboardService? _clipboardService;
    private HistoryPersistence? _persistence;
    private HotkeyManager? _hotkeyManager;
    private TrayService? _tray;
    private SingleInstanceGuard? _singleInstance;
    private CrossModeGuard? _crossModeGuard;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private FloatingWindow? _floatingWindow;
    private FloatingViewModel? _floatingViewModel;
    private Size? _floatingSize;
    private SettingsService? _settingsService;
    private SettingsWindow? _settingsWindow;
    private ThemeService? _themeService;
    private SystemThemeWatcher? _themeWatcher;
    private DataLocationResolution? _dataLocation;
    private StorageMigrationService? _storageMigrationService;
    private StartupService? _startupService;

    private bool _isExiting;
    private bool _isStartupLaunch;
    private bool _showRequestedBeforeWindowReady;
    private bool _storageRelocationSucceeded;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _isStartupLaunch = e.Args.Any(argument =>
            string.Equals(
                argument,
                StartupService.StartupArgument,
                StringComparison.OrdinalIgnoreCase));
        

        // ① 单实例与跨模式冲突：同模式第二次启动只唤醒已有窗口；
        //    另一模式已在运行时明确提示并退出，避免快捷键和托盘冲突。
        var identity = AppIdentity.Current;
        _singleInstance = new SingleInstanceGuard(identity.InstanceName);
        if (!_singleInstance.IsFirstInstance)
        {
            _singleInstance.SignalShow();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        try
        {
            _crossModeGuard = new CrossModeGuard();
        }
        catch (Exception ex)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            MessageBox.Show(
                $"无法检查另一个 ClipAssist 版本是否正在运行：{ex.Message}",
                "ClipAssist",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        if (!_crossModeGuard.IsFirstInstance)
        {
            _crossModeGuard.Dispose();
            _crossModeGuard = null;
            _singleInstance.Dispose();
            _singleInstance = null;
            MessageBox.Show(
                "检测到另一个 ClipAssist 版本正在运行。\n\n"
                + "为了避免争用全局快捷键和出现两个托盘图标，请先退出另一个版本后再启动本版本。",
                "ClipAssist",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _singleInstance.ShowRequested += OnExternalShowRequested;

        // ② 设置、数据与界面（沿用 M2/M3 的结构，不在这里重复实现业务逻辑）。
        var location = new DataLocationResolver(AppPaths.GetDataLocationFilePath()).Resolve();
        _dataLocation = location;
        _storageMigrationService = new StorageMigrationService(
            new DataLocationPointer(AppPaths.GetDataLocationFilePath()));

        _settingsService = new SettingsService(
            new SettingsStore(Path.Combine(location.Directory, AppPaths.SettingsFileName)));
        var settingsResult = _settingsService.Load();

        // 主题必须在主窗口创建前应用，避免启动时先闪一下浅色窗口。
        _themeService = new ThemeService();
        var savedTheme = ThemePreferenceExtensions.TryParse(
            _settingsService.Current.Theme?.Mode,
            out var parsedTheme)
            ? parsedTheme
            : ThemePreference.System;
        _themeService.Initialize(savedTheme);

        _themeWatcher = new SystemThemeWatcher();
        _themeWatcher.Changed += (_, _) => _themeService?.RefreshSystemTheme();

        var storage = new ClipboardStorage(location.Directory);
        var history = new ClipboardHistory();
        var persistence = new HistoryPersistence(storage, history);
        var loadWarning = persistence.Load();

        _clipboardService = new ClipboardService(history);
        _persistence = persistence;

        var startupNotice = JoinNotices(loadWarning, settingsResult.Warning);
        var dataDirectoryNotice = CreateDataDirectoryNotice(location);
        if (dataDirectoryNotice is not null)
        {
            startupNotice = JoinNotices(startupNotice, dataDirectoryNotice);
        }
        var viewModel = new MainViewModel(
            history,
            _clipboardService,
            startupNotice,
            saveHistory: persistence.SaveCurrentAsync);
        _viewModel = viewModel;
        _floatingViewModel = new FloatingViewModel(history);
        persistence.Notice += (_, message) => viewModel.ShowNotice(message);

        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        // 恢复上次普通状态下的位置；没有有效记录时保持 XAML 里的居中。
        var placement = WindowPlacementService.Sanitize(
            _settingsService.Current.Window,
            window.MinWidth,
            window.MinHeight,
            WindowPlacementService.VirtualScreen,
            WindowPlacementService.PrimaryWorkArea);

        WindowPlacementService.Apply(window, placement);

        window.Closing += OnMainWindowClosing;
        window.ExitRequested += (_, _) => ExitApplication();
        window.SettingsRequested += (_, _) => OpenSettingsWindow();
        window.FloatingModeRequested += (_, _) => OpenFloatingWindow();
        _window = window;
        MainWindow = window;

        // 开机登录时由 Run 项带 --startup 启动：只创建后台服务和托盘图标，
        // 不显示、不激活主窗口，也不把焦点交给搜索框。
        // 用户手动启动或从托盘入口打开时，仍走下面的正常显示路径。
        if (!_isStartupLaunch)
        {
            window.Show();
            _themeService.ApplyWindowChrome(window);
        }

        // ③ 托盘与快捷键。
        // 托盘初始化失败不能拖垮主程序；窗口仍可用，并保留 Ctrl+Q 作为退出路径。
        TrayService? tray = null;
        try
        {
            tray = new TrayService(IconPath);
            tray.ShowRequested += (_, _) => ShowMainWindow();
            tray.FloatingRequested += (_, _) => OpenFloatingWindow();
            tray.ExitRequested += (_, _) => ExitApplication();
            tray.SettingsRequested += (_, _) => OpenSettingsWindow();
            _tray = tray;
        }
        catch (Exception ex)
        {
            tray?.Dispose();
            _tray = null;
            viewModel.ShowNotice($"系统托盘初始化失败：{ex.Message} 可使用 Ctrl+Q 或关闭主窗口退出。");
        }

        if (_tray?.Warning is { } trayWarning)
        {
            viewModel.ShowNotice(trayWarning);
        }

        var hotkeyService = new HotkeyService();
        _hotkeyManager = new HotkeyManager(hotkeyService, _settingsService);
        // 全局快捷键只负责唤起紧凑浮窗，不改变主窗口的显示状态。
        _hotkeyManager.Pressed += (_, _) => OpenFloatingWindow();

        HotkeyInitializeResult hotkeyResult;
        try
        {
            hotkeyResult = _hotkeyManager.Initialize();
        }
        catch (Exception ex)
        {
            hotkeyResult = new HotkeyInitializeResult(
                null,
                $"初始化全局快捷键失败：{ex.Message}");
        }

        UpdateHotkeyPresentation();

        if (hotkeyResult.Warning is not null)
        {
            viewModel.ShowNotice(JoinNotices(hotkeyResult.Warning, dataDirectoryNotice) ?? string.Empty);
            _tray?.ShowBalloon("快捷键状态", hotkeyResult.Warning);
        }
        else if (hotkeyResult.EffectiveGesture is not null)
        {
            viewModel.ShowNotice(JoinNotices(
                $"全局快捷键 {hotkeyResult.EffectiveGesture.Value.DisplayText} 已生效。",
                dataDirectoryNotice) ?? string.Empty);
        }

        if (dataDirectoryNotice is not null)
        {
            _tray?.ShowBalloon("数据目录", dataDirectoryNotice);
        }

        if (_showRequestedBeforeWindowReady)
        {
            _showRequestedBeforeWindowReady = false;
            ShowMainWindow();
        }
    }

    /// <summary>
    /// 打开或激活浮窗。同一时间只保留一个浮窗实例；关闭后引用清空，可再次创建。
    /// </summary>
    private void OpenFloatingWindow()
    {
        if (_floatingViewModel is null)
        {
            return;
        }

        if (_floatingWindow is { IsLoaded: true } existing)
        {
            // 隐藏或最小化时复用同一个实例，不创建重复浮窗。
            existing.ShowAndFocus();
            return;
        }

        var floating = new FloatingWindow
        {
            DataContext = _floatingViewModel,
        };

        // 紧凑尺寸是首次打开的默认值；同一运行期间保留用户手动调整后的尺寸。
        if (_floatingSize is { Width: > 0, Height: > 0 } size)
        {
            floating.Width = Math.Max(floating.MinWidth, size.Width);
            floating.Height = Math.Max(floating.MinHeight, size.Height);
        }

        floating.ExpandRequested += (_, _) => ShowMainWindow();

        floating.Closing += (_, _) =>
        {
            var bounds = floating.WindowState == WindowState.Normal
                ? new Rect(floating.Left, floating.Top, floating.ActualWidth, floating.ActualHeight)
                : floating.RestoreBounds;

            if (double.IsFinite(bounds.Width) &&
                double.IsFinite(bounds.Height) &&
                bounds.Width >= floating.MinWidth &&
                bounds.Height >= floating.MinHeight)
            {
                _floatingSize = new Size(bounds.Width, bounds.Height);
            }
        };

        floating.Closed += (_, _) =>
        {
            if (ReferenceEquals(_floatingWindow, floating))
            {
                _floatingWindow = null;
            }
        };

        _floatingWindow = floating;
        floating.Show();
        _themeService?.ApplyWindowChrome(floating);
        floating.ShowAndFocus();
    }

    /// <summary>打开设置窗口。设置窗口只负责录入，注册/保存由 HotkeyManager 完成。</summary>
    private void OpenSettingsWindow()
    {
        if (_hotkeyManager is null)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        if (_window is null)
        {
            return;
        }

        if (!_window.IsVisible)
        {
            ShowMainWindow();
        }

        var dataDirectory = _dataLocation?.Directory ?? AppPaths.GetDataDirectory();
        _startupService ??= new StartupService(null, AppIdentity.Current.StartupValueName);
        var storageCanChange =
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                AppPaths.DataDirectoryEnvironmentVariable)) &&
            _settingsService?.CanPersist == true;

        var dialog = new SettingsWindow(
            _hotkeyManager.SavedGesture,
            _hotkeyManager.EffectiveGesture,
            _hotkeyManager.StatusMessage,
            _settingsService?.Current.Focus?.SelectSearchTextOnActivate ?? true,
            _themeService?.SelectedMode ?? ThemePreference.System,
            dataDirectory,
            storageCanChange,
            _startupService,
            ApplyStorageFromSettingsWindow,
            ApplyHotkeyFromSettingsWindow,
            ApplyThemeFromSettingsWindow)
        {
            Owner = _window,
        };

        dialog.SourceInitialized += (_, _) => _themeService?.ApplyWindowChrome(dialog);

        _settingsWindow = dialog;
        var storageChanged = false;

        try
        {
            dialog.ShowDialog();
            storageChanged = dialog.StorageChanged;
        }
        finally
        {
            _settingsWindow = null;
        }

        if (storageChanged)
        {
            RestartAfterStorageChange();
        }
    }

    private HotkeyApplyResult ApplyHotkeyFromSettingsWindow(
        HotkeyGesture gesture,
        bool selectSearchTextOnActivate)
    {
        if (_hotkeyManager is null)
        {
            return new HotkeyApplyResult(false, "快捷键服务不可用。", null);
        }

        var previous = _hotkeyManager.SavedGesture;
        var result = _hotkeyManager.Apply(gesture, selectSearchTextOnActivate);

        if (result.Success && result.EffectiveGesture is not null)
        {
            UpdateHotkeyPresentation();
            _viewModel?.ShowNotice(
                previous == result.EffectiveGesture
                    ? "呼出焦点设置已保存。"
                    : $"全局快捷键已更新为 {result.EffectiveGesture.Value.DisplayText}。");
        }

        return result;
    }

    /// <summary>
    /// 设置窗口选择主题。先切资源、再只写主题字段；写盘失败恢复原主题。
    /// 这里不调用 HotkeyManager，因此不会提交设置窗口里尚未保存的快捷键或焦点改动。
    /// </summary>
    private ThemeApplyResult ApplyThemeFromSettingsWindow(ThemePreference mode)
    {
        if (_themeService is null || _settingsService is null)
        {
            return new ThemeApplyResult(false, "主题服务不可用。");
        }

        var previous = _themeService.SelectedMode;

        try
        {
            _themeService.Apply(mode);
            var save = _settingsService.SaveTheme(mode);
            if (save.Success)
            {
                return new ThemeApplyResult(true, null);
            }

            _themeService.Apply(previous);
            return new ThemeApplyResult(false, save.Error ?? "保存主题失败，已恢复原主题。");
        }
        catch (Exception ex)
        {
            _themeService.Apply(previous);
            return new ThemeApplyResult(false, $"切换主题失败，已恢复原主题：{ex.Message}");
        }
    }

    /// <summary>
    /// 冻结剪贴板/历史写入，完成复制、校验和指针写入。
    /// 成功后保持冻结，待设置窗口关闭后重启；失败则恢复监听，旧目录继续使用。
    /// </summary>
    private async Task<StorageMigrationResult> ApplyStorageFromSettingsWindow(string targetDirectory)
    {
        if (_dataLocation is null ||
            _settingsService is null ||
            _clipboardService is null ||
            _persistence is null ||
            _storageMigrationService is null)
        {
            return new StorageMigrationResult(false, "数据存储服务不可用，未执行迁移。", null);
        }

        if (_storageRelocationSucceeded)
        {
            return new StorageMigrationResult(false, "数据目录已经迁移，应用即将重启。", null);
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                AppPaths.DataDirectoryEnvironmentVariable)))
        {
            return new StorageMigrationResult(
                false,
                "数据目录由 CLIPASSIST_DATA_DIR 环境变量固定，不能在此修改。",
                null);
        }

        if (!_settingsService.CanPersist)
        {
            return new StorageMigrationResult(
                false,
                "当前设置文件不可写或来自更高版本，未执行迁移。",
                null);
        }

        var clipboard = _clipboardService;
        var persistence = _persistence;

        try
        {
            await clipboard.SuspendCaptureAsync();
            await persistence.FlushForMigrationAsync();

            if (_window is not null)
            {
                var placement = WindowPlacementService.Capture(_window);
                if (placement is not null)
                {
                    var save = _settingsService.SaveWindowPlacement(placement);
                    if (!save.Success)
                    {
                        ResumeStorageServices(clipboard, persistence);
                        return new StorageMigrationResult(
                            false,
                            $"迁移前保存当前设置失败：{save.Error}",
                            null);
                    }
                }
            }

            var result = _storageMigrationService.Migrate(_dataLocation.Directory, targetDirectory);
            if (!result.Success)
            {
                ResumeStorageServices(clipboard, persistence);
                return result;
            }

            // 从这里开始不再恢复监听/保存，避免指针切换后继续写旧目录。
            _storageRelocationSucceeded = true;
            return result;
        }
        catch (Exception ex)
        {
            ResumeStorageServices(clipboard, persistence);
            return new StorageMigrationResult(
                false,
                $"迁移数据目录失败，已保留原目录：{ex.Message}",
                null);
        }
    }

    private static void ResumeStorageServices(
        ClipboardService clipboard,
        HistoryPersistence persistence)
    {
        try
        {
            persistence.ResumeAfterFailedMigration();
        }
        catch (Exception)
        {
        }

        try
        {
            clipboard.ResumeCapture();
        }
        catch (Exception)
        {
        }
    }

    private void RestartAfterStorageChange()
    {
        _isExiting = true;
        Shutdown();
    }

    private void UpdateHotkeyPresentation()
    {
        var gesture = _hotkeyManager?.EffectiveGesture;

        _tray?.SetTooltip(gesture is null
            ? "ClipAssist — 快捷键不可用，请从托盘打开"
            : $"ClipAssist — {gesture.Value.DisplayText} 呼出");
    }

    /// <summary>第二个实例请求显示窗口（后台线程 → UI 线程）。</summary>
    private void OnExternalShowRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_window is null)
            {
                _showRequestedBeforeWindowReady = true;
                return;
            }

            ShowMainWindow();
        }));
    }

    private void ShowMainWindow()
    {
        // 设置窗口是模态的，此时不要再把焦点抢到主窗口搜索框。
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var selectSearchText = _settingsService?.Current.Focus?.SelectSearchTextOnActivate ?? true;
        if (_window is null)
        {
            return;
        }

        _window.ShowAndActivate(selectSearchText);
        // 静默启动时窗口尚未创建 HWND，首次由托盘打开时再补标题栏主题。
        _themeService?.ApplyWindowChrome(_window);
    }

    /// <summary>关闭窗口 = 隐藏到托盘；只有托盘“退出程序”才会真正结束。</summary>
    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        // 托盘不可用时不能把唯一窗口隐藏，否则用户没有重新打开或退出的入口。
        if (_tray is null)
        {
            _isExiting = true;
            e.Cancel = false;
            Shutdown();
            return;
        }

        SaveWindowPlacement();

        e.Cancel = true;
        _window?.Hide();

        var gesture = _hotkeyManager?.EffectiveGesture?.DisplayText;
        var hint = string.IsNullOrEmpty(gesture)
            ? "双击托盘图标可以重新打开主窗口。"
            : $"按 {gesture} 可以打开剪贴板浮窗；双击托盘图标可以重新打开主窗口。";

        _tray?.ShowBackgroundHintOnce(hint);
    }

    private void ExitApplication()
    {
        SaveWindowPlacement();
        _isExiting = true;
        Shutdown();
    }

    /// <summary>
    /// 保存窗口当前的位置与大小。
    /// 只在“关闭到托盘”和“真正退出”时调用，不在拖动/缩放过程中反复写盘。
    /// SettingsService 会跳过与上次保存相同的位置，避免关闭 + 退出重复写。
    /// </summary>
    private void SaveWindowPlacement()
    {
        if (_settingsService is null || _window is null)
        {
            return;
        }

        // 设置文件来自更高版本或不可读时，启动提示已经说明，退出时不再反复弹提示。
        if (!_settingsService.CanPersist)
        {
            return;
        }

        var placement = WindowPlacementService.Capture(_window);
        if (placement is null)
        {
            // 窗口尚未显示或数值无效时宁可不写，避免覆盖上一次的有效记录。
            return;
        }

        var result = _settingsService.SaveWindowPlacement(placement);

        if (!result.Success && _window.IsVisible)
        {
            _viewModel?.ShowNotice(result.Error ?? "保存窗口位置失败。");
        }
    }

    private static string? CreateDataDirectoryNotice(DataLocationResolution location)
    {
        var fallbackNotice = location.Source == DataLocationSource.Fallback
            ? $"默认数据目录不可写，设置与历史已改存到 {location.Directory}。"
            : null;

        return JoinNotices(fallbackNotice, location.Warning);
    }

    private static string? JoinNotices(params string?[] messages)
    {
        var parts = messages
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToArray();

        return parts.Length == 0 ? null : string.Join(" ", parts);
    }

    private static string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ClipAssist.ico");

    protected override void OnExit(ExitEventArgs e)
    {
        // 迁移成功后不再保存旧目录；否则退出时仍可能把最后状态写回旧位置。
        if (!_storageRelocationSucceeded)
        {
            SaveWindowPlacement();
        }

        // 释放顺序：主题监听 → 快捷键 → 托盘 → 窗口事件 → 剪贴板监听 → 保存 → 单实例/跨模式守卫。
        _themeWatcher?.Dispose();
        _themeWatcher = null;

        _hotkeyManager?.Dispose();
        _hotkeyManager = null;

        _tray?.Dispose();
        _tray = null;

        if (_floatingWindow is not null)
        {
            if (_floatingWindow.IsLoaded)
            {
                _floatingWindow.Close();
            }

            _floatingWindow = null;
        }

        if (_window is not null)
        {
            _window.Closing -= OnMainWindowClosing;
            _window = null;
        }

        _clipboardService?.Dispose();
        _clipboardService = null;

        if (!_storageRelocationSucceeded)
        {
            _persistence?.SaveNow();
        }

        _persistence?.Dispose();
        _persistence = null;

        _crossModeGuard?.Dispose();
        _crossModeGuard = null;

        _singleInstance?.Dispose();
        _singleInstance = null;

        // 单实例互斥量释放后再启动新进程，避免新进程被当成第二个实例直接退出。
        if (_storageRelocationSucceeded)
        {
            TryStartNewInstance();
        }

        base.OnExit(e);
    }

    private static void TryStartNewInstance()
    {
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "ClipAssist.exe");
            if (!File.Exists(executable))
            {
                executable = Environment.ProcessPath ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // 新进程启动失败时旧数据仍在，用户可手动启动；此处无法再安全显示 UI。
        }
    }
}