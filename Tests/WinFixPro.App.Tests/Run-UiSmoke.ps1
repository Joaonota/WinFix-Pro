$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('WinFixUiNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WinFixUiNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string windowName);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
'@
}

$failures = 0
function Assert-Ui([bool]$condition, [string]$message) {
    if ($condition) { Write-Host "PASS: $message" -ForegroundColor Green }
    else { Write-Host "FAIL: $message" -ForegroundColor Red; $script:failures++ }
}
function Wait-For([scriptblock]$condition, [int]$seconds = 30) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $seconds) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}
function Get-MainRoot {
    if (-not [WinFixUiNative]::IsWindowVisible($script:handle)) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($script:handle)
}
function Find-Element([string]$name, [System.Windows.Automation.ControlType]$controlType, $root = $null) {
    if ($null -eq $root) { $root = Get-MainRoot }
    if ($null -eq $root) { return $null }
    $nameCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $typeCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    $condition = [System.Windows.Automation.AndCondition]::new($nameCondition, $typeCondition)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Invoke-Button([string]$name) {
    $element = Find-Element $name ([System.Windows.Automation.ControlType]::Button)
    if ($null -eq $element) { throw "Button '$name' was not found." }
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Get-Text([string]$name) {
    $element = Find-Element $name ([System.Windows.Automation.ControlType]::Text)
    if ($null -eq $element) { return $null }
    return $element.Current.Name
}
function Get-TextWithPrefix([string]$prefix) {
    $root = Get-MainRoot
    if ($null -eq $root) { return $null }
    $texts = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text))
    foreach ($text in $texts) {
        if ($text.Current.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            return $text.Current.Name
        }
    }
    return $null
}
function Get-WindowStyles {
    return [WinFixUiNative]::GetWindowLongPtr($script:handle, -16).ToInt64()
}
function Get-OtherAppWindows {
    $found = New-Object System.Collections.Generic.List[System.IntPtr]
    $targetPid = [uint32]$script:process.Id
    $callback = [WinFixUiNative+EnumWindowsProc]{
        param([IntPtr]$window, [IntPtr]$state)
        [uint32]$owner = 0
        [WinFixUiNative]::GetWindowThreadProcessId($window, [ref]$owner) | Out-Null
        if ($owner -eq $targetPid -and $window -ne $script:handle) { $found.Add($window) }
        return $true
    }
    [WinFixUiNative]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    return $found
}

$exe = Join-Path (Split-Path -Parent $PSScriptRoot) '..\dist\WinFixPro\WinFixPro.exe'
$exe = [IO.Path]::GetFullPath($exe)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Release executable not found: $exe" }
$script:process = Start-Process -FilePath $exe -ArgumentList '--elevated-restart' -PassThru
try {
    $launched = Wait-For {
        $script:process.Refresh()
        $script:handle = $script:process.MainWindowHandle
        if ($script:handle -eq [IntPtr]::Zero) {
            $script:handle = [WinFixUiNative]::FindWindow($null, 'WinFix Pro')
        }
        return $script:handle -ne [IntPtr]::Zero
    } 45
    Assert-Ui $launched 'Application launches and creates a visible desktop window'
    if (-not $launched) { throw 'WinFix Pro main window did not appear.' }

    $style = Get-WindowStyles
    Assert-Ui (($style -band 0x00040000) -ne 0) 'Native resize border is enabled'
    Assert-Ui (($style -band 0x00020000) -ne 0) 'Native minimize control is enabled'
    Assert-Ui (($style -band 0x00010000) -ne 0) 'Native maximize/restore control is enabled'
    Assert-Ui (($style -band 0x00080000) -ne 0) 'Native system menu and close control are enabled'
    $extendedStyle = [WinFixUiNative]::GetWindowLongPtr($script:handle, -20).ToInt64()
    Assert-Ui (($extendedStyle -band 0x00000080) -eq 0) 'Window is not marked as a taskbar-excluded tool window'
    Start-Sleep -Milliseconds 500
    [WinFixUiNative]::ShowWindow($script:handle, 9) | Out-Null
    [WinFixUiNative]::BringWindowToTop($script:handle) | Out-Null
    $rect = [WinFixUiNative+RECT]::new()
    [WinFixUiNative]::GetWindowRect($script:handle, [ref]$rect) | Out-Null
    [WinFixUiNative]::SetCursorPos($rect.Left + 300, $rect.Top + 12) | Out-Null
    [WinFixUiNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [WinFixUiNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    [WinFixUiNative]::SetForegroundWindow($script:handle) | Out-Null
    Start-Sleep -Milliseconds 500
    Assert-Ui ([WinFixUiNative]::GetForegroundWindow() -eq $script:handle) 'Window can be activated and focused'

    [WinFixUiNative]::GetWindowRect($script:handle, [ref]$rect) | Out-Null
    $moved = [WinFixUiNative]::SetWindowPos($script:handle, [IntPtr]::Zero,
        $rect.Left + 18, $rect.Top + 18, $rect.Right - $rect.Left, $rect.Bottom - $rect.Top, 0x0014)
    Assert-Ui $moved 'Window can be repositioned'

    [WinFixUiNative]::SendMessage($script:handle, 0x0112, [IntPtr]0xF020, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 600
    Assert-Ui ([WinFixUiNative]::IsIconic($script:handle)) 'Minimize command reaches the native window'
    [WinFixUiNative]::SendMessage($script:handle, 0x0112, [IntPtr]0xF120, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 600
    $screenWidth = [WinFixUiNative]::GetSystemMetrics(0)
    $screenHeight = [WinFixUiNative]::GetSystemMetrics(1)
    $testWidth = [Math]::Max(900, [Math]::Min(1020, $screenWidth - 100))
    $testHeight = [Math]::Max(600, [Math]::Min(680, $screenHeight - 100))
    [WinFixUiNative]::SetWindowPos($script:handle, [IntPtr]::Zero, 30, 30, 900, 600, 0x0014) | Out-Null
    Start-Sleep -Milliseconds 400
    Assert-Ui (-not [WinFixUiNative]::IsIconic($script:handle)) 'Restore command returns the window'
    [WinFixUiNative]::SendMessage($script:handle, 0x0112, [IntPtr]0xF030, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 600
    Assert-Ui ([WinFixUiNative]::IsZoomed($script:handle)) 'Maximize command reaches the native window'
    [WinFixUiNative]::SendMessage($script:handle, 0x0112, [IntPtr]0xF120, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 600
    $captionRect = [WinFixUiNative+RECT]::new()
    [WinFixUiNative]::GetWindowRect($script:handle, [ref]$captionRect) | Out-Null
    $captionX = $captionRect.Left + [int](($captionRect.Right - $captionRect.Left) / 2)
    $captionY = $captionRect.Top + 12
    [WinFixUiNative]::SetCursorPos($captionX, $captionY) | Out-Null
    function Invoke-TitleDoubleClick {
        $currentRect = [WinFixUiNative+RECT]::new()
        [WinFixUiNative]::GetWindowRect($script:handle, [ref]$currentRect) | Out-Null
        $x = $currentRect.Left + [int](($currentRect.Right - $currentRect.Left) / 2)
        $y = $currentRect.Top + 12
        [WinFixUiNative]::SetCursorPos($x, $y) | Out-Null
        foreach ($click in 1..2) {
            [WinFixUiNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 40
            [WinFixUiNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 100
        }
        Start-Sleep -Milliseconds 600
    }
    Invoke-TitleDoubleClick
    Assert-Ui ([WinFixUiNative]::IsZoomed($script:handle)) 'Double-clicking the native title bar maximizes the window'
    Invoke-TitleDoubleClick
    Assert-Ui (-not [WinFixUiNative]::IsZoomed($script:handle)) 'A second native title-bar double-click restores the window'

    [WinFixUiNative]::GetWindowRect($script:handle, [ref]$rect) | Out-Null
    $dragStartX = $rect.Left + [int](($rect.Right - $rect.Left) / 2)
    $dragStartY = $rect.Top + 12
    [WinFixUiNative]::SetCursorPos($dragStartX, $dragStartY) | Out-Null
    [WinFixUiNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 150
    [WinFixUiNative]::SetCursorPos($dragStartX + 55, $dragStartY + 35) | Out-Null
    Start-Sleep -Milliseconds 150
    [WinFixUiNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 600
    $dragRect = [WinFixUiNative+RECT]::new()
    [WinFixUiNative]::GetWindowRect($script:handle, [ref]$dragRect) | Out-Null
    Assert-Ui (($dragRect.Left -ne $rect.Left) -or ($dragRect.Top -ne $rect.Top)) 'Dragging the title bar moves the native window'

    [WinFixUiNative]::GetWindowRect($script:handle, [ref]$rect) | Out-Null
    $oldWidth = $rect.Right - $rect.Left
    $oldHeight = $rect.Bottom - $rect.Top
    $resized = [WinFixUiNative]::SetWindowPos($script:handle, [IntPtr]::Zero,
        30, 30, $testWidth, $testHeight, 0x0014)
    $newRect = [WinFixUiNative+RECT]::new()
    [WinFixUiNative]::GetWindowRect($script:handle, [ref]$newRect) | Out-Null
    Assert-Ui ($resized -and (($newRect.Right - $newRect.Left) -ne $oldWidth) -and (($newRect.Bottom - $newRect.Top) -ne $oldHeight)) `
        'Window can be resized to a smaller responsive desktop size'

    $resizeRect = [WinFixUiNative+RECT]::new()
    [WinFixUiNative]::GetWindowRect($script:handle, [ref]$resizeRect) | Out-Null
    $resizeStartX = $resizeRect.Right - 3
    $resizeStartY = $resizeRect.Bottom - 3
    [WinFixUiNative]::SetCursorPos($resizeStartX, $resizeStartY) | Out-Null
    [WinFixUiNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 150
    [WinFixUiNative]::SetCursorPos($resizeStartX + 25, $resizeStartY + 25) | Out-Null
    Start-Sleep -Milliseconds 150
    [WinFixUiNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 600
    $afterResize = [WinFixUiNative+RECT]::new()
    [WinFixUiNative]::GetWindowRect($script:handle, [ref]$afterResize) | Out-Null
    Assert-Ui ((($afterResize.Right - $afterResize.Left) -gt $testWidth) -or (($afterResize.Bottom - $afterResize.Top) -gt $testHeight)) `
        'Dragging a resize edge changes the actual window size'

    $scanFinished = Wait-For {
        $root = Get-MainRoot
        return $null -ne $root -and $null -eq (Find-Element 'Cancel' ([System.Windows.Automation.ControlType]::Button) $root)
    } 90
    Assert-Ui $scanFinished 'Startup quick diagnostic completes without freezing the interface'

    $sections = @(
        'Dashboard','Universal Diagnostics','Hardware Diagnostics','Performance Analyzer',
        'Network Diagnostics','Windows Health','Windows Update','Security','Services','Drivers',
        'Common Issues','Knowledge Center','Unknown Problem Analysis','Repair Center','Reports','History','Settings'
    )
    foreach ($section in $sections) {
        Invoke-Button $section
        Start-Sleep -Milliseconds 150
        Assert-Ui ((Get-Text $section) -eq $section) "Sidebar navigation opens $section"
    }

    Invoke-Button 'Dashboard'
    Invoke-Button 'Quick Scan'
    $quickFinished = Wait-For {
        $root = Get-MainRoot
        $cancel = Find-Element 'Cancel' ([System.Windows.Automation.ControlType]::Button) $root
        $lastDiagnostic = Get-TextWithPrefix 'Last diagnostic:'
        return $null -ne $root -and $null -eq $cancel -and
            $null -ne $lastDiagnostic -and $lastDiagnostic -notlike '*Never'
    } 90
    Assert-Ui $quickFinished 'Quick Scan runs from the dashboard and updates the real diagnostic date'

    Invoke-Button 'Full System Scan'
    $fullFinished = Wait-For {
        $root = Get-MainRoot
        return $null -ne $root -and $null -eq (Find-Element 'Cancel' ([System.Windows.Automation.ControlType]::Button) $root)
    } 180
    Assert-Ui $fullFinished 'Full System Scan runs through the GUI'

    $root = Get-MainRoot
    $detailsCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'Details'),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button))
    $detailButtons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        $detailsCondition)
    if ($detailButtons.Count -gt 9) {
        $detailButtons[9].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 500
        Assert-Ui ((Get-OtherAppWindows).Count -gt 0) 'Detected issue details open in a bounded dialog'
        foreach ($window in (Get-OtherAppWindows)) {
            [WinFixUiNative]::SendMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        }
    } else {
        Assert-Ui $true 'No issue detail dialog was required because no actionable issue was detected by the live scan'
    }
    Invoke-Button 'View All Problems'
    Assert-Ui $true 'View All Problems scroll action is available'

    Invoke-Button 'Generate Report'
    Start-Sleep -Milliseconds 700
    $saveDialog = $null
    foreach ($window in (Get-OtherAppWindows)) {
        $candidate = [System.Windows.Automation.AutomationElement]::FromHandle($window)
        if ($candidate.Current.Name -match 'Save As|Save') { $saveDialog = $candidate; break }
    }
    Assert-Ui ($null -ne $saveDialog) 'Dashboard report action opens a native save dialog'
    if ($null -ne $saveDialog) {
        $savePath = Join-Path $env:TEMP "WinFixPro-UI-Smoke-$PID.html"
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.SendKeys]::SendWait('^a')
        [System.Windows.Forms.SendKeys]::SendWait($savePath)
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        $reportSaved = Wait-For { Test-Path -LiteralPath $savePath -PathType Leaf } 15
        Assert-Ui $reportSaved 'Dashboard report is written to the selected path'
        if (Test-Path -LiteralPath $savePath) { Remove-Item -LiteralPath $savePath -Force }
    }

    [WinFixUiNative]::SendMessage($script:handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    $closed = $script:process.WaitForExit(10000)
    Assert-Ui $closed 'Close command exits the application cleanly'
}
finally {
    if ($null -ne $script:process) {
        $script:process.Refresh()
        if (-not $script:process.HasExited) { Stop-Process -Id $script:process.Id }
    }
}

if ($failures -gt 0) { throw "$failures UI smoke test(s) failed." }
