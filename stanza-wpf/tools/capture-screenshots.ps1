#Requires -Version 5
<#
.SYNOPSIS
    Capture README screenshots of the Stanza app automatically.

.DESCRIPTION
    Launches the app with a scratch copy of tools/demo.stanza, drives it with
    injected input (SendKeys / mouse), and saves PNG screenshots to <repo>/.assets:

      app-overview.png       main window (DOING block)
      app-task-expanded.png  expanded task card
      app-context-menu.png   right-click context menu
      app-tag-picker.png     tag picker popup

    Popups (context menu, picker) are separate top-level windows; the capture
    region is the union of all top-level windows owned by the app process.

    Run from anywhere; resolves paths relative to the repo. Requires the app
    to be built (dotnet build). Closes any running Stanza instance.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-screenshots.ps1
#>
param(
    [string]$Exe = "$PSScriptRoot\..\src\Stanza.App\bin\Debug\net10.0-windows\Stanza.exe",
    [string]$OutDir = "$PSScriptRoot\..\..\.assets"
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class CapWin {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool c);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public static void RClick(int x, int y) {
        SetCursorPos(x, y);
        mouse_event(0x08, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x10, 0, 0, 0, UIntPtr.Zero);
    }
}
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@
[CapWin]::SetProcessDPIAware() | Out-Null

$script:AppHwnd = [IntPtr]::Zero
$script:AppPid = 0
$ae = [System.Windows.Automation.AutomationElement]

function CType($t) { New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $t) }

function Get-App {
    $p = Get-Process Stanza -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { return $null }
    $script:AppPid = $p.Id
    return $ae::FromHandle($p.MainWindowHandle)
}

function Ensure-Foreground {
    for ($i = 0; $i -lt 30 -and [CapWin]::GetForegroundWindow() -ne $script:AppHwnd; $i++) {
        [CapWin]::ShowWindow($script:AppHwnd, 9) | Out-Null
        $fp = 0
        $ft = [CapWin]::GetWindowThreadProcessId([CapWin]::GetForegroundWindow(), [ref]$fp)
        $mt = [CapWin]::GetCurrentThreadId()
        [CapWin]::AttachThreadInput($mt, $ft, $true) | Out-Null
        [CapWin]::BringWindowToTop($script:AppHwnd) | Out-Null
        [CapWin]::SetForegroundWindow($script:AppHwnd) | Out-Null
        [CapWin]::AttachThreadInput($mt, $ft, $false) | Out-Null
        Start-Sleep -Milliseconds 150
    }
    if ([CapWin]::GetForegroundWindow() -ne $script:AppHwnd) { throw "cannot foreground app" }
}

function Send-Keys([string]$keys, [int]$settleMs = 0) {
    Ensure-Foreground
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    if ($settleMs -gt 0) { Start-Sleep -Milliseconds $settleMs }
}

# union of the visible frame rects of every top-level window owned by the app.
# the main window is borderless + transparent with a 16 DIP shadow margin
# (ShadowHost in MainWindow.xaml); that margin shows the desktop, so crop it.
function Get-AppRegion {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:AppPid)
    $wins = $ae::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    $margin = [int][Math]::Round(16.0 * [CapWin]::GetDpiForWindow($script:AppHwnd) / 96)
    $l = [int]::MaxValue; $t = [int]::MaxValue; $r = [int]::MinValue; $b = [int]::MinValue
    foreach ($w in $wins) {
        if ($w.Current.IsOffscreen) { continue }   # collapsed popups pollute the union
        $rect = New-Object RECT
        $hr = [CapWin]::DwmGetWindowAttribute([IntPtr]$w.Current.NativeWindowHandle, [CapWin]::DWMWA_EXTENDED_FRAME_BOUNDS, [ref]$rect, 16)
        if ($hr -ne 0 -or $rect.Right -le $rect.Left -or $rect.Bottom -le $rect.Top) { continue }
        if ([IntPtr]$w.Current.NativeWindowHandle -eq $script:AppHwnd) {
            $rect.Left += $margin; $rect.Top += $margin; $rect.Right -= $margin; $rect.Bottom -= $margin
        }
        if ($rect.Left -lt $l) { $l = $rect.Left }
        if ($rect.Top -lt $t) { $t = $rect.Top }
        if ($rect.Right -gt $r) { $r = $rect.Right }
        if ($rect.Bottom -gt $b) { $b = $rect.Bottom }
    }
    if ($l -eq [int]::MaxValue) { throw "no visible app window" }
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    return @([Math]::Max($l, $vs.Left), [Math]::Max($t, $vs.Top), [Math]::Min($r, $vs.Right), [Math]::Min($b, $vs.Bottom))
}

function Capture([string]$name) {
    Ensure-Foreground
    Start-Sleep -Milliseconds 350
    $region = Get-AppRegion
    $w = $region[2] - $region[0]; $h = $region[3] - $region[1]
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($region[0], $region[1], 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $path = Join-Path $OutDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $name ($w x $h)"
}

function Get-FirstTaskItem {
    $win = Get-App
    $tl = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "TaskList")))
    if (-not $tl) { throw "TaskList not found" }
    $items = $tl.FindAll([System.Windows.Automation.TreeScope]::Children, (CType ([System.Windows.Automation.ControlType]::ListItem)))
    if ($items.Count -eq 0) { throw "no task items" }
    return $items[0]
}

# find a control anywhere in the app's UI trees; popups (ContextMenu, pickers)
# are separate top-level windows, not descendants of the main window element
function Find-AppElement($controlType) {
    $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:AppPid)
    $cc = CType $controlType
    foreach ($top in $ae::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $pc)) {
        if ($top.Current.ControlType -eq $controlType) { return $top }
        $hit = $top.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cc)
        if ($hit) { return $hit }
    }
    return $null
}

# ---------- fixture & launch ----------

if (-not (Test-Path $Exe)) { Write-Host "FATAL: $Exe not found; run 'dotnet build' first"; exit 1 }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# open a scratch copy so the committed fixture can never be dirtied
$workDir = Join-Path $PSScriptRoot ".screenshot-tmp"
New-Item -ItemType Directory -Force -Path $workDir | Out-Null
$fixture = Join-Path $workDir "demo.stanza"
Copy-Item "$PSScriptRoot\demo.stanza" $fixture -Force

Get-Process Stanza -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$proc = Start-Process -FilePath $Exe -ArgumentList "`"$fixture`"" -PassThru
$win = $null
for ($i = 0; $i -lt 30 -and -not $win; $i++) { Start-Sleep -Milliseconds 500; $win = Get-App }
if (-not $win) { Write-Host "FATAL: app window not found"; exit 1 }
$script:AppHwnd = [IntPtr]$win.Current.NativeWindowHandle
Ensure-Foreground
Start-Sleep -Milliseconds 1200
[CapWin]::SetCursorPos(2, 2) | Out-Null   # park the cursor outside the window

# ---------- 1. overview ----------

Capture "app-overview.png"

# ---------- 2. expanded task card ----------

Send-Keys "j" 400
Send-Keys "{ENTER}" 900
Capture "app-task-expanded.png"
Send-Keys "{ESC}" 500

# ---------- 3. context menu ----------

$item = Get-FirstTaskItem
$r = $item.Current.BoundingRectangle
Ensure-Foreground
[CapWin]::RClick([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
Start-Sleep -Milliseconds 600
Capture "app-context-menu.png"

# ---------- 4. tag picker (context menu -> 标签…) ----------

$menu = $null
for ($i = 0; $i -lt 10 -and -not $menu; $i++) {
    Start-Sleep -Milliseconds 200
    $menu = Find-AppElement ([System.Windows.Automation.ControlType]::Menu)
}
# match the tag menu item without a non-ASCII literal: PS 5.1 parses BOM-less
# scripts as ANSI, which would mangle an inline "标签" (标签 = U+6807 U+7B7E)
$tagPattern = [char]0x6807 + [char]0x7B7E + '|Tag'
$tagItem = $null
if ($menu) {
    foreach ($mi in $menu.FindAll([System.Windows.Automation.TreeScope]::Descendants, (CType ([System.Windows.Automation.ControlType]::MenuItem)))) {
        if ($mi.Current.Name -match $tagPattern) { $tagItem = $mi; break }
    }
}
if ($tagItem) {
    $ip = $tagItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    if ($ip) { $ip.Invoke() } else {
        $tr = $tagItem.Current.BoundingRectangle
        [CapWin]::SetCursorPos([int]($tr.X + $tr.Width / 2), [int]($tr.Y + $tr.Height / 2)) | Out-Null
        [CapWin]::mouse_event(0x02, 0, 0, 0, [UIntPtr]::Zero)
        [CapWin]::mouse_event(0x04, 0, 0, 0, [UIntPtr]::Zero)
    }
    Start-Sleep -Milliseconds 700
    Capture "app-tag-picker.png"
} else {
    Write-Host "WARN: tag menu item not found, skipping app-tag-picker.png"
}

# ---------- cleanup ----------

Get-Process Stanza -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "done -> $OutDir"
