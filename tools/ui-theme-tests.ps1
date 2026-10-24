# ClipAssist V0.3 主题 UI 测试
# 只覆盖主题相关行为：三种模式、立即生效、重启恢复、跟随系统、旧配置、非法配置。
# 不修改真实 Windows 主题，也不广播系统主题变化。

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class ThemeTestWin32
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    public static IntPtr FindTopLevelWindow(uint targetPid, string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid != targetPid)
            {
                return true;
            }

            var text = new StringBuilder(256);
            GetWindowText(hWnd, text, text.Capacity);
            if (text.ToString() == title)
            {
                found = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }
}
"@

$ErrorActionPreference = 'Stop'
$pass = 0
$fail = 0

function Check([string]$name, [bool]$condition, [string]$detail = '') {
    if ($condition) {
        $script:pass++
        Write-Host "PASS  $name" -ForegroundColor Green
    }
    else {
        $script:fail++
        Write-Host "FAIL  $name  $detail" -ForegroundColor Red
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot 'bin\Debug\net10.0-windows\ClipAssist.exe'
if (-not (Test-Path $exe)) {
    throw "找不到 $exe，请先构建。"
}

if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw '请用 Windows PowerShell（默认 STA）运行本脚本。'
}

function Get-SystemThemeName {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
    $value = (Get-ItemProperty -Path $key -Name AppsUseLightTheme -ErrorAction SilentlyContinue).AppsUseLightTheme
    if ($null -ne $value -and [int]$value -eq 0) { return 'Dark' }
    return 'Light'
}

function Start-TestApp([string]$dataDir) {
    $env:CLIPASSIST_DATA_DIR = $dataDir
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = Split-Path -Parent $exe
    return [System.Diagnostics.Process]::Start($psi)
}

function Wait-Window([int]$processId, [string]$title, [int]$timeoutMs = 6000) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $timeoutMs) {
        $handle = [ThemeTestWin32]::FindTopLevelWindow([uint32]$processId, $title)
        if ($handle -ne [IntPtr]::Zero -and [ThemeTestWin32]::IsWindowVisible($handle)) {
            Start-Sleep -Milliseconds 300
            return [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        }
        Start-Sleep -Milliseconds 120
    }
    throw "等待窗口 [$title] 超时。"
}

function Get-WindowHandle([System.Windows.Automation.AutomationElement]$element) {
    return [IntPtr]$element.Current.NativeWindowHandle
}

function Open-Settings([System.Windows.Automation.AutomationElement]$main) {
    $buttonCondition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '设置')),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))
    $handle = [IntPtr]$main.Current.NativeWindowHandle
    [void][ThemeTestWin32]::SetForegroundWindow($handle)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $button = $null
    while ($watch.ElapsedMilliseconds -lt 6000) {
        # 每次都从 HWND 重新获取元素，避免 UI 线程刚完成布局时的旧缓存。
        $current = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        $button = $current.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
        if ($null -ne $button) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($null -eq $button) {
        $names = @($current.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { $_.Current.Name } |
            Where-Object { $_ })
        throw ('找不到主窗口设置按钮。UIA 名称：' + ($names -join ' | '))
    }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Close-Settings([System.Windows.Automation.AutomationElement]$settings) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CancelButton')
    $cancel = $settings.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $cancel) { throw '找不到设置窗口“取消”按钮。' }
    $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
}

function Get-ThemeCombo([System.Windows.Automation.AutomationElement]$settings) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ThemeSelector')
    $combo = $settings.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $combo) { throw '找不到主题下拉框。' }
    return $combo
}

function Get-SelectedTheme([System.Windows.Automation.AutomationElement]$combo) {
    $pattern = [System.Windows.Automation.SelectionPattern]$combo.GetCurrentPattern(
        [System.Windows.Automation.SelectionPattern]::Pattern)
    $selection = $pattern.Current.GetSelection()
    if ($selection.Count -eq 0) { return '' }
    return $selection[0].Current.Name
}

function Select-Theme([System.Windows.Automation.AutomationElement]$combo, [string]$name) {
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 500

    $items = @([System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem))))

    $target = @($items | Where-Object {
        $_.Current.Name -eq $name -and
        (@($_.GetSupportedPatterns()) -contains [System.Windows.Automation.SelectionItemPattern]::Pattern)
    }) | Select-Object -First 1

    if ($null -eq $target) { throw "找不到可选的主题项：$name" }
    $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 700
}

function Read-Settings([string]$dataDir) {
    $path = Join-Path $dataDir 'settings.json'
    if (-not (Test-Path $path)) { return $null }
    return Get-Content $path -Raw | ConvertFrom-Json
}

function Wait-SettingsTheme([string]$dataDir, [string]$expected, [int]$timeoutMs = 4000) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $timeoutMs) {
        $settings = Read-Settings $dataDir
        if ($null -ne $settings -and $null -ne $settings.theme -and $settings.theme.mode -eq $expected) {
            return $true
        }
        Start-Sleep -Milliseconds 120
    }
    return $false
}

function Count-ThemePixels([IntPtr]$handle, [int]$red, [int]$green, [int]$blue) {
    [ThemeTestWin32+RECT]$rect = New-Object ThemeTestWin32+RECT
    if (-not [ThemeTestWin32]::GetWindowRect($handle, [ref]$rect)) { return -1 }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { return -1 }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $matches = 0
        $total = 0
        # 从标题栏下方开始，避开窗口圆角和标题栏按钮。
        for ($y = 40; $y -lt $height - 10; $y += 3) {
            for ($x = 10; $x -lt $width - 10; $x += 3) {
                $color = $bitmap.GetPixel($x, $y)
                $total++
                if ([Math]::Abs($color.R - $red) -le 12 -and
                    [Math]::Abs($color.G - $green) -le 12 -and
                    [Math]::Abs($color.B - $blue) -le 12) {
                    $matches++
                }
            }
        }
        if ($total -eq 0) { return 0 }
        return $matches / $total
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Assert-ThemeColor([IntPtr]$handle, [string]$theme, [string]$label) {
    if ($theme -eq 'Dark') {
        $ratio = Count-ThemePixels $handle 32 32 32
    }
    else {
        $ratio = Count-ThemePixels $handle 243 243 243
    }
    Check "$label 为 $theme 主题" ($ratio -ge 0.35) ("背景像素占比=" + [Math]::Round($ratio, 3))
}

$systemTheme = Get-SystemThemeName
Write-Host "当前 Windows 应用主题：$systemTheme"

$process = $null
$dataDir = $null
$restartProcess = $null
$invalidProcess = $null

try {
    if (@(Get-Process -Name ClipAssist -ErrorAction SilentlyContinue).Count -gt 0) {
        throw '请先退出正在运行的 ClipAssist。'
    }

    # ---------- 1. 旧配置缺少 theme：默认跟随系统，窗口仍启动 ----------
    $dataDir = Join-Path $env:TEMP ('clipassist-theme-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    $legacyPath = Join-Path $dataDir 'settings.json'
    $legacyJson = @'
{
  "schemaVersion": 3,
  "window": { "left": 100, "top": 100, "width": 720, "height": 520 },
  "hotkey": { "gesture": "Ctrl+Shift+V" },
  "focus": { "selectSearchTextOnActivate": true }
}
'@
    [IO.File]::WriteAllText($legacyPath, $legacyJson, (New-Object Text.UTF8Encoding($false)))

    $process = Start-TestApp $dataDir
    $main = Wait-Window $process.Id 'ClipAssist'
    $mainHandle = Get-WindowHandle $main
    [void][ThemeTestWin32]::SetForegroundWindow($mainHandle)
    Start-Sleep -Milliseconds 500

    $rect = $main.Current.BoundingRectangle
    $initialWidth = $rect.Width
    $initialHeight = $rect.Height
    Check '旧配置无 theme 仍能启动' ($null -ne $main) '主窗口存在'
    Check '记录主窗口初始尺寸' ($initialWidth -gt 0 -and $initialHeight -gt 0) ("$([Math]::Round($initialWidth))x$([Math]::Round($initialHeight))")
    Assert-ThemeColor $mainHandle $systemTheme '旧配置启动后的主窗口'

    Open-Settings $main
    $settings = Wait-Window $process.Id '设置'
    $settingsHandle = Get-WindowHandle $settings
    $combo = Get-ThemeCombo $settings
    Check '旧配置默认选择跟随系统' ((Get-SelectedTheme $combo) -eq '跟随系统') (Get-SelectedTheme $combo)

    # ---------- 2. 三种主题立即生效并写盘 ----------
    Select-Theme $combo '浅色'
    Check '选择浅色后立即写盘' (Wait-SettingsTheme $dataDir 'light') (Read-Settings $dataDir).theme.mode
    [void][ThemeTestWin32]::SetForegroundWindow($settingsHandle)
    Assert-ThemeColor $settingsHandle 'Light' '选择浅色后的设置窗口'

    Select-Theme $combo '深色'
    Check '选择深色后立即写盘' (Wait-SettingsTheme $dataDir 'dark') (Read-Settings $dataDir).theme.mode
    [void][ThemeTestWin32]::SetForegroundWindow($settingsHandle)
    Assert-ThemeColor $settingsHandle 'Dark' '选择深色后的设置窗口'

    Select-Theme $combo '跟随系统'
    Check '选择跟随系统后立即写盘' (Wait-SettingsTheme $dataDir 'system') (Read-Settings $dataDir).theme.mode
    Check '跟随系统解析当前 Windows 主题' ((Get-SelectedTheme $combo) -eq '跟随系统') (Get-SelectedTheme $combo)
    [void][ThemeTestWin32]::SetForegroundWindow($settingsHandle)
    Assert-ThemeColor $settingsHandle $systemTheme '跟随系统时的设置窗口'

    # ---------- 3. 主题切换不得提交未保存的快捷键/焦点 ----------
    $manualCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'HotkeyManualInput')
    $manual = $settings.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $manualCondition)
    $manual.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Ctrl+Alt+K')

    $checkCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SelectSearchTextOnActivate')
    $check = $settings.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $checkCondition)
    $check.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()

    Select-Theme $combo '浅色'
    [void](Wait-SettingsTheme $dataDir 'light')
    Close-Settings $settings
    $afterThemeOnly = Read-Settings $dataDir
    Check '切换主题不提交未保存的快捷键' (
        $afterThemeOnly.hotkey.gesture -eq 'Ctrl+Shift+V'
    ) $afterThemeOnly.hotkey.gesture
    Check '切换主题不提交未保存的焦点设置' (
        $afterThemeOnly.focus.selectSearchTextOnActivate -eq $true
    ) $afterThemeOnly.focus.selectSearchTextOnActivate

    [void][ThemeTestWin32]::SetForegroundWindow($mainHandle)
    Assert-ThemeColor $mainHandle 'Light' '取消设置后的主窗口'
    $afterThemeRect = $main.Current.BoundingRectangle
    Check '主题切换不改变窗口尺寸' (
        [Math]::Abs($afterThemeRect.Width - $initialWidth) -le 1 -and
        [Math]::Abs($afterThemeRect.Height - $initialHeight) -le 1
    ) ("$([Math]::Round($afterThemeRect.Width))x$([Math]::Round($afterThemeRect.Height))")

    # ---------- 4. 重启恢复深色 ----------
    Open-Settings $main
    $settings = Wait-Window $process.Id '设置'
    $combo = Get-ThemeCombo $settings
    Select-Theme $combo '深色'
    [void](Wait-SettingsTheme $dataDir 'dark')
    Close-Settings $settings
    [void][ThemeTestWin32]::SetForegroundWindow($mainHandle)
    Assert-ThemeColor $mainHandle 'Dark' '保存深色后的主窗口'

    Stop-Process -Id $process.Id -Force
    $process.WaitForExit(5000) | Out-Null

    $restartProcess = Start-TestApp $dataDir
    $restartedMain = Wait-Window $restartProcess.Id 'ClipAssist'
    $restartedHandle = Get-WindowHandle $restartedMain
    [void][ThemeTestWin32]::SetForegroundWindow($restartedHandle)
    Start-Sleep -Milliseconds 500
    Assert-ThemeColor $restartedHandle 'Dark' '重启后的主窗口'

    Open-Settings $restartedMain
    $restartedSettings = Wait-Window $restartProcess.Id '设置'
    $restartedCombo = Get-ThemeCombo $restartedSettings
    Check '重启后恢复深色选择' ((Get-SelectedTheme $restartedCombo) -eq '深色') (Get-SelectedTheme $restartedCombo)
    Close-Settings $restartedSettings
    Stop-Process -Id $restartProcess.Id -Force
    $restartProcess.WaitForExit(5000) | Out-Null

    # ---------- 5. 非法主题配置只重置主题 ----------
    $invalidDir = Join-Path $env:TEMP ('clipassist-theme-invalid-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $invalidDir -Force | Out-Null
    $invalidJson = @'
{
  "schemaVersion": 3,
  "theme": { "mode": "neon" },
  "window": { "left": 120, "top": 120, "width": 720, "height": 520 },
  "hotkey": { "gesture": "Ctrl+Alt+K" },
  "focus": { "selectSearchTextOnActivate": false }
}
'@
    [IO.File]::WriteAllText((Join-Path $invalidDir 'settings.json'), $invalidJson, (New-Object Text.UTF8Encoding($false)))

    $invalidProcess = Start-TestApp $invalidDir
    $invalidMain = Wait-Window $invalidProcess.Id 'ClipAssist'
    $invalidHandle = Get-WindowHandle $invalidMain
    [void][ThemeTestWin32]::SetForegroundWindow($invalidHandle)
    Start-Sleep -Milliseconds 500
    Assert-ThemeColor $invalidHandle $systemTheme '非法主题配置启动后的主窗口'

    $invalidSettings = Read-Settings $invalidDir
    Check '非法主题回退跟随系统' ($invalidSettings.theme.mode -eq 'system') $invalidSettings.theme.mode
    Check '非法主题不丢窗口配置' ($null -ne $invalidSettings.window) 'window 仍存在'
    Check '非法主题不丢快捷键' ($invalidSettings.hotkey.gesture -eq 'Ctrl+Alt+K') $invalidSettings.hotkey.gesture
    Check '非法主题不丢焦点配置' (
        $invalidSettings.focus.selectSearchTextOnActivate -eq $false
    ) $invalidSettings.focus.selectSearchTextOnActivate
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    if ($restartProcess -and -not $restartProcess.HasExited) { Stop-Process -Id $restartProcess.Id -Force -ErrorAction SilentlyContinue }
    if ($invalidProcess -and -not $invalidProcess.HasExited) { Stop-Process -Id $invalidProcess.Id -Force -ErrorAction SilentlyContinue }
    if ($dataDir -and (Test-Path $dataDir)) { Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
    if ($invalidDir -and (Test-Path $invalidDir)) { Remove-Item $invalidDir -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host "结果: $pass 项通过 / $fail 项失败"
if ($fail -gt 0) { exit 1 }