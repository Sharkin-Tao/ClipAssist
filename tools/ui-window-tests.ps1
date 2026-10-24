# ClipAssist V0.2-01 窗口尺寸/位置端到端测试
# 说明：本脚本用 UIA 读取物理坐标，再按窗口 DPI 换算成 DIP；
#       不依赖键盘输入，因此无法验证真实鼠标拖拽和托盘点击。

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class V02Win {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("shcore.dll")] public static extern int GetProcessDpiAwareness(IntPtr h, out int value);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);

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
}
"@

$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\Debug\net10.0-windows\ClipAssist.exe'
$script:pass = 0
$script:fail = 0

if ([System.Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw '请用 Windows PowerShell（默认 STA）运行本脚本，例如：powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\ui-window-tests.ps1'
}

# 测试宿主使用物理像素还是 DIP，由 GetWindowRect 与 UIA 物理矩形实测判断
# （不同 PowerShell 宿主的 DPI 感知设置不一致，不能只看进程 API）。
$script:callerPhysical = $true

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

function Get-Abs([double]$value, [double]$target) {
    return [math]::Abs($value - $target)
}

function New-DataDir([string]$tag) {
    $dir = Join-Path $env:TEMP ("clipassist-v02-" + $tag + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    return $dir
}

function Start-App([string]$dataDir) {
    $env:CLIPASSIST_DATA_DIR = $dataDir

    # 用 CreateProcess 而不是 ShellExecute，避免从网络盘/受限路径启动时
    # 弹出“无法验证发布者”的 Windows 安全警告挡住 UI 自动化。
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
        Start-Sleep -Milliseconds 250
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

    Start-Sleep -Milliseconds 800
    return $proc
}

function Stop-App($proc) {
    if ($null -ne $proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        try { Wait-Process -Id $proc.Id -Timeout 5 -ErrorAction SilentlyContinue } catch { }
    }
}

function Get-PhysicalRect([IntPtr]$hwnd) {
    $element = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $rectangle = $element.Current.BoundingRectangle
    return [pscustomobject]@{
        Left   = $rectangle.X
        Top    = $rectangle.Y
        Width  = $rectangle.Width
        Height = $rectangle.Height
    }
}

function Get-DipRect([IntPtr]$hwnd) {
    $element = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $rectangle = $element.Current.BoundingRectangle
    $dpi = [V02Win]::GetDpiForWindow($hwnd)
    $scale = $dpi / 96.0
    return [pscustomobject]@{
        Left   = $rectangle.X / $scale
        Top    = $rectangle.Y / $scale
        Width  = $rectangle.Width / $scale
        Height = $rectangle.Height / $scale
        Dpi    = $dpi
    }
}

function Get-CallerRect([IntPtr]$hwnd) {
    $rect = New-Object -TypeName 'V02Win+RECT'
    [void][V02Win]::GetWindowRect($hwnd, [ref]$rect)
    return [pscustomobject]@{
        Left   = $rect.Left
        Top    = $rect.Top
        Width  = $rect.Right - $rect.Left
        Height = $rect.Bottom - $rect.Top
    }
}

function Test-CallerPhysical([IntPtr]$hwnd) {
    # 不感知 DPI 的宿主拿到的 GetWindowRect 宽度 = 物理宽度 / 缩放；感知宿主则与 UIA 物理宽度一致。
    $dpi = [V02Win]::GetDpiForWindow($hwnd)
    $scale = $dpi / 96.0
    $physical = Get-PhysicalRect $hwnd
    $caller = Get-CallerRect $hwnd

    if ((Get-Abs $caller.Width $physical.Width) -le 2) { return $true }
    if ((Get-Abs ($caller.Width * $scale) $physical.Width) -le 2) { return $false }
    return ($caller.Width -ge $physical.Width)
}

function Get-CompareRect([IntPtr]$hwnd) {
    # Screen/VirtualScreen 的坐标空间与宿主一致；这里返回同一空间下的窗口矩形。
    if ($script:callerPhysical) {
        return Get-PhysicalRect $hwnd
    }
    return Get-DipRect $hwnd
}

function Hide-Window([IntPtr]$hwnd) {
    [void][V02Win]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not [V02Win]::IsWindowVisible($hwnd)) { return }
        Start-Sleep -Milliseconds 100
    }
}

function Set-WindowBounds([IntPtr]$hwnd, [double]$left, [double]$top, [double]$width, [double]$height) {
    # 调用方传 DIP；物理坐标宿主需要换算，DIP 坐标宿主直接使用。
    $scale = if ($script:callerPhysical) { [V02Win]::GetDpiForWindow($hwnd) / 96.0 } else { 1.0 }
    [void][V02Win]::SetWindowPos(
        $hwnd,
        [IntPtr]::Zero,
        [int][math]::Round($left * $scale),
        [int][math]::Round($top * $scale),
        [int][math]::Round($width * $scale),
        [int][math]::Round($height * $scale),
        0x0014) # SWP_NOZORDER | SWP_NOACTIVATE

    Start-Sleep -Milliseconds 700
}

function Wait-SettingsFile([string]$dataDir, [int]$timeoutMs = 5000) {
    $path = Join-Path $dataDir 'settings.json'
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path $path) { return $path }
        Start-Sleep -Milliseconds 100
    }
    return $path
}

function Read-Settings([string]$dataDir) {
    $path = Join-Path $dataDir 'settings.json'
    if (-not (Test-Path $path)) { return $null }
    return (Get-Content $path -Raw | ConvertFrom-Json)
}

function Write-Settings([string]$dataDir, [double]$left, [double]$top, [double]$width, [double]$height, [int]$schemaVersion = 3) {
    $path = Join-Path $dataDir 'settings.json'
    $content = [ordered]@{
        schemaVersion = $schemaVersion
        window        = [ordered]@{
            left   = $left
            top    = $top
            width  = $width
            height = $height
        }
    }
    $json = $content | ConvertTo-Json -Depth 4
    # 不带 BOM，避免 JSON 读取器把 BOM 当成非法内容。
    [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Find-Uia($root, $controlType) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        $controlType)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
}

function Get-UiaNames($elements) {
    return @($elements | ForEach-Object { $_.Current.Name })
}

function Set-SearchText($root, [string]$text) {
    $edits = Find-Uia $root ([System.Windows.Automation.ControlType]::Edit)
    if ($edits.Count -eq 0) { throw '找不到搜索框' }
    $pattern = $edits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($text)
    Start-Sleep -Milliseconds 800
}

function Centered([double]$value, [double]$start, [double]$length) {
    return [math]::Abs($value - ($start + ($length / 2.0)))
}

function Get-WindowState($root) {
    $pattern = $root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    return $pattern.Current.WindowVisualState
}

$originalClipboard = ''
try { $originalClipboard = [System.Windows.Forms.Clipboard]::GetText() } catch { }

$tempDirs = @()
$processes = @()

try {
    Write-Host '--- A) 首次启动、自由缩放、关闭到托盘、重启恢复 ---'
    $dirA = New-DataDir 'a'
    $tempDirs += $dirA
    $appA = Start-App $dirA
    $processes += $appA
    $hwndA = $processes[-1].MainWindowHandle
    $script:callerPhysical = Test-CallerPhysical $hwndA
    $hostSpace = if ($script:callerPhysical) { '物理像素' } else { 'DIP' }
    Write-Host "测试宿主坐标空间: $hostSpace"

    $rect = Get-DipRect $hwndA
    Check '首次启动窗口可见' ([V02Win]::IsWindowVisible($hwndA)) "hwnd=$hwndA"
    Check '首次启动默认尺寸 720x520' (
        [math]::Abs($rect.Width - 720) -le 3 -and [math]::Abs($rect.Height - 520) -le 3
    ) "$([math]::Round($rect.Width,1))x$([math]::Round($rect.Height,1)) DIP"

    $workArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $compare = Get-CompareRect $hwndA
    $centerX = $compare.Left + ($compare.Width / 2)
    $centerY = $compare.Top + ($compare.Height / 2)
    $spaceName = if ($script:callerPhysical) { 'physical' } else { 'dip' }
    Check '首启窗口居中' (
        (Get-Abs $centerX ($workArea.Left + $workArea.Width / 2)) -le 8 -and
        (Get-Abs $centerY ($workArea.Top + $workArea.Height / 2)) -le 8
    ) "window=($([math]::Round($centerX,1)),$([math]::Round($centerY,1))) work=($([math]::Round($workArea.Left + $workArea.Width/2,1)),$([math]::Round($workArea.Top + $workArea.Height/2,1))) space=$spaceName"

    Check '首启自动生成默认 settings.json' (
        $null -ne (Wait-SettingsFile $dirA) -and
        $null -ne (Read-Settings $dirA) -and
        (Read-Settings $dirA).schemaVersion -eq 3 -and
        $null -eq (Read-Settings $dirA).window
    ) '首个配置文件存在且 window 为 null'

    $defaultSettingsPath = Join-Path $dirA 'settings.json'
    $defaultSettingsContent = [System.IO.File]::ReadAllText($defaultSettingsPath)
    $defaultSettingsWrite = (Get-Item $defaultSettingsPath).LastWriteTimeUtc

    Set-WindowBounds $($processes[0].MainWindowHandle) 200 150 900 650
    $resized = Get-DipRect $($processes[0].MainWindowHandle)
    Check '可调整窗口大小与位置' (
        (Get-Abs $resized.Width 900) -le 4 -and (Get-Abs $resized.Height 650) -le 4 -and
        (Get-Abs $resized.Left 200) -le 4 -and (Get-Abs $resized.Top 150) -le 4
    ) "$([math]::Round($resized.Left,1)),$([math]::Round($resized.Top,1)) $([math]::Round($resized.Width,1))x$([math]::Round($resized.Height,1))"

    # 多移动几次，设置文件不应在隐藏前发生变化。
    Set-WindowBounds $($processes[0].MainWindowHandle) 260 190 760 540
    Set-WindowBounds $($processes[0].MainWindowHandle) 200 150 900 650
    Start-Sleep -Milliseconds 400
    Check '拖动/缩放期间不写设置文件' (
        [System.IO.File]::ReadAllText($defaultSettingsPath) -eq $defaultSettingsContent -and
        (Get-Item $defaultSettingsPath).LastWriteTimeUtc -eq $defaultSettingsWrite
    ) '隐藏前内容与时间戳不变'

    Hide-Window $($processes[0].MainWindowHandle)
    $settingsPath = Wait-SettingsFile $dirA
    $settings = Read-Settings $dirA
    Check '关闭到托盘时写入 settings.json' ($null -ne $settings -and (Test-Path $settingsPath)) $settingsPath
    Check '保存的配置版本为 3' ($null -ne $settings -and $settings.schemaVersion -eq 3) "schemaVersion=$($settings.schemaVersion)"
    Check '保存的尺寸与位置正确' (
        $null -ne $settings -and
        (Get-Abs $settings.window.width 900) -le 3 -and (Get-Abs $settings.window.height 650) -le 3 -and
        (Get-Abs $settings.window.left 200) -le 3 -and (Get-Abs $settings.window.top 150) -le 3
    ) "saved=$($settings.window.left),$($settings.window.top) $($settings.window.width)x$($settings.window.height)"
    Check '关闭窗口后进程仍在（隐藏到托盘）' (-not $processes[0].HasExited) "pid=$($processes[0].Id)"

    Stop-App $processes[0]
    $proc2 = Start-App $dirA
    $processes += $proc2
    $restored = Get-DipRect $proc2.MainWindowHandle
    Check '重启后恢复上次的位置与大小' (
        (Get-Abs $restored.Width 900) -le 4 -and (Get-Abs $restored.Height 650) -le 4 -and
        (Get-Abs $restored.Left 200) -le 4 -and (Get-Abs $restored.Top 150) -le 4
    ) "$([math]::Round($restored.Left,1)),$([math]::Round($restored.Top,1)) $([math]::Round($restored.Width,1))x$([math]::Round($restored.Height,1))"
    Stop-App $proc2

    Write-Host '--- B) 保存位置远在屏幕外 / 显示器被拔掉 ---'
    $dirB = New-DataDir 'b'
    Write-Settings $dirB -5000 -5000 800 600
    $procB = Start-App $dirB
    $processes += $procB
    $rectB = Get-DipRect $procB.MainWindowHandle
    $rectBCompare = Get-CompareRect $procB.MainWindowHandle
    $virtual = [System.Windows.Forms.SystemInformation]::VirtualScreen
    Check '屏幕外位置恢复后完全可见' (
        $rectBCompare.Left -ge $virtual.Left - 1 -and $rectBCompare.Top -ge $virtual.Top - 1 -and
        ($rectBCompare.Left + $rectBCompare.Width) -le ($virtual.Right + 1) -and
        ($rectBCompare.Top + $rectBCompare.Height) -le ($virtual.Bottom + 1)
    ) "compare $([math]::Round($rectBCompare.Left,1)),$([math]::Round($rectBCompare.Top,1)) $([math]::Round($rectBCompare.Width,1))x$([math]::Round($rectBCompare.Height,1)) in $($virtual.Left),$($virtual.Top) $($virtual.Width)x$($virtual.Height)"
    Check '越界恢复保留原尺寸' (
        (Get-Abs $rectB.Width 800) -le 3 -and (Get-Abs $rectB.Height 600) -le 3
    ) "$([math]::Round($rectB.Width,1))x$([math]::Round($rectB.Height,1))"
    Stop-App $procB

    $dirC = New-DataDir 'c'
    Write-Settings $dirC ($virtual.Right + 400) 200 700 500
    $procC = Start-App $dirC
    $processes += $procC
    $rectC = Get-DipRect $procC.MainWindowHandle
    $rectCCompare = Get-CompareRect $procC.MainWindowHandle
    Check '显示器断开后的坐标被收回可见区域' (
        $rectCCompare.Left -ge $virtual.Left - 1 -and
        ($rectCCompare.Left + $rectCCompare.Width) -le ($virtual.Right + 1)
    ) "compare $([math]::Round($rectCCompare.Left,1))..$([math]::Round($rectCCompare.Left + $rectCCompare.Width,1))"
    Stop-App $procC

    Write-Host '--- C) 最小尺寸与控件可用性 ---'
    $dirD = New-DataDir 'd'
    Write-Settings $dirD 100 100 100 100
    $procD = Start-App $dirD
    $processes += $procD
    $rectD = Get-DipRect $procD.MainWindowHandle
    Check '过小尺寸恢复为最小 520x380' (
        (Get-Abs $rectD.Width 520) -le 3 -and (Get-Abs $rectD.Height 380) -le 3
    ) "$([math]::Round($rectD.Width,1))x$([math]::Round($rectD.Height,1))"

    $rootD = [System.Windows.Automation.AutomationElement]::FromHandle($procD.MainWindowHandle)
    $searchD = @(Find-Uia $rootD ([System.Windows.Automation.ControlType]::Edit))[0]
    $buttonsD = @(Find-Uia $rootD ([System.Windows.Automation.ControlType]::Button))
    $listD = @(Find-Uia $rootD ([System.Windows.Automation.ControlType]::List))
    $buttonNames = Get-UiaNames $buttonsD
    Check '最小尺寸下搜索框/列表/操作按钮都在' (
        $null -ne $searchD -and $listD.Count -ge 1 -and
        ($buttonNames -contains '复制') -and ($buttonNames -contains '清空历史')
    ) "buttons=$($buttonNames -join ',')"
    Stop-App $procD

    Write-Host '--- D) 最大化关闭后按普通窗口恢复 ---'
    $dirE = New-DataDir 'e'
    $procE = Start-App $dirE
    $processes += $procE
    $rootE = [System.Windows.Automation.AutomationElement]::FromHandle($procE.MainWindowHandle)
    $windowPattern = $rootE.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
    Start-Sleep -Milliseconds 1000
    Check '窗口已最大化' ((Get-WindowState $rootE) -eq 'Maximized') (Get-WindowState $rootE)

    Hide-Window $procE.MainWindowHandle
    $null = Wait-SettingsFile $dirE
    $settingsE = Read-Settings $dirE
    Check '最大化关闭保存的是普通尺寸' (
        $null -ne $settingsE -and
        (Get-Abs $settingsE.window.width 720) -le 3 -and (Get-Abs $settingsE.window.height 520) -le 3
    ) "saved=$($settingsE.window.width)x$($settingsE.window.height)"
    Check '最大化关闭后进程仍在' (-not $procE.HasExited) "pid=$($procE.Id)"
    Stop-App $procE

    $procE2 = Start-App $dirE
    $processes += $procE2
    $rootE2 = [System.Windows.Automation.AutomationElement]::FromHandle($procE2.MainWindowHandle)
    Check '重启后为普通窗口状态' ((Get-WindowState $rootE2) -eq 'Normal') (Get-WindowState $rootE2)
    $rectE2 = Get-DipRect $procE2.MainWindowHandle
    Check '重启后恢复普通尺寸' (
        (Get-Abs $rectE2.Width 720) -le 3 -and (Get-Abs $rectE2.Height 520) -le 3
    ) "$([math]::Round($rectE2.Width,1))x$([math]::Round($rectE2.Height,1))"
    Stop-App $procE2

    Write-Host '--- E) 损坏的设置文件 ---'
    $dirF = New-DataDir 'f'
    [System.IO.File]::WriteAllText(
        (Join-Path $dirF 'settings.json'),
        '{ this is not json',
        (New-Object System.Text.UTF8Encoding($false)))
    $procF = Start-App $dirF
    $processes += $procF
    $rectF = Get-DipRect $procF.MainWindowHandle
    Check '损坏设置仍能启动并居中' (
        [V02Win]::IsWindowVisible($procF.MainWindowHandle) -and
        (Get-Abs $rectF.Width 720) -le 3 -and (Get-Abs $rectF.Height 520) -le 3
    ) "$([math]::Round($rectF.Width,1))x$([math]::Round($rectF.Height,1))"
    $backupsF = @(Get-ChildItem $dirF -Filter 'settings.json.bad-*' -ErrorAction SilentlyContinue)
    Check '损坏设置生成备份文件' ($backupsF.Count -ge 1) "$($backupsF.Count) 个备份"
    $recoveredF = Read-Settings $dirF
    Check '损坏设置后重新创建默认配置' (
        $null -ne $recoveredF -and $recoveredF.schemaVersion -eq 3 -and $null -eq $recoveredF.window
    ) "schemaVersion=$($recoveredF.schemaVersion)"
    Stop-App $procF

    Write-Host '--- F) v1 设置迁移 ---'
    $dirH = New-DataDir 'h'
    Write-Settings $dirH 210 160 860 610 1
    $procH = Start-App $dirH
    $processes += $procH
    $rectH = Get-DipRect $procH.MainWindowHandle
    Check 'v1 设置迁移后恢复窗口位置' (
        (Get-Abs $rectH.Width 860) -le 4 -and (Get-Abs $rectH.Height 610) -le 4 -and
        (Get-Abs $rectH.Left 210) -le 4 -and (Get-Abs $rectH.Top 160) -le 4
    ) "$([math]::Round($rectH.Left,1)),$([math]::Round($rectH.Top,1)) $([math]::Round($rectH.Width,1))x$([math]::Round($rectH.Height,1))"
    $settingsH = Read-Settings $dirH
    Check 'v1 设置文件写回为 v3' (
        $null -ne $settingsH -and $settingsH.schemaVersion -eq 3 -and
        (Get-Abs $settingsH.window.width 860) -le 2
    ) "schemaVersion=$($settingsH.schemaVersion)"
    Stop-App $procH

    Write-Host '--- G) 非法 hotkey / 未知字段 ---'
    $dirI = New-DataDir 'i'
    $settingsI = @"
{
  "schemaVersion": 3,
  "hotkey": { "gesture": "Not+A+Real+Shortcut" },
  "theme": "neon",
  "window": { "left": 220, "top": 170, "width": 840, "height": 600 }
}
"@
    [System.IO.File]::WriteAllText(
        (Join-Path $dirI 'settings.json'),
        $settingsI,
        (New-Object System.Text.UTF8Encoding($false)))
    $procI = Start-App $dirI
    $processes += $procI
    $rectI = Get-DipRect $procI.MainWindowHandle
    Check '非法 hotkey/未知字段不影响启动和已知设置' (
        [V02Win]::IsWindowVisible($procI.MainWindowHandle) -and
        (Get-Abs $rectI.Width 840) -le 4 -and (Get-Abs $rectI.Height 600) -le 4
    ) "visible=$([V02Win]::IsWindowVisible($procI.MainWindowHandle)) $([math]::Round($rectI.Width,1))x$([math]::Round($rectI.Height,1))"
    Stop-App $procI

    Write-Host '--- H) 现有功能回归 ---'
    $dirG = New-DataDir 'g'
    $procG = Start-App $dirG
    $processes += $procG
    $marker = 'CLIPASSIST-V02-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    [System.Windows.Forms.Clipboard]::SetText($marker)
    Start-Sleep -Seconds 2

    $rootG = [System.Windows.Automation.AutomationElement]::FromHandle($procG.MainWindowHandle)
    $items = @(Find-Uia $rootG ([System.Windows.Automation.ControlType]::ListItem))
    $itemNames = Get-UiaNames $items
    Check '剪贴板监听仍工作' ($itemNames -contains $marker) "items=$($itemNames.Count)"

    $search = @(Find-Uia $rootG ([System.Windows.Automation.ControlType]::Edit))[0]
    $valuePattern = $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue('V02-')
    Start-Sleep -Milliseconds 800
    $filtered = @(Find-Uia $rootG ([System.Windows.Automation.ControlType]::ListItem))
    Check '搜索仍工作' ($filtered.Count -ge 1) "filtered=$($filtered.Count)"
    $valuePattern.SetValue('')
    Start-Sleep -Milliseconds 600

    $items = @(Find-Uia $rootG ([System.Windows.Automation.ControlType]::ListItem))
    $target = $items | Where-Object { $_.Current.Name -eq $marker } | Select-Object -First 1
    if ($null -ne $target) {
        $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 300
    }
    $copyButton = @(Find-Uia $rootG ([System.Windows.Automation.ControlType]::Button) | Where-Object { $_.Current.Name -eq '复制' })[0]
    $copyButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 800
    Check '复制按钮仍写回剪贴板' ([System.Windows.Forms.Clipboard]::GetText() -eq $marker) '剪贴板内容比较'

    Hide-Window $procG.MainWindowHandle
    Check '关闭到托盘后仍可隐藏' (-not [V02Win]::IsWindowVisible($procG.MainWindowHandle) -and -not $procG.HasExited) 'hidden but alive'

    $hotkeyWindow = [V02Win]::FindMessageWindow([uint32]$procG.Id, 'ClipAssist.HotkeyWindow')
    # 双 id 事务设计下当前生效 id 可能是 0x4341 或 0x4342，两个都发一次。
    [void][V02Win]::PostMessage($hotkeyWindow, 0x0312, [IntPtr]0x4341, [IntPtr]::Zero)
    [void][V02Win]::PostMessage($hotkeyWindow, 0x0312, [IntPtr]0x4342, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1000
    Check 'WM_HOTKEY 仍能呼出窗口' ([V02Win]::IsWindowVisible($procG.MainWindowHandle)) 'visible after WM_HOTKEY'

    $secondPsi = New-Object System.Diagnostics.ProcessStartInfo
    $secondPsi.FileName = $exe
    $secondPsi.UseShellExecute = $false
    $secondPsi.CreateNoWindow = $true
    $secondPsi.WorkingDirectory = Split-Path -Parent $exe
    $second = [System.Diagnostics.Process]::Start($secondPsi)
    Start-Sleep -Seconds 3
    Check '单实例仍生效' ($second.HasExited) "second exited=$($second.HasExited)"
    $count = @(Get-Process -Name ClipAssist -ErrorAction SilentlyContinue).Count
    Check '始终只有一个 ClipAssist 进程' ($count -eq 1) "count=$count"
    Stop-App $procG
}
catch {
    Write-Host "测试异常: $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    $script:fail++
}
finally {
    foreach ($process in $processes) { Stop-App $process }
    Get-Process -Name ClipAssist -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\CLIPASSIST_DATA_DIR -ErrorAction SilentlyContinue
    foreach ($directory in $tempDirs) {
        Remove-Item $directory -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($originalClipboard) {
        try { [System.Windows.Forms.Clipboard]::SetText($originalClipboard) } catch { }
    }
    Write-Host ''
    Write-Host "结果: $($script:pass) 项通过 / $($script:fail) 项失败"
}