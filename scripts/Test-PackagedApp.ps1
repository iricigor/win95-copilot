#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ExecutablePath,
    [Parameter(Mandatory)] [string] $FakeCliPath,
    [Parameter(Mandatory)] [string] $EvidenceDirectory,
    [switch] $RunDesktopTests,
    [switch] $RunRealTests,
    [string] $CopilotPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $RunDesktopTests) { throw 'Owned-window desktop tests require explicit -RunDesktopTests authorization.' }
if ($env:OS -ne 'Windows_NT' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run with Windows PowerShell 5.1 powershell.exe -STA on Windows.'
}
foreach ($path in @($ExecutablePath, $FakeCliPath, $EvidenceDirectory)) {
    if (-not [IO.Path]::IsPathRooted($path)) { throw 'All paths must be absolute.' }
}
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Select a new evidence directory; existing evidence is never overwritten.' }
$exe = (Get-Item -LiteralPath $ExecutablePath).FullName
$fake = (Get-Item -LiteralPath $FakeCliPath).FullName
if ($RunRealTests -and ([string]::IsNullOrWhiteSpace($CopilotPath) -or -not [IO.Path]::IsPathRooted($CopilotPath))) {
    throw 'Real packaged tests additionally require an absolute verified CopilotPath.'
}
[IO.Directory]::CreateDirectory($EvidenceDirectory) | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
public static class OwnedDesktop {
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder name, int length);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, string text);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] input, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public UNION u; }
    [StructLayout(LayoutKind.Explicit)] struct UNION {
        [FieldOffset(0)] public KEYBOARD k;
        [FieldOffset(0)] public MOUSE m;
    }
    [StructLayout(LayoutKind.Sequential)] struct KEYBOARD { public ushort vk, scan; public uint flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSE { public int x,y; public uint data,flags,time; public UIntPtr extra; }
    public static void Check(IntPtr h, int expected) {
        uint actual; GetWindowThreadProcessId(h, out actual);
        if (actual != expected || actual == 0) throw new InvalidOperationException("Window ownership changed.");
    }
    public static string ProbeDesktop() {
        // A non-input desktop is never switched into: UIA/keyboard input targets the current desktop.
        IntPtr current = GetThreadDesktop(GetCurrentThreadId());
        IntPtr isolated = CreateDesktop("WinChatProbe-"+Guid.NewGuid().ToString("N"), IntPtr.Zero, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
        if (isolated == IntPtr.Zero) return "CreateDesktop unavailable, error "+Marshal.GetLastWin32Error()+". Using authorized owned windows on the current desktop.";
        if (!CloseDesktop(isolated)) throw new Win32Exception();
        return "Isolated desktop created/closed without switching. It is not the input desktop and cannot exercise foreground keyboard/popups through this UIA harness; using authorized owned windows on the current desktop. Current desktop handle "+current+".";
    }
    public static void Keys(IntPtr h, int pid, ushort[] codes) {
        Check(h,pid);
        for (int attempt=0;attempt<20;attempt++) {
            uint foregroundPid; GetWindowThreadProcessId(GetForegroundWindow(),out foregroundPid);
            if (foregroundPid==pid) break;
            SetForegroundWindow(h);
            System.Threading.Thread.Sleep(50);
        }
        uint foreground; GetWindowThreadProcessId(GetForegroundWindow(),out foreground);
        if (foreground!=pid) throw new InvalidOperationException("Owned foreground input is unavailable; no keys were sent.");
        INPUT[] input = new INPUT[codes.Length*2];
        for (int i=0;i<codes.Length;i++) {
            input[i].type=1; input[i].u.k.vk=codes[i];
            input[input.Length-1-i].type=1; input[input.Length-1-i].u.k.vk=codes[i]; input[input.Length-1-i].u.k.flags=2;
        }
        Check(GetForegroundWindow(),pid);
        if (SendInput((uint)input.Length,input,Marshal.SizeOf(typeof(INPUT))) != input.Length) throw new Win32Exception();
    }
    public static void Resize(IntPtr h, int pid, int w, int ht) {
        Check(h,pid);
        if (!SetWindowPos(h,IntPtr.Zero,0,0,w,ht,0x0002|0x0004)) throw new Win32Exception();
    }
    public static void SystemClose(IntPtr h, int pid) {
        Check(h,pid);
        if (!PostMessage(h,0x0112,new IntPtr(0xF060),IntPtr.Zero)) throw new Win32Exception();
    }
    public static void CaptionDoubleClick(IntPtr h, int pid) {
        Check(h,pid);
        if (!PostMessage(h,0x00A3,new IntPtr(2),IntPtr.Zero)) throw new Win32Exception();
    }
    public static void SystemBoxDoubleClick(IntPtr h, int pid, int screenX, int screenY) {
        Check(h,pid);
        POINT point=new POINT { X=screenX,Y=screenY };
        Check(WindowFromPoint(point),pid);
        if(!SetCursorPos(screenX,screenY)) throw new Win32Exception();
        System.Threading.Thread.Sleep(50);
        Check(h,pid); Check(WindowFromPoint(point),pid);
        INPUT[] clicks=new INPUT[4];
        for(int i=0;i<clicks.Length;i++) { clicks[i].type=0; clicks[i].u.m.flags=i%2==0?2u:4u; }
        if(SendInput(4,clicks,Marshal.SizeOf(typeof(INPUT)))!=4) throw new Win32Exception();
    }
    public static void SetEdit(IntPtr h, int pid, string value) {
        Check(h,pid);
        var name=new System.Text.StringBuilder(256);
        GetClassName(h,name,name.Capacity);
        if (name.ToString()!="Edit") throw new InvalidOperationException("Native fallback requires a verified owned Edit control, not "+name+".");
        if (SendMessage(h,0x000C,IntPtr.Zero,value)==IntPtr.Zero) throw new InvalidOperationException("Owned native Edit rejected WM_SETTEXT.");
    }
    public static void ClickButton(IntPtr h, int pid) {
        Check(h,pid);
        var name=new System.Text.StringBuilder(256);
        GetClassName(h,name,name.Capacity);
        if (name.ToString()!="Button") throw new InvalidOperationException("Native click requires a verified owned Button control.");
        if (!PostMessage(h,0x00F5,IntPtr.Zero,IntPtr.Zero)) throw new Win32Exception();
    }
    public static int[] Capture(IntPtr h, int pid, string path) {
        Check(h,pid); RECT r;
        if (!GetWindowRect(h,out r)) throw new Win32Exception();
        int navy=0,white=0;
        using(Bitmap b=new Bitmap(r.R-r.L,r.B-r.T))
        using(Graphics g=Graphics.FromImage(b)) {
            IntPtr dc=g.GetHdc();
            try { if(!PrintWindow(h,dc,2)) throw new Win32Exception(); } finally { g.ReleaseHdc(dc); }
            for(int y=5;y<Math.Min(30,b.Height);y++) for(int x=30;x<b.Width-50;x++) {
                Color c=b.GetPixel(x,y);
                if(c.R==0 && c.G==0 && c.B==128) navy++;
                if(c.R==255 && c.G==255 && c.B==255) white++;
            }
            b.Save(path,System.Drawing.Imaging.ImageFormat.Png);
        }
        return new int[]{navy,white,r.R-r.L,r.B-r.T};
    }
}
'@

$results = New-Object 'System.Collections.Generic.List[object]'
$memory = New-Object 'System.Collections.Generic.List[object]'
$owned = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
$oldData = $env:WINCHAT_DATA_DIRECTORY
$oldPath = $env:PATH
$script:process = $null
$script:main = $null
$script:data = $null
$outcome = 'Failed'
$failure = $null
$failureAt = $null
$desktop = [OwnedDesktop]::ProbeDesktop()

function Assert-True($Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}
function Wait-For([scriptblock] $Check, [string] $Description, [int] $Seconds = 30) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Check) { return }
        Start-Sleep -Milliseconds 50
    } while ($watch.Elapsed.TotalSeconds -lt $Seconds)
    throw "Timed out: $Description"
}
function Assert-Owned($Element) {
    Assert-True ($null -ne $Element) 'Required owned UI Automation element was not found.'
    Assert-True ($Element.Current.ProcessId -eq $script:process.Id) 'UI Automation element is not owned by the test application.'
    Assert-True (-not $script:process.HasExited) 'Test application exited unexpectedly.'
}
function Find-Element($Parent, $Property, [string] $Value) {
    Assert-Owned $Parent
    $condition = New-Object Windows.Automation.PropertyCondition($Property, $Value)
    $element = $Parent.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -ne $element) { Assert-Owned $element }
    return $element
}
function By-Id([string] $Id, $Parent = $script:main) {
    return Find-Element $Parent ([Windows.Automation.AutomationElement]::AutomationIdProperty) $Id
}
function By-Name([string] $Name, $Parent = $script:main) {
    return Find-Element $Parent ([Windows.Automation.AutomationElement]::NameProperty) $Name
}
function Owned-Window([string] $Name) {
    $condition = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:process.Id)),
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $Name)))
    $element = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -ne $element) { Assert-Owned $element }
    return $element
}
function Invoke-Element($Element) {
    Assert-Owned $Element
    Assert-True $Element.Current.IsEnabled ("Disabled action: " + $Element.Current.Name)
    $pattern=$null
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) {
        $pattern.Invoke()
    } else {
        [OwnedDesktop]::ClickButton([IntPtr]$Element.Current.NativeWindowHandle,$script:process.Id)
    }
}
function Set-Value($Element, [string] $Value) {
    Assert-Owned $Element
    $pattern=$null
    if ($Element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern,[ref]$pattern)) {
        $pattern.SetValue($Value)
    } else {
        [OwnedDesktop]::SetEdit([IntPtr]$Element.Current.NativeWindowHandle,$script:process.Id,$Value)
    }
}
function Get-Value($Element) {
    Assert-Owned $Element
    return $Element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
}
function Keys([System.UInt16[]] $Codes, $Window = $script:main) {
    Assert-Owned $Window
    [OwnedDesktop]::Keys([IntPtr]$Window.Current.NativeWindowHandle, $script:process.Id, $Codes)
    Start-Sleep -Milliseconds 100
}
function Menu([string] $Top, [string] $Action) {
    $item = By-Name $Top
    Assert-Owned $item
    $item.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Wait-For { $null -ne (By-Name $Action) } "menu action $Action"
    Invoke-Element (By-Name $Action)
    Start-Sleep -Milliseconds 100
}
function Status { return (By-Id 'BackendStatus').Current.Name }
function Transcript {
    $element = By-Id 'Transcript'
    Assert-Owned $element
    return $element.GetCurrentPattern([Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(-1)
}
function Record([string] $Name) { $results.Add([pscustomobject]@{ Check = $Name; Outcome = 'Passed'; Utc = [DateTime]::UtcNow.ToString('o') }) }
function Scenario([string] $Name) {
    $work = Join-Path $script:data 'work'
    [IO.File]::WriteAllText((Join-Path $work 'chat-scenario'), $Name)
    foreach ($name in @('chat.continue','chat.first','chat.empty','chat.started','child.pid')) {
        $path = Join-Path $work $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
}
function Start-App([string] $Name, [bool] $AutoConnect = $false, [string] $BackendPath = $fake, [switch] $Reuse, [switch] $RealService) {
    $script:data = Join-Path $EvidenceDirectory $Name
    if (-not $Reuse) {
        [IO.Directory]::CreateDirectory((Join-Path $script:data 'work')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $script:data 'settings.json'), (@{
            ExecutablePath = $BackendPath; SendMode = 'ControlEnter'; WordWrap = $false; AutoConnect = $AutoConnect; ShowTimestamp = $false
        } | ConvertTo-Json))
    }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.WorkingDirectory = Split-Path $exe
    $info.UseShellExecute = $false
    $info.EnvironmentVariables['WINCHAT_DATA_DIRECTORY'] = $script:data
    foreach ($key in @($info.EnvironmentVariables.Keys)) {
        if (-not $RealService -and ($key -like 'COPILOT_*' -or $key -like 'OTEL_*' -or $key -in @('GH_TOKEN','GITHUB_TOKEN'))) {
            $info.EnvironmentVariables.Remove($key)
        }
    }
    $script:process = [Diagnostics.Process]::Start($info)
    $owned.Add($script:process)
    Wait-For { $script:process.Refresh(); $script:process.MainWindowHandle -ne [IntPtr]::Zero } 'published application main window'
    $script:main = [Windows.Automation.AutomationElement]::FromHandle($script:process.MainWindowHandle)
    Assert-Owned $script:main
    Wait-For { $null -ne (By-Id 'PromptInput') } 'published controls'
    $script:main.SetFocus()
    Start-Sleep -Milliseconds 100
}
function Close-App([string] $Mode = 'File') {
    switch ($Mode) {
        'File' { Menu 'File' 'Exit' }
        'AltF4' { Keys @(0x12,0x73) }
        'System' { Keys @(0x12,0x20); Wait-For { $null -ne (By-Name 'Close') } 'system Close'; Invoke-Element (By-Name 'Close') }
        'Message' { [OwnedDesktop]::SystemClose($script:process.MainWindowHandle,$script:process.Id) }
        'SystemBoxDoubleClick' {
            $button=By-Id 'SystemMenuButton'
            Assert-Owned $button
            $bounds=$button.Current.BoundingRectangle
            [OwnedDesktop]::SystemBoxDoubleClick($script:process.MainWindowHandle,$script:process.Id,
                [int]($bounds.Left+$bounds.Width/2),[int]($bounds.Top+$bounds.Height/2))
        }
    }
    Assert-True ($script:process.WaitForExit(10000)) "$Mode close did not settle within ten seconds."
    Assert-True ($script:process.ExitCode -eq 0) "$Mode close returned an error."
    Record "$Mode shutdown"
}
function Measure-Memory([string] $Workload) {
    $script:process.Refresh()
    $memory.Add([pscustomobject]@{
        Workload=$Workload; Utc=[DateTime]::UtcNow.ToString('o'); Kind='UI process only'; ProcessId=$script:process.Id
        WorkingSetBytes=$script:process.WorkingSet64; PrivateBytes=$script:process.PrivateMemorySize64
    })
    $pidFile=Join-Path $script:data 'work\chat.started'
    if (Test-Path -LiteralPath $pidFile) {
        $backendId=[int][IO.File]::ReadAllText($pidFile)
        $backend=Get-Process -Id $backendId -ErrorAction SilentlyContinue
        if ($null -ne $backend) {
            Assert-True ($backend.Path -eq $fake) 'Synthetic backend identity does not match the configured executable.'
            $memory.Add([pscustomobject]@{
                Workload=$Workload; Utc=[DateTime]::UtcNow.ToString('o'); Kind='Synthetic CLI process, not real Copilot'; ProcessId=$backendId
                WorkingSetBytes=$backend.WorkingSet64; PrivateBytes=$backend.PrivateMemorySize64
            })
        }
    }
}
function Requests {
    return @(Get-Content -LiteralPath (Join-Path $script:data 'work\chat.requests.jsonl') | ForEach-Object { ,($_ | ConvertFrom-Json) })
}
function Submit([string] $Text, [string] $Mode = 'Button') {
    Set-Value (By-Id 'PromptInput') $Text
    Wait-For { (By-Id 'SendButton').Current.IsEnabled } 'SEND enabled'
    if ($Mode -eq 'Button') { Invoke-Element (By-Id 'SendButton') }
    else { (By-Id 'PromptInput').SetFocus(); if ($Mode -eq 'CtrlEnter') { Keys @(0x11,0x0D) } else { Keys @(0x0D) } }
    Wait-For { (Transcript).Replace("`r`n","`n").Contains($Text.Replace("`r`n","`n")) } 'accepted prompt in published transcript'
}
function Latest-AssistantText {
    $text=Transcript
    $headings=[regex]::Matches($text,'(?m)^Assistant(\.*): ')
    if ($headings.Count -eq 0) { return '' }
    $last=$headings[$headings.Count-1]
    if ($last.Groups[1].Length -ne 0) { return '' }
    return $text.Substring($last.Index+$last.Length)
}

try {
    Start-App 'main'
    Wait-For { (Status) -like 'Backend unavailable*' } 'Auto-connect off startup'
    Assert-True (-not (By-Id 'SendButton').Current.IsEnabled) 'Empty/unavailable SEND was enabled.'
    $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion.Split('+')[0]
    Assert-True $script:main.Current.Name.Contains($version) 'Published title version mismatch.'
    Measure-Memory 'idle before Connect'
    $capture=[OwnedDesktop]::Capture($script:process.MainWindowHandle,$script:process.Id,(Join-Path $EvidenceDirectory 'active.png'))
    Assert-True ($capture[0] -gt 1000) 'Actual active window capture did not contain the navy title strip.'
    Record 'published launch/version/Auto-connect-off/empty SEND/actual active capture'
    foreach($top in @('File','Edit','Options','Help')) {
        $key=[System.UInt16][char]$top.Substring(0,1).ToUpperInvariant()
        Keys @(0x12,$key)
        Keys @(0x1B)
    }
    Menu 'Help' 'About WinChat-Copilot...'
    Wait-For { $null -ne (Owned-Window 'About WinChat-Copilot') } 'About modal'
    $about=Owned-Window 'About WinChat-Copilot'
    Assert-True ((By-Id 'VersionLabel' $about).Current.Name.Contains($version)) 'Published About version mismatch.'
    $inactive=[OwnedDesktop]::Capture($script:process.MainWindowHandle,$script:process.Id,(Join-Path $EvidenceDirectory 'inactive.png'))
    Assert-True ($inactive[0] -lt 100 -and $inactive[1] -gt 1000) 'Actual inactive title capture was not white rather than navy.'
    [void][OwnedDesktop]::Capture([IntPtr]$about.Current.NativeWindowHandle,$script:process.Id,(Join-Path $EvidenceDirectory 'about.png'))
    Keys @(0x0D) $about
    Wait-For { $null -eq (Owned-Window 'About WinChat-Copilot') } 'About Enter dismissal'
    Menu 'Help' 'About WinChat-Copilot...'
    Wait-For { $null -ne (Owned-Window 'About WinChat-Copilot') } 'About reopen'
    Keys @(0x1B) (Owned-Window 'About WinChat-Copilot')
    Record 'menu keyboard smoke/About actual modal/version/Enter/Escape/inactive capture'
    Invoke-Element (By-Id 'MaximizeButton')
    Wait-For { $script:main.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Current.WindowVisualState -eq [Windows.Automation.WindowVisualState]::Maximized } 'maximize'
    Invoke-Element (By-Id 'MaximizeButton')
    Invoke-Element (By-Id 'MinimizeButton')
    Wait-For { $script:main.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Current.WindowVisualState -eq [Windows.Automation.WindowVisualState]::Minimized } 'minimize'
    $script:main.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([Windows.Automation.WindowVisualState]::Normal)
    $dpi=[OwnedDesktop]::GetDpiForWindow($script:process.MainWindowHandle)
    [OwnedDesktop]::Resize($script:process.MainWindowHandle,$script:process.Id,[int](520*$dpi/96),[int](340*$dpi/96))
    Assert-True (-not (By-Id 'PromptInput').Current.IsOffscreen -and -not (By-Id 'SendButton').Current.IsOffscreen) 'Resizing hid required input/actions.'
    [OwnedDesktop]::Resize($script:process.MainWindowHandle,$script:process.Id,1000,650)
    [OwnedDesktop]::CaptionDoubleClick($script:process.MainWindowHandle,$script:process.Id)
    Wait-For { $script:main.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Current.WindowVisualState -eq [Windows.Automation.WindowVisualState]::Maximized } 'native caption double-click maximize'
    [OwnedDesktop]::CaptionDoubleClick($script:process.MainWindowHandle,$script:process.Id)
    Wait-For { $script:main.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Current.WindowVisualState -eq [Windows.Automation.WindowVisualState]::Normal } 'native caption double-click restore'
    $beforeMove=$script:main.Current.BoundingRectangle
    Keys @(0x12,0x20)
    Wait-For { $null -ne (By-Name 'Move') } 'system Move'
    Invoke-Element (By-Name 'Move')
    Keys @(0x27); Keys @(0x28); Keys @(0x0D)
    Assert-True ($script:main.Current.BoundingRectangle.Left -ne $beforeMove.Left -or $script:main.Current.BoundingRectangle.Top -ne $beforeMove.Top) 'System Move did not move the owned window.'
    $beforeSize=$script:main.Current.BoundingRectangle
    Keys @(0x12,0x20)
    Wait-For { $null -ne (By-Name 'Size') } 'system Size'
    Invoke-Element (By-Name 'Size')
    Keys @(0x27); Keys @(0x27); Keys @(0x0D)
    Assert-True ($script:main.Current.BoundingRectangle.Width -ne $beforeSize.Width) 'System Size did not resize the owned window.'
    Record "actual title minimize/maximize/restore/caption-double-click/system Move/Size/minimum 520x340 DIP layout at DPI $dpi"
    Menu 'Options' 'Backend Settings...'
    Wait-For { $null -ne (Owned-Window 'Backend Settings') } 'settings modal'
    $settings=Owned-Window 'Backend Settings'
    Set-Value (By-Id 'ExecutableInput' $settings) (Join-Path $EvidenceDirectory 'missing.exe')
    Invoke-Element (By-Name 'OK' $settings)
    Wait-For { -not [string]::IsNullOrWhiteSpace((By-Id 'ValidationMessage' $settings).Current.Name) } 'invalid explicit path guidance'
    Set-Value (By-Id 'ExecutableInput' $settings) $fake
    Invoke-Element (By-Name 'OK' $settings)
    Wait-For { $null -eq (Owned-Window 'Backend Settings') } 'settings save'
    Invoke-Element (By-Name 'Connect')
    Wait-For { (Status) -like 'Copilot ready*' } 'synthetic authenticated readiness'
    Record 'actual settings validation/save and synthetic subprocess Connect'
    $input=By-Id 'PromptInput'
    Set-Value $input 'keyboard'
    $input.SetFocus(); Keys @(0x0D)
    Assert-True ((Get-Value $input).Contains("`n")) 'Default Enter did not insert newline.'
    $input.SetFocus(); Keys @(0x11,0x41)
    Menu 'Edit' 'Copy'
    Set-Value $input ''
    $input.SetFocus()
    Menu 'Edit' 'Paste'
    Assert-True ((Get-Value $input).Contains('keyboard')) 'Focused Edit Copy/Paste did not round trip.'
    Record 'actual Ctrl+Enter-mode newline and focused Edit Copy/Paste'
    Scenario 'stream'
    Submit "synthetic first`nsecond" 'CtrlEnter'
    Wait-For { (Transcript).Contains('first ') } 'progressive synthetic transcript'
    Assert-True (-not (By-Id 'SendButton').Current.IsEnabled) 'SEND remained enabled during generation.'
    Assert-True ((By-Id 'PromptInput').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.IsReadOnly) 'Busy input not read-only.'
    Measure-Memory 'gated synthetic streaming after first delta'
    Menu 'Options' 'Word Wrap'
    Menu 'Options' 'Show Timestamp'
    Assert-True ((Transcript) -match '(?m)^\[\d{2}:\d{2}\] User: ' -and (Transcript) -match '(?m)^\[\d{2}:\d{2}\] Assistant: ') 'Timestamp toggle did not produce compact timestamps before both speaker names.'
    [IO.File]::WriteAllText((Join-Path $script:data 'work\chat.continue'),'continue')
    Wait-For { (Status) -like 'Copilot ready*' } 'stream completion'
    Assert-True ((Transcript).Contains([char]0x20AC) -and -not (Transcript).Contains([char]27) -and -not (Transcript).Contains('DIAGNOSTIC_NOT_RESPONSE')) 'Actual transcript leaked diagnostics or lost Unicode.'
    Assert-True ((Get-Value (By-Id 'PromptInput')) -eq '') 'Successful input did not clear.'
    Scenario 'complete'
    Menu 'Options' 'Send with Enter'
    Submit 'synthetic follow-up' 'Enter'
    Wait-For { (Status) -like 'Copilot ready*' } 'Enter-mode follow-up'
    $requests=Requests
    $sessions=@($requests | ForEach-Object { @($_ | Where-Object { $_ -like '--session-id=*' })[0] })
    Assert-True ($requests.Count -eq 2 -and $sessions[0] -eq $sessions[1]) 'Packaged follow-up did not retain one backend session.'
    Record 'actual SEND modes/streaming/busy guards/timestamp/Unicode filtering/synthetic session continuation'
    Scenario 'long-stream'
    Submit 'synthetic scrolling'
    Wait-For { (Transcript).Contains('synthetic line 149') } 'populated transcript'
    $transcript=By-Id 'Transcript'
    $scroll=$transcript.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    $scroll.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll,0)
    Start-Sleep -Milliseconds 100
    [IO.File]::WriteAllText((Join-Path $script:data 'work\chat.continue'),'continue')
    Wait-For { (Status) -like 'Copilot ready*' } 'scroll fixture completion'
    Assert-True ($scroll.Current.VerticalScrollPercent -lt 10) 'Actual streaming forced a scrolled-away reader to bottom.'
    $scroll.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll,100)
    Record 'actual populated transcript scroll-away preserved/return-to-bottom'
    Scenario 'delayed-first'
    $before=(Requests).Count
    Submit 'synthetic waiting dots'
    $transcript=By-Id 'Transcript'
    $scroll=$transcript.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    $scroll.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll,0)
    $textPattern=$transcript.GetCurrentPattern([Windows.Automation.TextPattern]::Pattern)
    $selection=$textPattern.DocumentRange.FindText('synthetic first',$false,$false)
    Assert-True ($null -ne $selection) 'Earlier synthetic message text was unavailable for selection.'
    $selection.Select()
    (By-Id 'PromptInput').SetFocus()
    $observed=New-Object 'System.Collections.Generic.List[int]'
    $cadence=[Diagnostics.Stopwatch]::StartNew()
    foreach($dots in @(1,2,3,4,5,1,2)) {
        $heading='Assistant'+('.' * $dots)+': '
        Wait-For { (Transcript).EndsWith($heading) -or (Transcript).EndsWith($heading+"`r`n") } "waiting heading $dots dots" 2
        $observed.Add($dots)
        Assert-True ($textPattern.GetSelection()[0].GetText(-1) -eq 'synthetic first') 'Waiting tick moved message selection.'
        Assert-True ($scroll.Current.VerticalScrollPercent -lt 10) 'Waiting tick forced scroll-away to bottom.'
        Assert-True ((By-Id 'PromptInput').Current.HasKeyboardFocus) 'Waiting tick moved input focus.'
    }
    Assert-True ($cadence.Elapsed.TotalMilliseconds -ge 2000 -and $cadence.Elapsed.TotalMilliseconds -lt 7000) 'Waiting cycle did not follow the bounded half-second cadence.'
    [void][OwnedDesktop]::Capture($script:process.MainWindowHandle,$script:process.Id,(Join-Path $EvidenceDirectory 'waiting.png'))
    [IO.File]::WriteAllText((Join-Path $script:data 'work\chat.first'),'continue')
    Wait-For { (Transcript).EndsWith('Assistant: first ') -or (Transcript).EndsWith("Assistant: first `r`n") } 'first text removed waiting dots before completion'
    Start-Sleep -Milliseconds 650
    Assert-True ((Transcript) -notmatch 'Assistant\.+: ') 'Waiting animation restarted during a streamed-text pause.'
    Assert-True ($textPattern.GetSelection()[0].GetText(-1) -eq 'synthetic first') 'First text moved earlier message selection.'
    Assert-True ($scroll.Current.VerticalScrollPercent -lt 10) 'First text forced scroll-away to bottom.'
    Assert-True ((Requests).Count -eq $before+1) 'Waiting animation submitted another backend request.'
    $scroll.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll,100)
    [IO.File]::WriteAllText((Join-Path $script:data 'work\chat.continue'),'continue')
    Wait-For { (Status) -like 'Copilot ready*' } 'waiting fixture completion'
    Record 'actual waiting 1/2/3/4/5/1/2 cadence, first-text removal/no restart, selection/focus/scroll and single request'
    Scenario 'delayed-first'
    Submit 'synthetic stop'
    Wait-For { Test-Path -LiteralPath (Join-Path $script:data 'work\chat.empty') } 'Stop fixture waiting before text'
    Invoke-Element (By-Id 'StopButton')
    Wait-For { (Transcript).Contains('[Cancelled]') } 'cancel marker'
    Assert-True ((Get-Value (By-Id 'PromptInput')) -eq 'synthetic stop') 'Cancelled input was lost.'
    Assert-True ((Transcript) -notmatch 'Assistant\.+: ') 'Stop retained waiting dots.'
    Record 'actual Stop before first text, removed waiting dots/cancelled marker/retained input/owned subprocess settlement'
    Scenario 'partial-failure'
    Submit 'synthetic failure'
    Wait-For { (Status) -like 'Copilot generating*' } 'failure fixture generating'
    [IO.File]::WriteAllText((Join-Path $script:data 'work\chat.continue'),'continue')
    Wait-For { $null -ne (Owned-Window 'WinChat-Copilot Error') } 'actionable error modal'
    $errorWindow=Owned-Window 'WinChat-Copilot Error'
    [void][OwnedDesktop]::Capture([IntPtr]$errorWindow.Current.NativeWindowHandle,$script:process.Id,(Join-Path $EvidenceDirectory 'error.png'))
    Invoke-Element (By-Name 'OK' $errorWindow)
    Wait-For { $null -eq (Owned-Window 'WinChat-Copilot Error') -and $null -ne (By-Id 'Transcript') } 'error dismissal and main control restoration'
    Assert-True ((Transcript).Contains('[Failed]') -and (Get-Value (By-Id 'PromptInput')) -eq 'synthetic failure') 'Failure discarded marker/input.'
    $before=(Requests).Count
    Start-Sleep -Milliseconds 300
    Assert-True ((Requests).Count -eq $before) 'Failure automatically duplicated a prompt.'
    Invoke-Element (By-Name 'Connect')
    Wait-For { (Status) -like 'Copilot ready*' } 'explicit same-session recovery'
    Record 'actual partial-failure/modal/retention/no retry/explicit recovery (synthetic CLI)'
    Scenario 'stream'
    Submit 'synthetic reset'
    Wait-For { (Status) -like 'Copilot generating*' } 'reset fixture generating'
    $oldSession=@((Requests)[-1] | Where-Object { $_ -like '--session-id=*' })[0]
    Menu 'File' 'Clear Window'
    Wait-For { (Status) -like 'Copilot ready*' } 'Clear Window settlement/preparation'
    Assert-True ([string]::IsNullOrWhiteSpace((Transcript))) 'Clear Window did not clear actual transcript.'
    Scenario 'complete'
    Submit 'synthetic after reset'
    Wait-For { (Status) -like 'Copilot ready*' } 'post-reset turn'
    $newSession=@((Requests)[-1] | Where-Object { $_ -like '--session-id=*' })[0]
    Assert-True ($oldSession -ne $newSession) 'Clear Window reused old synthetic backend session.'
    Record 'actual Clear Window during generation, empty transcript, fresh session'
    Menu 'File' 'Save Transcript...'
    Wait-For { $null -ne (Owned-Window 'Save Transcript') } 'native save dialog'
    $save=Owned-Window 'Save Transcript'
    $fileInput=By-Id '1001' (By-Id 'FileNameControlHost' $save)
    $export=Join-Path $EvidenceDirectory 'export.txt'
    Set-Value $fileInput $export
    Invoke-Element (By-Name 'Save' $save)
    Wait-For { Test-Path -LiteralPath $export } 'native selected export'
    $bytes=[IO.File]::ReadAllBytes($export)
    Assert-True (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)) 'Export contains UTF-8 BOM.'
    Assert-True ([IO.File]::ReadAllText($export).Contains('synthetic after reset')) 'Native export omitted current conversation.'
    # UIA omits the final paragraph terminator retained by plain-text export.
    Assert-True ([IO.File]::ReadAllText($export) -ceq ((Transcript) + "`r`n")) 'Native export headings/text differed from the completed displayed transcript.'
    Menu 'File' 'Save Transcript...'
    Wait-For { $null -ne (Owned-Window 'Save Transcript') } 'save cancellation dialog'
    Invoke-Element (By-Name 'Cancel' (Owned-Window 'Save Transcript'))
    Record 'actual native save/path/UTF-8 current transcript and cancellation'
    Menu 'Options' 'Auto-connect on Startup'
    Measure-Memory 'completed synthetic conversation'
    Close-App 'File'
    Start-App 'main' -Reuse
    Wait-For { (Status) -like 'Copilot ready*' } 'restart Auto-connect'
    $saved=Get-Content -LiteralPath (Join-Path $script:data 'settings.json') -Raw | ConvertFrom-Json
    Assert-True ($saved.AutoConnect -and $saved.WordWrap -and $saved.ShowTimestamp -and $saved.SendMode -eq 'Enter' -and $saved.ExecutablePath -eq $fake) 'Preferences did not survive restart.'
    Assert-True ([string]::IsNullOrWhiteSpace((Transcript))) 'Restart restored old conversation.'
    Record 'actual restart persisted all preferences, Auto-connect, no conversation restoration'
    Close-App 'AltF4'
    Start-App 'system'
    Close-App 'System'
    foreach($attempt in 1..3) {
        Start-App "system-box-$attempt"
        Close-App 'SystemBoxDoubleClick'
    }
    Start-App 'active-close' $true
    Wait-For { (Status) -like 'Copilot ready*' } 'active close readiness'
    Scenario 'with-child'
    Submit 'synthetic close'
    Wait-For { Test-Path -LiteralPath (Join-Path $script:data 'work\child.pid') } 'owned synthetic child'
    $childId=[int][IO.File]::ReadAllText((Join-Path $script:data 'work\child.pid'))
    $child=Get-Process -Id $childId
    Assert-True ($child.Path -eq $fake) 'Child executable identity mismatch.'
    $backendId=[int][IO.File]::ReadAllText((Join-Path $script:data 'work\chat.started'))
    $backendInfo=Get-CimInstance Win32_Process -Filter "ProcessId=$backendId"
    $childInfo=Get-CimInstance Win32_Process -Filter "ProcessId=$childId"
    Assert-True ($backendInfo.ParentProcessId -eq $script:process.Id -and $childInfo.ParentProcessId -eq $backendId) 'Synthetic child parentage does not belong to the test application.'
    $owned.Add($child)
    Close-App 'Message'
    Assert-True ($child.WaitForExit(10000)) 'Generation close left owned synthetic child running.'
    Record 'actual active-generation shutdown and owned synthetic child exit'
    if ($RunRealTests) {
        $actualCopilot=(Get-Item -LiteralPath $CopilotPath).FullName
        $cliVersion=@(& $actualCopilot --no-auto-update --version)
        Assert-True ($LASTEXITCODE -eq 0 -and $cliVersion[0] -eq 'GitHub Copilot CLI 1.0.81.') 'Real packaged tests require the verified bundled CLI 1.0.81.'
        Start-App 'real' $true $actualCopilot -RealService
        Wait-For { (Status) -like 'Copilot ready*' -or $null -ne (Owned-Window 'WinChat-Copilot Error') } 'real published authentication/readiness' 120
        Assert-True ((Status) -like 'Copilot ready*') 'Real published readiness failed; no login or retry attempted.'
        $token='WINCHAT_'+[guid]::NewGuid().ToString('N')
        Submit "Remember this synthetic token: $token. Reply only with the token. Do not use tools."
        Wait-For { @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($script:process.Id)" | Where-Object ExecutablePath -eq $actualCopilot).Count -gt 0 } 'owned real backend subprocess'
        foreach($backendInfo in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($script:process.Id)" | Where-Object ExecutablePath -eq $actualCopilot)) {
            $realBackend=Get-Process -Id $backendInfo.ProcessId -ErrorAction Stop
            $owned.Add($realBackend)
            $memory.Add([pscustomobject]@{
                Workload='real token turn during inference'; Utc=[DateTime]::UtcNow.ToString('o')
                Kind='Real Copilot CLI process, separately measured'; ProcessId=$realBackend.Id
                WorkingSetBytes=$realBackend.WorkingSet64; PrivateBytes=$realBackend.PrivateMemorySize64
            })
        }
        Wait-For { (Status) -like 'Copilot ready*' -or $null -ne (Owned-Window 'WinChat-Copilot Error') } 'real published first turn' 120
        Assert-True ((Status) -like 'Copilot ready*' -and (Transcript).Contains("Assistant: $token")) 'Real published first turn did not return the synthetic token.'
        Submit 'What synthetic token did I ask you to remember? Reply only with that token.'
        Wait-For { (Status) -like 'Copilot ready*' -or $null -ne (Owned-Window 'WinChat-Copilot Error') } 'real published context turn' 120
        Assert-True ((Status) -like 'Copilot ready*' -and ([regex]::Matches((Transcript),[regex]::Escape("Assistant: $token"))).Count -eq 2) 'Real published follow-up did not retain context.'
        Submit 'Print 300 numbered lines of harmless synthetic text, one line per number. Do not use tools.'
        Wait-For {
            (Status) -like 'Copilot generating*' -and (Latest-AssistantText).Trim().Length -gt 0
        } 'real published progressive output before Stop' 120
        Invoke-Element (By-Id 'StopButton')
        Wait-For { (Transcript).Contains('[Cancelled]') -and (By-Id 'PromptInput').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.IsReadOnly -eq $false } 'real published cancellation settlement' 30
        Assert-True (-not [string]::IsNullOrWhiteSpace((Get-Value (By-Id 'PromptInput')))) 'Real Stop lost original input.'
        Submit 'Print 300 more numbered lines of harmless synthetic text, one line per number. Do not use tools.'
        Wait-For {
            (Status) -like 'Copilot generating*' -and (Latest-AssistantText).Trim().Length -gt 0
        } 'real published progressive output before Clear Window' 120
        Menu 'File' 'Clear Window'
        Wait-For { (Status) -like 'Copilot ready*' -or $null -ne (Owned-Window 'WinChat-Copilot Error') } 'real published reset readiness' 120
        Assert-True ((Status) -like 'Copilot ready*' -and [string]::IsNullOrWhiteSpace((Transcript))) 'Real published reset failed.'
        Submit 'What synthetic token did I ask you to remember? If none was supplied in this conversation, reply only NO_TOKEN.'
        Wait-For { (Status) -like 'Copilot ready*' -or $null -ne (Owned-Window 'WinChat-Copilot Error') } 'real published reset isolation turn' 120
        Assert-True ((Status) -like 'Copilot ready*' -and (Transcript).Contains('Assistant: NO_TOKEN') -and -not (Transcript).Contains($token)) 'Real published fresh session did not isolate old context.'
        $script:process.Refresh()
        $memory.Add([pscustomobject]@{
            Workload='completed real Copilot context/reset conversation'; Utc=[DateTime]::UtcNow.ToString('o')
            Kind='UI process only, real backend workload'; ProcessId=$script:process.Id
            WorkingSetBytes=$script:process.WorkingSet64; PrivateBytes=$script:process.PrivateMemorySize64
        })
        Record 'real published CLI version/authenticated production readiness/zero-tool adapter/SEND/context/progressive Stop/reset during streaming'
        Close-App 'File'
    }
    $outcome='Passed'
} catch {
    $failure=$_.Exception.Message
    $failureAt=$_.ScriptStackTrace
    throw
} finally {
    $cleanup=$true
    foreach($app in @($owned.ToArray())) {
        if ($app.HasExited) { continue }
        foreach($descendant in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($app.Id)")) {
            if ($descendant.ExecutablePath -eq $fake -or ($RunRealTests -and $descendant.ExecutablePath -eq $CopilotPath)) {
                $handle=Get-Process -Id $descendant.ProcessId -ErrorAction SilentlyContinue
                if ($null -ne $handle) { $owned.Add($handle) }
            }
        }
    }
    foreach($parent in @($owned.ToArray())) {
        if ($parent.HasExited) { continue }
        foreach($descendant in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($parent.Id)")) {
            if ($descendant.ExecutablePath -eq $fake -or ($RunRealTests -and $descendant.ExecutablePath -eq $CopilotPath)) {
                $handle=Get-Process -Id $descendant.ProcessId -ErrorAction SilentlyContinue
                if ($null -ne $handle -and -not @($owned | Where-Object Id -eq $handle.Id).Count) { $owned.Add($handle) }
            }
        }
    }
    foreach($p in $owned) {
        if (-not $p.HasExited) {
            # These are retained process handles created or verified by this run, never a name-based kill.
            $p.Kill()
            if (-not $p.WaitForExit(10000)) { $cleanup=$false }
        }
    }
    $env:WINCHAT_DATA_DIRECTORY=$oldData
    $env:PATH=$oldPath
    [ordered]@{
        Outcome=$outcome; Failure=$failure; FailureAt=$failureAt; CleanupVerified=$cleanup; Executable=$exe
        ExecutableSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        ApplicationAssemblySha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path $exe) 'WinChatCopilot.dll') -Algorithm SHA256).Hash
        OSVersion=[Environment]::OSVersion.VersionString; DesktopProbe=$desktop
        Runtime=[Environment]::Version.ToString(); Checks=$results.ToArray(); Memory=$memory.ToArray()
        RealTestsRequested=$RunRealTests.IsPresent
        Scope='Actual published application with synthetic CLI; real-service checks only when explicitly requested and recorded. Not pixel-identical visual acceptance. Screenshots capture owned window handles only.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'results.json') -Encoding UTF8
    foreach($p in $owned) { $p.Dispose() }
    if (-not $cleanup) { throw 'Owned test-process cleanup incomplete.' }
}
