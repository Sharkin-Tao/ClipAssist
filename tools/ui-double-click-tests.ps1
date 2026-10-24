# ClipAssist V0.2-02 双击复制端到端测试
# 用真实鼠标事件（mouse_event）双击列表项，再用 UIA / 剪贴板验证结果。
# 注意：测试会临时改写系统剪贴板文本，结束时尽量恢复原文本。

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DblWin {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
"@

$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\Debug\net10.0-windows\ClipAssist.exe'
$script:pass = 0
$script:fail = 0
$script:appHwnd = [IntPtr]::Zero

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

function Get-ClipboardText {
    try {
        if ([System.Windows.Forms.Clipboard]::ContainsText()) {
            return [System.Windows.Forms.Clipboard]::GetText()
        }
        return ''
    }
    catch {
        return ''
    }
}

function Wait-ClipboardText([string]$expected, [int]$timeoutMilliseconds = 2500) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMilliseconds)
    do {
        $text = Get-ClipboardText
        if ([string]::Equals($text, $expected, [StringComparison]::Ordinal)) {
            return $text
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    return Get-ClipboardText
}

function Get-ListItems($root) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
}

function Get-ListItemByPrefix($root, [string]$prefix) {
    return Get-ListItems $root |
        Where-Object { $_.Current.Name.StartsWith($prefix, [StringComparison]::Ordinal) } |
        Select-Object -First 1
}

function Get-ListNames($root) {
    return @(Get-ListItems $root | ForEach-Object { $_.Current.Name })
}

function Get-TextNames($root) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) |
        ForEach-Object { $_.Current.Name })
}

function Test-NameSequence($first, $second) {
    if ($first.Count -ne $second.Count) { return $false }
    for ($i = 0; $i -lt $first.Count; $i++) {
        if (-not [string]::Equals($first[$i], $second[$i], [StringComparison]::Ordinal)) {
            return $false
        }
    }
    return $true
}

function Get-ClickPoint($item) {
    $rect = $item.Current.BoundingRectangle
    return [pscustomobject]@{
        X = [int]($rect.X + ($rect.Width / 2))
        Y = [int]($rect.Y + ($rect.Height / 2))
    }
}

function Assert-AppForeground {
    # 合成鼠标事件是全局输入的：先确保窗口在最前，避免被别的窗口/其它界面元素抢焦点导致漏击。
    if ($script:appHwnd -ne [IntPtr]::Zero) {
        [void][DblWin]::SetWindowPos($script:appHwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0003)
        [void][DblWin]::SetForegroundWindow($script:appHwnd)
        Start-Sleep -Milliseconds 120
    }
}

function Invoke-SingleClick([int]$x, [int]$y) {
    [void][DblWin]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 80
    [DblWin]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero) # left down
    [DblWin]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) # left up
}

function Invoke-DoubleClick([int]$x, [int]$y) {
    Assert-AppForeground
    Invoke-SingleClick $x $y
    Start-Sleep -Milliseconds 60
    Invoke-SingleClick $x $y
    Start-Sleep -Milliseconds 600
}

function Find-SearchBox($root, [int]$timeoutMilliseconds = 3000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMilliseconds)
    do {
        $edits = @($root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Edit))))
        if ($edits.Count -gt 0) { return $edits[0] }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)

    return $null
}

function Set-ClipboardText([string]$text, [int]$waitMilliseconds = 1200) {
    [System.Windows.Forms.Clipboard]::SetText($text)
    Start-Sleep -Milliseconds $waitMilliseconds
}

$app = $null
$dataDir = $null
$originalClipboard = Get-ClipboardText
$hadOriginalText = $originalClipboard.Length -gt 0

try {
    if (@(Get-Process -Name ClipAssist -ErrorAction SilentlyContinue).Count -gt 0) {
        throw '请先退出正在运行的 ClipAssist，再运行双击测试。'
    }

    $dataDir = Join-Path $env:TEMP ('clipassist-dbl-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    $env:CLIPASSIST_DATA_DIR = $dataDir

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = Split-Path -Parent $exe
    $app = [System.Diagnostics.Process]::Start($psi)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $app.Refresh()
        if ($app.HasExited) { throw "ClipAssist 启动失败，ExitCode=$($app.ExitCode)" }
        if ($app.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }

    Start-Sleep -Milliseconds 700
    $hwnd = $app.MainWindowHandle
    $script:appHwnd = $hwnd
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

    # 置顶，确保鼠标事件落在应用窗口上；不改窗口位置和大小。
    [void][DblWin]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0003)
    [void][DblWin]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 300

    $shortA = 'DBL-A-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $shortB = 'DBL-B-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $longText = 'DBL-LONG-START-' + [guid]::NewGuid().ToString('N').Substring(0, 8) +
        "`r`n" + ('L' * 4500) + "`r`n第二行`t带制表符`r`nDBL-LONG-END"
    $sentinel = 'DBL-SENTINEL-' + [guid]::NewGuid().ToString('N').Substring(0, 8)

    Set-ClipboardText $shortA
    Set-ClipboardText $shortB
    Set-ClipboardText $longText
    Set-ClipboardText $sentinel

    $initialNames = Get-ListNames $root
    Check '测试数据已进入历史列表' (
        $initialNames.Count -eq 4 -and
        ($initialNames[0].StartsWith($sentinel, [StringComparison]::Ordinal))
    ) "count=$($initialNames.Count)"

    # 1) 单击只选中，不复制
    $item = Get-ListItemByPrefix $root $shortA
    if ($null -eq $item) { throw "找不到列表项 $shortA" }
    $point = Get-ClickPoint $item
    Invoke-SingleClick $point.X $point.Y
    Start-Sleep -Milliseconds 800

    $selected = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
    Check '单击记录只选中' ($selected) "selected=$selected"
    Check '单击不改变剪贴板' ((Get-ClipboardText) -eq $sentinel) "clipboard=$(Get-ClipboardText)"

    # 2) 双击短文本，复制完整内容并给出状态提示
    $item = Get-ListItemByPrefix $root $shortA
    $point = Get-ClickPoint $item
    Invoke-DoubleClick $point.X $point.Y
    $clipboardAfterDouble = Wait-ClipboardText $shortA
    Check '双击短文本复制完整内容' ([string]::Equals($clipboardAfterDouble, $shortA, [StringComparison]::Ordinal)) "clipboard=$clipboardAfterDouble"
    Check '双击后有状态提示' ((Get-TextNames $root) -contains '已复制到剪贴板') 'status=已复制到剪贴板'

    $namesAfterDouble = Get-ListNames $root
    Start-Sleep -Milliseconds 2200
    $namesAfterWait = Get-ListNames $root
    Check '双击自身历史不会产生重复记录' (Test-NameSequence $initialNames $namesAfterWait) "count=$($namesAfterWait.Count)"
    Check '双击后列表顺序未因自身写入改变' (Test-NameSequence $namesAfterDouble $namesAfterWait) 'order stable'

    # 3) 双击长文本，逐字符比较。合成鼠标偶发漏击，这里最多重试 2 次并重新取元素位置。
    $clipboardLong = ''
    $selectedLong = $false
    $lastPoint = [pscustomobject]@{ X = 0; Y = 0 }
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $item = Get-ListItemByPrefix $root 'DBL-LONG-START-'
        if ($null -eq $item) { throw '找不到长文本列表项' }
        $point = Get-ClickPoint $item
        $lastPoint = $point
        Invoke-DoubleClick $point.X $point.Y
        $clipboardLong = Wait-ClipboardText $longText 1500
        $selectedLong = $item.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
        if ([string]::Equals($clipboardLong, $longText, [StringComparison]::Ordinal)) {
            break
        }
        Start-Sleep -Milliseconds 400
    }
    Check '双击长文本复制内容完全一致' (
        [string]::Equals($clipboardLong, $longText, [StringComparison]::Ordinal)
    ) "actualLength=$($clipboardLong.Length) expectedLength=$($longText.Length) selected=$selectedLong point=$($lastPoint.X),$($lastPoint.Y)"

    # 4) 搜索结果中的记录也能双击复制
    $searchBox = Find-SearchBox $root
    if ($null -eq $searchBox) { throw '找不到搜索框' }
    $searchPattern = $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $searchPattern.SetValue($shortB)
    Start-Sleep -Milliseconds 800

    $filteredItems = Get-ListItems $root
    Check '搜索后只剩目标记录' (
        $filteredItems.Count -eq 1 -and
        $filteredItems[0].Current.Name.StartsWith($shortB, [StringComparison]::Ordinal)
    ) "filtered=$($filteredItems.Count)"

    $point = Get-ClickPoint $filteredItems[0]
    Invoke-DoubleClick $point.X $point.Y
    $clipboardSearch = Wait-ClipboardText $shortB
    Check '搜索结果双击复制正确记录' (
        [string]::Equals($clipboardSearch, $shortB, [StringComparison]::Ordinal)
    ) "clipboard=$clipboardSearch"

    $searchPattern.SetValue('')
    Start-Sleep -Milliseconds 800

    # 5) 连续双击不同记录，不会复制错对象
    $itemA = Get-ListItemByPrefix $root $shortA
    $pointA = Get-ClickPoint $itemA
    Invoke-DoubleClick $pointA.X $pointA.Y
    $clipboardA = Wait-ClipboardText $shortA
    Check '连续双击第一条复制第一条' (
        [string]::Equals($clipboardA, $shortA, [StringComparison]::Ordinal)
    ) "clipboard=$clipboardA"

    $itemB = Get-ListItemByPrefix $root $shortB
    $pointB = Get-ClickPoint $itemB
    Invoke-DoubleClick $pointB.X $pointB.Y
    $clipboardB = Wait-ClipboardText $shortB
    Check '连续双击第二条复制第二条' (
        [string]::Equals($clipboardB, $shortB, [StringComparison]::Ordinal)
    ) "clipboard=$clipboardB"

    # 6) 原有复制按钮仍然正常
    $itemB = Get-ListItemByPrefix $root $shortB
    $pointB = Get-ClickPoint $itemB
    Invoke-SingleClick $pointB.X $pointB.Y
    Start-Sleep -Milliseconds 800

    $buttonSentinel = 'DBL-BUTTON-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    Set-ClipboardText $buttonSentinel

    $itemB = Get-ListItemByPrefix $root $shortB
    $pointB = Get-ClickPoint $itemB
    Invoke-SingleClick $pointB.X $pointB.Y
    Start-Sleep -Milliseconds 500

    $copyButton = @($root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button))) |
        Where-Object { $_.Current.Name -eq '复制' })[0]
    if ($null -eq $copyButton) { throw '找不到复制按钮' }
    $copyButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    $clipboardButton = Wait-ClipboardText $shortB
    Check '原有复制按钮仍能复制选中记录' (
        [string]::Equals($clipboardButton, $shortB, [StringComparison]::Ordinal)
    ) "clipboard=$clipboardButton"

    # 7) 双击列表空白处不应复制任何记录
    $listCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::List)
    $lists = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCondition))
    if ($lists.Count -gt 0) {
        $listRect = $lists[0].Current.BoundingRectangle
        $emptyX = [int]($listRect.X + ($listRect.Width / 2))
        $emptyY = [int]($listRect.Bottom - 20)
        $beforeEmpty = Get-ClipboardText
        Invoke-DoubleClick $emptyX $emptyY
        Check '双击列表空白处不复制' (
            [string]::Equals((Get-ClipboardText), $beforeEmpty, [StringComparison]::Ordinal)
        ) "clipboard=$(Get-ClipboardText)"
    }
    else {
        Check '找到历史列表控件' $false '未找到 List 控件'
    }
}
catch {
    Write-Host "测试异常: $($_.Exception.Message)"
    $script:fail++
}
finally {
    if ($null -ne $app -and -not $app.HasExited) {
        Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item Env:\CLIPASSIST_DATA_DIR -ErrorAction SilentlyContinue
    if ($null -ne $dataDir) {
        Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($hadOriginalText) {
        try { [System.Windows.Forms.Clipboard]::SetText($originalClipboard) } catch { }
    }
    Write-Host ''
    Write-Host "结果: $($script:pass) 项通过 / $($script:fail) 项失败"
}