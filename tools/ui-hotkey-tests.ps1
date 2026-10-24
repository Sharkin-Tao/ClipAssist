# ClipAssist V0.2-05 自定义全局快捷键端到端测试
#
# 验证方式说明：
#   当前 DSH 会话里 SendInput/keybd_event 被系统阻止（SendInput 返回 0），
#   无法注入真实键盘按键。因此本脚本用两种可验证的信号组合：
#     1) 外部进程尝试注册同一组合：失败说明 ClipAssist 真的持有该全局快捷键；
#     2) 向消息窗口发送 WM_HOTKEY：验证呼出窗口的消息处理路径。
#   设置界面的录入通过 UIA 的“手动输入”入口完成，避免依赖键盘注入。
#
# 未覆盖：elevated 窗口、RDP、IME、安全软件、键盘钩子、真实物理按键链路。

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class HkWin {
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);

    public static IntPtr FindMessageWindow(uint targetPid, string title) {
        var after = IntPtr.Zero;
        while (true) {
            after = FindWindowEx((IntPtr)(-3), after, null, null);
            if (after == IntPtr.Zero) break;
            uint pid; GetWindowThreadProcessId(after, out pid);
            if (pid != targetPid) continue;
            var text = new StringBuilder(256);
            GetWindowText(after, text, 256);
            if (text.ToString() == title) return after;
        }
        return IntPtr.Zero;
    }

    public static IntPtr FindTopLevelWindow(uint targetPid, string title) {
        var after = IntPtr.Zero;
        while (true) {
            after = FindWindowEx(IntPtr.Zero, after, null, null);
            if (after == IntPtr.Zero) break;
            uint pid; GetWindowThreadProcessId(after, out pid);
            if (pid != targetPid) continue;
            var text = new StringBuilder(256);
            GetWindowText(after, text, 256);
            if (text.ToString() == title) return after;
        }
        return IntPtr.Zero;
    }
}
"@

$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\Debug\net10.0-windows\ClipAssist.exe'
$script:pass = 0
$script:fail = 0
$script:probeId = 0x5200
$script:blockerId = 0x5301

if ([System.Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw '请用 Windows PowerShell（默认 STA）运行本脚本。'
}

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) {
        $script:pass++
        Write-Host "  [PASS] $name $detail"
    }
    else {
        $script:fail++
        Write-Host "  [FAIL] $name $detail"
    }
}

function New-DataDir([string]$tag) {
    $dir = Join-Path $env:TEMP ("clipassist-hotkey-" + $tag + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    return $dir
}

function Start-App([string]$dataDir) {
    $env:CLIPASSIST_DATA_DIR = $dataDir

    # 用 CreateProcess 而不是 ShellExecute，避免 Windows“无法验证发布者”警告挡住测试。
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = Split-Path -Parent $exe
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    [void]$proc.Start()
    $proc | Add-Member -NotePropertyName OutputTask -NotePropertyValue $proc.StandardOutput.ReadToEndAsync()
    $proc | Add-Member -NotePropertyName ErrorTask -NotePropertyValue $proc.StandardError.ReadToEndAsync()

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
        $proc.Refresh()
        if ($proc.HasExited) {
            $message = ''
            try { $message = $proc.OutputTask.Result + $proc.ErrorTask.Result } catch { }
            throw "启动失败 ExitCode=$($proc.ExitCode) $message"
        }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
            break
        }
    }

    Start-Sleep -Milliseconds 700
    return $proc
}

function Stop-App($proc) {
    if ($null -ne $proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        try { Wait-Process -Id $proc.Id -Timeout 5 -ErrorAction SilentlyContinue } catch { }
    }
}

function Hide-Window([IntPtr]$hwnd) {
    [void][HkWin]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not [HkWin]::IsWindowVisible($hwnd)) { return }
        Start-Sleep -Milliseconds 80
    }
}

function Wait-Visible([IntPtr]$hwnd, [int]$timeoutMs = 2500) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ([HkWin]::IsWindowVisible($hwnd)) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return [HkWin]::IsWindowVisible($hwnd)
}

# 在测试进程注册同一组合：成功 = 组合空闲（ClipAssist 没占用）；失败 = 已被占用。
function Test-HotkeyFree([uint32]$modifiers, [uint32]$vk) {
    $script:probeId++
    $ok = [HkWin]::RegisterHotKey([IntPtr]::Zero, $script:probeId, $modifiers, $vk)
    if ($ok) { [void][HkWin]::UnregisterHotKey([IntPtr]::Zero, $script:probeId) }
    return $ok
}

function Test-HotkeyOwnedByApp([uint32]$modifiers, [uint32]$vk) {
    return -not (Test-HotkeyFree $modifiers $vk)
}

function Send-HotkeyMessage([int]$processId) {
    $messageWindow = [HkWin]::FindMessageWindow([uint32]$processId, 'ClipAssist.HotkeyWindow')
    if ($messageWindow -eq [IntPtr]::Zero) { return $false }
    # 双 id 事务设计：当前生效 id 可能是 0x4341 或 0x4342，两个都发一次。
    [void][HkWin]::PostMessage($messageWindow, 0x0312, [IntPtr]0x4341, [IntPtr]::Zero)
    [void][HkWin]::PostMessage($messageWindow, 0x0312, [IntPtr]0x4342, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 250
    return $true
}

function Read-Settings([string]$dataDir) {
    $path = Join-Path $dataDir 'settings.json'
    if (-not (Test-Path $path)) { return $null }
    return (Get-Content $path -Raw | ConvertFrom-Json)
}

function Wait-SettingsGesture([string]$dataDir, [string]$expected, [int]$timeoutMs = 5000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        $settings = Read-Settings $dataDir
        if ($null -ne $settings -and $settings.hotkey.gesture -eq $expected) { return $true }
        Start-Sleep -Milliseconds 150
    }
    return $false
}

function Find-SettingsWindow([int]$processId, [int]$timeoutMs = 5000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        $handle = [HkWin]::FindTopLevelWindow([uint32]$processId, '设置')
        if ($handle -ne [IntPtr]::Zero) {
            return [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Find-Uia($root, $controlType, [string]$name = '') {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    $items = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
    if ($name.Length -gt 0) {
        return @($items | Where-Object { $_.Current.Name -eq $name })
    }
    return $items
}

function Find-ByAutomationId($root, [string]$id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-Texts($root) {
    $items = @($root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition))
    return (($items | ForEach-Object { $_.Current.Name } | Where-Object { $_.Length -gt 0 }) -join ' | ')
}

function Invoke-Element($element) {
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300
}

function Set-ManualGesture($dialog, [string]$text) {
    $box = Find-ByAutomationId $dialog 'HotkeyManualInput'
    if ($null -eq $box) { return $false }
    $pattern = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($text)
    Start-Sleep -Milliseconds 150
    return $true
}

function Open-Settings([IntPtr]$mainWindow, [int]$processId) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($mainWindow)
    $button = @(Find-Uia $root ([System.Windows.Automation.ControlType]::Button) '设置')[0]
    if ($null -eq $button) { return $null }
    Invoke-Element $button
    return Find-SettingsWindow $processId
}

$dirs = @()
$processes = @()

try {
    Write-Host '--- 环境预检 ---'
    Check '测试环境 Ctrl+Shift+V 空闲' (Test-HotkeyFree 0x0006 0x56) 'free'
    Check '测试环境 Ctrl+Alt+K 空闲' (Test-HotkeyFree 0x0003 0x4B) 'free'
    Check '测试环境 Ctrl+Alt+J 空闲' (Test-HotkeyFree 0x0003 0x4A) 'free'

    Write-Host '--- A) 默认快捷键 ---'
    $dirA = New-DataDir 'a'
    $dirs += $dirA
    $proc = Start-App $dirA
    $processes += $proc
    $hwnd = $proc.MainWindowHandle

    Start-Sleep -Milliseconds 500
    $settings = Read-Settings $dirA
    Check '首启配置为 schema 3 且快捷键默认 Ctrl+Shift+V' (
        $null -ne $settings -and $settings.schemaVersion -eq 3 -and
        $settings.hotkey.gesture -eq 'Ctrl+Shift+V'
    ) "schema=$($settings.schemaVersion) gesture=$($settings.hotkey.gesture)"
    Check 'ClipAssist 实际持有 Ctrl+Shift+V' (Test-HotkeyOwnedByApp 0x0006 0x56) 'owned by app'

    Hide-Window $hwnd
    Check '默认快捷键隐藏窗口' (-not [HkWin]::IsWindowVisible($hwnd)) 'hidden'
    $sent = Send-HotkeyMessage $proc.Id
    Check '默认快捷键消息能呼出窗口' ($sent -and (Wait-Visible $hwnd)) 'visible after WM_HOTKEY'

    Write-Host '--- B) 设置界面查看、录入切换与非法组合 ---'
    $dialog = Open-Settings $hwnd $proc.Id
    Check '点击“设置”打开设置窗口' ($null -ne $dialog) 'settings window'
    $dialogHwnd = if ($null -ne $dialog) { [IntPtr]$dialog.Current.NativeWindowHandle } else { [IntPtr]::Zero }
    $texts = if ($null -ne $dialog) { Get-Texts $dialog } else { '' }
    Check '设置界面显示当前快捷键' ($texts -like '*Ctrl+Shift+V*') $texts

    $capture = if ($null -ne $dialog) { Find-ByAutomationId $dialog 'HotkeyCapture' } else { $null }
    Check '设置界面有快捷键录入控件' ($null -ne $capture) 'capture control'

    if ($null -ne $capture) {
        Invoke-Element $capture
        $captureRecording = Get-Texts $capture
        Invoke-Element $capture
        $captureCancelled = Get-Texts $capture
        Check '录入框可进入并取消录入状态' (
            $captureRecording -like '*按下新的组合键*' -and
            $captureCancelled -like '*Ctrl+Shift+V*' -and
            $captureCancelled -notlike '*按下新的组合键*'
        ) "recording=$captureRecording cancelled=$captureCancelled"
    }

    if ($null -ne $dialog) {
        $manualOk = Set-ManualGesture $dialog 'Shift+A'
        $apply = Find-ByAutomationId $dialog 'HotkeyManualApply'
        if ($null -ne $apply) { Invoke-Element $apply }
        $allTexts = @(Find-Uia $dialog ([System.Windows.Automation.ControlType]::Text))
        $hasInvalidError = @($allTexts | Where-Object {
            $_.Current.Name -like '*不能只使用 Shift*' -or $_.Current.Name -like '*两个修饰键*'
        }).Count -gt 0
        Check '手动输入非法组合时给出错误提示' ($manualOk -and $hasInvalidError) (($allTexts | ForEach-Object { $_.Current.Name }) -join ' | ')

        $captureAfterInvalid = if ($null -ne $capture) { Get-Texts $capture } else { '' }
        Check '非法组合不会改变待保存的快捷键' ($captureAfterInvalid -like '*Ctrl+Shift+V*') $captureAfterInvalid

        # 手动框里留着非法内容时，点“保存”不能被静默忽略。
        $saveWithInvalid = @(Find-Uia $dialog ([System.Windows.Automation.ControlType]::Button) '保存')[0]
        if ($null -ne $saveWithInvalid) { Invoke-Element $saveWithInvalid }
        Start-Sleep -Milliseconds 400
        Check '手动输入非法时阻止保存并保持窗口打开' (
            [HkWin]::IsWindowVisible($dialogHwnd) -and
            (Read-Settings $dirA).hotkey.gesture -eq 'Ctrl+Shift+V'
        ) 'still open, config unchanged'

        $cancel = @(Find-Uia $dialog ([System.Windows.Automation.ControlType]::Button) '取消')[0]
        if ($null -ne $cancel) { Invoke-Element $cancel }
        Start-Sleep -Milliseconds 400
    }

    Check '取消后设置窗口关闭' ($null -eq $dialog -or -not [HkWin]::IsWindowVisible($dialogHwnd)) 'closed'

    Write-Host '--- C) 更换快捷键并立即生效 ---'
    $dialog = Open-Settings $hwnd $proc.Id
    $dialogHwnd = if ($null -ne $dialog) { [IntPtr]$dialog.Current.NativeWindowHandle } else { [IntPtr]::Zero }
    Check '再次打开设置窗口' ($null -ne $dialog) 'reopened'

    if ($null -ne $dialog) {
        $ok = Set-ManualGesture $dialog 'Ctrl+Alt+K'
        $apply = Find-ByAutomationId $dialog 'HotkeyManualApply'
        if ($null -ne $apply) { Invoke-Element $apply }
        $capture = Find-ByAutomationId $dialog 'HotkeyCapture'
        $captureText = if ($null -ne $capture) { Get-Texts $capture } else { '' }
        Check '手动输入新组合后显示 Ctrl+Alt+K' ($ok -and $captureText -like '*Ctrl+Alt+K*') $captureText

        $save = @(Find-Uia $dialog ([System.Windows.Automation.ControlType]::Button) '保存')[0]
        if ($null -ne $save) { Invoke-Element $save }
        Start-Sleep -Milliseconds 600
    }

    Check '保存后配置写入 Ctrl+Alt+K' (Wait-SettingsGesture $dirA 'Ctrl+Alt+K') 'persisted'
    Check '保存后设置窗口关闭' ($null -eq $dialog -or -not [HkWin]::IsWindowVisible($dialogHwnd)) 'closed after save'
    Check '更换后 Ctrl+Shift+V 已被释放' (Test-HotkeyFree 0x0006 0x56) 'old released'
    Check '更换后 ClipAssist 实际持有 Ctrl+Alt+K' (Test-HotkeyOwnedByApp 0x0003 0x4B) 'new owned'

    Hide-Window $hwnd
    [void](Send-HotkeyMessage $proc.Id)
    Check '新快捷键消息能呼出窗口' (Wait-Visible $hwnd) 'new hotkey shows window'

    Write-Host '--- D) 快速重复触发 ---'
    Hide-Window $hwnd
    for ($i = 0; $i -lt 8; $i++) {
        [void](Send-HotkeyMessage $proc.Id)
        Start-Sleep -Milliseconds 30
    }
    $proc.Refresh()
    $alive = -not $proc.HasExited
    $count = @(Get-Process -Name ClipAssist -ErrorAction SilentlyContinue).Count
    Check '快速重复触发后进程仍存活且只有一个实例' ($alive -and $count -eq 1) "alive=$alive count=$count"
    Check '快速重复触发后窗口可见' (Wait-Visible $hwnd) 'visible'

    Write-Host '--- E) 重启后恢复 ---'
    Stop-App $proc
    $proc = Start-App $dirA
    $processes += $proc
    $hwnd = $proc.MainWindowHandle
    Start-Sleep -Milliseconds 500

    $restarted = Read-Settings $dirA
    Check '重启后配置仍为 Ctrl+Alt+K' ($restarted.hotkey.gesture -eq 'Ctrl+Alt+K') "gesture=$($restarted.hotkey.gesture)"
    Check '重启后 ClipAssist 重新持有 Ctrl+Alt+K' (Test-HotkeyOwnedByApp 0x0003 0x4B) 'owned after restart'
    Check '重启后 Ctrl+Shift+V 仍空闲' (Test-HotkeyFree 0x0006 0x56) 'old free after restart'

    Hide-Window $hwnd
    [void](Send-HotkeyMessage $proc.Id)
    Check '重启后新快捷键仍能呼出窗口' (Wait-Visible $hwnd) 'new active after restart'

    Write-Host '--- F) 注册冲突时保留旧快捷键 ---'
    $dialog = Open-Settings $hwnd $proc.Id
    $dialogHwnd = if ($null -ne $dialog) { [IntPtr]$dialog.Current.NativeWindowHandle } else { [IntPtr]::Zero }
    Check '冲突测试前设置窗口可用' ($null -ne $dialog) 'settings available'

    if ($null -ne $dialog) {
        $ok = Set-ManualGesture $dialog 'Ctrl+Alt+J'
        $apply = Find-ByAutomationId $dialog 'HotkeyManualApply'
        if ($null -ne $apply) { Invoke-Element $apply }

        # 录入完成后、保存之前，由测试进程抢占 J，制造真实竞态。
        $blocked = [HkWin]::RegisterHotKey([IntPtr]::Zero, $script:blockerId, 0x0003, 0x4A)
        Check '测试进程成功占用 Ctrl+Alt+J' ($ok -and $blocked) 'RegisterHotKey(NULL)'

        $save = @(Find-Uia $dialog ([System.Windows.Automation.ControlType]::Button) '保存')[0]
        if ($null -ne $save) { Invoke-Element $save }
        Start-Sleep -Milliseconds 700

        $conflictTexts = Get-Texts $dialog
        Check '冲突时显示“已被占用”反馈' ($conflictTexts -like '*占用*') $conflictTexts
        Check '冲突时设置窗口保持打开' ([HkWin]::IsWindowVisible($dialogHwnd)) 'still open'
        Check '冲突时未覆盖原配置' ((Read-Settings $dirA).hotkey.gesture -eq 'Ctrl+Alt+K') 'config unchanged'
        Check '冲突后 ClipAssist 仍持有 Ctrl+Alt+K' (Test-HotkeyOwnedByApp 0x0003 0x4B) 'old still owned'

        if ($blocked) { [void][HkWin]::UnregisterHotKey([IntPtr]::Zero, $script:blockerId) }

        $cancel = @(Find-Uia $dialog ([System.Windows.Automation.ControlType]::Button) '取消')[0]
        if ($null -ne $cancel) { Invoke-Element $cancel }
        Start-Sleep -Milliseconds 400

        Hide-Window $hwnd
        [void](Send-HotkeyMessage $proc.Id)
        Check '冲突后旧快捷键仍能呼出窗口' (Wait-Visible $hwnd) 'old still works'
    }

    Write-Host '--- G) 退出后释放快捷键 ---'
    # 键盘注入被环境阻止，无法用 Ctrl+Q 走 App.OnExit；这里强制终止，
    # 只能证明“进程消失后系统释放组合”，OnExit 的注销由单元测试覆盖。
    Stop-App $proc
    Check '进程退出后 Ctrl+Alt+K 已释放' (Test-HotkeyFree 0x0003 0x4B) 'released after exit'
}
finally {
    [void][HkWin]::UnregisterHotKey([IntPtr]::Zero, $script:blockerId)
    foreach ($process in $processes) { Stop-App $process }
    Get-Process -Name ClipAssist -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    foreach ($dir in $dirs) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item (Join-Path $env:TEMP 'v0205-hotkey-out.txt'), (Join-Path $env:TEMP 'v0205-hotkey-err.txt') -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "结果: $script:pass 项通过 / $script:fail 项失败"
if ($script:fail -gt 0) { exit 1 }