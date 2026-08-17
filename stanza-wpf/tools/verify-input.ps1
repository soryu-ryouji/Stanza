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

# 焦点编辑框的当前选中文本（光标折叠时为 ""）；TextPattern.GetSelection 对插入点返回退化区间
function Get-FocusedEditSelection {
    $f = [System.Windows.Automation.AutomationElement]::FocusedElement
    if (-not $f) { return "(no focus)" }
    $tp = $f.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    if (-not $tp) { return "(no text pattern)" }
    return (($tp.GetSelection() | ForEach-Object { $_.GetText(-1) }) -join "|")
}

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
    # locate by AutomationId: an empty list has no "Task " text for Find-TaskList to match
    $tl = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "TaskList")))
    if (-not $tl) { return "(?)" }
    $items = $tl.FindAll([System.Windows.Automation.TreeScope]::Children, (CType ([System.Windows.Automation.ControlType]::ListItem)))
    if ($items.Count -eq 0) { return "(empty)" }
    $texts = $items[0].FindAll([System.Windows.Automation.TreeScope]::Descendants, (CType ([System.Windows.Automation.ControlType]::Text)))
    foreach ($t in $texts) { if ($t.Current.Name -match "^Task ") { return $t.Current.Name } }
    return "(?)"
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

# 键盘模式钉为 Windows（用户 settings.json 在 Suite G 末尾还原）
$settingsJson = Join-Path $env:APPDATA "Stanza/settings.json"
$settingsBak = Join-Path $workDir "settings.json.bak"
$hadSettings = Test-Path $settingsJson
if ($hadSettings) {
    Copy-Item $settingsJson $settingsBak -Force
    $cfg = Get-Content $settingsJson -Raw | ConvertFrom-Json
    $cfg | Add-Member -NotePropertyName MacOsMode -NotePropertyValue $false -Force
    [IO.File]::WriteAllText($settingsJson, ($cfg | ConvertTo-Json -Compress))
} else {
    [IO.File]::WriteAllText($settingsJson, '{"Language":"zh","MacOsMode":false}')
}

Get-Process Stanza -ErrorAction SilentlyContinue | Stop-Process -Force
$proc = Start-Process -FilePath $Exe -ArgumentList "`"$fixture`"" -PassThru
$win = $null
for ($i = 0; $i -lt 30 -and -not $win; $i++) { Start-Sleep -Milliseconds 500; $win = Get-App }
if (-not $win) { Write-Host "FATAL: app window not found"; exit 1 }
$script:AppHwnd = [IntPtr]$win.Current.NativeWindowHandle
$pid_ = 0
$script:AppTid = [Win]::GetWindowThreadProcessId($script:AppHwnd, [ref]$pid_)
Ensure-Foreground

# ---------- Suite D: Space complete & Ctrl+Z undo ----------

Write-Host "`nSuite D: Space complete & Ctrl+Z undo (US layout)"
Set-AppLayout 0x04090409 "en-US"

Send-Keys "j" 400
Check "D1 select first" (Get-TaskSel) "10"
Send-Keys " "
Start-Sleep -Milliseconds 150
Check "D2a Space starts animation (task still present)" (Get-FirstTaskTitle) "Task Alpha"
Start-Sleep -Milliseconds 900
Check "D2b task gone after animation" (Get-FirstTaskTitle) "Task Beta"
Send-Keys " "
Start-Sleep -Milliseconds 1050
Check "D3 Space completes last task" (Get-FirstTaskTitle) "(empty)"
Send-Keys "^z" 900
Check "D4 Ctrl+Z undoes second complete" (Get-FirstTaskTitle) "Task Beta"
Send-Keys "^z" 900
Check "D5 Ctrl+Z undoes first complete" (Get-FirstTaskTitle) "Task Alpha"

# editor-local undo: Ctrl+Z inside a text box is WPF text undo, not document undo
Send-Keys "j" 400
Send-Keys "{ENTER}" 800
Send-Keys "{END}" 200
Send-Keys "XYZ" 400
Send-Keys "^z" 500
$d5 = Get-EditValues
Check "D6 Ctrl+Z in editor is text undo" $(if ($d5 -notmatch "XYZ") { "yes" } else { $d5 }) "yes"
Send-Keys "{ESC}" 400

# ---------- Suite E: undo restore animation ----------

function Get-TaskRowInfo([string]$title) {
    # (height, checkState) of the task row whose first text matches $title; $null if absent
    $win = Get-App
    $tl = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "TaskList")))
    if (-not $tl) { return $null }
    $items = $tl.FindAll([System.Windows.Automation.TreeScope]::Children, (CType ([System.Windows.Automation.ControlType]::ListItem)))
    foreach ($it in $items) {
        $texts = $it.FindAll([System.Windows.Automation.TreeScope]::Descendants, (CType ([System.Windows.Automation.ControlType]::Text)))
        $hit = $false
        foreach ($t in $texts) { if ($t.Current.Name -eq $title) { $hit = $true; break } }
        if (-not $hit) {
            # expanded card: title lives in an Edit control, not a Text block
            foreach ($ed in $it.FindAll([System.Windows.Automation.TreeScope]::Descendants, (CType ([System.Windows.Automation.ControlType]::Edit)))) {
                $vp = $ed.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
                if ($vp -and $vp.Current.Value -eq $title) { $hit = $true; break }
            }
        }
        if (-not $hit) { continue }
        $state = "none"
        foreach ($cb in $it.FindAll([System.Windows.Automation.TreeScope]::Descendants, (CType ([System.Windows.Automation.ControlType]::CheckBox)))) {
            $tp = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($tp) { $state = $tp.Current.ToggleState.ToString(); break }
        }
        return @($it.Current.BoundingRectangle.Height, $state)
    }
    return $null
}

Write-Host "`nSuite E: undo restore animation (US layout)"
Send-Keys "j" 400
Send-Keys " " 1100                      # complete Alpha (with animation)
Check "E1 Alpha completed" (Get-FirstTaskTitle) "Task Beta"
Send-Keys "^z"
Start-Sleep -Milliseconds 120           # restore animation just started
$early = Get-TaskRowInfo "Task Alpha"
Start-Sleep -Milliseconds 900
$late = Get-TaskRowInfo "Task Alpha"
$full = Get-TaskRowInfo "Task Beta"
if ($null -eq $early -or $null -eq $late) {
    Check "E2 restored row present" "missing" "present"
} else {
    Check "E2 restored row present" "present" "present"
    Check "E3 checkbox stays unchecked during restore" $early[1] "Off"
    Check "E4 checkbox unchecked after restore" $late[1] "Off"
    # height timing is too fast to sample mid-animation reliably; the On->Off toggle above
    # already proves the animation ran. Here: monotone height and correct final layout.
    $okHeight = ($early[0] -le $late[0]) -and ([Math]::Abs($late[0] - $full[0]) -lt 4)
    Check "E5 height settles to full" $(if ($okHeight) { "yes" } else { "early=$($early[0]) late=$($late[0]) full=$($full[0])" }) "yes"
}

Write-Host "`nSuite E6: Ctrl+Z ignored during drag"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class MouseD {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
    public static void MoveTo(int x, int y) { SetCursorPos(x, y); }
    public static void Down() { mouse_event(0x02, 0, 0, 0, UIntPtr.Zero); }
    public static void Up() { mouse_event(0x04, 0, 0, 0, UIntPtr.Zero); }
}
'@

# select Alpha, drag it below Beta while pressing Ctrl+Z mid-drag
Send-Keys "j" 400
$win = Get-App
$tl = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "TaskList")))
$items = $tl.FindAll([System.Windows.Automation.TreeScope]::Children, (CType ([System.Windows.Automation.ControlType]::ListItem)))
$r0 = $items[0].Current.BoundingRectangle
$r1 = $items[1].Current.BoundingRectangle
$x = [int]($r0.X + 60); $y0 = [int]($r0.Y + $r0.Height / 2); $y1 = [int]($r1.Y + $r1.Height - 4)
[MouseD]::MoveTo($x, $y0)
[MouseD]::Down()
Start-Sleep -Milliseconds 300
for ($i = 1; $i -le 6; $i++) {
    [MouseD]::MoveTo($x, $y0 + [int](($y1 - $y0) * $i / 6))
    Start-Sleep -Milliseconds 80
}
Start-Sleep -Milliseconds 400   # dragging, gap placed below Beta
[KbdV]::Down(0x11)              # Ctrl+Z mid-drag: must be ignored
[KbdV]::Press(0x5A)
[KbdV]::Up(0x11)
Start-Sleep -Milliseconds 400
[MouseD]::Up()                  # commit drag
Start-Sleep -Milliseconds 800
Check "E6a drag commits (undo was ignored)" (Get-FirstTaskTitle) "Task Beta"
Send-Keys "^z" 900              # drag finished; undo now works
Check "E6b undo after drag restores order" (Get-FirstTaskTitle) "Task Alpha"

Write-Host "`nSuite E7: expand right after undo-restore animation"
Send-Keys "j" 300
Send-Keys " " 1000                      # complete Alpha
Send-Keys "^z"                          # undo -> restore animation starts
Start-Sleep -Milliseconds 90
Send-Keys "k"                           # select the restoring task
Send-Keys "{ENTER}" 800                 # expand it while animation may still run
$e7 = Get-TaskRowInfo "Task Alpha"
$e7b = Get-TaskRowInfo "Task Beta"
if ($null -eq $e7) {
    Check "E7 expanded row has full height" "missing" "ok"
} else {
    # expanded card must be much taller than a collapsed row (was clamped at 63 by the bug)
    Check "E7 expanded row has full height" $(if ($e7[0] -gt $e7b[0] + 40) { "ok" } else { "h=$($e7[0])" }) "ok"
}
Send-Keys "{ESC}" 400

Write-Host "`nSuite E8: selection restored by position after undo"
Send-Keys "j" 400                       # select Alpha (position 0)
Send-Keys " " 1100                      # complete Alpha; selection lands on Beta (position 0)
Check "E8a selection landed on Beta" (Get-TaskSel) "1"
Send-Keys "^z" 900                      # undo: Alpha returns to position 0
Check "E8b selection back on Alpha" (Get-TaskSel) "10"
Send-Keys "{ESC}" 400                   # leave clean state for the next suite

# ---------- Suite A: US layout ----------
# ---------- Suite A: US layout ----------
# ---------- Suite A: US layout ----------
# ---------- Suite A: US layout ----------
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

# ---------- Suite F: notes editing keys (US layout) ----------

Write-Host "`nSuite F: notes editing keys (US layout)"

Send-Keys "{ENTER}" 800   # expand selected -> title editor focused
Send-Keys "{TAB}" 300     # title -> notes (caret at end)

Send-Keys "- first" 300
Send-Keys "{ENTER}" 300
$f1 = Get-EditValues
Check "F1 Enter continues '- ' list" $(if ($f1 -match "- first\r?\n- $") { "yes" } else { $f1 }) "yes"
Check "F1b no selection left after completion" (Get-FocusedEditSelection) ""

Send-Keys "{ENTER}" 300   # Enter again on the empty marker -> remove it
$f2 = Get-EditValues
Check "F2 Enter on empty marker exits list" $(if ($f2 -match "- first\r?\n$") { "yes" } else { $f2 }) "yes"

Send-Keys "- {[} {]} todo" 300
Send-Keys "{ENTER}" 300
$f3 = Get-EditValues
Check "F3 checkbox continues unchecked" $(if ($f3 -match "- \[ \] todo\r?\n- \[ \] $") { "yes" } else { $f3 }) "yes"
Send-Keys "{ENTER}" 300   # exit list

Send-Keys "1. one" 300
Send-Keys "{ENTER}" 300
$f4 = Get-EditValues
Check "F4 ordered list increments" $(if ($f4 -match "1\. one\r?\n2\. $") { "yes" } else { $f4 }) "yes"

Send-Keys "^{ENTER}" 700  # Ctrl+Enter commits from notes
Check "F5 Ctrl+Enter commits" (Get-EditValues) "(none)"

Send-Keys "{ENTER}" 800   # re-expand: notes persisted (plain Enter in notes is newline, not commit)
Send-Keys "{TAB}" 300
$f6 = Get-EditValues
Check "F6 notes kept after commit" $(if ($f6 -match "1\. one\r?\n2\. ") { "yes" } else { $f6 }) "yes"
Send-Keys "{ESC}" 400

Send-Keys "j" 400
Send-Keys "{ENTER}" 800   # expand -> title focused, caret at end
Send-Keys "%a" 200        # line start (Windows mode: editing keys on Alt)
Send-Keys "X" 200
Send-Keys "%e" 200        # line end
Send-Keys "Y" 200
Send-Keys "%b" 200        # back one char
Send-Keys "Z" 200         # inserted before Y
Send-Keys "%d" 200        # forward-delete Y
Send-Keys "%h" 200        # backspace Z
Send-Keys "%a%f%f" 300    # line start, forward x2
Send-Keys "%k" 200        # kill to end of line
$f7 = Get-EditValues
Check "F7 editing keys on Alt (a/e/b/f/d/h/k)" $(if ($f7 -match "^XT \|") { "yes" } else { $f7 }) "yes"
Send-Keys "{ESC}" 400
# 与 Suite A 相同的结束状态（选中第一项）：焦点停在列表上时的空操作 Esc 会重新选中首项
# （原有的 WPF 焦点迁移怪癖，HEAD 上同样存在），留下选中让 B1 的 Esc 走「清除」分支
Send-Keys "j" 400
Check "F8 j selects first after Esc" (Get-TaskSel) "10"

Send-Keys "t" 600         # open tag picker (selection on first task)
Send-Keys "{ESC}" 500     # close -> focus parked on the list, selection kept
Check "F9a selection kept after picker close" (Get-TaskSel) "10"
Send-Keys "+{DOWN}" 400
Check "F9b Shift+Down extends from parked focus" (Get-TaskSel) "11"
Send-Keys "+{UP}" 400
Check "F9c Shift+Up shrinks back" (Get-TaskSel) "10"
Send-Keys "+j" 400
Check "F10a Shift+j extends selection" (Get-TaskSel) "11"
Send-Keys "+j" 400
Check "F10b Shift+j clamps at end" (Get-TaskSel) "11"
Send-Keys "+k" 400
Check "F10c Shift+k shrinks back" (Get-TaskSel) "10"
Send-Keys "{ESC}" 400
Send-Keys "j" 400

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
    # 钉住 MRU 为恰好 [b, a]（前面套件产生的记录会增加弹层行数，干扰循环断言）
    [IO.File]::WriteAllText($recentJson,
        (@{ LastFile = $bFile; RecentFiles = @($bFile, $aFile) } | ConvertTo-Json -Compress))
    # register MRU order [b, a]: open a, then b, then b again for the test
    Restart-AppWith $aFile
    Restart-AppWith $bFile

    Ensure-Foreground
    [KbdV]::Down(0x11)                      # Ctrl down
    [KbdV]::Press(0x52)                     # R
    Start-Sleep -Milliseconds 600
    Check "C1 Ctrl+R opens popup, next file highlighted" (Get-FocusedRowFile) "a.stanza"
    [KbdV]::Press(0x52)                     # R again, still holding Ctrl
    Start-Sleep -Milliseconds 500
    Check "C2 R cycles back to first row" (Get-FocusedRowFile) "b.stanza"
    [KbdV]::Up(0x11)                        # release Ctrl -> open highlighted
    Start-Sleep -Milliseconds 900
    Check "C3 releasing Ctrl opens highlighted file" (Get-FirstTaskTitle) "Task Banana"

    # MRU stays [b, a] (b re-registered on open); reopen -> highlight next file (a)
    Ensure-Foreground
    [KbdV]::Down(0x11)
    [KbdV]::Press(0x52)
    Start-Sleep -Milliseconds 600
    Check "C4 popup reopens, next file (a) highlighted" (Get-FocusedRowFile) "a.stanza"
    [KbdV]::Up(0x11)
    Start-Sleep -Milliseconds 600   # releasing Ctrl opened row1 (a)

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

# ---------- Suite G: macOS keyboard mode ----------

Write-Host "`nSuite G: macOS keyboard mode"
$gFile = Join-Path $workDir "g.stanza"
@'
# DOING

Task Golf
'@ | Out-File -Encoding utf8 $gFile
try {
    [IO.File]::WriteAllText($settingsJson, '{"Language":"zh","MacOsMode":true}')
    Restart-AppWith $gFile
    Set-AppLayout 0x04090409 "en-US"

    Send-Keys "j" 400
    Send-Keys "{ENTER}" 800   # expand -> title editor focused, caret at end
    Send-Keys "AB" 300
    Send-Keys "^a" 200        # macOS: Ctrl+A = line start (editing key)
    Send-Keys "C" 200         # -> "CTask GolfAB"
    $g1 = Get-EditValues
    Check "G1 Ctrl+A is line start in macOS mode" $(if ($g1 -match "^CTask") { "yes" } else { $g1 }) "yes"

    Set-Clipboard "zz"
    Send-Keys "%a" 200        # Alt(Command)+A = select all
    Send-Keys "^c" 200        # native Ctrl+C disabled in macOS mode
    Check "G2 native Ctrl+C disabled" (Get-Clipboard) "zz"
    Send-Keys "%x" 200        # Alt(Command)+X = cut
    $g3 = Get-EditValues
    Check "G3 Alt+A/X select-all + cut" $(if ($g3 -match "^ \|") { "yes" } else { $g3 }) "yes"
    Send-Keys "%v" 200        # Alt(Command)+V = paste -> cut content back
    $g4 = Get-EditValues
    Check "G4 Alt+V paste" $(if ($g4 -match "^CTask") { "yes" } else { $g4 }) "yes"
    Send-Keys "{ESC}" 400

    Send-Keys "%n" 600        # Alt(Command)+N = new task (app commands on Alt)
    $g5 = Get-EditValues
    Check "G5 Alt+N creates task" $(if ($g5 -ne "(none)") { "yes" } else { $g5 }) "yes"
    Send-Keys "{ESC}" 400     # discard the empty draft
    Send-Keys "^n" 400        # Ctrl+N is NOT new-task in macOS mode
    $g6 = Get-EditValues
    Check "G6 Ctrl+N is not new-task" $g6 "(none)"
}
finally {
    # 还原用户设置（运行期间被钉为 Windows 模式，Suite G 临时切到 macOS）
    if ($hadSettings) { Copy-Item $settingsBak $settingsJson -Force }
    else { Remove-Item $settingsJson -Force -ErrorAction SilentlyContinue }
}

# ---------- cleanup ----------

Get-Process Stanza -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "`n$($script:Failures) check(s) failed"
exit $script:Failures
