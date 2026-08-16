#Requires -Version 5
<#
.SYNOPSIS
    UI automation regression suite for Stanza keyboard input.

.DESCRIPTION
    Drives the real app with injected keystrokes (SendKeys) and asserts behavior
    through UIAutomation. Two suites:

      A (US layout)            vim hjkl / arrow navigation, editor typing, Esc recovery
      B (Chinese IME forced)   hjkl navigation under IME, rapid Enter-commit + nav,
                               IME composition still working inside the title editor

    The fixture file is generated under tools/.verify-tmp (gitignored) and the app
    is launched with it. The app window is brought to the foreground before every
    keystroke batch (injected input only reaches the foreground process).

    Run from anywhere; resolves paths relative to the repo. Exit code is the
    number of failed checks.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-input.ps1
#>
param(
    [string]$Exe = "$PSScriptRoot\..\src\Stanza.App\bin\Debug\net10.0-windows\Stanza.exe"
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool c);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint idThread);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    public const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
}
'@

$script:Failures = 0
function Check([string]$name, [string]$actual, [string]$expected) {
    if ($actual -eq $expected) {
        Write-Host "  ok   $name ($actual)"
    } else {
        $script:Failures++
        Write-Host "  FAIL $name : got '$actual', expect '$expected'"
    }
}

# ---------- window / input helpers ----------

$script:AppHwnd = [IntPtr]::Zero
$script:AppTid = 0

function Ensure-Foreground {
    for ($i = 0; $i -lt 30 -and [Win]::GetForegroundWindow() -ne $script:AppHwnd; $i++) {
        [Win]::ShowWindow($script:AppHwnd, 9) | Out-Null
        $fp = 0
        $ft = [Win]::GetWindowThreadProcessId([Win]::GetForegroundWindow(), [ref]$fp)
        $mt = [Win]::GetCurrentThreadId()
        [Win]::AttachThreadInput($mt, $ft, $true) | Out-Null
        [Win]::BringWindowToTop($script:AppHwnd) | Out-Null
        [Win]::SetForegroundWindow($script:AppHwnd) | Out-Null
        [Win]::AttachThreadInput($mt, $ft, $false) | Out-Null
        Start-Sleep -Milliseconds 150
    }
    if ([Win]::GetForegroundWindow() -ne $script:AppHwnd) { throw "cannot foreground app" }
}

function Send-Keys([string]$keys, [int]$settleMs = 0) {
    Ensure-Foreground
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    if ($settleMs -gt 0) { Start-Sleep -Milliseconds $settleMs }
}

function Set-AppLayout([long]$hkl, [string]$label) {
    [Win]::PostMessage($script:AppHwnd, [Win]::WM_INPUTLANGCHANGEREQUEST, [IntPtr]::Zero, [IntPtr]$hkl) | Out-Null
    Start-Sleep -Milliseconds 600
    $cur = [Win]::GetKeyboardLayout($script:AppTid).ToInt64() -band 0xFFFF
    Write-Host "layout: 0x$($cur.ToString('X4')) ($label)"
}

# ---------- UIAutomation helpers ----------

$ae = [System.Windows.Automation.AutomationElement]
function CType($t) { New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $t) }

function Get-App {
    $p = Get-Process Stanza -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { return $null }
    return $ae::FromHandle($p.MainWindowHandle)
}

function Find-TaskList($win) {
    foreach ($l in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (CType ([System.Windows.Automation.ControlType]::List)))) {
        foreach ($it in $l.FindAll([System.Windows.Automation.TreeScope]::Children, (CType ([System.Windows.Automation.ControlType]::ListItem)))) {
            foreach ($t in $it.FindAll([System.Windows.Automation.TreeScope]::Descendants, (CType ([System.Windows.Automation.ControlType]::Text)))) {
                if ($t.Current.Name -match "^Task ") { return $l }
            }
        }
    }
    return $null
}

function Get-TaskSel {
    $win = Get-App
    $tl = Find-TaskList $win
    if (-not $tl) { return "(?)" }
    $items = $tl.FindAll([System.Windows.Automation.TreeScope]::Children, (CType ([System.Windows.Automation.ControlType]::ListItem)))
    $out = @()
    foreach ($it in $items) {
        $sip = $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($sip.Current.IsSelected) { $out += "1" } else { $out += "0" }
    }
    return ($out -join "")
}

function Get-EditValues {
    $win = Get-App
    $edits = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (CType ([System.Windows.Automation.ControlType]::Edit)))
    $names = @()
    foreach ($e in $edits) {
        $vp = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if ($vp) { $names += $vp.Current.Value }
    }
    if ($names.Count -eq 0) { return "(none)" }
    return ($names -join " | ")
}

# ---------- fixture & launch ----------

$workDir = Join-Path $PSScriptRoot ".verify-tmp"
New-Item -ItemType Directory -Force -Path $workDir | Out-Null
$fixture = Join-Path $workDir "test.stanza"
@'
# DOING

Task Alpha

Task Beta

# WAIT

Task Charlie

# DONE

# DELETE
'@ | Out-File -Encoding utf8 $fixture

Get-Process Stanza -ErrorAction SilentlyContinue | Stop-Process -Force
$proc = Start-Process -FilePath $Exe -ArgumentList "`"$fixture`"" -PassThru
$win = $null
for ($i = 0; $i -lt 30 -and -not $win; $i++) { Start-Sleep -Milliseconds 500; $win = Get-App }
if (-not $win) { Write-Host "FATAL: app window not found"; exit 1 }
$script:AppHwnd = [IntPtr]$win.Current.NativeWindowHandle
$pid_ = 0
$script:AppTid = [Win]::GetWindowThreadProcessId($script:AppHwnd, [ref]$pid_)
Ensure-Foreground

# ---------- Suite A: US layout ----------

Write-Host "`nSuite A: US layout"
Set-AppLayout 0x04090409 "en-US"

Check "A1 initial selection" (Get-TaskSel) "00"
Send-Keys "j" 400
Check "A2 j selects first" (Get-TaskSel) "10"
Send-Keys "j" 400
Check "A3 j moves down" (Get-TaskSel) "01"
Send-Keys "j" 400
Check "A4 j stays at end" (Get-TaskSel) "01"
Send-Keys "k" 400
Check "A5 k moves up" (Get-TaskSel) "10"
Send-Keys "h" 300
Send-Keys "l" 300
Check "A6 h/l do not move" (Get-TaskSel) "10"

Send-Keys "{ENTER}" 800   # expand -> title editor focused
Send-Keys "{END}" 200
Send-Keys "hjkl" 500
Check "A7 letters type into editor, no nav" (Get-TaskSel) "10"
$a8 = Get-EditValues
Check "A8 editor received letters" $(if ($a8 -match "hjkl") { "yes" } else { $a8 }) "yes"

Send-Keys "{ESC}" 400     # collapse + clear selection
Send-Keys "j" 400
Check "A9 j works right after Esc" (Get-TaskSel) "10"

# ---------- Suite B: Chinese IME ----------

Write-Host "`nSuite B: Chinese IME (0804)"
Set-AppLayout 0x08040804 "zh-CN"

Send-Keys "{ESC}" 300     # make sure nothing is expanded/selected
Send-Keys "j" 400
Check "B1 zh j selects first" (Get-TaskSel) "10"
Send-Keys "j" 400
Check "B2 zh j moves down" (Get-TaskSel) "01"
Send-Keys "k" 400
Check "B3 zh k moves up" (Get-TaskSel) "10"

$rapidFail = 0
for ($round = 1; $round -le 10; $round++) {
    Send-Keys "{ENTER}" 700   # expand
    Send-Keys "{ENTER}"       # commit
    $nav = "k"; $expected = "10"
    if ($round % 2 -eq 1) { $nav = "j"; $expected = "01" }
    Send-Keys $nav 250        # navigate immediately after commit
    $sel = Get-TaskSel
    $edits = Get-EditValues
    if (($sel -ne $expected) -or ($edits -match "^Task .*(j|k)")) {
        $rapidFail++
        Write-Host "  FAIL B4 round ${round}: sel=$sel expect $expected, edits=$edits"
    }
}
Check "B4 rapid Enter-commit + nav x10 (zh)" "$rapidFail" "0"

Send-Keys "{ENTER}" 800   # expand selected
Send-Keys "{END}" 200
Send-Keys "q" 500         # letter goes to IME composition / editor, not nav
$selAfterQ = Get-TaskSel
Check "B5 letter in editor does not nav (zh)" $selAfterQ "10"
$b6 = Get-EditValues
Check "B6 editor received letter (zh)" $(if ($b6 -match "q") { "yes" } else { $b6 }) "yes"

# ---------- Suite C: Ctrl+R quick open (VS Code style) ----------

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class KbdV {
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    public static void Down(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); }
    public static void Up(byte vk) { keybd_event(vk, 0, 2, UIntPtr.Zero); }
    public static void Press(byte vk) { Down(vk); Up(vk); }
}
'@

function Get-FocusedRowFile {
    # focused recent-row button: read its descendant text (button Name is empty for composite content)
    $f = [System.Windows.Automation.AutomationElement]::FocusedElement
    if (-not $f) { return "(no focus)" }
    $texts = $f.FindAll([System.Windows.Automation.TreeScope]::Descendants, (CType ([System.Windows.Automation.ControlType]::Text)))
    foreach ($t in $texts) { if ($t.Current.Name -match "\.stanza$") { return $t.Current.Name } }
    return "(not a recent row)"
}

function Find-RecentRow([string]$name) {
    # visible popup row button whose descendant text equals the given file name; $null when popup closed
    $win = Get-App
    $buttons = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (CType ([System.Windows.Automation.ControlType]::Button)))
    foreach ($b in $buttons) {
        $texts = $b.FindAll([System.Windows.Automation.TreeScope]::Descendants, (CType ([System.Windows.Automation.ControlType]::Text)))
        foreach ($t in $texts) { if ($t.Current.Name -eq $name) { return $b } }
    }
    return $null
}

function Get-FirstTaskTitle {
    $win = Get-App
    $tl = Find-TaskList $win
    if (-not $tl) { return "(?)" }
    $items = $tl.FindAll([System.Windows.Automation.TreeScope]::Children, (CType ([System.Windows.Automation.ControlType]::ListItem)))
    if ($items.Count -eq 0) { return "(empty)" }
    $texts = $items[0].FindAll([System.Windows.Automation.TreeScope]::Descendants, (CType ([System.Windows.Automation.ControlType]::Text)))
    foreach ($t in $texts) { if ($t.Current.Name -match "^Task ") { return $t.Current.Name } }
    return "(?)"
}

function Restart-AppWith([string]$file) {
    Get-Process Stanza -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    $p = Start-Process -FilePath $Exe -ArgumentList "`"$file`"" -PassThru
    $w = $null
    for ($i = 0; $i -lt 30 -and -not $w; $i++) { Start-Sleep -Milliseconds 500; $w = Get-App }
    if (-not $w) { throw "app window not found after restart" }
    $script:AppHwnd = [IntPtr]$w.Current.NativeWindowHandle
    $pid_ = 0
    $script:AppTid = [Win]::GetWindowThreadProcessId($script:AppHwnd, [ref]$pid_)
    Ensure-Foreground
}

Write-Host "`nSuite C: Ctrl+R quick open"
$recentJson = Join-Path $env:APPDATA "Stanza/recent.json"
$recentBak = Join-Path $workDir "recent.json.bak"
Copy-Item $recentJson $recentBak -Force

$aFile = Join-Path $workDir "a.stanza"
$bFile = Join-Path $workDir "b.stanza"
@'
# DOING

Task Apple
'@ | Out-File -Encoding utf8 $aFile
@'
# DOING

Task Banana
'@ | Out-File -Encoding utf8 $bFile

try {
    # register MRU order [b, a]: open a, then b, then b again for the test
    Restart-AppWith $aFile
    Restart-AppWith $bFile

    Ensure-Foreground
    [KbdV]::Down(0x11)                      # Ctrl down
    [KbdV]::Press(0x52)                     # R
    Start-Sleep -Milliseconds 600
    Check "C1 Ctrl+R opens popup, first row highlighted" (Get-FocusedRowFile) "b.stanza"
    [KbdV]::Press(0x52)                     # R again, still holding Ctrl
    Start-Sleep -Milliseconds 500
    Check "C2 R cycles to second row" (Get-FocusedRowFile) "a.stanza"
    [KbdV]::Up(0x11)                        # release Ctrl -> open highlighted
    Start-Sleep -Milliseconds 900
    Check "C3 releasing Ctrl opens highlighted file" (Get-FirstTaskTitle) "Task Apple"

    # opening a re-registered MRU as [a, b]; Esc must cancel without opening
    Ensure-Foreground
    [KbdV]::Down(0x11)
    [KbdV]::Press(0x52)
    Start-Sleep -Milliseconds 600
    Check "C4 popup reopens, first row now a" (Get-FocusedRowFile) "a.stanza"
    [KbdV]::Up(0x11)
    Start-Sleep -Milliseconds 600   # releasing Ctrl opened row0 (a, already current)

    # C5/C6: open the popup via the toolbar button (no Ctrl involved), then plain Esc cancels.
    # (Ctrl+Esc is the Windows Start-menu chord; the OS always wins that one.)
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class MouseC {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
    public static void ClickAt(int x, int y) {
        SetCursorPos(x, y);
        mouse_event(0x02, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x04, 0, 0, 0, UIntPtr.Zero);
    }
}
'@
    Ensure-Foreground
    $win = Get-App
    $recentsBtn = $null
    foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (CType ([System.Windows.Automation.ControlType]::Button)))) {
        if ($b.Current.AutomationId -eq "RecentButton") { $recentsBtn = $b; break }
    }
    if (-not $recentsBtn) { throw "RecentButton not found" }
    $rb = $recentsBtn.Current.BoundingRectangle
    [MouseC]::ClickAt([int]($rb.X + $rb.Width / 2), [int]($rb.Y + $rb.Height / 2))
    Start-Sleep -Milliseconds 700
    $row = Find-RecentRow "a.stanza"
    Check "C5 button opens popup" $(if ($row) { "open" } else { "closed" }) "open"
    Ensure-Foreground
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 500
    $row = Find-RecentRow "a.stanza"
    $still = Get-FirstTaskTitle
    if (($row -eq $null) -and ($still -eq "Task Apple")) { $c6 = "ok" } else { $c6 = "popup=$(if ($row) { 'open' } else { 'closed' }), file=$still" }
    Check "C6 Esc closes popup without opening" $c6 "ok"
}
finally {
    Copy-Item $recentBak $recentJson -Force
}

# ---------- cleanup ----------

Get-Process Stanza -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "`n$($script:Failures) check(s) failed"
exit $script:Failures
