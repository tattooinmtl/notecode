# Shared Win32 helpers for the GUI scripts.
#
# Dot-source it:   . 'C:\NexusOne\prototypes\notecode\scripts\winutil.ps1'
#
# It defines the Win32Shot class and two helpers:
#   Get-AppWindow   - the real top level window of a process
#   Set-AppForeground - puts that window in front, bypassing the foreground lock
#
# The .NET Process.MainWindowHandle property is not used on purpose. It caches a
# handle and its heuristic can land on a hidden helper window (a tooltip, a GDI+
# hook window) whose rect is 0x0, which is why the capture used to fail with
# "window has no area" on a perfectly healthy app.

if (-not ('Win32Shot' -as [type])) {
    Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Win32Shot {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);

    // The visible window of a process with the largest area. Windows without a
    // caption are skipped so that tooltips and GDI+ hook windows are never picked.
    public static IntPtr PickWindow(uint wantPid) {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((h, l) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid != wantPid) return true;
            if (!IsWindowVisible(h)) return true;
            if (GetWindowTextLength(h) == 0) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // The visible window of a process whose caption contains the wanted text. A
    // dialog such as "Add provider" is smaller than the main window, so the
    // largest-window rule would never pick it.
    public static IntPtr PickWindowByTitle(uint wantPid, string wanted) {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((h, l) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid != wantPid) return true;
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            if (sb.ToString().IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // Every visible captioned window of a process, largest first.
    public static List<IntPtr> Windows(uint wantPid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == wantPid && IsWindowVisible(h) && GetWindowTextLength(h) > 0) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static bool RectOf(IntPtr hWnd, out RECT r) { return GetWindowRect(hWnd, out r); }

    // The thread that owns the foreground window, for the AttachThreadInput dance.
    public static uint ForegroundThreadId() {
        uint pid;
        return GetWindowThreadProcessId(GetForegroundWindow(), out pid);
    }
}
"@
}

# --- typing into the app -----------------------------------------------------
#
# SendKeys only reaches the foreground window, and Windows will not hand the
# foreground to a background process. On this machine the foreground is a system
# service window (GameInputServiceWindow), so SetForegroundWindow is refused,
# SwitchToThisWindow is ignored, and every keystroke went nowhere. The keys are
# therefore posted straight to the control's own window, which needs no foreground.

if (-not ('PostKey' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class PostKey {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static void Char(IntPtr h, char c) {
        PostMessage(h, 0x0102, (IntPtr)(ushort)c, IntPtr.Zero);          // WM_CHAR
    }
    public static void Key(IntPtr h, int vk) {
        PostMessage(h, 0x0100, (IntPtr)vk, IntPtr.Zero);                  // WM_KEYDOWN
        PostMessage(h, 0x0101, (IntPtr)vk, IntPtr.Zero);                  // WM_KEYUP
    }
    // BM_CLICK, posted rather than sent. InvokePattern.Invoke blocks until the
    // action finishes, and a button that opens a modal dialog never finishes
    // until the dialog is closed - so the test would wait for itself.
    public static void Click(IntPtr h) {
        PostMessage(h, 0x00F5, IntPtr.Zero, IntPtr.Zero);                 // BM_CLICK
    }
    // A raw mouse click at a point in the control's own coordinates. The cross on
    // a tab is painted rather than a control, so there is nothing to invoke.
    public static void Post(IntPtr h, uint msg, IntPtr wp, IntPtr lp) {
        PostMessage(h, msg, wp, lp);
    }
    // A real click on a control: press and release at a point in its own
    // coordinates. BM_CLICK only reaches a real Button, so anything else - a
    // custom control, a drawn hit target - is clicked this way.
    public static void MouseClick(IntPtr h, int x, int y) {
        IntPtr lp = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        PostMessage(h, 0x0201, (IntPtr)1, lp);                              // WM_LBUTTONDOWN
        PostMessage(h, 0x0202, IntPtr.Zero, lp);                            // WM_LBUTTONUP
    }
    // The capture reads the screen, so the window has to be on top of it. Being
    // topmost does not need the foreground.
    public static void TopMost(IntPtr h) {
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
    }
    public static void NotTopMost(IntPtr h) {
        SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
    }
}
"@
}

# Virtual key codes, so the call sites read as keys and not as numbers.
$script:VK_RETURN = 0x0D
$script:VK_TAB    = 0x09
$script:VK_ESCAPE = 0x1B
$script:VK_UP     = 0x26
$script:VK_DOWN   = 0x28
$script:VK_LEFT   = 0x25
$script:VK_RIGHT  = 0x27

function Get-ControlHandle {
    <#
      .SYNOPSIS
        The window handle of an automation element, so keys can be posted to it.
    #>
    param($Element)
    return [IntPtr]$Element.Current.NativeWindowHandle
}

function Type-IntoControl {
    <#
      .SYNOPSIS
        Types text into a control, one character at a time.
    #>
    param(
        [Parameter(Mandatory = $true)][IntPtr]$Hwnd,
        [Parameter(Mandatory = $true)][string]$Text,
        [int]$DelayMs = 25
    )

    foreach ($c in $Text.ToCharArray()) {
        [PostKey]::Char($Hwnd, $c)
        if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }
    }
}

function Click-Control {
    <#
      .SYNOPSIS
        Clicks a control without waiting for it to finish.
      .NOTES
        InvokePattern.Invoke blocks until the action is done, and a button that
        opens a modal dialog is never done until the dialog closes. The click is
        posted instead, so the caller carries on.
    #>
    param([Parameter(Mandatory = $true)][IntPtr]$Hwnd)

    [PostKey]::Click($Hwnd)
    Start-Sleep -Milliseconds 300
}

function Send-KeyToControl {    <#
      .SYNOPSIS
        Presses a key in a control.
      .NOTES
        No WM_CHAR is posted. The prompt handles Enter in its KeyDown event and
        asks for the key press to be suppressed, so posting the character as well
        would insert the line break that the app deliberately avoids.
    #>
    param(
        [Parameter(Mandatory = $true)][IntPtr]$Hwnd,
        [Parameter(Mandatory = $true)][int]$VirtualKey
    )

    [PostKey]::Key($Hwnd, $VirtualKey)
    Start-Sleep -Milliseconds 150
}

function Get-AppWindow {
    <#
      .SYNOPSIS
        The top level window of the newest instance of a process.
      .PARAMETER ProcessName
        The process name, for example notecode.
      .PARAMETER Title
        Optional caption fragment. A dialog such as "Add provider" is smaller than
        the main window, so the largest-window rule would never pick it.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ProcessName,
        [string]$Title = ""
    )

    $procs = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
            Sort-Object StartTime -Descending
    if (-not $procs) { return $null }

    foreach ($p in $procs) {
        if ($Title) {
            $h = [Win32Shot]::PickWindowByTitle([uint32]$p.Id, $Title)
            if ($h -ne [IntPtr]::Zero) { return $h }
            continue
        }
        $h = [Win32Shot]::PickWindow([uint32]$p.Id)
        if ($h -ne [IntPtr]::Zero) { return $h }
    }
    return $null
}

function Set-AppForeground {
    <#
      .SYNOPSIS
        Brings a window to the front. Windows refuses SetForegroundWindow from a
        background process, so the foreground thread is attached for the call.
    #>
    param([Parameter(Mandatory = $true)][IntPtr]$Hwnd)

    $fg = [Win32Shot]::ForegroundThreadId()
    $me = [Win32Shot]::GetCurrentThreadId()
    if ($fg -ne $me) { [void][Win32Shot]::AttachThreadInput($me, $fg, $true) }
    [void][Win32Shot]::SetForegroundWindow($Hwnd)
    [void][Win32Shot]::BringWindowToTop($Hwnd)
    if ($fg -ne $me) { [void][Win32Shot]::AttachThreadInput($me, $fg, $false) }
}

# --- a profile of the test's own ------------------------------------------------
#
# notecode keeps the user's data - the conversations, the saved hardware
# profiles - under %APPDATA%\notecode, and it puts the last conversation back
# on screen when it starts.
#
# A test that shares that profile therefore reads the previous run's
# conversation as if it were its own: a word that a check looks for in the
# transcript can be left over from a run that finished an hour ago, and the
# check passes without proving anything. It also leaves its own conversation
# behind for the next test, and for the user to find later.
#
# NOTECODE_DATA_DIR moves the folder the app keeps its data in. It is set here,
# once, and every script that loads this file gets a profile of its own - so
# the transcript a test reads is the one that test caused, and the user's own
# sessions are not written over.
$script:TestProfile = Join-Path ([System.IO.Path]::GetTempPath()) `
    ("notecode-test-profile-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $script:TestProfile -Force | Out-Null
# Set once per process. screenshot.ps1 loads this file too, and a script that
# loads it twice must not end up with two profiles: the app is launched with
# the first one, and a second launch would use the other.
if (-not $env:NOTECODE_DATA_DIR) { $env:NOTECODE_DATA_DIR = $script:TestProfile }

function Get-AppDataDir {
    <#
      .SYNOPSIS
        The folder the app keeps the user's data in.
      .NOTES
        It is NOTECODE_DATA_DIR when that is set - the tests set it, so the
        app writes to a profile of the test's own - and %APPDATA%\notecode
        otherwise. A test that wants to read what the app wrote has to look
        where the app wrote it.
    #>
    if ($env:NOTECODE_DATA_DIR) { return $env:NOTECODE_DATA_DIR }
    return (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'notecode')
}

function New-TestSession {
    <#
      .SYNOPSIS
        Writes a session into the app's data folder, so a test has one to work with.
      .NOTES
        The session window lists what is on disk. A test that starts from an
        empty profile has nothing to list; a test that starts from the user's
        profile lists whatever the user happened to have, which is not a
        fixture and is not the same from one run to the next.

        The file is the app's own format: one file per session, named after
        the session's id.
    #>
    param(
        [string]$Title = 'a session for the test',
        [string]$Workspace = ''
    )

    $dir = Join-Path (Get-AppDataDir) 'sessions'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null

    $id = 'test-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $now = [DateTime]::UtcNow.ToString('o')
    $session = [ordered]@{
        Id         = $id
        Title      = $Title
        CreatedUtc = $now
        UpdatedUtc = $now
        Pinned     = $false
        Workspace  = $Workspace
        Messages   = @(
            [ordered]@{ Role = 'user';      Text = $Title;                          AtUtc = $now }
            [ordered]@{ Role = 'assistant'; Text = 'this session was written by a test'; AtUtc = $now }
        )
    }

    $path = Join-Path $dir ($id + '.json')
    Set-Content -Path $path -Value ($session | ConvertTo-Json -Depth 6 -Compress) -Encoding utf8
    return $id
}

# --- UI Automation -----------------------------------------------------------
#
# The scripts drive the app through UI Automation rather than through the mouse,
# so the checks do not depend on where a window happens to sit on the screen.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

function Get-AppAutomationRoot {
    <#
      .SYNOPSIS
        The top level automation element of a process.
      .NOTES
        The first window of the process is not always the one the test wants.
        notecode opens a splash screen before the editor, and that splash is a
        top level window of the same process. It carries its own caption so it
        can be told apart, and it is skipped here: a test that asked for the
        app and got the splash would find no prompt box.
    #>
    param([Parameter(Mandatory = $true)][int]$ProcessId)

    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)

    try {
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $cond)
    } catch {
        # The tree can hold an element for a window that is being closed right
        # now, and asking for it then throws. The caller's loop asks again.
        return $null
    }

    foreach ($w in $windows) {
        try {
            if ($w.Current.Name -like '*hardware scan*') { continue }
            return $w
        } catch { }
    }

    # Only the splash is up. Returning nothing is the honest answer: a caller that
    # waits for the app keeps waiting, instead of being handed the splash and
    # failing to find a prompt box in it.
    return $null
}

function Find-ByName {
    <#
      .SYNOPSIS
        The first descendant with a given name, optionally of a given control type.
    #>
    param(
        $Root,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Type = ''
    )

    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    if ($Type) {
        $tc = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::$Type)
        $both = New-Object System.Windows.Automation.AndCondition($cond, $tc)
        return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $both)
    }
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-TextWith {
    <#
      .SYNOPSIS
        The first text element under a window whose text contains a string.
      .NOTES
        The tree under a window is rebuilt whenever the window repaints, and an
        element from the tree that was there a moment ago throws "Unrecognized
        error" when it is touched. A stale tree is a normal event in a window
        that is busy drawing, not a failure of the app, so the window is looked
        up again and the search is done once more.

        The text is matched without a wildcard pattern on purpose. The
        transcript holds backticks and asterisks, and in a -like pattern both
        mean something other than themselves: a backtick is the escape
        character and an asterisk matches anything.
    #>
    param(
        $Window,
        [int]$ProcessId = 0,
        [Parameter(Mandatory = $true)][string]$Needle
    )

    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $root = $Window
        if ($attempt -gt 0 -and $ProcessId -gt 0) {
            $root = Get-AppAutomationRoot -ProcessId $ProcessId
            if (-not $root) { return $null }
        }

        try {
            # Every descendant, not only the text ones.
            #
            # The transcript is a web surface, and a line that it rendered as
            # a list item carries its words in the list item's name rather
            # than in a text element - the version line, for one. A search
            # that only looked at text elements would not find it, and would
            # report that the app had not printed it.
            $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
        } catch {
            # The tree was rebuilt while the search was running. Ask again with
            # a window that is looked up now.
            continue
        }

        foreach ($t in $all) {
            try {
                $name = $t.Current.Name
                if ($name -and $name.IndexOf($Needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { return $t }
            } catch {
                # This element belongs to the tree that was just replaced. The
                # next one may not.
            }
        }
        return $null
    }

    return $null
}

function Get-PromptBox {
    <#
      .SYNOPSIS
        The AI prompt text box of a notecode window.
      .NOTES
        The element is stored in $script:PromptBox rather than returned: returning
        an automation element from a function makes PowerShell unroll it, and the
        caller ends up holding an enumerator instead of the element.
    #>
    param($Window)

    $send = Find-ByName $Window 'Send' 'Button'
    if (-not $send) { throw "the Send button was not found" }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $panel = $walker.GetParent($send)
    $editCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edits = $panel.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
    if ($edits.Count -eq 0) { throw "no AI input box found" }
    $script:PromptBox = $edits[0]
}

function Set-Prompt {
    <#
      .SYNOPSIS
        Types into the AI prompt.
      .NOTES
        The value is not set directly: that leaves the caret at the start, and the
        palette follows the caret, so the test would prove nothing. Typing the way
        a person does is what the test is for.
    #>
    param($PromptBox, [Parameter(Mandatory = $true)][string]$Text)

    $PromptBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
    Start-Sleep -Milliseconds 200
    Type-IntoControl -Hwnd (Get-ControlHandle $PromptBox) -Text $Text
    Start-Sleep -Milliseconds 350
}

function Get-Prompt {
    param($PromptBox)
    return $PromptBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}

function Get-NotecodeConfig {
    <#
      .SYNOPSIS
        Reads runtime\notecode.config.json with %VARIABLE% expanded.
      .NOTES
        A path to something in the user's own folders is written with
        %USERPROFILE% in it, so the same file works on every machine. The app,
        the wrapper and the MCP server all expand it; a test that reads the
        file has to do the same or it will look for a path that does not exist.
    #>
    param(
        [string]$Config = 'C:\NexusOne\prototypes\notecode\runtime\notecode.config.json'
    )

    $raw = Get-Content $Config -Raw
    # The value goes back into the JSON as a JSON string, so its backslashes
    # are escaped on the way in. Putting the raw value in would break the
    # file: "C:\Users" is not a valid JSON string.
    $expanded = [regex]::Replace($raw, '%([A-Za-z_][A-Za-z0-9_]*)%', {
        param($m)
        $v = [Environment]::GetEnvironmentVariable($m.Groups[1].Value)
        if ($null -eq $v) { return $m.Value }
        return ($v | ConvertTo-Json -Compress).Trim('"')
    })
    return $expanded | ConvertFrom-Json
}

function Use-LocalProvider {
    <#
      .SYNOPSIS
        Puts the local model in charge, whatever a previous test left behind.
      .NOTES
        A test that points the app at a mock provider and then fails to take it
        away leaves the next test talking to a provider that is not running. The
        failure then looks like a product bug, and it is not one. Every test
        that talks to the model starts by saying which provider it wants.
    #>
    param(
        [string]$Config = 'C:\NexusOne\prototypes\notecode\runtime\notecode.config.json',
        [string]$Name = 'local'
    )

    $probe = 'C:\NexusOne\prototypes\notecode\src\ToolProbe\ToolProbe.csproj'
    & dotnet run --project $probe -c Release --no-build -- --setactive $Config $Name | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "the provider could not be set to '$Name'"
    }
}
