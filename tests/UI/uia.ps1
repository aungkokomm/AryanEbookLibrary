# Shared helpers for off-screen checks of the scratch app: UI Automation, PrintWindow captures, posted keys.
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class W7 {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    public static string Class(IntPtr h) { var s = new System.Text.StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO {
        public int cbSize; public int flags; public IntPtr hwndActive; public IntPtr hwndFocus; public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner; public IntPtr hwndMoveSize; public IntPtr hwndCaret; public RECT rcCaret; }
    public static System.Collections.Generic.List<IntPtr> Windows(uint pid) {
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    public static System.Collections.Generic.List<IntPtr> AllWindows() {
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((h, l) => { if (IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    public static uint Owner(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
    public static string Text(IntPtr h) { var s = new System.Text.StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
}
'@
[void][W7]::SetThreadDpiAwarenessContext([IntPtr](-4))
$script:VS = [System.Windows.Forms.SystemInformation]::VirtualScreen

function Park([IntPtr]$h, [int]$w, [int]$ht) {
    [void][W7]::ShowWindow($h, 9)   # SW_RESTORE, out of maximized
    [void][W7]::SetWindowPos($h, [IntPtr]::Zero, $script:VS.Left - 2600, 0, $w, $ht, 0x0014)
}

function Capture([IntPtr]$h, [string]$path) {
    $r = New-Object W7+RECT
    [void][W7]::GetWindowRect($h, [ref]$r)
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc(); [void][W7]::PrintWindow($h, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($path); $bmp.Dispose()
    Write-Host "captured $(Split-Path $path -Leaf)"
}

function Find([IntPtr]$h, [string]$name, [string]$type = $null) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($h)
    $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)
    if ($type) {
        $c = New-Object Windows.Automation.AndCondition($c,
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::$type)))
    }
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
}

function Act([IntPtr]$h, [string]$name, [string]$type = $null) {
    $el = Find $h $name $type
    if ($null -eq $el) { Write-Host "not found: $name"; return $false }
    $pat = $null
    if ($el.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); Write-Host "invoked $name"; return $true }
    if ($el.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); Write-Host "selected $name"; return $true }
    if ($el.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) { $pat.Toggle(); Write-Host "toggled $name"; return $true }
    if ($el.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pat)) { $pat.Expand(); Write-Host "expanded $name"; return $true }
    Write-Host "no pattern on: $name"; return $false
}

function SetText([IntPtr]$h, [string]$name, [string]$text) {
    $el = Find $h $name
    if ($null -eq $el) { Write-Host "not found: $name"; return }
    $pat = $null
    if (-not $el.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pat)) {
        # an AutoSuggestBox: its text box is inside it
        $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::IsValuePatternAvailableProperty, $true)
        $inner = $el.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
        if ($null -eq $inner) { Write-Host "no value pattern: $name"; return }
        $pat = $inner.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    }
    $pat.SetValue($text); Write-Host "typed into $name"
}

function Focus([IntPtr]$h, [string]$name, [string]$type = $null) {
    $el = Find $h $name $type
    if ($null -eq $el) { Write-Host "not found: $name"; return $false }
    $el.SetFocus(); Write-Host "focused $name"; return $true
}

function Key([IntPtr]$h, [int]$vk) {
    $pid2 = 0
    $tid = [W7]::GetWindowThreadProcessId($h, [ref]$pid2)
    $info = New-Object W7+GUITHREADINFO
    $info.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($info)
    [void][W7]::GetGUIThreadInfo($tid, [ref]$info)
    $target = if ($info.hwndFocus -ne [IntPtr]::Zero) { $info.hwndFocus } else { $h }
    [void][W7]::PostMessage($target, 0x0100, [IntPtr]$vk, [IntPtr]1)
    [void][W7]::PostMessage($target, 0x0101, [IntPtr]$vk, [IntPtr]([int]0xC0000001))
}

function Scroll([IntPtr]$h, [double]$percent) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($h)
    $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)
    foreach ($el in $root.FindAll([Windows.Automation.TreeScope]::Descendants, $c)) {
        $pat = $el.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
        if ($pat.Current.VerticallyScrollable) { $pat.SetScrollPercent(-1, $percent); Write-Host "scrolled to $percent"; return }
    }
    Write-Host "no scrollable pane"
}

function NewWindow([int]$procId, [IntPtr[]]$known, [string]$title, [int]$waitMs = 8000) {
    for ($t = 0; $t -lt $waitMs; $t += 200) {
        foreach ($w in [W7]::Windows($procId)) { if ($known -notcontains $w -and [W7]::Text($w) -eq $title) { return $w } }
        Start-Sleep -Milliseconds 200
    }
    return [IntPtr]::Zero
}
