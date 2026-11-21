<#
.SYNOPSIS
  Measures a published PC Mieru (and, for -Mode compare, Windows Task Manager) from the outside and writes raw CSV plus summary.json.

.DESCRIPTION
  DESIGN-A section 10. The script only reads the process from outside (CPU time, private bytes, working set, handle count, once a
  second); nobody reads the screen. It never elevates itself, never trims a working set, and never reboots anything.

  What is recorded
    * Environment (no serial numbers, no PC name, no user name, no folder paths): OS build, logical CPU count, CPU name, total memory,
      power plan name, GPU name and driver version, the exe's file version and SHA256, the publish setting if it can be read
      (self-contained, ReadyToRun, the runtimeconfig properties), the date, the uptime.
    * Every second, per process: CPU time, private bytes, working set, handle count of the GUI (PCMieru.exe) and of each of its
      PCMieru.SensorHost children. A process is tracked by PID and creation time: a SensorHost that exits and is started again is a new
      series, and two processes are never joined into one series (a delta is always taken inside one series). Rows are written for
      "main" (the GUI alone), "child" (each SensorHost) and "total" (main plus the children alive at that second). The total working set
      may count shared pages twice.
    * CPU: dCPU seconds / dwall seconds / logical CPUs * 100 (percent of the whole machine) and dCPU seconds / dwall seconds * 1000
      (ms per second). A window's mean is total CPU / total wall time of the window; its max is the largest one-second interval.
      Windows CPU time is accounted in scheduler ticks (about 15.6 ms), so one interval can be off by that much; the mean is not.
      Sizes are MB = 1048576 bytes (as publish.ps1 prints the folder size).
    * Startup precision: the GUI's marks use DateTime.UtcNow, which on .NET for Windows is GetSystemTimePreciseAsFileTime (a precise
      clock). The process creation time is what the OS recorded at its coarse system-time tick (about 15.6 ms on most machines), so one
      startup figure carries that much noise; read it over -Runs repeats. Both are UTC times of the same machine clock: nothing comes
      from this script's own clock, and the script's launch overhead is not part of the figure.

  Modes
    warm     One unmeasured run first (so the file cache and the first-run costs are gone), then -Runs measured runs. Each run starts
             PCMieru.exe with --measure <markerfile>, samples for 30 s (the "first 30 s" window) and then -Seconds (the "steady"
             window), and closes the window politely (CloseMainWindow, then waits; it kills the process only after a timeout and says
             so in the summary). Startup is the GUI's mark time minus the process creation time the OS recorded (not the script's
             clock). Two marks: first_list (the process list was first painted with rows) and cpu_list (first painted with CPU values,
             from the second sample). A mark that never came is "missing" (null), never 0.
    cold     The same, but exactly one run and no unmeasured run before it. Start it right after an OS reboot: the script only records
             "cold" and the uptime (and warns when the uptime is long). It does not reboot anything. -Runs is ignored.
    compare  Alternates PCMieru and Task Manager (taskmgr.exe), -Runs times each, never both at once, with the same windows.
             PCMieru gets the unmeasured warm-up run first. Task Manager has no marks (its startup is missing). Task Manager asks for
             elevation when it is started from a normal shell, so Windows may show the UAC prompt: answer it yourself; the script
             never elevates. A counter that cannot be read from an elevated process is recorded as missing with the reason. A
             Task Manager window that a normal-integrity script cannot close is left to you: the script waits up to 3 minutes for you
             to close it. Close any open Task Manager before you start.
    long     One run of -LongHours (default 2). Every minute the GUI is switched to the next page (all pages in a cycle), and every
             10 s a small test process (cmd /c exit) is started and ended. The page switch posts an arrow-key message to the
             navigation list box of the GUI window (PostMessage), so it needs no focus and takes none from you; the script then
             reads the selection back (LB_GETCURSEL) and records whether the page really changed. The GUI window itself opens in the
             foreground at the start, as any launch does. Private bytes and handles: the mean of the first 10 minutes, the mean of
             the last 10 minutes, and a least-squares slope per hour (the windows shrink to half the run if it is shorter than
             20 minutes).

  Targets (printed next to the results, not judged in code): private bytes < 100 MB, steady CPU mean < 0.3 % and < 10 ms/s, warm
  start < 1 s. -Zip prints the ZIP size next to v0.2.0's 51.6 MB.

  -Fakes starts the GUI with --fakes (fake sensors and fake data). That is for trials of this script only; every output is marked
  "fakes" and the numbers do not describe the product.

  Needs: a published PCMieru.exe, Windows PowerShell 5.1 or pwsh. Leave the machine alone while it runs; a game or another heavy
  program running at the same time makes the numbers worthless.
  Output folder: <Output>\summary.json, <Output>\<mode>-r<N>-<target>.csv (raw, one row per process per second) and the marker files.
  Dot-sourcing the script defines its functions and runs nothing.
  ASCII only on purpose (5.1 reads BOM-less files as ANSI).

.PARAMETER Exe
  The published PCMieru.exe.
.PARAMETER Mode
  warm (default), cold, long or compare.
.PARAMETER Output
  Output folder (made if needed; it must not hold a summary.json yet). Default: a folder under the temp folder.
.PARAMETER Seconds
  Length of the steady window after the first 30 s (default 300).
.PARAMETER Runs
  Measured runs (default 3; per target for compare).
.PARAMETER LongHours
  Length of the long run in hours (default 2).
.PARAMETER Fakes
  Pass --fakes to the GUI (trials only; marked in the summary).
.PARAMETER Zip
  A release ZIP: its size is printed next to v0.2.0's 51.6 MB and recorded.

.EXAMPLE
  powershell -File tools\measure.ps1 -Exe C:\release\PCMieru.exe -Mode warm -Output C:\measure\warm -Zip C:\release\PCMieru-0.4.0-win-x64.zip

.EXAMPLE
  powershell -File tools\measure.ps1 -Exe C:\release\PCMieru.exe -Mode warm -Runs 1 -Seconds 30 -Fakes -Output C:\measure\trial

.EXAMPLE
  powershell -File tools\measure.ps1 -Exe C:\release\PCMieru.exe -Mode long -LongHours 2 -Output C:\measure\long
#>
[CmdletBinding()]
param(
    [string]$Exe = '',
    [ValidateSet('warm', 'cold', 'long', 'compare')]
    [string]$Mode = 'warm',
    [string]$Output = '',
    [int]$Seconds = 300,
    [int]$Runs = 3,
    [double]$LongHours = 2,
    [switch]$Fakes,
    [string]$Zip = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---- constants (a trial or a test may change the first two) ----
$script:FirstWindowSeconds = 30
$script:CloseTimeoutSeconds = 20
$script:SensorHostName = 'PCMieru.SensorHost'
$script:ZipV020Mb = 51.6
$script:NavPageSeconds = 60
$script:ChurnSeconds = 10
$script:FakesOn = $false
$script:ExePath = ''

# ---- small helpers ----

function Get-Inv { [Globalization.CultureInfo]::InvariantCulture }

function Fmt($Value, [string]$Format = '0.###') {
    if ($null -eq $Value) { return 'missing' }
    return ([double]$Value).ToString($Format, (Get-Inv))
}

function Round-Or-Null($Value, [int]$Digits = 3) {
    if ($null -eq $Value) { return $null }
    return [math]::Round([double]$Value, $Digits)
}

function Get-Median($Values) {
    $sorted = @($Values | Where-Object { $null -ne $_ } | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    return ($sorted[$mid - 1] + $sorted[$mid]) / 2.0
}

# values per run, their median and range; null entries are skipped (a missing value is not 0)
function Get-Aggregate($Values) {
    $list = New-Object System.Collections.ArrayList
    $present = New-Object System.Collections.ArrayList
    foreach ($v in $Values) {
        [void]$list.Add($(if ($null -eq $v) { $null } else { [math]::Round([double]$v, 3) }))
        if ($null -ne $v) { [void]$present.Add([double]$v) }
    }
    $median = $null; $min = $null; $max = $null
    if ($present.Count -gt 0) {
        $median = [math]::Round((Get-Median $present), 3)
        $min = [math]::Round(($present | Measure-Object -Minimum).Minimum, 3)
        $max = [math]::Round(($present | Measure-Object -Maximum).Maximum, 3)
    }
    return [ordered]@{ values = $list; present = $present.Count; median = $median; min = $min; max = $max }
}

function Get-ShortError($ErrorRecord) {
    $text = $ErrorRecord.Exception.Message
    if ($null -eq $text) { return 'unknown error' }
    return ($text -replace '\s+', ' ').Trim()
}

function Get-Sha256Hex([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ---- environment ----

function Test-ReadyToRun([string]$Path) {
    # ManagedNativeHeader of the CLR header is set in a ReadyToRun image. $null when it cannot be read.
    try {
        $fs = [IO.File]::OpenRead($Path)
        try {
            $br = New-Object IO.BinaryReader($fs)
            $fs.Position = 0x3C
            $pe = [long]$br.ReadInt32()
            $fs.Position = $pe
            if ($br.ReadUInt32() -ne 0x00004550) { return $null }
            $fs.Position = $pe + 6
            $sections = [int]$br.ReadUInt16()
            $fs.Position = $pe + 20
            $optSize = [long]$br.ReadUInt16()
            $opt = $pe + 24
            $fs.Position = $opt
            $magic = $br.ReadUInt16()
            $dirs = if ($magic -eq 0x20B) { $opt + 112 } else { $opt + 96 }
            $fs.Position = $dirs + 14 * 8
            $clrRva = [long]$br.ReadUInt32()
            if ($clrRva -eq 0) { return $false }
            $table = $opt + $optSize
            $offset = $null
            for ($i = 0; $i -lt $sections; $i++) {
                $fs.Position = $table + 40 * $i + 8
                $vsize = [long]$br.ReadUInt32()
                $va = [long]$br.ReadUInt32()
                $rawSize = [long]$br.ReadUInt32()
                $raw = [long]$br.ReadUInt32()
                if ($clrRva -ge $va -and $clrRva -lt $va + [math]::Max($vsize, $rawSize)) { $offset = $clrRva - $va + $raw; break }
            }
            if ($null -eq $offset) { return $null }
            $fs.Position = $offset + 64
            $nativeRva = [long]$br.ReadUInt32()
            $nativeSize = [long]$br.ReadUInt32()
            return ($nativeRva -ne 0 -and $nativeSize -ne 0)
        }
        finally { $fs.Dispose() }
    }
    catch { return $null }
}

function Get-PublishInfo([string]$ExeFile) {
    $dir = Split-Path -Parent $ExeFile
    $info = [ordered]@{
        self_contained = $null
        ready_to_run = $null
        ready_to_run_checked = $null
        runtimeconfig = $null
        note = 'read from the published files; what is not readable is null'
    }
    try {
        $info.self_contained = ((Test-Path -LiteralPath (Join-Path $dir 'coreclr.dll')) -or (Test-Path -LiteralPath (Join-Path $dir 'hostfxr.dll')))
    }
    catch { }
    $checked = $null
    foreach ($name in @('PCMieru.Core.dll', 'PCMieru.dll')) {
        $candidate = Join-Path $dir $name
        if (Test-Path -LiteralPath $candidate) {
            $r2r = Test-ReadyToRun $candidate
            if ($null -ne $r2r) { $info.ready_to_run = $r2r; $info.ready_to_run_checked = $name; $checked = $name; break }
        }
    }
    $configPath = Join-Path $dir ([IO.Path]::GetFileNameWithoutExtension($ExeFile) + '.runtimeconfig.json')
    if (Test-Path -LiteralPath $configPath) {
        try {
            $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
            $props = [ordered]@{}
            $options = $config.runtimeOptions
            if ($null -ne $options.PSObject.Properties['configProperties']) {
                foreach ($p in $options.configProperties.PSObject.Properties) { $props[$p.Name] = $p.Value }
            }
            $frameworks = @()
            if ($null -ne $options.PSObject.Properties['includedFrameworks']) { $frameworks = @($options.includedFrameworks | ForEach-Object { $_.name }) }
            elseif ($null -ne $options.PSObject.Properties['framework']) { $frameworks = @($options.framework.name) }
            $info.runtimeconfig = [ordered]@{ properties = $props; frameworks = $frameworks }
        }
        catch { $info.runtimeconfig = $null }
    }
    return $info
}

function Get-EnvironmentInfo([string]$ExeFile) {
    $inv = Get-Inv
    $os = $null; $cpu = $null; $mem = $null; $gpus = @()
    try { $os = Get-CimInstance -ClassName Win32_OperatingSystem } catch { }
    try { $cpu = @(Get-CimInstance -ClassName Win32_Processor) } catch { }
    try { $mem = Get-CimInstance -ClassName Win32_ComputerSystem } catch { }
    try { $gpus = @(Get-CimInstance -ClassName Win32_VideoController) } catch { }

    $ubr = $null
    try { $ubr = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name UBR).UBR } catch { }
    $build = $null
    if ($null -ne $os) { $build = [string]$os.Version; if ($null -ne $ubr) { $build = $build + '.' + $ubr } }

    $logical = $null
    if ($null -ne $cpu -and $cpu.Count -gt 0) {
        $sum = 0
        foreach ($c in $cpu) { $sum += [int]$c.NumberOfLogicalProcessors }
        if ($sum -gt 0) { $logical = $sum }
    }
    if ($null -eq $logical) { $logical = [Environment]::ProcessorCount }

    $plan = $null
    try {
        $text = (& powercfg.exe /getactivescheme | Out-String).Trim()
        if ($text -match '\(([^()]*)\)\s*$') { $plan = $Matches[1] }
    }
    catch { }

    $gpuList = New-Object System.Collections.ArrayList
    foreach ($g in $gpus) {
        [void]$gpuList.Add([ordered]@{ name = [string]$g.Name; driver_version = [string]$g.DriverVersion })
    }

    $uptime = $null
    if ($null -ne $os) { try { $uptime = [math]::Round(((Get-Date) - $os.LastBootUpTime).TotalMinutes, 1) } catch { } }

    $fileInfo = Get-Item -LiteralPath $ExeFile
    return [ordered]@{
        date_utc = [DateTime]::UtcNow.ToString('o', $inv)
        os_build = $build
        os_caption = $(if ($null -ne $os) { [string]$os.Caption } else { $null })
        logical_cpus = $logical
        cpu_name = $(if ($null -ne $cpu -and $cpu.Count -gt 0) { ([string]$cpu[0].Name).Trim() } else { $null })
        total_memory_gb = $(if ($null -ne $mem) { [math]::Round([double]$mem.TotalPhysicalMemory / 1GB, 1) } else { $null })
        power_plan = $plan
        gpus = $gpuList
        uptime_minutes = $uptime
        exe = [ordered]@{
            file = $fileInfo.Name
            file_version = $fileInfo.VersionInfo.FileVersion
            product_version = $fileInfo.VersionInfo.ProductVersion
            sha256 = (Get-Sha256Hex $ExeFile)
            publish = (Get-PublishInfo $ExeFile)
        }
    }
}

# ---- window messages (page switching in the long run) ----

function Initialize-Win32 {
    if ('PcmMeasureWin' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class PcmMeasureWin
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint LB_GETCOUNT = 0x018B;
    private const uint LB_GETCURSEL = 0x0188;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    // The navigation list box: a direct child of the main window whose window class is a list box.
    public static IntPtr FindNavList(IntPtr main)
    {
        List<IntPtr> found = new List<IntPtr>();
        EnumChildWindows(main, delegate(IntPtr hwnd, IntPtr lParam)
        {
            if (GetParent(hwnd) == main)
            {
                StringBuilder name = new StringBuilder(256);
                GetClassName(hwnd, name, 256);
                if (name.ToString().ToUpperInvariant().Contains("LISTBOX"))
                {
                    found.Add(hwnd);
                }
            }
            return true;
        }, IntPtr.Zero);
        return found.Count == 1 ? found[0] : IntPtr.Zero;
    }

    private static int Ask(IntPtr hwnd, uint msg)
    {
        IntPtr result;
        IntPtr ok = SendMessageTimeout(hwnd, msg, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out result);
        return ok == IntPtr.Zero ? -2 : result.ToInt32();
    }

    public static int Current(IntPtr nav) { return Ask(nav, LB_GETCURSEL); }

    public static int Count(IntPtr nav) { return Ask(nav, LB_GETCOUNT); }

    // Posts the key to the list box itself; no focus is needed and none is taken.
    public static bool PressKey(IntPtr nav, int virtualKey, int scanCode, bool extended)
    {
        long down = 1L | ((long)scanCode << 16) | (extended ? (1L << 24) : 0L);
        long up = down | (1L << 30) | (1L << 31);
        bool a = PostMessage(nav, WM_KEYDOWN, new IntPtr(virtualKey), new IntPtr(down));
        bool b = PostMessage(nav, WM_KEYUP, new IntPtr(virtualKey), new IntPtr(up));
        return a && b;
    }
}
'@
}

# Next page: arrow down, or Home after the last one. Returns what happened; never throws.
function Step-NavPage([System.Diagnostics.Process]$Proc, [hashtable]$State) {
    $r = [ordered]@{ ok = $false; from = $null; to = $null; count = $null; note = $null }
    try {
        Initialize-Win32
        $Proc.Refresh()
        $main = $Proc.MainWindowHandle
        if ($main -eq [IntPtr]::Zero) { $r.note = 'no main window handle'; return $r }
        if ($null -eq $State.Nav -or $State.Nav -eq [IntPtr]::Zero) { $State.Nav = [PcmMeasureWin]::FindNavList($main) }
        $nav = $State.Nav
        if ($nav -eq [IntPtr]::Zero) { $State.Nav = $null; $r.note = 'the navigation list box was not found'; return $r }
        $from = [PcmMeasureWin]::Current($nav)
        $count = [PcmMeasureWin]::Count($nav)
        $r.from = $from; $r.count = $count
        if ($from -lt 0 -or $count -le 0) { $State.Nav = $null; $r.note = 'the list box did not answer'; return $r }
        if ($from -ge $count - 1) { [void][PcmMeasureWin]::PressKey($nav, 0x24, 0x47, $true) }   # VK_HOME
        else { [void][PcmMeasureWin]::PressKey($nav, 0x28, 0x50, $true) }                        # VK_DOWN
        $to = $from
        for ($i = 0; $i -lt 20; $i++) {
            [Threading.Thread]::Sleep(50)
            $to = [PcmMeasureWin]::Current($nav)
            if ($to -ne $from) { break }
        }
        $r.to = $to
        $r.ok = ($to -ne $from -and $to -ge 0)
        if (-not $r.ok) { $r.note = 'the selection did not change' }
    }
    catch { $r.note = Get-ShortError $_ }
    return $r
}

# ---- process series ----

function Get-ParentId([int]$ProcId) {
    try {
        $p = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + $ProcId) -Property ProcessId, ParentProcessId
        if ($null -ne $p) { return [int]$p.ParentProcessId }
    }
    catch { }
    return $null
}

function Test-Alive([System.Diagnostics.Process]$Proc) {
    try { return (-not $Proc.HasExited) }
    catch {
        try {
            $other = [System.Diagnostics.Process]::GetProcessById($Proc.Id)
            $other.Dispose()
            return $true
        }
        catch { return $false }
    }
}

function New-Series([System.Diagnostics.Process]$Proc, [string]$Role, [double]$T, [DateTime]$NowUtc, [bool]$IsMain) {
    $start = $null
    $reason = $null
    try { $start = $Proc.StartTime.ToUniversalTime() } catch { $reason = Get-ShortError $_ }
    # CPU time starts at 0 when the process is created, so the first interval runs from the creation to the first sample.
    $prevT = $null
    if ($null -ne $start) { $prevT = $T - ($NowUtc - $start).TotalSeconds }
    elseif ($IsMain) { $prevT = 0.0 }
    $name = $Role
    try { $name = $Proc.ProcessName } catch { }
    return @{
        Proc = $Proc; Role = $Role; ProcId = $Proc.Id; Name = $name; StartUtc = $start; StartReason = $reason
        PrevT = $prevT; PrevCpu = 0.0
        FirstT = $T; LastT = $T; Samples = 0
        Missing = @{}; MissingTicks = @{}
        Ended = $false
    }
}

function Note-Missing([hashtable]$Series, [string]$Counter, $ErrorRecord) {
    if (-not $Series.Missing.ContainsKey($Counter)) { $Series.Missing[$Counter] = Get-ShortError $ErrorRecord; $Series.MissingTicks[$Counter] = 0 }
    $Series.MissingTicks[$Counter] = $Series.MissingTicks[$Counter] + 1
}

# One second's reading of one process. A counter that cannot be read is $null for that row and is noted with its reason.
function Read-Series([hashtable]$Series, [double]$T) {
    $row = @{
        T = $T; Role = $Series.Role; Name = $Series.Name; ProcId = $Series.ProcId; StartUtc = $Series.StartUtc
        Dt = $null; Cpu = $null; CpuDelta = $null; Private = $null; Ws = $null; Handles = $null
    }
    $proc = $Series.Proc
    try { $proc.Refresh() } catch { }
    try { $row.Private = [long]$proc.PrivateMemorySize64 } catch { Note-Missing $Series 'private_bytes' $_ }
    try { $row.Ws = [long]$proc.WorkingSet64 } catch { Note-Missing $Series 'working_set_bytes' $_ }
    try { $row.Handles = [int]$proc.HandleCount } catch { Note-Missing $Series 'handles' $_ }
    try {
        $cpu = $proc.TotalProcessorTime.TotalSeconds
        $row.Cpu = $cpu
        if ($null -ne $Series.PrevT -and $T -gt $Series.PrevT) {
            $row.CpuDelta = [math]::Max(0.0, $cpu - $Series.PrevCpu)
            $row.Dt = $T - $Series.PrevT
        }
        $Series.PrevT = $T
        $Series.PrevCpu = $cpu
    }
    catch { Note-Missing $Series 'cpu_time' $_ }
    $Series.LastT = $T
    $Series.Samples = $Series.Samples + 1
    return $row
}

# New PCMieru.SensorHost processes whose parent is the GUI. $Seen remembers every process looked at (pid + creation time).
function Find-NewChildren([hashtable]$Main, [hashtable]$Seen) {
    $found = New-Object System.Collections.ArrayList
    foreach ($p in [System.Diagnostics.Process]::GetProcessesByName($script:SensorHostName)) {
        $keep = $false
        try {
            $start = $p.StartTime.ToUniversalTime()
            $key = '{0}:{1}' -f $p.Id, $start.Ticks
            if (-not $Seen.ContainsKey($key)) {
                $parent = Get-ParentId $p.Id
                $afterMain = ($null -eq $Main.StartUtc) -or ($start -ge $Main.StartUtc.AddSeconds(-1))
                if ($parent -eq $Main.ProcId -and $afterMain) { $Seen[$key] = 'child'; $keep = $true }
                else { $Seen[$key] = 'other' }
            }
        }
        catch { }
        if ($keep) { [void]$found.Add($p) } else { $p.Dispose() }
    }
    return , $found
}

# ---- the marks the GUI writes (--measure) ----

function Read-Marks([string]$Path) {
    $result = [ordered]@{ first_list = $null; cpu_list = $null; lines = 0 }
    if (-not (Test-Path -LiteralPath $Path)) { return $result }
    $text = $null
    try {
        $fs = New-Object System.IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $text = (New-Object System.IO.StreamReader($fs, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $fs.Dispose() }
    }
    catch { return $result }
    foreach ($line in ($text -split "`n")) {
        $line = $line.Trim()
        if ($line.Length -eq 0) { continue }
        $result.lines = $result.lines + 1
        if ($line -match '^\{"mark":"(first_list|cpu_list)","utc":"([^"]+)"\}$') {
            $name = $Matches[1]
            if ($null -eq $result[$name]) {
                try { $result[$name] = [DateTime]::Parse($Matches[2], (Get-Inv), [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() } catch { }
            }
        }
    }
    return $result
}

function Get-Startup($Marks, $CreatedUtc) {
    $inv = Get-Inv
    $first = $null; $cpu = $null
    if ($null -ne $CreatedUtc -and $null -ne $Marks) {
        if ($null -ne $Marks.first_list) { $first = [math]::Round(($Marks.first_list - $CreatedUtc).TotalMilliseconds, 1) }
        if ($null -ne $Marks.cpu_list) { $cpu = [math]::Round(($Marks.cpu_list - $CreatedUtc).TotalMilliseconds, 1) }
    }
    $note = $null
    if ($null -eq $CreatedUtc) { $note = 'the process creation time could not be read: startup is missing' }
    elseif ($null -eq $Marks -or ($null -eq $Marks.first_list -and $null -eq $Marks.cpu_list)) { $note = 'no mark came: startup is missing' }
    elseif ($null -eq $Marks.first_list -or $null -eq $Marks.cpu_list) { $note = 'one of the two marks did not come: it is missing' }
    return [ordered]@{
        created_utc = $(if ($null -ne $CreatedUtc) { $CreatedUtc.ToString('o', $inv) } else { $null })
        first_list_utc = $(if ($null -ne $Marks -and $null -ne $Marks.first_list) { $Marks.first_list.ToString('o', $inv) } else { $null })
        cpu_list_utc = $(if ($null -ne $Marks -and $null -ne $Marks.cpu_list) { $Marks.cpu_list.ToString('o', $inv) } else { $null })
        first_list_ms = $first
        cpu_list_ms = $cpu
        note = $note
    }
}

# ---- statistics ----

# Mean and max of CPU, private bytes, working set and handles over the rows whose time is in (From, To].
function Get-WindowStats($Rows, [double]$From, [double]$To, [int]$Cpus) {
    $n = 0
    $sumDelta = 0.0; $sumDt = 0.0; $maxRate = $null; $intervals = 0
    $privSum = 0.0; $privN = 0; $privMax = $null
    $wsSum = 0.0; $wsN = 0; $wsMax = $null
    $hSum = 0.0; $hN = 0; $hMax = $null
    $lastT = $null
    foreach ($r in $Rows) {
        if ($r.T -le $From -or $r.T -gt $To) { continue }
        $n++
        $lastT = $r.T
        if ($null -ne $r.CpuDelta -and $null -ne $r.Dt -and $r.Dt -gt 0) {
            $rate = $r.CpuDelta / $r.Dt
            $sumDelta += $r.CpuDelta; $sumDt += $r.Dt; $intervals++
            if ($null -eq $maxRate -or $rate -gt $maxRate) { $maxRate = $rate }
        }
        if ($null -ne $r.Private) { $privSum += $r.Private; $privN++; if ($null -eq $privMax -or $r.Private -gt $privMax) { $privMax = $r.Private } }
        if ($null -ne $r.Ws) { $wsSum += $r.Ws; $wsN++; if ($null -eq $wsMax -or $r.Ws -gt $wsMax) { $wsMax = $r.Ws } }
        if ($null -ne $r.Handles) { $hSum += $r.Handles; $hN++; if ($null -eq $hMax -or $r.Handles -gt $hMax) { $hMax = $r.Handles } }
    }
    $meanRate = $null
    if ($sumDt -gt 0) { $meanRate = $sumDelta / $sumDt }
    $mb = 1048576.0
    return [ordered]@{
        window_from_s = $From
        window_to_s = $To
        samples = $n
        cpu_intervals = $intervals
        complete = ($null -ne $lastT -and $lastT -ge $To - 1.5)
        cpu_percent_mean = $(if ($null -ne $meanRate) { Round-Or-Null ($meanRate / $Cpus * 100.0) 4 } else { $null })
        cpu_percent_max = $(if ($null -ne $maxRate) { Round-Or-Null ($maxRate / $Cpus * 100.0) 4 } else { $null })
        cpu_ms_per_s_mean = $(if ($null -ne $meanRate) { Round-Or-Null ($meanRate * 1000.0) 2 } else { $null })
        cpu_ms_per_s_max = $(if ($null -ne $maxRate) { Round-Or-Null ($maxRate * 1000.0) 2 } else { $null })
        private_mb_mean = $(if ($privN -gt 0) { Round-Or-Null ($privSum / $privN / $mb) 2 } else { $null })
        private_mb_max = $(if ($null -ne $privMax) { Round-Or-Null ($privMax / $mb) 2 } else { $null })
        working_set_mb_mean = $(if ($wsN -gt 0) { Round-Or-Null ($wsSum / $wsN / $mb) 2 } else { $null })
        working_set_mb_max = $(if ($null -ne $wsMax) { Round-Or-Null ($wsMax / $mb) 2 } else { $null })
        handles_mean = $(if ($hN -gt 0) { Round-Or-Null ($hSum / $hN) 1 } else { $null })
        handles_max = $hMax
    }
}

# First-window mean against last-window mean and a least-squares slope per hour of one field ('Private' in MB, 'Handles' as is).
function Get-Trend($Rows, [string]$Field, [double]$Scale, [double]$WindowSeconds = 600) {
    $ts = New-Object System.Collections.ArrayList
    $vs = New-Object System.Collections.ArrayList
    foreach ($r in $Rows) {
        $v = $r[$Field]
        if ($null -ne $v) { [void]$ts.Add([double]$r.T); [void]$vs.Add([double]$v / $Scale) }
    }
    if ($ts.Count -lt 2) { return $null }
    $t0 = $ts[0]; $t1 = $ts[$ts.Count - 1]
    $w = [math]::Min($WindowSeconds, ($t1 - $t0) / 2.0)
    $firstSum = 0.0; $firstN = 0; $lastSum = 0.0; $lastN = 0
    $sx = 0.0; $sy = 0.0; $sxx = 0.0; $sxy = 0.0
    for ($i = 0; $i -lt $ts.Count; $i++) {
        $x = $ts[$i] / 3600.0
        $y = $vs[$i]
        $sx += $x; $sy += $y; $sxx += $x * $x; $sxy += $x * $y
        if ($ts[$i] -le $t0 + $w) { $firstSum += $y; $firstN++ }
        if ($ts[$i] -ge $t1 - $w) { $lastSum += $y; $lastN++ }
    }
    $n = $ts.Count
    $den = $n * $sxx - $sx * $sx
    $slope = $null
    if ($den -ne 0) { $slope = ($n * $sxy - $sx * $sy) / $den }
    $firstMean = $firstSum / [math]::Max(1, $firstN)
    $lastMean = $lastSum / [math]::Max(1, $lastN)
    return [ordered]@{
        samples = $n
        window_seconds_used = [math]::Round($w, 1)
        first_window_mean = Round-Or-Null $firstMean 3
        last_window_mean = Round-Or-Null $lastMean 3
        change = Round-Or-Null ($lastMean - $firstMean) 3
        slope_per_hour = Round-Or-Null $slope 4
    }
}

# ---- running the app ----

function Start-App([string]$MarkFile) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:ExePath
    $psi.WorkingDirectory = Split-Path -Parent $script:ExePath
    $psi.UseShellExecute = $false
    $arguments = '--measure "' + $MarkFile + '"'
    if ($script:FakesOn) { $arguments += ' --fakes' }
    $psi.Arguments = $arguments
    return [System.Diagnostics.Process]::Start($psi)
}

function Start-TaskMgr {
    $path = Join-Path $env:SystemRoot 'System32\taskmgr.exe'
    $before = @{}
    foreach ($p in [System.Diagnostics.Process]::GetProcessesByName('Taskmgr')) { $before[$p.Id] = $true; $p.Dispose() }
    try { Start-Process -FilePath $path | Out-Null }
    catch { throw ('Task Manager did not start (' + (Get-ShortError $_) + '). If the UAC prompt was declined, run again and accept it.') }
    $limit = [Diagnostics.Stopwatch]::StartNew()
    while ($limit.Elapsed.TotalSeconds -lt 90) {
        foreach ($p in [System.Diagnostics.Process]::GetProcessesByName('Taskmgr')) {
            if (-not $before.ContainsKey($p.Id)) { return $p }
            $p.Dispose()
        }
        [Threading.Thread]::Sleep(250)
    }
    throw 'Task Manager did not appear within 90 s'
}

# CloseMainWindow, wait; kill only after the timeout and say so. With -NoKill the process is left alone (Task Manager).
function Close-Politely([System.Diagnostics.Process]$Proc, [int]$TimeoutSeconds, [switch]$NoKill) {
    $info = [ordered]@{ method = $null; waited_seconds = $null; exit_code = $null; note = $null }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    if (-not (Test-Alive $Proc)) {
        $info.method = 'already-exited'
        $info.note = 'the process had already ended when the script went to close it'
    }
    else {
        $sent = $false
        try { $Proc.Refresh(); if ($Proc.MainWindowHandle -ne [IntPtr]::Zero) { $sent = $Proc.CloseMainWindow() } } catch { }
        $ended = $false
        try { $ended = $Proc.WaitForExit($TimeoutSeconds * 1000) } catch { $ended = -not (Test-Alive $Proc) }
        if ($ended) { $info.method = 'CloseMainWindow' }
        elseif ($NoKill) {
            $info.method = 'left-running'
            $info.note = 'CloseMainWindow did not end the process in ' + $TimeoutSeconds + ' s (sent=' + $sent + ')'
        }
        else {
            $info.method = 'Kill'
            $info.note = 'CloseMainWindow (sent=' + $sent + ') did not end the process within ' + $TimeoutSeconds + ' s; it was killed'
            try { $Proc.Kill(); [void]$Proc.WaitForExit(10000) } catch { $info.note += '; Kill failed: ' + (Get-ShortError $_) }
        }
    }
    $info.waited_seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    try { if ($Proc.HasExited) { $info.exit_code = $Proc.ExitCode } } catch { }
    return $info
}

function Write-CsvRow($Writer, $r) {
    $inv = Get-Inv
    $fields = @(
        $r.T.ToString('0.000', $inv)
        $r.Role
        $r.Name
        $(if ($null -ne $r.ProcId) { [string]$r.ProcId } else { '' })
        $(if ($null -ne $r.StartUtc) { $r.StartUtc.ToString('o', $inv) } else { '' })
        $(if ($null -ne $r.Dt) { $r.Dt.ToString('0.000', $inv) } else { '' })
        $(if ($null -ne $r.Cpu) { $r.Cpu.ToString('0.000', $inv) } else { '' })
        $(if ($null -ne $r.CpuDelta) { $r.CpuDelta.ToString('0.0000', $inv) } else { '' })
        $(if ($null -ne $r.Private) { [string]$r.Private } else { '' })
        $(if ($null -ne $r.Ws) { [string]$r.Ws } else { '' })
        $(if ($null -ne $r.Handles) { [string]$r.Handles } else { '' })
    )
    $Writer.WriteLine([string]::Join(',', $fields))
}

function Get-SeriesSummary([hashtable]$S) {
    $inv = Get-Inv
    $missing = New-Object System.Collections.ArrayList
    foreach ($k in $S.Missing.Keys) { [void]$missing.Add([ordered]@{ counter = $k; reason = $S.Missing[$k]; ticks = $S.MissingTicks[$k] }) }
    return [ordered]@{
        role = $S.Role
        name = $S.Name
        pid = $S.ProcId
        start_utc = $(if ($null -ne $S.StartUtc) { $S.StartUtc.ToString('o', $inv) } else { $null })
        start_unreadable_reason = $S.StartReason
        first_t_s = [math]::Round($S.FirstT, 1)
        last_t_s = [math]::Round($S.LastT, 1)
        samples = $S.Samples
        ended = $S.Ended
        missing_counters = $missing
    }
}

# One measured run (or, with -Warmup, an unmeasured one that only waits for the marks). Returns the run's summary.
function Invoke-MeasuredRun {
    [CmdletBinding()]
    param(
        [ValidateSet('pcmieru', 'taskmgr')][string]$Target,
        [string]$Label,
        [string]$RunDir,
        [double]$DurationSeconds,
        [int]$Cpus,
        [switch]$LongMode,
        [switch]$Warmup
    )

    $inv = Get-Inv
    $isApp = ($Target -eq 'pcmieru')
    $markFile = Join-Path $RunDir ($Label + '-marks.jsonl')
    $csvName = $Label + '.csv'
    $writer = $null
    if (-not $Warmup) {
        $writer = New-Object System.IO.StreamWriter((Join-Path $RunDir $csvName), $false, (New-Object System.Text.UTF8Encoding($false)))
        $writer.NewLine = "`n"
        $writer.WriteLine('t_s,role,name,pid,start_utc,dt_s,cpu_s,cpu_delta_s,private_bytes,working_set_bytes,handles')
    }

    $mainRows = New-Object System.Collections.ArrayList
    $totalRows = New-Object System.Collections.ArrayList
    $events = New-Object System.Collections.ArrayList
    $warnings = New-Object System.Collections.ArrayList
    $children = New-Object System.Collections.ArrayList
    $finished = New-Object System.Collections.ArrayList
    $seen = @{}
    $proc = $null
    $main = $null
    $ended = 'duration reached'
    $close = $null
    $marks = $null
    $nav = @{ Nav = $null }
    $pageTries = 0; $pageOk = 0
    $churnStarted = 0; $churnFailed = 0
    $markDoneT = $null

    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        if ($isApp) { $proc = Start-App $markFile }
        else { $proc = Start-TaskMgr; $clock.Restart() }
        $startNow = [DateTime]::UtcNow
        $main = New-Series $proc 'main' 0.0 $startNow $true
        if ($isApp -and $null -ne $main.StartUtc) {
            $main.PrevT = 0.0   # the stopwatch was started just before the process; the creation time is not used for the zero
        }
        $prevTickT = 0.0
        $next = 1.0
        $lastLog = 0.0
        $nextPage = [double]$script:NavPageSeconds
        $nextChurn = [double]$script:ChurnSeconds

        while ($true) {
            $now = $clock.Elapsed.TotalSeconds
            if ($now -lt $next) { [Threading.Thread]::Sleep([int][math]::Max(1, [math]::Floor(($next - $now) * 1000.0) - 1)); continue }
            $t = $clock.Elapsed.TotalSeconds
            $nowUtc = [DateTime]::UtcNow
            $next = $next + 1.0
            if ($next -le $t) { $next = [math]::Floor($t) + 1.0 }

            if (-not (Test-Alive $proc)) { $ended = 'the process ended by itself'; $main.Ended = $true; break }

            if ($isApp) {
                foreach ($p in (Find-NewChildren $main $seen)) {
                    [void]$children.Add((New-Series $p 'child' $t $nowUtc $false))
                }
            }

            $tick = New-Object System.Collections.ArrayList
            $mainRow = Read-Series $main $t
            [void]$tick.Add($mainRow)
            foreach ($c in @($children)) {
                if (-not (Test-Alive $c.Proc)) {
                    $c.Ended = $true
                    [void]$finished.Add($c)
                    $children.Remove($c)
                    continue
                }
                [void]$tick.Add((Read-Series $c $t))
            }

            $total = @{ T = $t; Role = 'total'; Name = 'total'; ProcId = $null; StartUtc = $null; Dt = ($t - $prevTickT); Cpu = $null; CpuDelta = $null; Private = 0L; Ws = 0L; Handles = 0 }
            foreach ($r in $tick) {
                if ($null -ne $r.CpuDelta) { $total.CpuDelta = $(if ($null -eq $total.CpuDelta) { $r.CpuDelta } else { $total.CpuDelta + $r.CpuDelta }) }
                foreach ($pair in @(@('Private', 'Private'), @('Ws', 'Ws'), @('Handles', 'Handles'))) {
                    $f = $pair[0]
                    if ($null -eq $total[$f] -or $null -eq $r[$f]) { $total[$f] = $null } else { $total[$f] = $total[$f] + $r[$f] }
                }
            }
            $prevTickT = $t

            [void]$mainRows.Add($mainRow)
            [void]$totalRows.Add($total)
            if ($null -ne $writer) {
                foreach ($r in $tick) { Write-CsvRow $writer $r }
                Write-CsvRow $writer $total
                if ([int][math]::Floor($t) % 10 -eq 0) { $writer.Flush() }
            }

            if ($isApp -and ($null -eq $marks -or $null -eq $marks.first_list -or $null -eq $marks.cpu_list)) {
                $marks = Read-Marks $markFile
                if ($null -ne $marks.first_list -and $null -ne $marks.cpu_list -and $null -eq $markDoneT) { $markDoneT = $t }
            }

            if ($Warmup) {
                if ($null -ne $markDoneT -and $t -ge $markDoneT + 5) { $ended = 'warm-up done'; break }
                if ($t -ge 65) { $ended = 'warm-up: the marks did not come within 65 s'; break }
            }
            elseif ($t -ge $DurationSeconds) { break }

            if ($LongMode) {
                if ($t -ge $nextPage) {
                    $nextPage += $script:NavPageSeconds
                    $step = Step-NavPage $proc $nav
                    $pageTries++
                    if ($step.ok) { $pageOk++ }
                    [void]$events.Add([ordered]@{ t_s = [math]::Round($t, 1); kind = 'page'; ok = $step.ok; from = $step.from; to = $step.to; count = $step.count; note = $step.note })
                }
                if ($t -ge $nextChurn) {
                    $nextChurn += $script:ChurnSeconds
                    $churnStarted++
                    try {
                        $churn = New-Object System.Diagnostics.ProcessStartInfo
                        $churn.FileName = Join-Path $env:SystemRoot 'System32\cmd.exe'
                        $churn.Arguments = '/c exit'
                        $churn.UseShellExecute = $false
                        $churn.CreateNoWindow = $true
                        $cp = [System.Diagnostics.Process]::Start($churn)
                        if (-not $cp.WaitForExit(5000)) { $churnFailed++; try { $cp.Kill() } catch { } }
                        $cp.Dispose()
                    }
                    catch { $churnFailed++ }
                }
                if ($t - $lastLog -ge 300) {
                    $lastLog = $t
                    Write-Host ('  long run: {0:N0} of {1:N0} s, private {2} MB, handles {3}' -f $t, $DurationSeconds, (Fmt ($mainRow.Private / 1048576.0) '0.0'), (Fmt $mainRow.Handles '0'))
                }
            }
        }
    }
    finally {
        if ($null -ne $writer) { $writer.Flush(); $writer.Dispose() }
    }

    # close
    if ($null -ne $proc) {
        if ($isApp) { $close = Close-Politely $proc $script:CloseTimeoutSeconds }
        else {
            $close = Close-Politely $proc 15 -NoKill
            if ($close.method -eq 'left-running') {
                Write-Host '  Task Manager runs elevated and could not be closed from here. Close it yourself (waiting up to 3 minutes).' -ForegroundColor Yellow
                $wait = [Diagnostics.Stopwatch]::StartNew()
                while ((Test-Alive $proc) -and $wait.Elapsed.TotalSeconds -lt 180) { [Threading.Thread]::Sleep(500) }
                if (Test-Alive $proc) { throw 'Task Manager is still open: close it and run again' }
                $close.note = $close.note + '; it was closed by hand'
            }
        }
    }
    if ($isApp -and $null -ne $main) {
        $limit = [Diagnostics.Stopwatch]::StartNew()
        foreach ($c in @($children)) {
            $c.Ended = $true
            while ((Test-Alive $c.Proc) -and $limit.Elapsed.TotalSeconds -lt 10) { [Threading.Thread]::Sleep(200) }
            if (Test-Alive $c.Proc) {
                [void]$warnings.Add('a ' + $script:SensorHostName + ' (pid ' + $c.ProcId + ') was still running 10 s after the GUI closed; it was killed')
                try { $c.Proc.Kill() } catch { }
            }
            [void]$finished.Add($c)
        }
        $children.Clear()
    }
    if ($isApp) { $marks = Read-Marks $markFile }

    $run = [ordered]@{
        label = $Label
        target = $Target
        csv = $(if ($Warmup) { $null } else { $csvName })
        marks_file = $(if ($isApp) { $Label + '-marks.jsonl' } else { $null })
        ended = $ended
        closed = $close
        duration_s = $(if ($mainRows.Count -gt 0) { [math]::Round($mainRows[$mainRows.Count - 1].T, 1) } else { 0 })
        samples = $mainRows.Count
        clock_zero = $(if ($isApp) { 'just before the process was started' } else { 'when the Task Manager process was first seen (after any UAC prompt)' })
    }
    if ($Warmup) { return $run }

    $created = $null
    if ($null -ne $main) { $created = $main.StartUtc }
    $run['startup'] = $(if ($isApp) { Get-Startup $marks $created } else { [ordered]@{ note = 'Task Manager has no marks: startup is missing'; first_list_ms = $null; cpu_list_ms = $null } })
    $series = New-Object System.Collections.ArrayList
    if ($null -ne $main) { [void]$series.Add((Get-SeriesSummary $main)) }
    foreach ($c in $finished) { [void]$series.Add((Get-SeriesSummary $c)) }
    $run['series'] = $series
    $run['child_series_count'] = $finished.Count

    $windows = [ordered]@{}
    if ($LongMode) {
        $windows['all'] = [ordered]@{
            main = (Get-WindowStats $mainRows 0 $DurationSeconds $Cpus)
            total = (Get-WindowStats $totalRows 0 $DurationSeconds $Cpus)
        }
    }
    else {
        $first = [double]$script:FirstWindowSeconds
        $windows['first_30s'] = [ordered]@{
            main = (Get-WindowStats $mainRows 0 $first $Cpus)
            total = (Get-WindowStats $totalRows 0 $first $Cpus)
        }
        $windows['steady'] = [ordered]@{
            main = (Get-WindowStats $mainRows $first $DurationSeconds $Cpus)
            total = (Get-WindowStats $totalRows $first $DurationSeconds $Cpus)
        }
    }
    $run['windows'] = $windows

    if ($LongMode) {
        $run['long'] = [ordered]@{
            page_switching = [ordered]@{
                method = 'PostMessage of VK_DOWN (VK_HOME after the last page) to the navigation list box; no focus needed, none taken'
                needs_focus = $false
                attempts = $pageTries
                changed_page = $pageOk
            }
            test_processes = [ordered]@{ command = 'cmd /c exit'; every_seconds = $script:ChurnSeconds; started = $churnStarted; failed = $churnFailed }
            trend = [ordered]@{
                main_private_mb = (Get-Trend $mainRows 'Private' 1048576.0)
                main_handles = (Get-Trend $mainRows 'Handles' 1.0)
                total_private_mb = (Get-Trend $totalRows 'Private' 1048576.0)
                total_handles = (Get-Trend $totalRows 'Handles' 1.0)
            }
            events = $events
        }
    }

    $missingAny = New-Object System.Collections.ArrayList
    foreach ($s in $series) { foreach ($m in $s.missing_counters) { [void]$missingAny.Add([ordered]@{ series = ($s.name + ' pid ' + $s.pid); counter = $m.counter; reason = $m.reason; ticks = $m.ticks }) } }
    $run['missing'] = $missingAny
    $run['warnings'] = $warnings
    return $run
}

# ---- report ----

function Write-RunReport($Run, [string]$Mode) {
    $name = $Run.target
    Write-Host ('--- {0}: {1} ---' -f $Run.label, $name) -ForegroundColor Cyan
    Write-Host ('  ended: {0}; closed by: {1}; {2} samples' -f $Run.ended, $(if ($null -ne $Run.closed) { $Run.closed.method } else { 'n/a' }), $Run.samples)
    if ($null -ne $Run.closed -and $null -ne $Run.closed.note) { Write-Host ('  note: ' + $Run.closed.note) -ForegroundColor Yellow }
    $s = $Run.startup
    if ($Run.target -eq 'pcmieru') {
        Write-Host ('  startup  first list {0} ms   cpu list {1} ms    [target: warm start < 1000 ms]' -f (Fmt $s.first_list_ms '0.0'), (Fmt $s.cpu_list_ms '0.0'))
        if ($null -ne $s.note) { Write-Host ('  startup note: ' + $s.note) -ForegroundColor Yellow }
    }
    foreach ($w in $Run.windows.Keys) {
        foreach ($who in @('main', 'total')) {
            $x = $Run.windows[$w][$who]
            $label = $(if ($who -eq 'main') { 'GUI alone' } else { 'total    ' })
            Write-Host ('  {0,-9} {1}: cpu mean {2} % ({3} ms/s) max {4} % ({5} ms/s); private mean {6} max {7} MB; ws mean {8} max {9} MB; handles mean {10} max {11}{12}' -f `
                $w, $label, (Fmt $x.cpu_percent_mean '0.000'), (Fmt $x.cpu_ms_per_s_mean '0.0'), (Fmt $x.cpu_percent_max '0.000'), (Fmt $x.cpu_ms_per_s_max '0.0'),
                (Fmt $x.private_mb_mean '0.0'), (Fmt $x.private_mb_max '0.0'), (Fmt $x.working_set_mb_mean '0.0'), (Fmt $x.working_set_mb_max '0.0'),
                (Fmt $x.handles_mean '0'), (Fmt $x.handles_max '0'), $(if ($x.complete) { '' } else { '  (window not complete)' }))
        }
    }
    if ($Run.Contains('long') -and $null -ne $Run['long']) {
        $l = $Run['long']
        Write-Host ('  pages: {0} switches tried, {1} changed the page; test processes: {2} started, {3} failed' -f $l.page_switching.attempts, $l.page_switching.changed_page, $l.test_processes.started, $l.test_processes.failed)
        foreach ($k in $l.trend.Keys) {
            $tr = $l.trend[$k]
            if ($null -eq $tr) { Write-Host ('  trend {0}: missing' -f $k); continue }
            Write-Host ('  trend {0}: first {1} min mean {2}, last mean {3}, change {4}, slope {5} per hour' -f $k, (Fmt ($tr.window_seconds_used / 60.0) '0.0'), (Fmt $tr.first_window_mean '0.000'), (Fmt $tr.last_window_mean '0.000'), (Fmt $tr.change '0.000'), (Fmt $tr.slope_per_hour '0.000'))
        }
    }
    foreach ($m in $Run.missing) { Write-Host ('  missing: {0} {1}: {2}' -f $m.series, $m.counter, $m.reason) -ForegroundColor Yellow }
    foreach ($w in $Run.warnings) { Write-Host ('  warning: ' + $w) -ForegroundColor Yellow }
}

function Write-Targets {
    Write-Host 'Targets (printed, not judged here): private bytes < 100 MB; steady CPU mean < 0.3 % and < 10 ms/s; warm start < 1 s.' -ForegroundColor DarkGray
}

# ---- main ----

function Save-Summary([System.Collections.IDictionary]$Summary, [string]$Path) {
    $json = $Summary | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($Path, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
}

function Add-Aggregate([System.Collections.IDictionary]$Summary, [string]$Target, $RunList) {
    $mine = @($RunList | Where-Object { $_.target -eq $Target })
    if ($mine.Count -eq 0) { return }
    $agg = [ordered]@{
        runs = $mine.Count
        startup_first_list_ms = (Get-Aggregate @($mine | ForEach-Object { $_.startup.first_list_ms }))
        startup_cpu_list_ms = (Get-Aggregate @($mine | ForEach-Object { $_.startup.cpu_list_ms }))
    }
    foreach ($w in @('first_30s', 'steady', 'all')) {
        if (-not $mine[0].windows.Contains($w)) { continue }
        foreach ($who in @('main', 'total')) {
            $agg[$w + '_' + $who] = [ordered]@{
                cpu_percent_mean = (Get-Aggregate @($mine | ForEach-Object { $_.windows[$w][$who].cpu_percent_mean }))
                cpu_ms_per_s_mean = (Get-Aggregate @($mine | ForEach-Object { $_.windows[$w][$who].cpu_ms_per_s_mean }))
                private_mb_mean = (Get-Aggregate @($mine | ForEach-Object { $_.windows[$w][$who].private_mb_mean }))
                private_mb_max = (Get-Aggregate @($mine | ForEach-Object { $_.windows[$w][$who].private_mb_max }))
                working_set_mb_mean = (Get-Aggregate @($mine | ForEach-Object { $_.windows[$w][$who].working_set_mb_mean }))
                handles_mean = (Get-Aggregate @($mine | ForEach-Object { $_.windows[$w][$who].handles_mean }))
            }
        }
    }
    $Summary.aggregate[$Target] = $agg
}

function Invoke-Main {
    $oldCulture = [Threading.Thread]::CurrentThread.CurrentCulture
    [Threading.Thread]::CurrentThread.CurrentCulture = (Get-Inv)
    try {
        if ([string]::IsNullOrEmpty($Exe)) { throw '-Exe is required: the published PCMieru.exe' }
        if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "-Exe not found: $Exe" }
        if ($Seconds -lt 1) { throw '-Seconds must be 1 or more' }
        if ($Runs -lt 1) { throw '-Runs must be 1 or more' }
        if ($LongHours -le 0) { throw '-LongHours must be above 0' }
        $script:ExePath = (Resolve-Path -LiteralPath $Exe).Path
        $script:FakesOn = [bool]$Fakes

        if ([string]::IsNullOrEmpty($Output)) { $Output = Join-Path ([IO.Path]::GetTempPath()) ('pcmieru-measure-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss')) }
        $Output = [IO.Path]::GetFullPath($Output)
        $summaryPath = Join-Path $Output 'summary.json'
        if (Test-Path -LiteralPath $summaryPath) { throw "the output folder already holds a summary.json; use a new folder: $Output" }
        New-Item -ItemType Directory -Force -Path $Output | Out-Null

        Write-Host ('PC Mieru measure.ps1 - mode {0}{1}' -f $Mode, $(if ($Fakes) { ' - FAKES (a trial: the numbers do not describe the product)' } else { '' })) -ForegroundColor Cyan
        Write-Targets
        $environment = Get-EnvironmentInfo $script:ExePath
        $cpus = [int]$environment.logical_cpus
        Write-Host ('environment: {0} / {1} logical CPUs / {2} GB / power plan {3} / uptime {4} min' -f $environment.os_build, $cpus, (Fmt $environment.total_memory_gb '0.0'), $environment.power_plan, (Fmt $environment.uptime_minutes '0'))

        $summary = [ordered]@{
            tool = 'tools/measure.ps1'
            mode = $Mode
            fakes = [bool]$Fakes
            fakes_note = $(if ($Fakes) { 'the GUI ran with --fakes (fake sensors and data): a trial of the script, not a measurement of the product' } else { $null })
            environment = $environment
            parameters = [ordered]@{
                seconds = $Seconds
                first_window_seconds = $script:FirstWindowSeconds
                runs = $Runs
                long_hours = $(if ($Mode -eq 'long') { $LongHours } else { $null })
                sample_interval_seconds = 1
                close_timeout_seconds = $script:CloseTimeoutSeconds
            }
            targets = [ordered]@{
                private_mb_below = 100
                steady_cpu_percent_below = 0.3
                steady_cpu_ms_per_s_below = 10
                warm_start_seconds_below = 1
                note = 'printed next to the results; nothing here is judged pass/fail in code'
            }
            zip = $null
            temperature = $null
            warmup = $null
            runs = (New-Object System.Collections.ArrayList)
            aggregate = [ordered]@{}
        }

        if (-not [string]::IsNullOrEmpty($Zip)) {
            if (Test-Path -LiteralPath $Zip -PathType Leaf) {
                $item = Get-Item -LiteralPath $Zip
                $mb = [math]::Round($item.Length / 1048576.0, 1)
                $summary.zip = [ordered]@{ file = $item.Name; bytes = $item.Length; mb = $mb; v0_2_0_mb = $script:ZipV020Mb; difference_mb = [math]::Round($mb - $script:ZipV020Mb, 1) }
                Write-Host ('ZIP: {0} MB (v0.2.0: {1} MB, difference {2} MB)' -f (Fmt $mb '0.0'), (Fmt $script:ZipV020Mb '0.0'), (Fmt ($mb - $script:ZipV020Mb) '+0.0;-0.0'))
            }
            else {
                $summary.zip = [ordered]@{ file = $null; note = 'the -Zip file was not found: its size is missing' }
                Write-Host 'ZIP: not found (size missing)' -ForegroundColor Yellow
            }
        }

        $stamp = $Mode
        $count = 0

        if ($Mode -eq 'long') {
            $summary.temperature = 'warm (a long run starts after an unmeasured run, like warm)'
            $summary.warmup = (Invoke-MeasuredRun -Target pcmieru -Label 'long-warmup' -RunDir $Output -DurationSeconds 0 -Cpus $cpus -Warmup)
            Write-Host ('warm-up run: {0}' -f $summary.warmup.ended)
            [Threading.Thread]::Sleep(5000)
            $run = Invoke-MeasuredRun -Target pcmieru -Label 'long-r1-pcmieru' -RunDir $Output -DurationSeconds ($LongHours * 3600.0) -Cpus $cpus -LongMode
            [void]$summary.runs.Add($run)
            Write-RunReport $run $Mode
            Add-Aggregate $summary 'pcmieru' $summary.runs
            Save-Summary $summary $summaryPath
        }
        elseif ($Mode -eq 'cold') {
            $summary.temperature = 'cold: one run, no warm-up; meant to be started right after an OS reboot'
            if ($null -ne $environment.uptime_minutes -and $environment.uptime_minutes -gt 15) {
                Write-Host ('WARNING: the machine has been up for {0} minutes: this is not a cold start. Reboot and run again right after the sign-in.' -f (Fmt $environment.uptime_minutes '0')) -ForegroundColor Yellow
            }
            if ($Runs -ne 1) { Write-Host 'cold mode makes exactly one run (-Runs is ignored)' -ForegroundColor Yellow }
            $run = Invoke-MeasuredRun -Target pcmieru -Label 'cold-r1-pcmieru' -RunDir $Output -DurationSeconds ($script:FirstWindowSeconds + $Seconds) -Cpus $cpus
            $run['uptime_minutes_at_start'] = $environment.uptime_minutes
            [void]$summary.runs.Add($run)
            Write-RunReport $run $Mode
            Add-Aggregate $summary 'pcmieru' $summary.runs
            Save-Summary $summary $summaryPath
        }
        else {
            $summary.temperature = 'warm (after one unmeasured run)'
            if ($Mode -eq 'compare') {
                $open = @([System.Diagnostics.Process]::GetProcessesByName('Taskmgr'))
                if ($open.Count -gt 0) { throw 'Task Manager is open: close it and run again (compare starts and ends its own)' }
            }
            $summary.warmup = (Invoke-MeasuredRun -Target pcmieru -Label ($Mode + '-warmup') -RunDir $Output -DurationSeconds 0 -Cpus $cpus -Warmup)
            Write-Host ('warm-up run (not measured): {0}' -f $summary.warmup.ended)
            $duration = $script:FirstWindowSeconds + $Seconds
            for ($i = 1; $i -le $Runs; $i++) {
                $targets = @('pcmieru')
                if ($Mode -eq 'compare') { $targets = @('pcmieru', 'taskmgr') }
                foreach ($target in $targets) {
                    [Threading.Thread]::Sleep(5000)
                    $run = Invoke-MeasuredRun -Target $target -Label ('{0}-r{1}-{2}' -f $Mode, $i, $target) -RunDir $Output -DurationSeconds $duration -Cpus $cpus
                    [void]$summary.runs.Add($run)
                    Write-RunReport $run $Mode
                    Save-Summary $summary $summaryPath
                }
            }
            Add-Aggregate $summary 'pcmieru' $summary.runs
            if ($Mode -eq 'compare') { Add-Aggregate $summary 'taskmgr' $summary.runs }
            Save-Summary $summary $summaryPath
        }

        Write-Targets
        Write-Host ('summary: {0}' -f $summaryPath)
        return $summary
    }
    finally {
        [Threading.Thread]::CurrentThread.CurrentCulture = $oldCulture
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $null = Invoke-Main
}
