# ClipAssist V0.2-03 搜索关键词高亮 UI 测试
# 验证：过滤、大小写、中文/英文、特殊字符、清空、连续输入、长预览、高亮渲染、复制原文。
# 高亮颜色通过屏幕像素检查；不依赖截图人工判断。

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class HighWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
"@

$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\Debug\net10.0-windows\ClipAssist.exe'
$script:pass = 0
$script:fail = 0

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

function Get-ListItems($root) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
}

function Get-ListNames($root) {
    return @(Get-ListItems $root | ForEach-Object { $_.Current.Name })
}

function Set-Search($valuePattern, [string]$text, [int]$waitMilliseconds = 700) {
    $valuePattern.SetValue($text)
    Start-Sleep -Milliseconds $waitMilliseconds
}

function Get-SearchBox($root) {
    $editCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edits = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCondition))
    if ($edits.Count -eq 0) { throw '找不到搜索框' }
    return $edits[0]
}

function Get-ListRect($root) {
    $listCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::List)
    $lists = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCondition))
    if ($lists.Count -eq 0) { throw '找不到历史列表' }
    return $lists[0].Current.BoundingRectangle
}

function Measure-HighlightPixels($root) {
    $rect = Get-ListRect $root
    $width = [int]$rect.Width
    $height = [int]$rect.Height
    if ($width -le 0 -or $height -le 0) { return 0 }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bitmap.Size)
        $count = 0
        for ($y = 0; $y -lt $height; $y += 2) {
            $run = 0
            for ($x = 0; $x -lt $width; $x += 1) {
                $color = $bitmap.GetPixel($x, $y)
                # #FFE58F 的近似范围；避开白色、灰色、蓝色选中背景和黑色文字。
                $isYellow = $color.R -ge 235 -and $color.G -ge 200 -and
                    $color.B -ge 90 -and $color.B -le 200 -and
                    ($color.R - $color.B) -ge 60
                if ($isYellow) {
                    $run++
                }
                else {
                    # 单像素宽的 ClearType 彩色边缘不算高亮，至少连续两个像素才算。
                    if ($run -ge 2) { $count += $run }
                    $run = 0
                }
            }
            if ($run -ge 2) { $count += $run }
        }
        return $count
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$app = $null
$dataDir = $null
$originalClipboard = Get-ClipboardText
$hadOriginalText = $originalClipboard.Length -gt 0

try {
    if (@(Get-Process -Name ClipAssist -ErrorAction SilentlyContinue).Count -gt 0) {
        throw '请先退出正在运行的 ClipAssist，再运行高亮测试。'
    }

    $dataDir = Join-Path $env:TEMP ('clipassist-highlight-' + [guid]::NewGuid().ToString('N'))
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
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    [void][HighWin]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0003)
    [void][HighWin]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 300

    $alpha = 'Alpha keyword one'
    $beta = 'BETA KEYWORD TWO keyword'
    $chinese = '中文 keyword 混合 Keyword'
    $special = '特殊字符 (a.b)[c]*+? 测试'
    $longText = "LONG-START`r`n" + (("keyword 关键词 行`t" * 300)) + "`r`nLONG-END"

    foreach ($text in @($alpha, $beta, $chinese, $special, $longText)) {
        [System.Windows.Forms.Clipboard]::SetText($text)
        Start-Sleep -Milliseconds 900
    }

    $allItems = Get-ListItems $root
    Check '测试数据已进入列表' ($allItems.Count -eq 5) "count=$($allItems.Count)"

    $searchBox = Get-SearchBox $root
    $searchPattern = $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)

    Set-Search $searchPattern 'keyword'
    Check '英文关键词过滤（不区分大小写）' ((Get-ListItems $root).Count -eq 4) "count=$((Get-ListItems $root).Count)"

    Set-Search $searchPattern 'KEYWORD'
    Check '大写关键词过滤结果一致' ((Get-ListItems $root).Count -eq 4) "count=$((Get-ListItems $root).Count)"

    Set-Search $searchPattern 'keyword two'
    Check '连续英文搜索词过滤正确' ((Get-ListItems $root).Count -eq 1) "count=$((Get-ListItems $root).Count)"

    Set-Search $searchPattern '关键词'
    Check '中文关键词过滤正确' ((Get-ListItems $root).Count -eq 1) "count=$((Get-ListItems $root).Count)"

    Set-Search $searchPattern '*+?'
    Check '特殊字符搜索不崩溃且结果正确' (
        $null -ne (Get-ListNames $root) -and (Get-ListItems $root).Count -eq 1
    ) "count=$((Get-ListItems $root).Count)"

    Set-Search $searchPattern ''
    Check '清空搜索恢复全部记录' ((Get-ListItems $root).Count -eq 5) "count=$((Get-ListItems $root).Count)"

    # 连续快速修改搜索词，最后清空，验证不会残留旧过滤/异常。
    foreach ($term in @('keyword', 'BETA', '中文', 'keyword two', '*+?')) {
        $searchPattern.SetValue($term)
    }
    Set-Search $searchPattern '' 900
    Check '快速连续修改搜索词后仍能恢复' ((Get-ListItems $root).Count -eq 5) "count=$((Get-ListItems $root).Count)"

    # 高亮必须真的画出来：搜索时有黄色像素，清空后没有。
    Set-Search $searchPattern 'keyword' 900
    $pixelsWithHighlight = Measure-HighlightPixels $root
    Check '搜索时屏幕上出现高亮颜色' ($pixelsWithHighlight -gt 50) "yellowPixels=$pixelsWithHighlight"

    Set-Search $searchPattern '' 900
    $pixelsWithoutHighlight = Measure-HighlightPixels $root
    Check '清空搜索后高亮消失' ($pixelsWithoutHighlight -lt 10) "yellowPixels=$pixelsWithoutHighlight"

    # 长文本预览保持单行，不应被撑成多行。
    Set-Search $searchPattern 'LONG-START'
    $longItems = Get-ListItems $root
    Check '长文本搜索结果只有一条' ($longItems.Count -eq 1) "count=$($longItems.Count)"
    if ($longItems.Count -eq 1) {
        $longItemHeight = $longItems[0].Current.BoundingRectangle.Height
        Check '长文本预览保持一行' ($longItemHeight -lt 70) "height=$longItemHeight"

        $longItems[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 400

        $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        $copyButton = @($root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $buttonCondition) |
            Where-Object { $_.Current.Name -eq '复制' })[0]

        if ($null -eq $copyButton) { throw '找不到复制按钮' }
        $copyButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 700

        $clipboardAfterCopy = Get-ClipboardText
        Check '高亮过滤后复制仍是完整原文' (
            [string]::Equals($clipboardAfterCopy, $longText, [StringComparison]::Ordinal)
        ) "actualLength=$($clipboardAfterCopy.Length) expectedLength=$($longText.Length)"
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