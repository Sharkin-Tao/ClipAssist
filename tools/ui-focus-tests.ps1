# ClipAssist V0.2-06：快捷键呼出后的搜索框焦点测试。
# 说明：本会话无法注入真实键盘输入，搜索文本通过 UI Automation ValuePattern 修改；
# 托盘双击无法可靠模拟，脚本用第二实例请求显示来覆盖与托盘共用的 ShowMainWindow 路径。

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class FocusWin {
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);

    public static IntPtr FindTopWindow(uint targetPid, string title) {
        var after = IntPtr.Zero;
        while (true) {
            after = FindWindowEx(IntPtr.Zero, after, null, null);
            if (after == IntPtr.Zero) break;
            uint pid; GetWindowThreadProcessId(after, out pid);
            if (pid != targetPid) continue;
            var text = new StringBuilder(256); GetWindowText(after, text, 256);
            if (text.ToString() == title) return after;
        }
        return IntPtr.Zero;
    }

    public static IntPtr FindMessageWindow(uint targetPid, string title) {
        var after = IntPtr.Zero;
        while (true) {
            after = FindWindowEx((IntPtr)(-3), after, null, null);
            if (after == IntPtr.Zero) break;
            uint pid; GetWindowThreadProcessId(after, out pid);
            if (pid != targetPid) continue;
            var text = new StringBuilder(256); GetWindowText(after, text, 256);
            if (text.ToString() == title) return after;
        }
        return IntPtr.Zero;
    }
}
"@

$exe = Join-Path $PSScriptRoot '..\bin\Debug\net10.0-windows\ClipAssist.exe'
$script:passed = 0
$script:failed = 0
$script:failures = @()

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) {
        $script:passed++
        Write-Host "PASS  $name"
    }
    else {
        $script:failed++
        $script:failures += "$name ($detail)"
        Write-Host "FAIL  $name  $detail" -ForegroundColor Red
    }
}

function New-DataDir([string]$tag) {
    $dir = Join-Path $env:TEMP ("clipassist-focus-$tag-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    return $dir
}

function Start-App([string]$dataDir) {
    $env:CLIPASSIST_DATA_DIR = $dataDir

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = Split-Path -Parent $exe

    $proc = [System.Diagnostics.Process]::Start($psi)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
        $proc.Refresh()
        if ($proc.HasExited) {
            throw "启动失败 ExitCode=$($proc.ExitCode)"
        }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
            break
        }
    }

    Start-Sleep -Milliseconds 800
    return $proc
}

function Stop-App($proc) {
    if ($null -eq $proc) { return }
    try {
        if (-not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            [void]$proc.WaitForExit(3000)
        }
    }
    catch { }
}

function Find-ById($root, [string]$automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))[0]
}

function Find-Button($root, [string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $name)
    return @(
        $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) |
            Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button }
    )[0]
}

function Find-ListItems($root) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
}

function Set-Search($search, [string]$text) {
    $value = $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue($text)
    Start-Sleep -Milliseconds 700
}

function Get-SelectedText($search) {
    try {
        $textPattern = $search.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        $ranges = $textPattern.GetSelection()
        if ($ranges.Count -eq 0) { return '' }
        return $ranges[0].GetText(-1)
    }
    catch {
        return '<no-text-pattern>'
    }
}

function Send-Hotkey([int]$targetPid) {
    $messageWindow = [FocusWin]::FindMessageWindow([uint32]$targetPid, 'ClipAssist.HotkeyWindow')
    if ($messageWindow -eq [IntPtr]::Zero) {
        throw '找不到 ClipAssist.HotkeyWindow'
    }

    [void][FocusWin]::PostMessage($messageWindow, 0x0312, [IntPtr]0x4341, [IntPtr]::Zero)
    [void][FocusWin]::PostMessage($messageWindow, 0x0312, [IntPtr]0x4342, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 900
}

function Close-ToTray([IntPtr]$hwnd) {
    [void][FocusWin]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 500
}

function Read-Settings([string]$dataDir) {
    $path = Join-Path $dataDir 'settings.json'
    if (-not (Test-Path $path)) { return $null }
    return (Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Write-SeedHistory([string]$dataDir) {
    $entries = @(
        [ordered]@{
            id        = '11111111-1111-1111-1111-111111111111'
            text      = 'alpha one'
            createdAt = '2026-10-01T09:00:00+08:00'
            updatedAt = '2026-10-01T09:00:00+08:00'
        },
        [ordered]@{
            id        = '22222222-2222-2222-2222-222222222222'
            text      = 'beta two'
            createdAt = '2026-10-01T09:01:00+08:00'
            updatedAt = '2026-10-01T09:01:00+08:00'
        },
        [ordered]@{
            id        = '33333333-3333-3333-3333-333333333333'
            text      = 'gamma three'
            createdAt = '2026-10-01T09:02:00+08:00'
            updatedAt = '2026-10-01T09:02:00+08:00'
        }
    )

    $json = ConvertTo-Json -InputObject $entries -Depth 4
    [System.IO.File]::WriteAllText(
        (Join-Path $dataDir 'history.json'),
        $json,
        (New-Object System.Text.UTF8Encoding($false)))
}

$tempDirs = @()
$app = $null
$probeForm = $null

try {
    if (-not (Test-Path $exe)) { throw "找不到程序：$exe" }
    if (@(Get-Process -Name ClipAssist -ErrorAction SilentlyContinue).Count -gt 0) {
        throw '请先退出正在运行的 ClipAssist，再运行焦点测试。'
    }

    $dir = New-DataDir 'main'
    $tempDirs += $dir
    Write-SeedHistory $dir
    $app = Start-App $dir

    $hwnd = $app.MainWindowHandle
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $search = Find-ById $root 'SearchBox'
    if ($null -eq $search) { throw '找不到搜索框（AutomationId=SearchBox）' }

    Write-Host '--- A) 启动阶段不主动抢焦点 ---'
    Check '启动后搜索框存在' ($null -ne $search) 'SearchBox'
    Check '启动时未无条件聚焦搜索框' (-not $search.Current.HasKeyboardFocus) "focused=$($search.Current.HasKeyboardFocus)"

    Write-Host '--- B) 搜索绑定、过滤、清空 ---'
    Set-Search $search 'alpha'
    $alphaItems = Find-ListItems $root
    Check '输入搜索词后过滤生效' ($alphaItems.Count -eq 1) "items=$($alphaItems.Count)"
    Check '搜索文本写入绑定' (
        $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'alpha'
    ) 'value=alpha'

    Set-Search $search 'alphaX'
    $noItems = Find-ListItems $root
    Check '无匹配时列表为空' ($noItems.Count -eq 0) "items=$($noItems.Count)"

    Set-Search $search ''
    $allItems = Find-ListItems $root
    Check '清空搜索后恢复全部记录' ($allItems.Count -eq 3) "items=$($allItems.Count)"

    Write-Host '--- C) 隐藏窗口时快捷键呼出 ---'
    Set-Search $search 'alpha beta'
    Close-ToTray $hwnd
    Check '关闭后窗口隐藏' (-not [FocusWin]::IsWindowVisible($hwnd)) 'hidden'
    Send-Hotkey $app.Id
    Check '快捷键后窗口可见' ([FocusWin]::IsWindowVisible($hwnd)) 'visible'
    Check '隐藏呼出后搜索框获得键盘焦点' ($search.Current.HasKeyboardFocus) "focused=$($search.Current.HasKeyboardFocus)"
    Check '隐藏呼出后全选已有搜索文本' ((Get-SelectedText $search) -eq 'alpha beta') "selected=$(Get-SelectedText $search)"
    Check '聚焦/全选不改变搜索文本' (
        $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'alpha beta'
    ) 'value unchanged'

    Write-Host '--- D) 窗口已显示时再次快捷呼出 ---'
    $sameHwnd = $app.MainWindowHandle
    Send-Hotkey $app.Id
    Check '窗口已显示时仍保持可见' ([FocusWin]::IsWindowVisible($hwnd)) 'visible'
    Check '不会创建重复窗口/进程' (
        $app.MainWindowHandle -eq $sameHwnd -and
        @(Get-Process -Name ClipAssist -ErrorAction SilentlyContinue).Count -eq 1
    ) "hwnd=$sameHwnd"
    Check '已显示时再次呼出仍聚焦搜索框' ($search.Current.HasKeyboardFocus) "focused=$($search.Current.HasKeyboardFocus)"
    Check '已显示时再次呼出仍全选文本' ((Get-SelectedText $search) -eq 'alpha beta') "selected=$(Get-SelectedText $search)"

    Write-Host '--- E) 最小化后快捷呼出 ---'
    [void][FocusWin]::ShowWindow($hwnd, 6)
    Start-Sleep -Milliseconds 400
    Check '最小化生效' ([FocusWin]::IsIconic($hwnd)) 'iconic'
    Send-Hotkey $app.Id
    Check '快捷键恢复最小化窗口' (-not [FocusWin]::IsIconic($hwnd) -and [FocusWin]::IsWindowVisible($hwnd)) 'restored'
    Check '最小化恢复后聚焦搜索框' ($search.Current.HasKeyboardFocus) "focused=$($search.Current.HasKeyboardFocus)"
    Check '最小化恢复后全选文本' ((Get-SelectedText $search) -eq 'alpha beta') "selected=$(Get-SelectedText $search)"

    Write-Host '--- F) 托盘/第二实例恢复路径 ---'
    Close-ToTray $hwnd
    $secondPsi = New-Object System.Diagnostics.ProcessStartInfo
    $secondPsi.FileName = $exe
    $secondPsi.UseShellExecute = $false
    $secondPsi.CreateNoWindow = $true
    $secondPsi.WorkingDirectory = Split-Path -Parent $exe
    $second = [System.Diagnostics.Process]::Start($secondPsi)
    Start-Sleep -Seconds 2
    Check '第二实例退出（单实例仍生效）' ($second.HasExited) "exited=$($second.HasExited)"
    Check '第二实例/托盘路径恢复窗口' ([FocusWin]::IsWindowVisible($hwnd)) 'visible'
    Check '第二实例/托盘路径聚焦搜索框' ($search.Current.HasKeyboardFocus) "focused=$($search.Current.HasKeyboardFocus)"
    Check '第二实例/托盘路径全选文本' ((Get-SelectedText $search) -eq 'alpha beta') "selected=$(Get-SelectedText $search)"

    Write-Host '--- G) 设置中关闭“全选”后的行为 ---'
    $settingsButton = Find-Button $root '设置'
    $settingsButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    $dialogHandle = [FocusWin]::FindTopWindow([uint32]$app.Id, '设置')
    if ($dialogHandle -eq [IntPtr]::Zero) { throw '找不到设置窗口' }
    $dialog = [System.Windows.Automation.AutomationElement]::FromHandle($dialogHandle)
    $checkbox = Find-ById $dialog 'SelectSearchTextOnActivate'
    Check '设置窗口显示“呼出时全选”选项' ($null -ne $checkbox) 'checkbox'
    $toggle = $checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    Check '默认开启全选' ($toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) "state=$($toggle.Current.ToggleState)"
    $toggle.Toggle()
    $save = Find-Button $dialog '保存'
    $save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
    Check '保存后设置窗口关闭' (-not [FocusWin]::IsWindowVisible($dialogHandle)) 'closed'

    $settings = Read-Settings $dir
    Check '全选设置持久化为 false' (
        $null -ne $settings -and
        $null -ne $settings.focus -and
        $settings.focus.selectSearchTextOnActivate -eq $false
    ) "focus=$($settings.focus.selectSearchTextOnActivate)"

    Close-ToTray $hwnd
    Send-Hotkey $app.Id
    Check '关闭全选后仍聚焦搜索框' ($search.Current.HasKeyboardFocus) "focused=$($search.Current.HasKeyboardFocus)"
    Check '关闭全选后不选中已有文本' ((Get-SelectedText $search) -eq '') "selected=$(Get-SelectedText $search)"
    Check '关闭全选后搜索文本保持不变' (
        $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'alpha beta'
    ) 'value unchanged'

    Write-Host '--- H) 重新开启全选并验证持久化 ---'
    $settingsButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    $dialogHandle = [FocusWin]::FindTopWindow([uint32]$app.Id, '设置')
    $dialog = [System.Windows.Automation.AutomationElement]::FromHandle($dialogHandle)
    $checkbox = Find-ById $dialog 'SelectSearchTextOnActivate'
    $toggle = $checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    Check '重新打开设置时读取到 false' ($toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) "state=$($toggle.Current.ToggleState)"
    $toggle.Toggle()
    $save = Find-Button $dialog '保存'
    $save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
    $settings = Read-Settings $dir
    Check '重新开启后持久化为 true' (
        $null -ne $settings -and $settings.focus.selectSearchTextOnActivate -eq $true
    ) "focus=$($settings.focus.selectSearchTextOnActivate)"

    Close-ToTray $hwnd
    Send-Hotkey $app.Id
    Check '重新开启后恢复全选' ((Get-SelectedText $search) -eq 'alpha beta') "selected=$(Get-SelectedText $search)"

    Write-Host '--- I) 其他应用在前台时不主动抢焦点 ---'
    # 先最小化 ClipAssist，让出前台，再用独立窗口模拟“用户正在别的应用里操作”。
    [void][FocusWin]::ShowWindow($hwnd, 6)
    Start-Sleep -Milliseconds 500

    $probeForm = New-Object System.Windows.Forms.Form
    $probeForm.Text = 'ClipAssist Focus Probe'
    $probeForm.Width = 320
    $probeForm.Height = 200
    $probeBox = New-Object System.Windows.Forms.TextBox
    $probeBox.Text = 'probe'
    $probeForm.Controls.Add($probeBox)
    $probeForm.Show()
    $probeForm.Activate()
    [void]$probeBox.Focus()
    [FocusWin]::SwitchToThisWindow($probeForm.Handle, $true)
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 600
    [System.Windows.Forms.Application]::DoEvents()
    Check '测试探针窗口已显示并取得自身焦点' (
        $probeForm.Visible -and $probeBox.Focused
    ) "visible=$($probeForm.Visible) focused=$($probeBox.Focused)"
    Check '应用保持最小化，不自行恢复' (
        [FocusWin]::IsIconic($hwnd)
    ) "iconic=$([FocusWin]::IsIconic($hwnd)) fg=$([FocusWin]::GetForegroundWindow())"
    Start-Sleep -Seconds 1
    [System.Windows.Forms.Application]::DoEvents()
    Check '等待后探针窗口仍显示' (
        $probeForm.Visible
    ) "visible=$($probeForm.Visible)"
    Check '等待后应用仍保持最小化' (
        [FocusWin]::IsIconic($hwnd)
    ) "iconic=$([FocusWin]::IsIconic($hwnd)) fg=$([FocusWin]::GetForegroundWindow())"
}
catch {
    Write-Host "测试异常：$($_.Exception.Message)" -ForegroundColor Red
    $script:failed++
    $script:failures += $_.Exception.Message
}
finally {
    if ($null -ne $probeForm) {
        try { $probeForm.Close() } catch { }
        $probeForm.Dispose()
    }

    Stop-App $app
    foreach ($dir in $tempDirs) {
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host ''
    if ($script:failed -eq 0) {
        Write-Host "结果: $($script:passed) 项通过 / 0 项失败" -ForegroundColor Green
    }
    else {
        Write-Host "结果: $($script:passed) 项通过 / $($script:failed) 项失败" -ForegroundColor Red
        foreach ($failure in $script:failures) { Write-Host "  - $failure" }
    }

    $exitCode = 0
    if ($script:failed -ne 0) { $exitCode = 1 }
    exit $exitCode
}