<#
.SYNOPSIS
  Measures a published PC Mieru (and, for -Mode compare, Windows Task Manager) from the outside and writes raw CSV plus summary.json.

.DESCRIPTION
  DESIGN-A section 10. The script only reads the process from outside (CPU time, cycle time, private bytes, working set, handle
  count, once a second); nobody reads the screen. It never elevates itself, never trims a working set, and never reboots anything.

  How a process is read: through one small Win32 reader (Add-Type P/Invoke) that opens the process with
  PROCESS_QUERY_LIMITED_INFORMATION only: GetProcessTimes (creation time, CPU time), QueryProcessCycleTime, GetProcessHandleCount
  and GetProcessMemoryInfo (private bytes, working set). That right is granted for a process of a HIGHER integrity level too, so a
  normal shell can read an elevated Task Manager; Process.StartTime and Process.TotalProcessorTime (which ask for
  PROCESS_QUERY_INFORMATION) cannot, and are not used. The same reader is used for PCMieru, its SensorHost and Task Manager, in
  Windows PowerShell 5.1 and in pwsh. One handle is kept per process series and closed when the series ends. summary.json records
  the script's own elevation (is_elevated) and, for every process read, its integrity level (from its token; "unknown" with the
  reason when Windows refuses the token).

  What is recorded
    * Environment (no serial numbers, no PC name, no user name, no folder paths): OS build, logical CPU count, CPU name, total memory,
      power plan name, GPU name and driver version, the exe's file version and SHA256, the publish setting if it can be read
      (self-contained, ReadyToRun, the runtimeconfig properties), the date, the uptime.
    * Every second, per process: CPU time, cycle time, private bytes, working set, handle count of the GUI (PCMieru.exe) and of each of
      its PCMieru.SensorHost children. A process is tracked by PID and creation time: a SensorHost that exits and is started again is
      a new series, and two processes are never joined into one series (a delta is always taken inside one series). Rows are written
      for "main" (the GUI alone), "child" (each SensorHost) and "total" (main plus the children alive at that second). The total
      working set may count shared pages twice. A SensorHost is taken as the GUI's child by its parent process id; when that lookup
      (or its creation time) fails it is tried again at the next sample, up to 10 times, and only then given up with a warning that
      names the pid (a failed lookup never marks a process "not ours" at once).
    * CPU: dCPU seconds / dwall seconds / logical CPUs * 100 (percent of the whole machine) and dCPU seconds / dwall seconds * 1000
      (ms per second). A window's mean is total CPU / total wall time of the window; its max is the largest one-second interval.
      CPU TIME IS TICK-CHARGED: Windows adds a whole scheduler tick (about 15.6 ms) to the thread that is running when the tick
      comes, so for short work driven by a timer (what a monitor does) the figure can be biased up or down, not only blurred, and a
      long mean can still be off in one direction. The CYCLE figure is the cross-check: QueryProcessCycleTime counts the cycles the
      process's threads really ran. cycles_per_s and its GHz-equivalent (cycles per s / 1e9) are printed and recorded next to the CPU
      time. They are in the OS's reference-cycle units, not the clock of the core the thread ran on, so they are NOT turned into a
      percentage here (that would look exact and is not): compare them with each other (PC Mieru against Task Manager, one run
      against another). When the two figures disagree, trust neither alone.
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
             PCMieru gets the unmeasured warm-up run first. Task Manager has no marks (its startup is missing).
             Task Manager is started at YOUR integrity level, never elevated: taskmgr.exe is marked "highest available", so the
             script starts it with the environment variable __COMPAT_LAYER=RunAsInvoker set for that one child process only (the
             script's own environment is not changed). It then runs as an ordinary window of your session, shows the same process
             list, and CloseMainWindow works on it. The comparison is therefore PC Mieru against the NON-elevated Task Manager. The
             summary records taskmgr_launch = "as_invoker" and the integrity level that was measured for it. If Task Manager still
             comes up elevated (integrity high), the script does not try to close it: it records closed_by = "left_open", leaves it
             running and says so at the end (close it yourself). On some Windows 11 builds Task Manager restarts ITSELF elevated
             even when started as the invoker (the started process ends at once and an elevated one takes its place): the script
             then follows the Task Manager that stays (task_manager.measured_pid, task_manager.start_note) and reads it through
             the limited-information handle, which works on an elevated process too. When a Task Manager is already open before a
             run, no new one is started (Windows would only bring the open one to the front): the run measures the open one and
             the summary says it was not a fresh start. A counter that cannot be read is recorded as missing with the reason; the
             script never elevates, never shows or answers a UAC prompt.
             Fairness is checked and recorded, never changed: before each Task Manager run the script READS Task Manager's settings
             (the page it starts on and its small-view flag from settings.json under %LOCALAPPDATA%\Microsoft\Windows\TaskManager;
             the update speed is not stored in a readable place on Windows 11, so it is recorded as unknown) and 5 s into each run
             it reads the window size of both apps (GetWindowRect). A warning goes into the summary and the report when Task Manager
             does not start on Processes, when its update speed cannot be confirmed as Normal, or when the two window sizes
             differ by more than 10 %. The script never writes Task Manager's settings (Task Manager rewrites its own settings.json
             when it closes: that is its own doing). Close any open Task Manager before you start.
    long     One run of -LongHours (default 2). Every minute the GUI is switched to the next page (all pages in a cycle, except the
             checks page: opening it lists the real check history, so the cycle jumps over it and never selects it), and every
             10 s a small test process (cmd /c exit) is started and ended. The page switch posts arrow-key messages to the
             navigation list box of the GUI window (PostMessage), so it needs no focus and takes none from you; the script then
             reads the selection back (LB_GETCURSEL) and records whether the page really changed. If Shift, Ctrl, Alt or a Windows
             key is held at that moment (GetAsyncKeyState) the step is skipped and counted (modifier_skips), because a list box
             reads those keys even from a posted message. The GUI window itself opens in the foreground at the start, as any launch
             does. Private bytes and handles: the mean of the first 10 minutes, the mean of the last 10 minutes, and a least-squares
             slope per hour (the windows shrink to half the run if it is shorter than 20 minutes).
    record   DESIGN-B section 11 step 8: what a recording costs. One unmeasured run first (no recording), then -Runs measured runs
             (-Runs 1 is usual: a run is -LeadSeconds + -RecordSeconds + -AfterSeconds + 5 s long). The GUI is started with the
             test-only recording switches, so it records ITSELF without a click:
               PCMieru.exe --measure <marks.jsonl> --measure-record <folder> [--measure-record-lead <s>] [--measure-record-for <s>]
                           [--measure-record-interval <ms>]
             --measure-record is refused without --measure (one line on stderr, exit 1); the folder is the ONLY place the recording
             writes (run-<id>.json/.csv); it must be a full path outside Documents, %LOCALAPPDATA%\PCMieru and %APPDATA% (the script
             makes it under <Output>, one folder per run, so it is a scratch folder). A measured run still reads and writes no user
             settings (gui.ini is never read or written, with or without the new switches). --measure-record-lead is the wait after the
             window is shown (default 60), --measure-record-for the recording time before the stop (default 180), and
             --measure-record-interval (500, 1000, 2000 or 5000; -UserIntervalMs here) is the USER's sampling interval of that run,
             in memory only. The GUI writes what the script cannot read from outside to <marks name>.record.jsonl next to the marks
             (one JSON object per line: begin, start, period, stop, stopped; the system time in 7 digits and a Z).
             Windows (the first 30 s are startup and are left out, 2 s are left out on each side of the start and of the stop):
             before (idle, the user's interval), recording, after (idle again). Each has the GUI alone, the SensorHost alone and the
             total: private bytes, CPU % and ms/s, cycles. Read from the recording's own CSV (one row per second): per source (core,
             gpu1..N) the fresh / repeat / missing rows and their rate, the periodMs the rows carry, the age of the data at the row,
             frame-number jumps (frames the writer missed), the spacing of the rows, whether the CPU package power, AC and battery
             columns are present or missing. The writer's backlog is not public: it is seen as dropped ticks (the run's own count), rows
             against seconds, frame jumps, the longest row gap and how long the stop took (the writer draining). The period after the
             stop: Core's period is read from the snapshots, one period line per snapshot. What a line carries is the gap Core MEASURED
             since the snapshot before it, not the nominal period, so it jitters (999..1018 ms for a 1000 ms beat): each phase is read
             as a distribution (min / median / mean / max), its median is put to the nearest of 500, 1000, 2000 and 5000 ms, and it
             counts as holding that period when at least 90 % of the gaps are within 25 % of it. Core is under 1000 ms while recording
             and back at the user's interval after the stop (judged on the second half of the after phase and on its last line, as
             the first gaps after the stop may still be the recording's). Use -UserIntervalMs 2000 to see it come back: with the
             default 1000 the two are the same and the script says so. The SensorHost's own period cannot be read from outside: it is
             shown through the CSV's gpu periodMs while recording
             and through the SensorHost's CPU and cycles before / recording / after. With -Fakes the recording is in memory and no CSV
             is written (the script says so).
             TODO (DESIGN-B section 10, the update side): the states "waiting for a check" and "a check is due" are NOT measured here.
             They exist when the update side (src/PcMieru.Update, another worktree) is merged. Each needs its own scenario (the GUI
             idle in that state for -Seconds: steady CPU, private bytes) and a test-only way to put the GUI into it without a network
             call. Add them to this mode (or a -Mode update) then; until then the idle targets are judged for the recording states only.

  Targets (printed next to the results, not judged in code): private bytes < 100 MB, steady CPU mean < 0.3 % and < 10 ms/s, warm
  start < 1 s. -Zip prints the ZIP size next to v0.2.0's 51.6 MB.

  -Fakes starts the GUI with --fakes (fake sensors and fake data). That is for trials of this script only; every output is marked
  "fakes" and the numbers do not describe the product.

  Needs: a published PCMieru.exe, Windows PowerShell 5.1 or pwsh. Leave the machine alone while it runs; a game or another heavy
  program running at the same time makes the numbers worthless.
  Output folder: <Output>\summary.json, <Output>\<mode>-r<N>-<target>.csv (raw, one row per process per second) and the marker files.
  The folder is checked for being writable before any process is started (a file is made and removed); if it is not, the script stops
  with one line and starts nothing. A GUI that cannot make its own marker file also writes one line to stderr and exits 1 (no
  message box), and the script reports that instead of waiting for marks that cannot come.
  Dot-sourcing the script defines its functions and runs nothing.
  ASCII only on purpose (5.1 reads BOM-less files as ANSI).

.PARAMETER Exe
  The published PCMieru.exe.
.PARAMETER Mode
  warm (default), cold, long, compare or record.
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
.PARAMETER RecordSeconds
  record mode: how long the GUI records before it stops (default 180; 5 to 7200).
.PARAMETER AfterSeconds
  record mode: how long the run goes on after the stop (default 60; 10 to 3600).
.PARAMETER LeadSeconds
  record mode: how long after the window is shown the recording starts (default 60; 0 to 3600; the first 30 s are startup, so 40 or more
  leaves a "before" window).
.PARAMETER UserIntervalMs
  record mode: the user's sampling interval of the measured run (500, 1000, 2000 or 5000; default 0 = leave the GUI's own 1000). A
  recording samples every second, so 2000 is how the run shows Core's period going back after the stop.

.EXAMPLE
  powershell -File tools\measure.ps1 -Exe C:\release\PCMieru.exe -Mode warm -Output C:\measure\warm -Zip C:\release\PCMieru-0.4.0-win-x64.zip

.EXAMPLE
  powershell -File tools\measure.ps1 -Exe C:\release\PCMieru.exe -Mode warm -Runs 1 -Seconds 30 -Fakes -Output C:\measure\trial

.EXAMPLE
  powershell -File tools\measure.ps1 -Exe C:\release\PCMieru.exe -Mode long -LongHours 2 -Output C:\measure\long

.EXAMPLE
  powershell -File tools\measure.ps1 -Exe C:\release\PCMieru.exe -Mode record -Runs 1 -RecordSeconds 180 -AfterSeconds 60 -UserIntervalMs 2000 -Output C:\measure\record
#>
[CmdletBinding()]
param(
    [string]$Exe = '',
    [ValidateSet('warm', 'cold', 'long', 'compare', 'record')]
    [string]$Mode = 'warm',
    [string]$Output = '',
    [int]$Seconds = 300,
    [int]$Runs = 3,
    [double]$LongHours = 2,
    [switch]$Fakes,
    [string]$Zip = '',
    [int]$RecordSeconds = 180,
    [int]$AfterSeconds = 60,
    [int]$LeadSeconds = 60,
    [int]$UserIntervalMs = 0
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
# A parent-id or creation-time lookup that fails is tried again at the next sample, this many times in all, and then given up with a warning.
$script:LookupMaxTries = 10
# Seconds into a run when the window sizes are read (compare fairness).
$script:WindowSizeAtSeconds = 5
# Task Manager's window may differ this much (relative, in width or height) from PC Mieru's before a warning is written.
$script:WindowSizeTolerance = 0.10
# Test seams: a script block that answers "parent id of this pid" / "the processes of this name"; $null means the real thing.
$script:ParentIdProvider = $null
$script:ProcessLister = $null
# The title of the checks page in the GUI's navigation list (ja: U+70B9 U+691C; en: Checks). gui.nav.checks in CheckStrings.cs; a test pins both.
$script:ChecksTitles = @('Checks', ([string][char]0x70B9 + [string][char]0x691C))
# record mode: seconds left out of a window on each side of the start and of the stop (the start makes the files, the stop drains the writer),
# and the seconds a run is kept after -LeadSeconds + -RecordSeconds + -AfterSeconds (the GUI's lead runs from its window being shown, and the
# start and the stop take a moment). A recording samples every second (DESIGN-B section 2): Core's period under a recording lease.
$script:RecordGuardSeconds = 2
$script:RecordMarginSeconds = 5
$script:RecordingPeriodMs = 1000
# The periods the GUI can run at (the user's choices), how near one a measured gap must be to count as holding it (a quarter of it), and the share of a
# phase's gaps that must be that near (Core's gap jitters; see Get-PeriodSummary).
$script:NominalPeriodsMs = @(500, 1000, 2000, 5000)
$script:PeriodToleranceFraction = 0.25
$script:PeriodHoldShare = 0.9
# "Back at the user's interval" is judged from the gaps of the phase after the stop, and a few gaps say little: with fewer lines than this it is not judged.
$script:MinAfterPeriodLines = 6

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

# ---- reading a process across integrity levels ----

# One reader for every process (PCMieru, SensorHost, Task Manager). It asks for PROCESS_QUERY_LIMITED_INFORMATION only, which Windows
# grants for a process of a higher integrity level as well (Process.StartTime and TotalProcessorTime need PROCESS_QUERY_INFORMATION and
# fail on an elevated process). Plain C# 5 on purpose: Windows PowerShell 5.1 compiles it with the old CodeDom compiler.
function Initialize-ProcReader {
    if ('PcmProc' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public sealed class PcmOpen
{
    public IntPtr Handle;
    public int Error;
}

public sealed class PcmCounters
{
    public bool HasCreation;
    public DateTime CreationUtc;
    public int TimesError;
    public double CpuSeconds;
    public double Cycles;
    public int CyclesError;
    public long PrivateBytes;
    public long WorkingSet;
    public int MemoryError;
    public int Handles;
    public int HandlesError;
}

public sealed class PcmToken
{
    public int OpenError;
    public bool HasElevation;
    public bool Elevated;
    public int ElevationError;
    public bool HasIntegrity;
    public int IntegrityRid;
    public int IntegrityError;
}

public static class PcmProc
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessHandleCount(IntPtr process, out uint count);

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS_EX counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int length, out int needed);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    private static int Err()
    {
        int e = Marshal.GetLastWin32Error();
        return e == 0 ? -1 : e;
    }

    public static string ErrorText(int code)
    {
        if (code == -1) { return "unknown error"; }
        try { return new Win32Exception(code).Message; } catch (Exception) { return "error " + code; }
    }

    // The pseudo handle of this very process (never closed).
    public static IntPtr Self() { return GetCurrentProcess(); }

    public static PcmOpen Open(int pid)
    {
        PcmOpen r = new PcmOpen();
        r.Handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (r.Handle == IntPtr.Zero) { r.Error = Err(); }
        return r;
    }

    public static void Close(IntPtr handle)
    {
        if (handle != IntPtr.Zero) { CloseHandle(handle); }
    }

    // 1: still running, 0: ended (its exit time is set), -1: could not tell. Needs only the limited-information right.
    public static int RunState(IntPtr handle)
    {
        long creation, exit, kernel, user;
        if (!GetProcessTimes(handle, out creation, out exit, out kernel, out user)) { return -1; }
        return exit > 0 ? 0 : 1;
    }

    // Every counter is read on its own: one that cannot be read has its own error code and the others still come.
    public static PcmCounters Read(IntPtr handle)
    {
        PcmCounters c = new PcmCounters();
        long creation, exit, kernel, user;
        if (GetProcessTimes(handle, out creation, out exit, out kernel, out user))
        {
            c.CpuSeconds = (double)(kernel + user) / 1.0e7;
            if (creation > 0)
            {
                c.HasCreation = true;
                c.CreationUtc = DateTime.FromFileTimeUtc(creation);
            }
        }
        else { c.TimesError = Err(); }

        ulong cycles;
        if (QueryProcessCycleTime(handle, out cycles)) { c.Cycles = (double)cycles; }
        else { c.CyclesError = Err(); }

        PROCESS_MEMORY_COUNTERS_EX mem = new PROCESS_MEMORY_COUNTERS_EX();
        mem.cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX));
        if (GetProcessMemoryInfo(handle, out mem, mem.cb))
        {
            c.PrivateBytes = (long)mem.PrivateUsage.ToUInt64();
            c.WorkingSet = (long)mem.WorkingSetSize.ToUInt64();
        }
        else { c.MemoryError = Err(); }

        uint handles;
        if (GetProcessHandleCount(handle, out handles)) { c.Handles = (int)handles; }
        else { c.HandlesError = Err(); }
        return c;
    }

    // Elevation and integrity level from the token (TOKEN_QUERY on the process's token). Windows may refuse: the error codes say so.
    public static PcmToken Token(IntPtr process)
    {
        PcmToken t = new PcmToken();
        IntPtr token;
        if (!OpenProcessToken(process, TOKEN_QUERY, out token))
        {
            t.OpenError = Err();
            return t;
        }
        try
        {
            IntPtr small = Marshal.AllocHGlobal(16);
            try
            {
                int needed;
                if (GetTokenInformation(token, 20, small, 4, out needed))   // TokenElevation
                {
                    t.HasElevation = true;
                    t.Elevated = Marshal.ReadInt32(small) != 0;
                }
                else { t.ElevationError = Err(); }
            }
            finally { Marshal.FreeHGlobal(small); }

            int length;
            GetTokenInformation(token, 25, IntPtr.Zero, 0, out length);      // TokenIntegrityLevel: ask for the size
            if (length <= 0) { t.IntegrityError = Err(); return t; }
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (GetTokenInformation(token, 25, buffer, length, out length))
                {
                    IntPtr sid = Marshal.ReadIntPtr(buffer);                  // TOKEN_MANDATORY_LABEL.Label.Sid
                    int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    IntPtr last = GetSidSubAuthority(sid, (uint)(count - 1));
                    t.IntegrityRid = Marshal.ReadInt32(last);
                    t.HasIntegrity = true;
                }
                else { t.IntegrityError = Err(); }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
        return t;
    }
}
'@
}

function Get-Win32Reason([string]$Call, [int]$Code) {
    Initialize-ProcReader
    return ('{0} failed: error {1} ({2})' -f $Call, $Code, ([PcmProc]::ErrorText($Code)))
}

function Open-ProcHandle([int]$ProcId) {
    Initialize-ProcReader
    return [PcmProc]::Open($ProcId)
}

function Close-ProcHandle($Handle) {
    if ($null -ne $Handle -and $Handle -ne [IntPtr]::Zero) { [PcmProc]::Close($Handle) }
}

# Integrity level name of a Windows RID.
function Get-IntegrityName([int]$Rid) {
    if ($Rid -ge 0x4000) { return 'system' }
    if ($Rid -ge 0x3000) { return 'high' }
    if ($Rid -ge 0x2100) { return 'medium_plus' }
    if ($Rid -ge 0x2000) { return 'medium' }
    if ($Rid -ge 0x1000) { return 'low' }
    return 'untrusted'
}

# Integrity level and elevation of the process behind a handle (a limited-rights handle is enough to ask). "unknown" with the reason when
# Windows refuses the token; never throws.
function Get-ProcessToken($Handle) {
    $r = [ordered]@{ integrity = 'unknown'; integrity_rid = $null; elevated = $null; reason = $null }
    try {
        Initialize-ProcReader
        if ($null -eq $Handle -or $Handle -eq [IntPtr]::Zero) { $r.reason = 'no process handle'; return $r }
        $t = [PcmProc]::Token($Handle)
        if ($t.OpenError -ne 0) { $r.reason = Get-Win32Reason 'OpenProcessToken' $t.OpenError; return $r }
        if ($t.HasElevation) { $r.elevated = [bool]$t.Elevated }
        if ($t.HasIntegrity) { $r.integrity_rid = [int]$t.IntegrityRid; $r.integrity = Get-IntegrityName ([int]$t.IntegrityRid) }
        else { $r.reason = Get-Win32Reason 'GetTokenInformation(TokenIntegrityLevel)' $t.IntegrityError }
    }
    catch { $r.reason = Get-ShortError $_ }
    return $r
}

# This script's own elevation and integrity level (summary.json: script).
function Get-ScriptToken {
    Initialize-ProcReader
    $token = Get-ProcessToken ([PcmProc]::Self())
    return [ordered]@{ is_elevated = $token.elevated; integrity = $token.integrity; integrity_rid = $token.integrity_rid; reason = $token.reason }
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
    private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutText(IntPtr hwnd, uint msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint LB_GETCOUNT = 0x018B;
    private const uint LB_GETCURSEL = 0x0188;
    private const uint LB_GETTEXT = 0x0189;
    private const uint LB_GETTEXTLEN = 0x018A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    // The main window of a process: its largest visible top-level window that has no owner, leaving out the drop shadow of a tooltip
    // (class SysShadow, unowned and visible while a tooltip shows) and tooltips. Process.MainWindowHandle takes the first such window in
    // z-order, so a tooltip shown under a resting mouse pointer made it the 285 x 40 shadow and WM_CLOSE went nowhere. Zero: none.
    public static IntPtr MainWindowOf(int processId)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = -1;
        EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
        {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            if (owner != (uint)processId || !IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != IntPtr.Zero) { return true; }
            StringBuilder name = new StringBuilder(256);
            GetClassName(hwnd, name, 256);
            string cls = name.ToString();
            if (cls == "SysShadow" || cls.IndexOf("tooltips_class", StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) { return true; }
            long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea) { bestArea = area; best = hwnd; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // What the process's main window is doing, as the window sees it: "normal" (visible, not minimized), "minimized", "hidden" (it has a top-level
    // window and none is visible: the tray) or "none" (no top-level window at all, e.g. the process has not made one yet or is gone). The GUI
    // asks the sampler for slow mode (5 s) while it is minimized or hidden, so these two are the states in which the user interval is not in force.
    public static string WindowStateOf(int processId)
    {
        IntPtr visible = IntPtr.Zero;
        long visibleArea = -1;
        bool anyTopLevel = false;
        EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
        {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            if (owner != (uint)processId || GetWindow(hwnd, 4) != IntPtr.Zero) { return true; }
            StringBuilder name = new StringBuilder(256);
            GetClassName(hwnd, name, 256);
            string cls = name.ToString();
            if (cls == "SysShadow" || cls.IndexOf("tooltips_class", StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            anyTopLevel = true;
            if (!IsWindowVisible(hwnd)) { return true; }
            RECT rect;
            long area = GetWindowRect(hwnd, out rect) ? (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top) : 0;
            if (area > visibleArea) { visibleArea = area; visible = hwnd; }
            return true;
        }, IntPtr.Zero);
        if (visible != IntPtr.Zero) { return IsIconic(visible) ? "minimized" : "normal"; }
        return anyTopLevel ? "hidden" : "none";
    }

    // WM_CLOSE to a top-level window, as Process.CloseMainWindow does, but to the window MainWindowOf chose.
    public static bool PostClose(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero && PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }

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

    // The text of one list box item (LB_GETTEXT; Windows copies it across the process boundary for this standard control). null: no answer.
    public static string Text(IntPtr nav, int index)
    {
        IntPtr result;
        IntPtr ok = SendMessageTimeout(nav, LB_GETTEXTLEN, new IntPtr(index), IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out result);
        if (ok == IntPtr.Zero) { return null; }
        int length = result.ToInt32();
        if (length < 0 || length > 1000) { return null; }
        StringBuilder text = new StringBuilder(length + 2);
        ok = SendMessageTimeoutText(nav, LB_GETTEXT, new IntPtr(index), text, SMTO_ABORTIFHUNG, 1000, out result);
        if (ok == IntPtr.Zero || result.ToInt32() < 0) { return null; }
        return text.ToString();
    }

    // Width and height of a top-level window as GetWindowRect reports them (read only; works for a window of a higher integrity level too).
    // null: the window could not be measured.
    public static int[] WindowSize(IntPtr hwnd)
    {
        RECT rect;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out rect)) { return null; }
        return new int[] { rect.Right - rect.Left, rect.Bottom - rect.Top };
    }

    // The name of a modifier key that is held down right now (Shift, Ctrl, Alt, Win), or null. A list box reads these keys from the keyboard
    // state even for a posted key message, so a step is not made while one is down.
    public static string ModifierHeld()
    {
        int[] keys = new int[] { 0x10, 0x11, 0x12, 0x5B, 0x5C };
        string[] names = new string[] { "Shift", "Ctrl", "Alt", "Win", "Win" };
        for (int i = 0; i < keys.Length; i++)
        {
            if ((GetAsyncKeyState(keys[i]) & 0x8000) != 0) { return names[i]; }
        }
        return null;
    }

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

# ---- the page cycle of the long run (pure functions: no window is touched) ----

# The keys a step can press: name -> virtual key, scan code (all four are the extended keys).
$script:NavKeys = @{
    HOME = @{ vk = 0x24; scan = 0x47 }
    END  = @{ vk = 0x23; scan = 0x4F }
    UP   = @{ vk = 0x26; scan = 0x48 }
    DOWN = @{ vk = 0x28; scan = 0x50 }
}

# Where a key leaves the selection of a list box that has $Count items (DOWN stops at the last item, UP at the first).
function Get-NavKeyResult([string]$Key, [int]$Pos, [int]$Count) {
    switch ($Key) {
        'HOME' { return 0 }
        'END' { return $Count - 1 }
        'DOWN' { return [math]::Min($Pos + 1, $Count - 1) }
        'UP' { return [math]::Max($Pos - 1, 0) }
    }
    return $Pos
}

# The next page of the cycle, the checks page left out ($ChecksIndex $null: nothing is left out).
function Get-NavTarget([int]$Count, [int]$From, $ChecksIndex) {
    $to = ($From + 1) % $Count
    if ($null -ne $ChecksIndex -and $to -eq [int]$ChecksIndex) { $to = ($to + 1) % $Count }
    return $to
}

# The shortest run of keys from the page $From to the next page of the cycle that never selects the checks page on the way (opening it lists the
# real check history, which a measurement must not do). Candidates: nothing or HOME or END first, then DOWN or UP any number of times; every key
# must move the selection. ok = $false with a note when the checks page is not known or no such route exists: the caller then never switches.
function Get-NavPlan([int]$Count, [int]$From, $ChecksIndex) {
    $plan = [ordered]@{ ok = $false; from = $From; target = $null; steps = @(); visits = @(); note = $null }
    if ($null -eq $ChecksIndex) { $plan.note = 'the checks page was not found in the navigation list: no page is switched'; return $plan }
    if ($Count -lt 3 -or $From -lt 0 -or $From -ge $Count) { $plan.note = 'the navigation list is too short or the selection is unknown: no page is switched'; return $plan }
    $checks = [int]$ChecksIndex
    $target = Get-NavTarget $Count $From $checks
    $plan.target = $target
    if ($target -eq $From) { $plan.note = 'there is no other page to switch to'; return $plan }
    $best = $null
    foreach ($opener in @('', 'HOME', 'END')) {
        foreach ($repeat in @('DOWN', 'UP')) {
            for ($k = 0; $k -le $Count; $k++) {
                $route = New-Object System.Collections.ArrayList
                if ($opener -ne '') { [void]$route.Add($opener) }
                for ($i = 0; $i -lt $k; $i++) { [void]$route.Add($repeat) }
                if ($route.Count -eq 0) { continue }
                if ($null -ne $best -and $route.Count -ge $best.Len) { continue }
                $pos = $From
                $ok = $true
                $visits = New-Object System.Collections.ArrayList
                foreach ($key in $route) {
                    $next = Get-NavKeyResult $key $pos $Count
                    if ($next -eq $pos -or $next -eq $checks) { $ok = $false; break }
                    [void]$visits.Add($next)
                    $pos = $next
                }
                if ($ok -and $pos -eq $target) { $best = @{ Len = $route.Count; Route = @($route); Visits = @($visits) } }
            }
        }
    }
    if ($null -eq $best) { $plan.note = 'no route reaches the next page without selecting the checks page: no page is switched'; return $plan }
    $steps = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $best.Route.Count; $i++) {
        $name = [string]$best.Route[$i]
        [void]$steps.Add([ordered]@{ key = $name; vk = $script:NavKeys[$name].vk; scan = $script:NavKeys[$name].scan; expect = [int]$best.Visits[$i] })
    }
    $plan.steps = $steps
    $plan.visits = @($best.Visits)
    $plan.ok = $true
    return $plan
}

function Test-ChecksTitle($Text) {
    if ($null -eq $Text) { return $false }
    foreach ($title in $script:ChecksTitles) {
        if ([string]::Equals(([string]$Text).Trim(), $title, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

# The index of the checks page in the navigation list box, read from the item texts. $null when no item reads as the checks page.
function Find-ChecksIndex($Nav, [int]$Count) {
    for ($i = 0; $i -lt $Count; $i++) {
        if (Test-ChecksTitle ([PcmMeasureWin]::Text($Nav, $i))) { return $i }
    }
    return $null
}

# Next page of the cycle (see Get-NavPlan). Returns what happened; never throws. skipped: a modifier key was held (nothing, or not all, was
# pressed). ok: the selection ended on the planned page. A step that cannot be planned safely presses nothing.
function Step-NavPage([System.Diagnostics.Process]$Proc, [hashtable]$State) {
    $r = [ordered]@{ ok = $false; skipped = $false; from = $null; to = $null; count = $null; keys = @(); note = $null }
    try {
        Initialize-Win32
        $held = [PcmMeasureWin]::ModifierHeld()
        if ($null -ne $held) { $r.skipped = $true; $r.note = $held + ' was held: the step was skipped'; return $r }
        $main = Get-MainWindow $Proc
        if ($main -eq [IntPtr]::Zero) { $r.note = 'no main window handle'; return $r }
        if ($null -eq $State.Nav -or $State.Nav -eq [IntPtr]::Zero) { $State.Nav = [PcmMeasureWin]::FindNavList($main) }
        $nav = $State.Nav
        if ($nav -eq [IntPtr]::Zero) { $State.Nav = $null; $r.note = 'the navigation list box was not found'; return $r }
        $from = [PcmMeasureWin]::Current($nav)
        $count = [PcmMeasureWin]::Count($nav)
        $r.from = $from; $r.count = $count
        if ($from -lt 0 -or $count -le 0) { $State.Nav = $null; $r.note = 'the list box did not answer'; return $r }
        if ($null -eq $State.ChecksIndex) { $State.ChecksIndex = Find-ChecksIndex $nav $count }
        $plan = Get-NavPlan $count $from $State.ChecksIndex
        if (-not $plan.ok) { $r.note = $plan.note; return $r }
        $r.keys = @($plan.steps | ForEach-Object { $_.key })
        $to = $from
        foreach ($step in $plan.steps) {
            $held = [PcmMeasureWin]::ModifierHeld()
            if ($null -ne $held) { $r.skipped = $true; $r.note = $held + ' was held: the step was stopped half way'; break }
            [void][PcmMeasureWin]::PressKey($nav, [int]$step.vk, [int]$step.scan, $true)
            for ($i = 0; $i -lt 20; $i++) {
                [Threading.Thread]::Sleep(50)
                $to = [PcmMeasureWin]::Current($nav)
                if ($to -eq [int]$step.expect) { break }
            }
            if ($to -ne [int]$step.expect) { $r.note = 'the selection did not reach the page expected after ' + $step.key; break }
        }
        $r.to = $to
        $r.ok = (-not $r.skipped -and $to -eq [int]$plan.target)
        if (-not $r.ok -and $null -eq $r.note) { $r.note = 'the selection did not end on the planned page' }
    }
    catch { $r.note = Get-ShortError $_ }
    return $r
}

# The main window of a process (see PcmMeasureWin.MainWindowOf); [IntPtr]::Zero when it has none (yet).
function Get-MainWindow([System.Diagnostics.Process]$Proc) {
    Initialize-Win32
    return [PcmMeasureWin]::MainWindowOf([int]$Proc.Id)
}

# The size of the window of a process (GetWindowRect: width, height). width/height $null with a note when it cannot be measured.
function Get-WindowSize($Proc) {
    $r = [ordered]@{ width = $null; height = $null; note = $null }
    try {
        Initialize-Win32
        $handle = Get-MainWindow $Proc
        if ($handle -eq [IntPtr]::Zero) { $r.note = 'no main window handle'; return $r }
        $size = [PcmMeasureWin]::WindowSize($handle)
        if ($null -eq $size) { $r.note = 'GetWindowRect failed'; return $r }
        $r.width = [int]$size[0]; $r.height = [int]$size[1]
    }
    catch { $r.note = Get-ShortError $_ }
    return $r
}

# What the window of a process is doing: normal / minimized / hidden / none (see PcmMeasureWin.WindowStateOf); $null when it cannot be asked.
# The GUI runs its sampler in slow mode (5 s) while it is minimized or hidden, so the report says which state each moment of a record run was in.
function Get-WindowState($Proc) {
    try {
        Initialize-Win32
        return [PcmMeasureWin]::WindowStateOf([int]$Proc.Id)
    }
    catch { return $null }
}

# ---- process series ----

# The parent process id, or $null when the lookup fails (an answer of $null is "try again", never "not ours").
function Get-ParentId([int]$ProcId) {
    if ($null -ne $script:ParentIdProvider) { return (& $script:ParentIdProvider $ProcId) }
    try {
        $p = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + $ProcId) -Property ProcessId, ParentProcessId
        if ($null -ne $p) { return [int]$p.ParentProcessId }
    }
    catch { }
    return $null
}

function Get-ProcessesNamed([string]$Name) {
    if ($null -ne $script:ProcessLister) { return @(& $script:ProcessLister $Name) }
    return @([System.Diagnostics.Process]::GetProcessesByName($Name))
}

function Dispose-Quietly($Object) {
    try { if ($null -ne $Object) { $Object.Dispose() } } catch { }
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

# A series is one process, identified by its pid AND its creation time (a pid can come back for another process; that is a new series). It holds
# ONE handle (PROCESS_QUERY_LIMITED_INFORMATION) for its whole life: the handle also keeps the pid from being reused, and it is closed by
# Close-Series when the series ends. $Proc is only for what needs a Process object (name, main window, CloseMainWindow, waiting).
function New-Series($Proc, [string]$Role, [double]$T, [DateTime]$NowUtc, [bool]$IsMain) {
    $name = $Role
    try { $name = $Proc.ProcessName } catch { }
    $handle = $null
    $start = $null
    $reason = $null
    $token = $null
    $open = Open-ProcHandle ([int]$Proc.Id)
    if ($open.Handle -ne [IntPtr]::Zero) {
        $handle = $open.Handle
        $c = [PcmProc]::Read($handle)
        if ($c.HasCreation) { $start = $c.CreationUtc }
        else { $reason = Get-Win32Reason 'GetProcessTimes' $c.TimesError }
        $token = Get-ProcessToken $handle
    }
    else {
        $reason = Get-Win32Reason 'OpenProcess' $open.Error
        $token = [ordered]@{ integrity = 'unknown'; integrity_rid = $null; elevated = $null; reason = $reason }
    }
    # CPU time and cycle time start at 0 when the process is created, so the first interval runs from the creation to the first sample.
    $prevT = $null
    if ($null -ne $start) { $prevT = $T - ($NowUtc - $start).TotalSeconds }
    elseif ($IsMain) { $prevT = 0.0 }
    return @{
        Proc = $Proc; Role = $Role; ProcId = [int]$Proc.Id; Name = $name; StartUtc = $start; StartReason = $reason
        Handle = $handle; OpenReason = $(if ($null -eq $handle) { $reason } else { $null }); Token = $token
        PrevT = $prevT; PrevCpu = 0.0
        PrevCycT = $prevT; PrevCycles = 0.0; LastCycles = $null; SumCycDelta = 0.0; SumCycDt = 0.0
        FirstT = $T; LastT = $T; Samples = 0
        Missing = @{}; MissingTicks = @{}
        Ended = $false
    }
}

# Closes the series' handle (once). The series keeps its numbers; it can no longer be read.
function Close-Series([hashtable]$Series) {
    if ($null -ne $Series.Handle) {
        Close-ProcHandle $Series.Handle
        $Series.Handle = $null
    }
}

function Close-AllSeries($Main, $Children, $Finished) {
    if ($null -ne $Main) { Close-Series $Main }
    foreach ($s in @($Children)) { Close-Series $s }
    foreach ($s in @($Finished)) { Close-Series $s }
}

# Is the process of this series still running? From its own handle when it has one (no rights beyond the limited-information one); otherwise
# from the Process object.
function Test-SeriesAlive([hashtable]$Series) {
    if ($null -ne $Series.Handle) {
        $state = [PcmProc]::RunState($Series.Handle)
        if ($state -ge 0) { return ($state -eq 1) }
    }
    return (Test-Alive $Series.Proc)
}

function Note-Missing([hashtable]$Series, [string]$Counter, [string]$Reason) {
    if (-not $Series.Missing.ContainsKey($Counter)) { $Series.Missing[$Counter] = $Reason; $Series.MissingTicks[$Counter] = 0 }
    $Series.MissingTicks[$Counter] = $Series.MissingTicks[$Counter] + 1
}

# One second's reading of one process through the series' handle. A counter that cannot be read is $null for that row and is noted with
# its reason (the others still come).
function Read-Series([hashtable]$Series, [double]$T) {
    $row = @{
        T = $T; Role = $Series.Role; Name = $Series.Name; ProcId = $Series.ProcId; StartUtc = $Series.StartUtc
        Dt = $null; Cpu = $null; CpuDelta = $null; Cycles = $null; CyclesDelta = $null; CycDt = $null; Private = $null; Ws = $null; Handles = $null
    }
    if ($null -eq $Series.Handle) {
        $why = $(if ($null -ne $Series.OpenReason) { $Series.OpenReason } else { 'the process handle is closed' })
        foreach ($counter in @('cpu_time', 'cycles', 'private_bytes', 'working_set_bytes', 'handles')) { Note-Missing $Series $counter $why }
    }
    else {
        $c = [PcmProc]::Read($Series.Handle)
        if ($c.MemoryError -eq 0) { $row.Private = [long]$c.PrivateBytes; $row.Ws = [long]$c.WorkingSet }
        else {
            $why = Get-Win32Reason 'GetProcessMemoryInfo' $c.MemoryError
            Note-Missing $Series 'private_bytes' $why
            Note-Missing $Series 'working_set_bytes' $why
        }
        if ($c.HandlesError -eq 0) { $row.Handles = [int]$c.Handles } else { Note-Missing $Series 'handles' (Get-Win32Reason 'GetProcessHandleCount' $c.HandlesError) }
        if ($c.TimesError -eq 0) {
            $cpu = [double]$c.CpuSeconds
            $row.Cpu = $cpu
            if ($null -ne $Series.PrevT -and $T -gt $Series.PrevT) {
                $row.CpuDelta = [math]::Max(0.0, $cpu - $Series.PrevCpu)
                $row.Dt = $T - $Series.PrevT
            }
            $Series.PrevT = $T
            $Series.PrevCpu = $cpu
        }
        else { Note-Missing $Series 'cpu_time' (Get-Win32Reason 'GetProcessTimes' $c.TimesError) }
        # Cycle time has its own previous reading: one that failed leaves a gap that the next delta must not pretend to cover in one interval.
        if ($c.CyclesError -eq 0) {
            $cycles = [double]$c.Cycles
            $row.Cycles = $cycles
            if ($null -ne $Series.PrevCycT -and $T -gt $Series.PrevCycT) {
                $row.CyclesDelta = [math]::Max(0.0, $cycles - $Series.PrevCycles)
                $row.CycDt = $T - $Series.PrevCycT
                $Series.SumCycDelta = $Series.SumCycDelta + $row.CyclesDelta
                $Series.SumCycDt = $Series.SumCycDt + $row.CycDt
            }
            $Series.PrevCycT = $T
            $Series.PrevCycles = $cycles
            $Series.LastCycles = $cycles
        }
        else { Note-Missing $Series 'cycles' (Get-Win32Reason 'QueryProcessCycleTime' $c.CyclesError) }
    }
    $Series.LastT = $T
    $Series.Samples = $Series.Samples + 1
    return $row
}

# One SensorHost looked at once: 'child' when it is new and the GUI's child, '' otherwise (not ours, already seen, or a lookup that failed and
# will be tried again). $Seen remembers every process looked at, by pid + creation time: 'child', 'other' (its parent is somebody else),
# 'unknown_parent' (the parent lookup failed LookupMaxTries times) and, by pid alone, 'failed' (the process could not be opened or its creation
# time not read LookupMaxTries times). A lookup that fails is NOT a verdict: it is tried again at the next sample, up to $script:LookupMaxTries
# times, and only then given up, with a warning that names the pid.
function Get-ChildVerdict($P, [hashtable]$Main, [hashtable]$Seen, $Warnings) {
    $pidKey = 'pid:' + $P.Id
    if ($Seen.ContainsKey($pidKey)) { return '' }
    $start = $null
    $why = $null
    $open = Open-ProcHandle ([int]$P.Id)
    if ($open.Handle -eq [IntPtr]::Zero) { $why = Get-Win32Reason 'OpenProcess' $open.Error }
    else {
        try {
            $c = [PcmProc]::Read($open.Handle)
            if ($c.HasCreation) { $start = $c.CreationUtc } else { $why = Get-Win32Reason 'GetProcessTimes' $c.TimesError }
        }
        finally { Close-ProcHandle $open.Handle }
    }
    if ($null -eq $start) {
        $triesKey = 'tries:' + $P.Id
        $tries = 1
        if ($Seen.ContainsKey($triesKey)) { $tries = [int]$Seen[$triesKey] + 1 }
        $Seen[$triesKey] = $tries
        if ($tries -ge $script:LookupMaxTries) {
            $Seen[$pidKey] = 'failed'
            [void]$Warnings.Add(('a {0} (pid {1}) was not measured: its creation time could not be read in {2} tries ({3})' -f $script:SensorHostName, $P.Id, $tries, $why))
        }
        return ''
    }
    $key = '{0}:{1}' -f $P.Id, $start.Ticks
    if ($Seen.ContainsKey($key)) { return '' }
    $parent = Get-ParentId ([int]$P.Id)
    if ($null -eq $parent) {
        $triesKey = 'ptries:' + $key
        $tries = 1
        if ($Seen.ContainsKey($triesKey)) { $tries = [int]$Seen[$triesKey] + 1 }
        $Seen[$triesKey] = $tries
        if ($tries -ge $script:LookupMaxTries) {
            $Seen[$key] = 'unknown_parent'
            [void]$Warnings.Add(('a {0} (pid {1}) was not counted: its parent process could not be looked up in {2} tries; it may be the GUI''s child' -f $script:SensorHostName, $P.Id, $tries))
        }
        return ''
    }
    $afterMain = ($null -eq $Main.StartUtc) -or ($start -ge $Main.StartUtc.AddSeconds(-1))
    if ([int]$parent -eq $Main.ProcId -and $afterMain) { $Seen[$key] = 'child'; return 'child' }
    $Seen[$key] = 'other'
    return ''
}

# New PCMieru.SensorHost processes whose parent is the GUI, as ready series.
function Find-NewChildren([hashtable]$Main, [hashtable]$Seen, [double]$T, [DateTime]$NowUtc, $Warnings) {
    $found = New-Object System.Collections.ArrayList
    foreach ($p in (Get-ProcessesNamed $script:SensorHostName)) {
        $verdict = ''
        try { $verdict = Get-ChildVerdict $p $Main $Seen $Warnings } catch { }
        if ($verdict -eq 'child') { [void]$found.Add((New-Series $p 'child' $T $NowUtc $false)) }
        else { Dispose-Quietly $p }
    }
    # The series themselves, one by one (not the list as one object): the caller and @() both see 0, 1 or n items.
    return $found.ToArray()
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
    $cycSum = 0.0; $cycDt = 0.0; $cycMax = $null; $cycIntervals = 0
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
        if ($null -ne $r.CyclesDelta -and $null -ne $r.CycDt -and $r.CycDt -gt 0) {
            $cycRate = $r.CyclesDelta / $r.CycDt
            $cycSum += $r.CyclesDelta; $cycDt += $r.CycDt; $cycIntervals++
            if ($null -eq $cycMax -or $cycRate -gt $cycMax) { $cycMax = $cycRate }
        }
        if ($null -ne $r.Private) { $privSum += $r.Private; $privN++; if ($null -eq $privMax -or $r.Private -gt $privMax) { $privMax = $r.Private } }
        if ($null -ne $r.Ws) { $wsSum += $r.Ws; $wsN++; if ($null -eq $wsMax -or $r.Ws -gt $wsMax) { $wsMax = $r.Ws } }
        if ($null -ne $r.Handles) { $hSum += $r.Handles; $hN++; if ($null -eq $hMax -or $r.Handles -gt $hMax) { $hMax = $r.Handles } }
    }
    $meanRate = $null
    if ($sumDt -gt 0) { $meanRate = $sumDelta / $sumDt }
    $meanCyc = $null
    if ($cycDt -gt 0) { $meanCyc = $cycSum / $cycDt }
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
        cycles_intervals = $cycIntervals
        cycles_per_s_mean = $(if ($null -ne $meanCyc) { Round-Or-Null $meanCyc 0 } else { $null })
        cycles_per_s_max = $(if ($null -ne $cycMax) { Round-Or-Null $cycMax 0 } else { $null })
        ghz_equivalent_mean = $(if ($null -ne $meanCyc) { Round-Or-Null ($meanCyc / 1.0e9) 4 } else { $null })
        ghz_equivalent_max = $(if ($null -ne $cycMax) { Round-Or-Null ($cycMax / 1.0e9) 4 } else { $null })
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

# ---- record mode (-Mode record) ----

# A property of an object, or $Default when it has none (strict mode throws on a missing property).
function Get-Prop($Obj, [string]$Name, $Default = $null) {
    if ($null -eq $Obj) { return $Default }
    $p = $Obj.PSObject.Properties[$Name]
    if ($null -eq $p) { return $Default }
    return $p.Value
}

function Read-SharedText([string]$Path) {
    $fs = New-Object System.IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { return (New-Object System.IO.StreamReader($fs, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $fs.Dispose() }
}

function ConvertTo-UtcDate($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [DateTime]) { return $Value.ToUniversalTime() }
    try { return [DateTime]::Parse([string]$Value, (Get-Inv), [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() } catch { return $null }
}

# One line of a CSV. The recording quotes a cell with a comma or a quote in it (RFC 4180), so a plain split is not enough.
function Split-CsvLine([string]$Line) {
    $fields = New-Object System.Collections.ArrayList
    $sb = New-Object System.Text.StringBuilder
    $quoted = $false
    for ($i = 0; $i -lt $Line.Length; $i++) {
        $c = $Line[$i]
        if ($quoted) {
            if ($c -eq [char]34) {
                if ($i + 1 -lt $Line.Length -and $Line[$i + 1] -eq [char]34) { [void]$sb.Append([char]34); $i++ }
                else { $quoted = $false }
            }
            else { [void]$sb.Append($c) }
        }
        elseif ($c -eq [char]34) { $quoted = $true }
        elseif ($c -eq [char]44) { [void]$fields.Add($sb.ToString()); [void]$sb.Clear() }
        else { [void]$sb.Append($c) }
    }
    [void]$fields.Add($sb.ToString())
    return , $fields.ToArray()
}

# The folders a measured run must never write into (the GUI refuses a --measure-record folder inside any of them; so does this script, before it starts anything).
function Get-UserDataFolders {
    # The same four as the GUI's MeasureRecordPlan.UserDataFolders: the Documents folder as Windows says it (E:\MovedFromC\Documents when it is redirected),
    # the plain Documents of the profile (a redirected one leaves it behind), the app's own data folder and Roaming.
    return @(
        [Environment]::GetFolderPath('MyDocuments')
        (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Documents')
        (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PCMieru')
        [Environment]::GetFolderPath('ApplicationData')
    )
}

# The real place a path leads to (the GUI's FolderGuard): the nearest part that exists is opened and asked for its final path, which follows every link
# (junction, symbolic link) and expands every 8.3 short name (ADMINI~1); the part that does not exist yet is appended as written. Nothing is created.
# Plain C# 5 on purpose (Windows PowerShell 5.1 compiles it with the old CodeDom compiler).
function Initialize-RealPath {
    if ('PcmRealPath' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class PcmRealPath
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(IntPtr file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static readonly IntPtr Invalid = new IntPtr(-1);

    private static string TrimEnd(string path)
    {
        string root = Path.GetPathRoot(path);
        if (path.Length > 0 && path.Length > (root == null ? 0 : root.Length)) { return path.TrimEnd('\\', '/'); }
        return path;
    }

    private static bool TryAttributes(string path, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(path); return true; }
        catch (Exception) { attributes = 0; return false; }
    }

    private static string Final(string existing)
    {
        string name = existing.StartsWith(@"\\?\", StringComparison.Ordinal) ? existing
            : existing.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + existing.Substring(2)
            : @"\\?\" + existing;
        IntPtr handle = CreateFile(name, 0x80, 0x7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle == Invalid || handle == IntPtr.Zero) { return null; }
        try
        {
            StringBuilder buffer = new StringBuilder(512);
            while (true)
            {
                uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0) { return null; }
                if (length < buffer.Capacity) { break; }
                buffer.EnsureCapacity((int)length + 1);
            }
            string text = buffer.ToString();
            if (text.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) { return @"\\" + text.Substring(8); }
            return text.StartsWith(@"\\?\", StringComparison.Ordinal) ? text.Substring(4) : text;
        }
        finally { CloseHandle(handle); }
    }

    private static bool HasLinkAbove(string path)
    {
        for (string parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            FileAttributes attributes;
            if (TryAttributes(parent, out attributes) && (attributes & FileAttributes.ReparsePoint) != 0) { return true; }
        }
        return false;
    }

    // The real spelling of the path, or null when it cannot be known (a link on the way that cannot be followed: a junction whose target is gone).
    public static string Resolve(string path)
    {
        string full;
        try { full = TrimEnd(Path.GetFullPath(path)); }
        catch (Exception) { return null; }
        Stack<string> tail = new Stack<string>();
        string existing = full;
        while (true)
        {
            FileAttributes attributes;
            if (TryAttributes(existing, out attributes))
            {
                string final = Final(existing);
                if (final == null)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || HasLinkAbove(existing)) { return null; }
                    final = existing;
                }
                while (tail.Count > 0) { final = Path.Combine(final, tail.Pop()); }
                return final;
            }
            string parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent)) { return full; }
            tail.Push(Path.GetFileName(existing));
            existing = parent;
        }
    }
}
'@
}

# The spellings of a path to compare: the one given (made absolute) and, when it is another, the real one. Empty for a blank path.
function Get-PathSpellings([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return @() }
    Initialize-RealPath
    $full = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetPathRoot($full) -ne $full) { $full = $full.TrimEnd('\', '/') }
    $real = [PcmRealPath]::Resolve($full)
    if ($null -ne $real -and $real -ine $full) { return @($full, $real) }
    return @($full)
}

# Whether the path is the root or under it, in any spelling of either (a short name, a junction, a redirected Documents).
function Test-InsideFolder([string]$Root, [string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Root) -or [string]::IsNullOrWhiteSpace($Path)) { return $false }
    foreach ($parent in @(Get-PathSpellings $Root)) {
        foreach ($child in @(Get-PathSpellings $Path)) {
            if (($child -ieq $parent) -or $child.StartsWith($parent.TrimEnd('\', '/') + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
        }
    }
    return $false
}

# False when a part of the path is a link that cannot be followed: where it leads is not known, so it is refused (never guessed).
function Test-PathResolvable([string]$Path) {
    Initialize-RealPath
    return ($null -ne [PcmRealPath]::Resolve($Path))
}

# Where the GUI puts the record events of the run with this marks file: next to it, <marks name>.record.jsonl (MeasureRecording.EventsPathFor).
function Get-RecordEventsPath([string]$MarkFile) {
    $full = [IO.Path]::GetFullPath($MarkFile)
    return (Join-Path ([IO.Path]::GetDirectoryName($full)) ([IO.Path]::GetFileNameWithoutExtension($full) + '.record.jsonl'))
}

# The events the GUI wrote: one JSON object per line. A line that is not an object with an "event" is counted and skipped.
function Read-RecordEvents([string]$Path) {
    $result = [ordered]@{ found = $false; events = (New-Object System.Collections.ArrayList); bad_lines = 0 }
    if ([string]::IsNullOrEmpty($Path) -or -not (Test-Path -LiteralPath $Path)) { return $result }
    $text = $null
    try { $text = Read-SharedText $Path } catch { return $result }
    $result.found = $true
    foreach ($line in ($text -split "`n")) {
        $line = $line.Trim()
        if ($line.Length -eq 0) { continue }
        try {
            $o = $line | ConvertFrom-Json
            if ($null -eq $o -or $null -eq $o.PSObject.Properties['event']) { $result.bad_lines = $result.bad_lines + 1; continue }
            [void]$result.events.Add($o)
        }
        catch { $result.bad_lines = $result.bad_lines + 1 }
    }
    return $result
}

# begin / start / stop / stopped as seconds from the stopwatch zero, and what each said.
function Get-RecordTimeline($Events, [DateTime]$ZeroUtc) {
    $t = [ordered]@{
        begin_s = $null; lead_seconds = $null; record_seconds = $null; user_interval_ms = $null
        start_s = $null; start_ok = $null; start_ms = $null; start_problem = $null; run_id = $null
        stop_s = $null; self_ended = $null
        stopped_s = $null; stop_ms = $null; saved = $null; status = $null; stop_reason = $null
        rows = $null; dropped_ticks = $null; duration_us = $null; csv = $null; json = $null; error = $null
        errors = (New-Object System.Collections.ArrayList)
    }
    foreach ($e in @($Events)) {
        $name = [string](Get-Prop $e 'event' '')
        $utc = ConvertTo-UtcDate (Get-Prop $e 'utc')
        $off = $null
        if ($null -ne $utc) { $off = [math]::Round(($utc - $ZeroUtc).TotalSeconds, 3) }
        if ($name -eq 'begin' -and $null -eq $t.begin_s) {
            $t.begin_s = $off
            $t.lead_seconds = Get-Prop $e 'leadSeconds'
            $t.record_seconds = Get-Prop $e 'recordSeconds'
            $t.user_interval_ms = Get-Prop $e 'userIntervalMs'
        }
        elseif ($name -eq 'start' -and $null -eq $t.start_s) {
            $t.start_s = $off
            $t.start_ok = [bool](Get-Prop $e 'ok' $false)
            $t.start_ms = Get-Prop $e 'startMs'
            if ($t.start_ok) { $t.run_id = Get-Prop $e 'runId' }
            else { $t.start_problem = (([string](Get-Prop $e 'problem' '')) + ' ' + ([string](Get-Prop $e 'detail' ''))).Trim() }
        }
        elseif ($name -eq 'stop' -and $null -eq $t.stop_s) {
            $t.stop_s = $off
            $t.self_ended = Get-Prop $e 'selfEnded'
        }
        elseif ($name -eq 'stopped' -and $null -eq $t.stopped_s) {
            $t.stopped_s = $off
            $t.stop_ms = Get-Prop $e 'stopMs'
            $t.saved = Get-Prop $e 'saved'
            $t.status = Get-Prop $e 'status'
            $t.stop_reason = Get-Prop $e 'stopReason'
            $t.rows = Get-Prop $e 'rows'
            $t.dropped_ticks = Get-Prop $e 'droppedTicks'
            $t.duration_us = Get-Prop $e 'durationUs'
            $t.csv = Get-Prop $e 'csv'
            $t.json = Get-Prop $e 'json'
            $t.error = Get-Prop $e 'error'
        }
        elseif ($name -eq 'error') { [void]$t.errors.Add([string](Get-Prop $e 'message' '')) }
    }
    if ($null -eq $t.csv -or [string]$t.csv -eq '') {
        # The recording's own path from the start event (the stopped event may not have come).
        foreach ($e in @($Events)) { if ([string](Get-Prop $e 'event' '') -eq 'start' -and [bool](Get-Prop $e 'ok' $false)) { $t.csv = Get-Prop $e 'csv' } }
    }
    return $t
}

# The sampling period Core reported on its snapshots, per phase of the recording (the GUI's period lines, one per snapshot).
# What a line carries is the gap Core MEASURED since the snapshot before it (Snapshot.IntervalMs), not the nominal period: it jitters around the
# period the loop runs at (999..1018 ms for a 1000 ms beat on this PC). So a phase is read as a distribution: its median is put to the nearest period the
# GUI can run at (500, 1000, 2000, 5000 ms) and counted as holding it only when most gaps are within a quarter of that period. After the stop the
# first gaps may still be the recording's, so "back at the user's interval" is judged on the second half of the phase and on its last line.
function Get-NominalPeriod($Ms) {
    if ($null -eq $Ms) { return $null }
    $best = $null
    foreach ($n in $script:NominalPeriodsMs) {
        if ($null -eq $best -or [math]::Abs([double]$Ms - $n) -lt [math]::Abs([double]$Ms - $best)) { $best = $n }
    }
    return [int]$best
}

function Get-GapStats($Gaps) {
    $v = @($Gaps | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ })
    if ($v.Count -eq 0) { return $null }
    $m = $v | Measure-Object -Minimum -Maximum -Average
    $median = Get-Median $v
    $class = Get-NominalPeriod $median
    $near = @($v | Where-Object { [math]::Abs($_ - $class) -le ($script:PeriodToleranceFraction * $class) }).Count
    $half = [int][math]::Floor($v.Count / 2)
    $settled = Get-Median @($v[$half..($v.Count - 1)])
    return [ordered]@{
        min = [math]::Round($m.Minimum, 1)
        median = [math]::Round($median, 1)
        mean = [math]::Round($m.Average, 1)
        max = [math]::Round($m.Maximum, 1)
        nominal_ms = $class
        share_near_nominal = [math]::Round($near / [double]$v.Count, 3)
        settled_median = [math]::Round($settled, 1)
        settled_nominal_ms = (Get-NominalPeriod $settled)
        last = [int]$v[$v.Count - 1]
        last_nominal_ms = (Get-NominalPeriod $v[$v.Count - 1])
    }
}

# What the window was doing at a moment (seconds from the stopwatch zero): the last look at or before it. $null: no look had been taken by then.
function Get-WindowStateAt($States, $AtSeconds) {
    if ($null -eq $AtSeconds) { return $null }
    $at = $null
    foreach ($s in @($States | Where-Object { $null -ne $_ })) {
        if ([double]$s.t_s -le [double]$AtSeconds) { $at = [string]$s.state } else { break }
    }
    return $at
}

# The window's state at the start, at the stop and through the phase after the stop (every state it had from the "stopped" event to the end of the
# run), from the changes the run loop noted ({ t_s; state }, in time order). minimized and hidden are slow mode: the sampler runs at 5 s there, not at
# the user's interval, so a run whose stop or after phase was spent in one cannot show "back at the user interval".
function Get-WindowStateSummary($States, $Timeline, [double]$Duration) {
    $list = @()
    if ($null -ne $States) { $list = @($States) }
    $atStart = Get-WindowStateAt $list $Timeline.start_s
    $atStop = Get-WindowStateAt $list $Timeline.stop_s
    $afterStates = New-Object System.Collections.ArrayList
    if ($null -ne $Timeline.stopped_s) {
        $from = [double]$Timeline.stopped_s
        $first = Get-WindowStateAt $list $from
        if ($null -ne $first) { [void]$afterStates.Add($first) }
        foreach ($s in $list) {
            if ([double]$s.t_s -gt $from -and [double]$s.t_s -le $Duration -and -not $afterStates.Contains([string]$s.state)) { [void]$afterStates.Add([string]$s.state) }
        }
    }
    $slow = @('minimized', 'hidden')
    $slowAtStop = ($slow -contains $atStop)
    $slowAfter = (@($afterStates | Where-Object { $slow -contains $_ }).Count -gt 0)
    $slowAtStart = ($slow -contains $atStart)
    return [ordered]@{
        looks = $list.Count
        at_start = $atStart
        at_stop = $atStop
        after = @($afterStates)
        changes = $list
        slow_mode_at_start = $slowAtStart
        slow_mode_at_stop = $slowAtStop
        slow_mode_after_the_stop = $slowAfter
        note = $(if ($list.Count -eq 0) { 'the window state was not read (no look was taken)' } elseif ($slowAtStart -or $slowAtStop -or $slowAfter) { 'the window was minimized or hidden at some point that matters: the sampler was in slow mode (5 s), not at the user interval' } else { $null })
    }
}

# $WindowSummary (optional, Get-WindowStateSummary): a run whose after phase was spent minimized or hidden is in slow mode, so the return to the user's
# interval is not judged. Alongside the gap statistics the period lines carry what the loop itself ran at (loopPeriodMs) and how many recording
# leases it held (leases): an exact reading, no jitter, so "recording at 1 s" is also checked from it (per phase: loop_period_ms, leases and the
# count of lines that agree with the phase; during_recording_loop_period and after_stop_no_lease). A GUI that does not write them leaves those null.
function Get-PeriodSummary($Events, $UserIntervalMs, $WindowSummary = $null) {
    $phases = [ordered]@{}
    foreach ($name in @('before', 'recording', 'stopping', 'after')) {
        $phases[$name] = @{ lines = 0; gaps = (New-Object System.Collections.ArrayList); effective = @{}; snapshots = 0; loop = @{}; leases = @{}; loop_lines = 0; agree = 0 }
    }
    foreach ($e in @($Events)) {
        if ([string](Get-Prop $e 'event' '') -ne 'period') { continue }
        $ph = [string](Get-Prop $e 'phase' '')
        if (-not $phases.Contains($ph)) { continue }
        $p = $phases[$ph]
        $p.lines = $p.lines + 1
        $core = Get-Prop $e 'coreIntervalMs'
        if ($null -ne $core) { [void]$p.gaps.Add([double]$core) }
        $eff = Get-Prop $e 'effectiveIntervalMs'
        if ($null -ne $eff) { $p.effective[[string]$eff] = 1 }
        $snap = Get-Prop $e 'snapshotsInPhase'
        if ($null -ne $snap) { $p.snapshots = [int]$snap }
        if ($null -eq $UserIntervalMs) { $UserIntervalMs = Get-Prop $e 'userIntervalMs' }
        # What the loop itself runs at and how many leases it holds (not the jittering gap): the exact reading of "recording at 1 s".
        $loopMs = Get-Prop $e 'loopPeriodMs'
        $leaseCount = Get-Prop $e 'leases'
        if ($null -ne $loopMs -and $null -ne $leaseCount) {
            $p.loop_lines = $p.loop_lines + 1
            $p.loop[[string]$loopMs] = 1
            $p.leases[[string]$leaseCount] = 1
            # A line agrees with its phase when it runs at the recording period holding a lease (recording, stopping), or holds none and runs at the
            # period the pump asked for (before, after).
            if ($ph -eq 'recording' -or $ph -eq 'stopping') { if ([int]$loopMs -eq $script:RecordingPeriodMs -and [int]$leaseCount -ge 1) { $p.agree = $p.agree + 1 } }
            elseif ([int]$leaseCount -eq 0 -and $null -ne $eff -and [int]$loopMs -eq [int]$eff) { $p.agree = $p.agree + 1 }
        }
    }
    $out = [ordered]@{}
    foreach ($name in $phases.Keys) {
        $p = $phases[$name]
        $g = Get-GapStats $p.gaps
        $first = $null; $last = $null
        if ($p.gaps.Count -gt 0) { $first = [int]$p.gaps[0]; $last = [int]$p.gaps[$p.gaps.Count - 1] }
        $out[$name] = [ordered]@{
            period_lines = $p.lines
            core_gap_ms = $g
            nominal_period_ms = $(if ($null -ne $g) { $g.nominal_ms } else { $null })
            effective_interval_ms = @($p.effective.Keys | ForEach-Object { [int]$_ } | Sort-Object)
            first_core_interval_ms = $first
            last_core_interval_ms = $last
            snapshots_at_last_line = $p.snapshots
            loop_period_ms = @($p.loop.Keys | ForEach-Object { [int]$_ } | Sort-Object)
            leases = @($p.leases.Keys | ForEach-Object { [int]$_ } | Sort-Object)
            loop_lines = $p.loop_lines
            loop_lines_agreeing = $p.agree
        }
    }
    $user = $null
    if ($null -ne $UserIntervalMs) { $user = [int]$UserIntervalMs }
    $rec = $out['recording']
    $after = $out['after']
    $duringOk = $null
    if ($null -ne $rec.core_gap_ms) { $duringOk = ($rec.core_gap_ms.nominal_ms -eq $script:RecordingPeriodMs -and $rec.core_gap_ms.share_near_nominal -ge $script:PeriodHoldShare) }
    $returns = $null
    if ($null -ne $after.core_gap_ms -and $null -ne $user) { $returns = ($after.core_gap_ms.settled_nominal_ms -eq $user -and $after.core_gap_ms.last_nominal_ms -eq $user) }
    # A few gaps say little, and a minimized or hidden window is in slow mode (5 s): in both cases the return to the user's interval is not judged.
    $notJudged = $null
    if ($after.period_lines -gt 0 -and $after.period_lines -lt $script:MinAfterPeriodLines) {
        $returns = $null
        $notJudged = ('only {0} period line(s) came after the stop ({1} or more are needed to judge a return to the user interval; a longer -AfterSeconds gives the after phase more lines)' -f $after.period_lines, $script:MinAfterPeriodLines)
    }
    elseif ($null -ne $WindowSummary -and $WindowSummary.slow_mode_after_the_stop -eq $true) {
        $returns = $null
        $notJudged = ('the window was {0} after the stop (slow mode, 5 s): the sampler was not at the user interval, so a return to it is not judged' -f ($WindowSummary.after -join '/'))
    }
    $observable = ($null -ne $user -and $user -ne $script:RecordingPeriodMs)
    $note = $null
    if ($rec.period_lines -eq 0) { $note = 'no snapshot reached the window while recording: the period during the recording is missing' }
    elseif ($after.period_lines -eq 0) { $note = 'no snapshot reached the window after the stop: the period after the stop is missing' }
    elseif (-not $observable) { $note = ('the user interval ({0} ms) equals the recording period ({1} ms): a return to the user setting cannot be told from staying at the recording period; run with -UserIntervalMs 2000' -f $user, $script:RecordingPeriodMs) }
    if ($null -ne $notJudged) { $note = $(if ($null -ne $note) { $note + '; ' + $notJudged } else { $notJudged }) }
    # The loop's own reading (no jitter): while recording every line must say the recording period with a lease held; after the stop no line may
    # hold a lease. Null when the GUI wrote no such field (an older PCMieru.exe) - the gap statistics above are then all there is.
    $duringLoop = $null
    if ($rec.loop_lines -gt 0) { $duringLoop = ($rec.loop_lines_agreeing -eq $rec.loop_lines -and $rec.period_lines -eq $rec.loop_lines) }
    $afterLoop = $null
    if ($after.loop_lines -gt 0) { $afterLoop = ($after.loop_lines_agreeing -eq $after.loop_lines -and $after.period_lines -eq $after.loop_lines) }
    return [ordered]@{
        user_interval_ms = $user
        recording_period_ms = $script:RecordingPeriodMs
        measured = 'Core reports the gap it measured between snapshots, which jitters around the period the loop runs at: each phase is judged on the median gap put to the nearest of 500/1000/2000/5000 ms'
        loop_reading = 'the period lines also carry loopPeriodMs (what the sampler loop itself runs at) and leases (how many recordings hold it): an exact reading, no jitter'
        during_recording_is_recording_period = $duringOk
        during_recording_loop_period_is_recording_period = $duringLoop
        after_stop_loop_period_is_user_setting_without_lease = $afterLoop
        after_stop_returns_to_user_interval = $returns
        return_observable = $observable
        note = $note
        window_state = $WindowSummary
        phases = $out
    }
}

# The recording's own CSV, read from the outside: per source the fresh / repeat / missing rows, the period the rows carry, the frames the writer
# skipped (a jump in the frame number of a fresh row), the age of the data at the row, the spacing of the rows, and what every value column held.
function Get-RecordCsvStats([string]$Path) {
    $inv = Get-Inv
    $out = [ordered]@{
        file = $(if ([string]::IsNullOrEmpty($Path)) { $null } else { Split-Path -Leaf $Path })
        read = $false
        note = $null
        rows = 0
        bad_rows = 0
        row_gap_ms = $null
        row_gaps_over_1500_ms = 0
        sources = [ordered]@{}
        values = [ordered]@{}
    }
    if ([string]::IsNullOrEmpty($Path) -or -not (Test-Path -LiteralPath $Path)) { $out.note = 'the recording CSV was not found'; return $out }
    $text = $null
    try { $text = Read-SharedText $Path } catch { $out.note = 'the recording CSV could not be read: ' + (Get-ShortError $_); return $out }
    $lines = @($text -split "`n" | ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_.Length -gt 0 })
    if ($lines.Count -eq 0) { $out.note = 'the recording CSV is empty'; return $out }
    $header = Split-CsvLine $lines[0]
    if ($header.Count -lt 2 -or $header[0] -ne 'offsetUs' -or $header[1] -ne 'utc') { $out.note = 'the first line is not a recording header (offsetUs,utc,...)'; return $out }
    $out.read = $true

    $col = @{}
    for ($i = 0; $i -lt $header.Count; $i++) { $col[$header[$i]] = $i }
    $metaSuffixes = @('.frame', '.acquiredOffsetUs', '.periodMs', '.state', '.reason')
    $sources = New-Object System.Collections.ArrayList
    foreach ($h in $header) {
        if (-not $h.EndsWith('.state')) { continue }
        $name = $h.Substring(0, $h.Length - '.state'.Length)
        $ix = @{}
        foreach ($sfx in $metaSuffixes) { $ix[$sfx] = $(if ($col.ContainsKey($name + $sfx)) { [int]$col[$name + $sfx] } else { -1 }) }
        [void]$sources.Add(@{ Name = $name; Ix = $ix; Fresh = 0; Repeat = 0; Missing = 0; Other = 0; Periods = @{}; Reasons = @{}; Jumps = 0L; JumpEvents = 0; MaxJump = 0L; Generations = 0; LastKey = $null; LastSeq = $null; AgeSum = 0.0; AgeN = 0; AgeMax = $null })
    }
    $valueCols = New-Object System.Collections.ArrayList
    for ($i = 2; $i -lt $header.Count; $i++) {
        $isMeta = $false
        foreach ($sfx in $metaSuffixes) { if ($header[$i].EndsWith($sfx)) { $isMeta = $true } }
        if (-not $isMeta) { [void]$valueCols.Add(@{ Name = $header[$i]; Index = $i; N = 0; Empty = 0; Num = 0; Min = $null; Max = $null; Sum = 0.0; Texts = @{} }) }
    }

    $prevOff = $null
    $gapSum = 0.0; $gapN = 0; $gapMin = $null; $gapMax = $null
    $frameRx = '^(c|g\d+)-(\d+)$'
    for ($r = 1; $r -lt $lines.Count; $r++) {
        $f = Split-CsvLine $lines[$r]
        $off = 0L
        if ($f.Count -ne $header.Count -or -not [long]::TryParse($f[0], [Globalization.NumberStyles]::AllowLeadingSign, $inv, [ref]$off)) { $out.bad_rows = $out.bad_rows + 1; continue }
        $out.rows = $out.rows + 1
        if ($null -ne $prevOff) {
            $gap = ($off - $prevOff) / 1000.0
            $gapSum += $gap; $gapN++
            if ($null -eq $gapMin -or $gap -lt $gapMin) { $gapMin = $gap }
            if ($null -eq $gapMax -or $gap -gt $gapMax) { $gapMax = $gap }
            if ($gap -gt 1500) { $out.row_gaps_over_1500_ms = $out.row_gaps_over_1500_ms + 1 }
        }
        $prevOff = $off

        foreach ($s in $sources) {
            $ix = $s.Ix
            $state = $(if ($ix['.state'] -ge 0) { [string]$f[$ix['.state']] } else { '' })
            if ($state -eq 'fresh') { $s.Fresh = $s.Fresh + 1 }
            elseif ($state -eq 'repeat') { $s.Repeat = $s.Repeat + 1 }
            elseif ($state -eq 'missing') { $s.Missing = $s.Missing + 1 }
            else { $s.Other = $s.Other + 1 }
            if ($ix['.periodMs'] -ge 0) {
                $pv = [string]$f[$ix['.periodMs']]
                if ($pv.Length -gt 0) { $s.Periods[$pv] = 1 + $(if ($s.Periods.ContainsKey($pv)) { $s.Periods[$pv] } else { 0 }) }
            }
            if ($ix['.reason'] -ge 0) {
                $rv = [string]$f[$ix['.reason']]
                if ($rv.Length -gt 0) { $s.Reasons[$rv] = 1 + $(if ($s.Reasons.ContainsKey($rv)) { $s.Reasons[$rv] } else { 0 }) }
            }
            if (($state -eq 'fresh' -or $state -eq 'repeat') -and $ix['.frame'] -ge 0) {
                $fv = [string]$f[$ix['.frame']]
                if ($fv -match $frameRx) {
                    $key = $Matches[1]; $seq = [long]$Matches[2]
                    if ($null -ne $s.LastKey -and $s.LastKey -ne $key) { $s.Generations = $s.Generations + 1 }
                    elseif ($null -ne $s.LastSeq -and $state -eq 'fresh' -and $seq -gt $s.LastSeq + 1) {
                        $skipped = $seq - $s.LastSeq - 1
                        $s.Jumps = $s.Jumps + $skipped; $s.JumpEvents = $s.JumpEvents + 1
                        if ($skipped -gt $s.MaxJump) { $s.MaxJump = $skipped }
                    }
                    $s.LastKey = $key; $s.LastSeq = $seq
                }
            }
            if ($state -eq 'fresh' -and $ix['.acquiredOffsetUs'] -ge 0) {
                $acq = 0L
                if ([long]::TryParse([string]$f[$ix['.acquiredOffsetUs']], [Globalization.NumberStyles]::AllowLeadingSign, $inv, [ref]$acq)) {
                    $age = ($off - $acq) / 1000.0
                    $s.AgeSum += $age; $s.AgeN++
                    if ($null -eq $s.AgeMax -or $age -gt $s.AgeMax) { $s.AgeMax = $age }
                }
            }
        }
        foreach ($v in $valueCols) {
            $cell = [string]$f[$v.Index]
            if ($cell.Length -eq 0) { $v.Empty = $v.Empty + 1; continue }
            $v.N = $v.N + 1
            $d = 0.0
            if ([double]::TryParse($cell, [Globalization.NumberStyles]::Float, $inv, [ref]$d)) {
                $v.Num = $v.Num + 1; $v.Sum += $d
                if ($null -eq $v.Min -or $d -lt $v.Min) { $v.Min = $d }
                if ($null -eq $v.Max -or $d -gt $v.Max) { $v.Max = $d }
            }
            elseif ($v.Texts.Count -lt 6 -or $v.Texts.ContainsKey($cell)) { $v.Texts[$cell] = 1 + $(if ($v.Texts.ContainsKey($cell)) { $v.Texts[$cell] } else { 0 }) }
        }
    }

    if ($gapN -gt 0) { $out.row_gap_ms = [ordered]@{ min = (Round-Or-Null $gapMin 1); mean = (Round-Or-Null ($gapSum / $gapN) 1); max = (Round-Or-Null $gapMax 1) } }
    $total = [double][math]::Max(1, $out.rows)
    foreach ($s in $sources) {
        $periods = [ordered]@{}
        foreach ($k in ($s.Periods.Keys | Sort-Object { [int]$_ })) { $periods[[string]$k] = $s.Periods[$k] }
        $reasons = [ordered]@{}
        foreach ($k in ($s.Reasons.Keys | Sort-Object)) { $reasons[[string]$k] = $s.Reasons[$k] }
        $out.sources[$s.Name] = [ordered]@{
            fresh = $s.Fresh; repeat = $s.Repeat; missing = $s.Missing; other = $s.Other
            fresh_pct = (Round-Or-Null (100.0 * $s.Fresh / $total) 1)
            repeat_pct = (Round-Or-Null (100.0 * $s.Repeat / $total) 1)
            missing_pct = (Round-Or-Null (100.0 * $s.Missing / $total) 1)
            period_ms = $periods
            reasons = $reasons
            frame_jumps = $s.Jumps
            frame_jump_events = $s.JumpEvents
            max_frame_jump = $s.MaxJump
            generation_changes = $s.Generations
            age_ms_mean = $(if ($s.AgeN -gt 0) { Round-Or-Null ($s.AgeSum / $s.AgeN) 1 } else { $null })
            age_ms_max = (Round-Or-Null $s.AgeMax 1)
        }
    }
    foreach ($v in $valueCols) {
        $texts = [ordered]@{}
        foreach ($k in ($v.Texts.Keys | Sort-Object)) { $texts[[string]$k] = $v.Texts[$k] }
        $out.values[$v.Name] = [ordered]@{
            rows_with_value = $v.N
            rows_empty = $v.Empty
            min = (Round-Or-Null $v.Min 3)
            max = (Round-Or-Null $v.Max 3)
            mean = $(if ($v.Num -gt 0) { Round-Or-Null ($v.Sum / $v.Num) 3 } else { $null })
            texts = $texts
        }
    }
    return $out
}

# One row for the SensorHost(s) alone in a tick: the sum of what the child series read (the same sum the total row makes, without the GUI).
function New-ChildRow($Tick, [double]$T, [double]$Dt) {
    $kids = @($Tick | Where-Object { $_.Role -eq 'child' })
    if ($kids.Count -eq 0) { return $null }
    $row = @{ T = $T; Role = 'child'; Name = 'children'; ProcId = $null; StartUtc = $null; Dt = $Dt; Cpu = $null; CpuDelta = $null; Cycles = $null; CyclesDelta = $null; CycDt = $null; Private = 0L; Ws = 0L; Handles = 0 }
    foreach ($r in $kids) {
        if ($null -ne $r.CpuDelta) { $row.CpuDelta = $(if ($null -eq $row.CpuDelta) { $r.CpuDelta } else { $row.CpuDelta + $r.CpuDelta }) }
        if ($null -ne $r.CyclesDelta) {
            $row.CyclesDelta = $(if ($null -eq $row.CyclesDelta) { $r.CyclesDelta } else { $row.CyclesDelta + $r.CyclesDelta })
            $row.CycDt = $Dt
        }
        foreach ($f in @('Private', 'Ws', 'Handles')) {
            if ($null -eq $row[$f] -or $null -eq $r[$f]) { $row[$f] = $null } else { $row[$f] = $row[$f] + $r[$f] }
        }
    }
    foreach ($r in $kids) {
        if ($null -ne $r.Dt -and $r.Dt -gt $row.Dt) { $row.Dt = $r.Dt }
        if ($null -ne $row.CycDt -and $null -ne $r.CycDt -and $r.CycDt -gt $row.CycDt) { $row.CycDt = $r.CycDt }
    }
    return $row
}

# The windows of a record run, from the events' times (seconds from the stopwatch zero): before the start, while recording, after the stop. The
# first 30 s are startup and are left out of "before"; RecordGuardSeconds are left out on each side of the start and the stop. A window that
# cannot be formed (the recording never started, or never stopped) is empty with a note, never invented.
function Get-RecordWindows($MainRows, $ChildRows, $TotalRows, $Timeline, [double]$Duration, [int]$Cpus, $Notes) {
    $g = [double]$script:RecordGuardSeconds
    $first = [double]$script:FirstWindowSeconds
    $startS = $Timeline.start_s; $stopS = $Timeline.stop_s; $stoppedS = $Timeline.stopped_s
    if ($null -ne $startS -and $Timeline.start_ok -ne $true) { $startS = $null }
    $spans = [ordered]@{}
    $spans['before'] = @($first, $(if ($null -ne $startS) { $startS - $g } else { $Duration }))
    if ($null -eq $startS) { $spans['recording'] = @(0.0, 0.0); [void]$Notes.Add('the recording did not start: the recording and after windows are empty') }
    elseif ($null -eq $stopS) { $spans['recording'] = @(($startS + $g), $Duration); [void]$Notes.Add('no stop event came: the recording window runs to the end of the run and the after window is empty') }
    else { $spans['recording'] = @(($startS + $g), ($stopS - $g)) }
    if ($null -eq $startS -or $null -eq $stoppedS) { $spans['after'] = @(0.0, 0.0) } else { $spans['after'] = @(($stoppedS + $g), $Duration) }
    $win = [ordered]@{}
    foreach ($k in $spans.Keys) {
        $from = [double]$spans[$k][0]; $to = [double]$spans[$k][1]
        if ($to - $from -lt 3 -and -not ($from -eq 0.0 -and $to -eq 0.0)) { [void]$Notes.Add(('the {0} window is {1} s long: its figures are not meaningful (-LeadSeconds 40 or more leaves a before window)' -f $k, (Fmt ([math]::Max(0.0, $to - $from)) '0.0'))) }
        $win[$k] = [ordered]@{
            main = (Get-WindowStats $MainRows $from $to $Cpus)
            child = (Get-WindowStats $ChildRows $from $to $Cpus)
            total = (Get-WindowStats $TotalRows $from $to $Cpus)
        }
    }
    return $win
}

# Everything the record scenario adds to a run: the timeline, the periods, the CSV read from outside, the backlog evidence and the SensorHost period
# evidence, plus the windows (returned under "windows"; the caller puts them where the other windows are). $Warnings gets one line per problem.
function Get-RecordSection($Plan, [string]$EventsPath, [DateTime]$ZeroUtc, [double]$Duration, $MainRows, $ChildRows, $TotalRows, [int]$Cpus, $Warnings, $WindowStates = $null) {
    $ev = Read-RecordEvents $EventsPath
    $timeline = Get-RecordTimeline $ev.events $ZeroUtc
    $windowSummary = $null
    if ($null -ne $WindowStates) { $windowSummary = Get-WindowStateSummary $WindowStates $timeline $Duration }
    $periods = Get-PeriodSummary $ev.events $timeline.user_interval_ms $windowSummary
    $notes = New-Object System.Collections.ArrayList
    $windows = Get-RecordWindows $MainRows $ChildRows $TotalRows $timeline $Duration $Cpus $notes
    foreach ($n in $notes) { [void]$Warnings.Add($n) }
    if ($null -ne $windowSummary -and ($windowSummary.slow_mode_at_start -or $windowSummary.slow_mode_at_stop -or $windowSummary.slow_mode_after_the_stop)) {
        [void]$Warnings.Add(('the PC Mieru window was minimized or hidden during the run (at the start: {0}, at the stop: {1}, after the stop: {2}): the sampler ran in slow mode (5 s) there, which is not the user interval' -f $windowSummary.at_start, $windowSummary.at_stop, ($windowSummary.after -join '/')))
    }

    if (-not $ev.found) { [void]$Warnings.Add('the record events file was not written: the GUI did not begin the recording (an old PCMieru.exe without --measure-record?)') }
    elseif ($null -eq $timeline.start_s) { [void]$Warnings.Add('no start event: the GUI never tried to start the recording') }
    elseif ($timeline.start_ok -ne $true) { [void]$Warnings.Add('the recording did not start: ' + $timeline.start_problem) }
    elseif ($null -eq $timeline.stopped_s) { [void]$Warnings.Add('no stopped event: the recording was still running when the run ended (the window close stopped it)') }
    foreach ($m in $timeline.errors) { [void]$Warnings.Add('the GUI reported an error in the record driver: ' + $m) }
    if ($ev.bad_lines -gt 0) { [void]$Warnings.Add(('{0} line(s) of the record events file are not events' -f $ev.bad_lines)) }

    $csvPath = $timeline.csv
    if ([string]::IsNullOrEmpty([string]$csvPath) -and $null -ne $Plan) {
        $found = @(Get-ChildItem -LiteralPath $Plan.Folder -Filter 'run-*.csv' -ErrorAction SilentlyContinue | Select-Object -First 1)
        if ($found.Count -gt 0) { $csvPath = $found[0].FullName }
    }
    $csv = Get-RecordCsvStats ([string]$csvPath)
    if (-not $csv.read -and $timeline.start_ok -eq $true) {
        $why = $(if ($script:FakesOn) { 'with -Fakes the recording is in memory and writes no CSV' } else { [string]$csv.note })
        [void]$Warnings.Add('the recording CSV could not be analysed: ' + $why)
    }

    $jumps = 0L
    foreach ($k in $csv.sources.Keys) { $jumps += [long]$csv.sources[$k].frame_jumps }
    $expected = $null
    if ($null -ne $timeline.duration_us) { $expected = [math]::Floor([double]$timeline.duration_us / 1.0e6) }
    if ($null -ne $timeline.dropped_ticks -and [long]$timeline.dropped_ticks -gt 0) { [void]$Warnings.Add(('the recorder dropped {0} tick(s): the writer could not keep up' -f $timeline.dropped_ticks)) }
    if ($jumps -gt 0) { [void]$Warnings.Add(('{0} frame(s) were skipped between rows of the recording CSV (frame-number jumps)' -f $jumps)) }
    $backlog = [ordered]@{
        note = 'the writer queue depth is not public; it is seen as dropped ticks (the run''s own count), rows against seconds, frame-number jumps, the longest gap between rows and how long the stop took (the writer draining)'
        dropped_ticks = $timeline.dropped_ticks
        rows_reported = $timeline.rows
        rows_in_csv = $csv.rows
        rows_expected_from_duration = $expected
        frame_jumps_total = $jumps
        longest_row_gap_ms = $(if ($null -ne $csv.row_gap_ms) { $csv.row_gap_ms.max } else { $null })
        row_gaps_over_1500_ms = $csv.row_gaps_over_1500_ms
        stop_ms = $timeline.stop_ms
        start_ms = $timeline.start_ms
    }

    $gpuPeriods = [ordered]@{}
    foreach ($k in $csv.sources.Keys) { if ($k -like 'gpu*') { $gpuPeriods[$k] = $csv.sources[$k].period_ms } }
    $b = $windows['before'].child; $rw = $windows['recording'].child; $a = $windows['after'].child
    $ratio = $null
    if ($null -ne $rw.cycles_per_s_mean -and $null -ne $a.cycles_per_s_mean -and $rw.cycles_per_s_mean -gt 0) { $ratio = Round-Or-Null ($a.cycles_per_s_mean / $rw.cycles_per_s_mean) 3 }
    $sensorHost = [ordered]@{
        method = 'indirect (its period is not readable from outside): the periodMs the recording CSV carries for each GPU while recording, and the SensorHost''s own cycles and CPU in the three windows'
        gpu_period_ms_while_recording = $gpuPeriods
        child_cycles_per_s_before = $b.cycles_per_s_mean
        child_cycles_per_s_recording = $rw.cycles_per_s_mean
        child_cycles_per_s_after = $a.cycles_per_s_mean
        child_cpu_ms_per_s_before = $b.cpu_ms_per_s_mean
        child_cpu_ms_per_s_recording = $rw.cpu_ms_per_s_mean
        child_cpu_ms_per_s_after = $a.cpu_ms_per_s_mean
        cycles_after_over_recording = $ratio
        note = $(if ($null -eq $ratio) { 'no SensorHost figures in one of the windows (a fake run, or no SensorHost): the period after the stop is not observable here' } else { 'a SensorHost back at the user interval does less per second than one at the recording period when the user interval is longer than 1000 ms' })
    }

    return [ordered]@{
        plan = $(if ($null -ne $Plan) { [ordered]@{ folder = $Plan.Folder; lead_seconds = $Plan.Lead; record_seconds = $Plan.For; user_interval_ms = $Plan.Interval } } else { $null })
        events_file = $(if ([string]::IsNullOrEmpty($EventsPath)) { $null } else { Split-Path -Leaf $EventsPath })
        events_found = $ev.found
        events_count = $ev.events.Count
        guard_seconds = $script:RecordGuardSeconds
        timeline = $timeline
        periods = $periods
        csv = $csv
        backlog = $backlog
        sensorhost_period = $sensorHost
        windows = $windows
    }
}

# ---- running the app ----

# What the GUI wrote to stderr (it writes one line there and exits 1 when it cannot start a measurement run: a marker file that cannot be made).
# Stderr is read in the background from the start, so a full pipe never blocks the GUI.
$script:AppErrTask = $null

# $RecordPlan (record mode only): @{ Folder; Lead; For; Interval } becomes the test-only --measure-record switches (the exact form is in the help).
function New-AppStartInfo([string]$MarkFile, $RecordPlan = $null) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:ExePath
    $psi.WorkingDirectory = Split-Path -Parent $script:ExePath
    $psi.UseShellExecute = $false
    $psi.RedirectStandardError = $true
    $arguments = '--measure "' + $MarkFile + '"'
    if ($script:FakesOn) { $arguments += ' --fakes' }
    if ($null -ne $RecordPlan) {
        $arguments += ' --measure-record "' + ([string]$RecordPlan.Folder).TrimEnd('\') + '" --measure-record-lead ' + [int]$RecordPlan.Lead + ' --measure-record-for ' + [int]$RecordPlan.For
        if ([int]$RecordPlan.Interval -gt 0) { $arguments += ' --measure-record-interval ' + [int]$RecordPlan.Interval }
    }
    $psi.Arguments = $arguments
    return $psi
}

function Start-App([string]$MarkFile, $RecordPlan = $null) {
    $proc = [System.Diagnostics.Process]::Start((New-AppStartInfo $MarkFile $RecordPlan))
    $script:AppErrTask = $proc.StandardError.ReadToEndAsync()
    return $proc
}

# The one line of stderr the GUI left ('' when it left none). Call it after the process ended.
function Get-AppStderr {
    if ($null -eq $script:AppErrTask) { return '' }
    try {
        if ($script:AppErrTask.Wait(3000)) { return (([string]$script:AppErrTask.Result) -replace '\s+', ' ').Trim() }
    }
    catch { }
    return ''
}

# The GUI ended by itself while the script was waiting for it: that run is not a measurement. A non-zero exit code or a line on stderr is
# said at once, with the line, instead of waiting for marks that cannot come.
function Assert-AppStillRunning([System.Diagnostics.Process]$Proc) {
    $code = $null
    try { $Proc.WaitForExit(2000) | Out-Null; if ($Proc.HasExited) { $code = $Proc.ExitCode } } catch { }
    $text = Get-AppStderr
    if (($null -ne $code -and $code -ne 0) -or $text.Length -gt 0) {
        throw ('PCMieru.exe ended by itself (exit code {0}){1}' -f $(if ($null -ne $code) { $code } else { 'unknown' }), $(if ($text.Length -gt 0) { ': ' + $text } else { '' }))
    }
}

# Task Manager is marked "highest available": started the usual way it comes up elevated (no prompt on this kind of machine) and a normal shell
# can neither close nor fully read it. __COMPAT_LAYER=RunAsInvoker on THIS child's start info only (never the script's own environment) makes it
# run at the integrity level of the caller, as an ordinary window of the session.
function New-TaskMgrStartInfo {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $env:SystemRoot 'System32\taskmgr.exe'
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['__COMPAT_LAYER'] = 'RunAsInvoker'
    return $psi
}

function Get-TaskMgrInfo {
    $path = Join-Path $env:SystemRoot 'System32\taskmgr.exe'
    $info = [ordered]@{ file_version = $null; product_version = $null; launch = 'as_invoker'; note = $null }
    try {
        $item = Get-Item -LiteralPath $path
        $info.file_version = $item.VersionInfo.FileVersion
        $info.product_version = $item.VersionInfo.ProductVersion
    }
    catch { $info.note = 'the file version could not be read: ' + (Get-ShortError $_) }
    return $info
}

# Returns @{ Proc; LaunchedPid; Note }. The Task Manager measured is the one that has a main window: a started process that hands over to an
# elevated copy of itself (it ends at once, without a window) is followed to the copy; an already-open Task Manager is used as it is.
function Start-TaskMgr {
    $open = @(Get-ProcessesNamed 'Taskmgr')
    foreach ($p in $open) {
        if ((Test-Alive $p) -and (Get-MainWindow $p) -ne [IntPtr]::Zero) {
            foreach ($q in $open) { if ($q.Id -ne $p.Id) { Dispose-Quietly $q } }
            return @{ Proc = $p; LaunchedPid = $null; Note = 'Task Manager was already open (pid ' + $p.Id + '): this run measures it as it is, not a fresh start' }
        }
    }
    $before = @{}
    foreach ($p in $open) { $before[$p.Id] = $true; Dispose-Quietly $p }
    $started = $null
    try { $started = [System.Diagnostics.Process]::Start((New-TaskMgrStartInfo)) }
    catch { throw ('Task Manager did not start (' + (Get-ShortError $_) + ')') }
    $launchedPid = [int]$started.Id
    Dispose-Quietly $started
    $limit = [Diagnostics.Stopwatch]::StartNew()
    while ($limit.Elapsed.TotalSeconds -lt 90) {
        foreach ($p in @(Get-ProcessesNamed 'Taskmgr')) {
            if (-not $before.ContainsKey($p.Id) -and (Test-Alive $p) -and (Get-MainWindow $p) -ne [IntPtr]::Zero) {
                $note = $null
                if ($p.Id -ne $launchedPid) { $note = 'the started Task Manager (pid ' + $launchedPid + ') handed over to pid ' + $p.Id + ' (it restarted itself); pid ' + $p.Id + ' is measured' }
                return @{ Proc = $p; LaunchedPid = $launchedPid; Note = $note }
            }
            Dispose-Quietly $p
        }
        [Threading.Thread]::Sleep(250)
    }
    throw 'Task Manager did not show a window within 90 s'
}

# ---- fairness of the comparison (read only: Task Manager's settings are never written) ----

function Get-TaskMgrSettingsPath {
    return (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\TaskManager\settings.json')
}

# Task Manager's own settings, read only: the page it starts on (CurrentView, 0 = Processes) and the small-view flag. Windows 11 keeps no readable
# update speed, so that is always recorded as unknown (update_speed = $null) and the caller warns about it.
function Read-TaskMgrSettings([string]$Path = '') {
    if ($Path -eq '') { $Path = Get-TaskMgrSettingsPath }
    $r = [ordered]@{ read = $false; start_page = $null; start_page_name = $null; small_view = $null; update_speed = $null; note = $null }
    try {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $r.note = 'Task Manager settings.json was not found'; return $r }
        $json = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        $r.read = $true
        $view = $json.PSObject.Properties['CurrentView']
        if ($null -ne $view) {
            $r.start_page = [int]$view.Value
            $r.start_page_name = $(if ($r.start_page -eq 0) { 'Processes' } else { 'page ' + $r.start_page })
        }
        else { $r.note = 'settings.json has no CurrentView: the start page is unknown' }
        $small = $json.PSObject.Properties['SmallView']
        if ($null -ne $small) { $r.small_view = [bool]$small.Value }
    }
    catch { $r.read = $false; $r.note = 'settings.json could not be read: ' + (Get-ShortError $_) }
    return $r
}

# Warnings about the fairness of one compare pair: the settings of Task Manager and the two window sizes. Never changes anything.
function Get-CompareWarnings($Settings, $PcWindow, $TmWindow) {
    $w = New-Object System.Collections.ArrayList
    if ($null -eq $Settings -or -not $Settings.read) { [void]$w.Add('Task Manager settings could not be read: it may not start on Processes') }
    elseif ($null -eq $Settings.start_page) { [void]$w.Add('Task Manager does not say which page it starts on: it may not start on Processes') }
    elseif ($Settings.start_page -ne 0) { [void]$w.Add('Task Manager does not start on Processes (it starts on ' + $Settings.start_page_name + '): its numbers are for another page') }
    [void]$w.Add('Task Manager''s update speed cannot be read on this Windows: set it to Normal by hand (View, Update speed) before the run, or the CPU of both apps is not comparable')
    foreach ($pair in @(@('PC Mieru', $PcWindow), @('Task Manager', $TmWindow))) {
        if ($null -eq $pair[1] -or $null -eq $pair[1].width) { [void]$w.Add('the window size of ' + $pair[0] + ' could not be read') }
    }
    if ($null -ne $PcWindow -and $null -ne $PcWindow.width -and $null -ne $TmWindow -and $null -ne $TmWindow.width) {
        $dw = [math]::Abs($PcWindow.width - $TmWindow.width) / [double][math]::Max($PcWindow.width, $TmWindow.width)
        $dh = [math]::Abs($PcWindow.height - $TmWindow.height) / [double][math]::Max($PcWindow.height, $TmWindow.height)
        if ($dw -gt $script:WindowSizeTolerance -or $dh -gt $script:WindowSizeTolerance) {
            [void]$w.Add(('the window sizes differ by more than {0} %: PC Mieru {1} x {2}, Task Manager {3} x {4} (a bigger window draws more)' -f ([int]($script:WindowSizeTolerance * 100)), $PcWindow.width, $PcWindow.height, $TmWindow.width, $TmWindow.height))
        }
    }
    return $w.ToArray()
}

# Writes the folder-writable probe: a file is made and removed before anything is started. Throws one line when it cannot be done.
function Assert-WritableFolder([string]$Dir, [string]$ProbeName = '') {
    try {
        New-Item -ItemType Directory -Force -Path $Dir | Out-Null
        if ($ProbeName -eq '') { $ProbeName = '.write-test-' + [Guid]::NewGuid().ToString('N') }
        $probe = Join-Path $Dir $ProbeName
        [IO.File]::WriteAllText($probe, 'x')
        Remove-Item -LiteralPath $probe -Force
    }
    catch { throw ('the output folder cannot be written (' + (Get-ShortError $_) + '); nothing was started: ' + $Dir) }
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
        try { $sent = [PcmMeasureWin]::PostClose((Get-MainWindow $Proc)) } catch { }
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
        $(if ($null -ne $r.Cycles) { $r.Cycles.ToString('0', $inv) } else { '' })
        $(if ($null -ne $r.CyclesDelta) { $r.CyclesDelta.ToString('0', $inv) } else { '' })
        $(if ($null -ne $r.CyclesDelta -and $null -ne $r.CycDt -and $r.CycDt -gt 0) { ($r.CyclesDelta / $r.CycDt).ToString('0', $inv) } else { '' })
        $(if ($null -ne $r.CyclesDelta -and $null -ne $r.CycDt -and $r.CycDt -gt 0) { ($r.CyclesDelta / $r.CycDt / 1.0e9).ToString('0.0000', $inv) } else { '' })
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
        integrity = $S.Token.integrity
        integrity_rid = $S.Token.integrity_rid
        elevated = $S.Token.elevated
        integrity_unknown_reason = $S.Token.reason
        cycles_total = $S.LastCycles
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
        [switch]$Warmup,
        [hashtable]$Record = $null
    )

    $inv = Get-Inv
    $isApp = ($Target -eq 'pcmieru')
    # record mode: the GUI records itself (--measure-record); a warm-up never does.
    if ($Warmup -or -not $isApp) { $Record = $null }
    $markFile = Join-Path $RunDir ($Label + '-marks.jsonl')
    $csvName = $Label + '.csv'
    $writer = $null
    if (-not $Warmup) {
        $writer = New-Object System.IO.StreamWriter((Join-Path $RunDir $csvName), $false, (New-Object System.Text.UTF8Encoding($false)))
        $writer.NewLine = "`n"
        $writer.WriteLine('t_s,role,name,pid,start_utc,dt_s,cpu_s,cpu_delta_s,cycles,cycles_delta,cycles_per_s,ghz_equivalent,private_bytes,working_set_bytes,handles')
    }

    $mainRows = New-Object System.Collections.ArrayList
    $totalRows = New-Object System.Collections.ArrayList
    $childRows = New-Object System.Collections.ArrayList
    $zeroUtc = [DateTime]::UtcNow
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
    $nav = @{ Nav = $null; ChecksIndex = $null }
    $pageTries = 0; $pageOk = 0; $pageSkips = 0; $pageRefused = 0
    $churnStarted = 0; $churnFailed = 0
    $markDoneT = $null
    $windowSize = $null
    $windowStates = New-Object System.Collections.ArrayList # record mode: { t_s; state } at the start and at every change of the window's state
    $lastWindowState = '(not asked yet)'
    $tmSettings = $null
    $tmInfo = $null
    $tmStart = $null
    if (-not $isApp) { $tmSettings = Read-TaskMgrSettings; $tmInfo = Get-TaskMgrInfo }

    $clock = [Diagnostics.Stopwatch]::StartNew()
    $zeroUtc = [DateTime]::UtcNow   # the record events' system times become seconds from this zero
    try {
        if ($isApp) { $proc = Start-App $markFile $Record }
        else {
            $tmStart = Start-TaskMgr
            $proc = $tmStart.Proc
            $clock.Restart()
            $tmInfo['launched_pid'] = $tmStart.LaunchedPid
            $tmInfo['measured_pid'] = [int]$proc.Id
            $tmInfo['start_note'] = $tmStart.Note
            if ($null -ne $tmStart.Note) { [void]$warnings.Add($tmStart.Note) }
        }
        $startNow = [DateTime]::UtcNow
        $main = New-Series $proc 'main' 0.0 $startNow $true
        if (-not $isApp -and $null -eq $tmStart.LaunchedPid -and $null -ne $main.Handle) {
            # A Task Manager that was already open has been running long before this run: its first interval starts at clock zero, not at its
            # creation (or the first second would carry all the CPU of its earlier life).
            $c0 = [PcmProc]::Read($main.Handle)
            if ($c0.TimesError -eq 0) { $main.PrevT = 0.0; $main.PrevCpu = [double]$c0.CpuSeconds }
            if ($c0.CyclesError -eq 0) { $main.PrevCycT = 0.0; $main.PrevCycles = [double]$c0.Cycles }
        }
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

            if (-not (Test-SeriesAlive $main)) {
                $ended = 'the process ended by itself'
                $main.Ended = $true
                # An app that ended before it ever ran says why on stderr (a marker file that cannot be made): that is the run's error, not a result.
                if ($isApp) { Assert-AppStillRunning $proc }
                break
            }

            if ($isApp) {
                foreach ($cs in (Find-NewChildren $main $seen $t $nowUtc $warnings)) { [void]$children.Add($cs) }
            }

            $tick = New-Object System.Collections.ArrayList
            $mainRow = Read-Series $main $t
            [void]$tick.Add($mainRow)
            foreach ($c in @($children)) {
                if (-not (Test-SeriesAlive $c)) {
                    $c.Ended = $true
                    Close-Series $c
                    [void]$finished.Add($c)
                    $children.Remove($c)
                    continue
                }
                [void]$tick.Add((Read-Series $c $t))
            }

            $total = @{ T = $t; Role = 'total'; Name = 'total'; ProcId = $null; StartUtc = $null; Dt = ($t - $prevTickT); Cpu = $null; CpuDelta = $null; Cycles = $null; CyclesDelta = $null; CycDt = $null; Private = 0L; Ws = 0L; Handles = 0 }
            foreach ($r in $tick) {
                if ($null -ne $r.CpuDelta) { $total.CpuDelta = $(if ($null -eq $total.CpuDelta) { $r.CpuDelta } else { $total.CpuDelta + $r.CpuDelta }) }
                if ($null -ne $r.CyclesDelta) {
                    $total.CyclesDelta = $(if ($null -eq $total.CyclesDelta) { $r.CyclesDelta } else { $total.CyclesDelta + $r.CyclesDelta })
                    $total.CycDt = $total.Dt
                }
                foreach ($pair in @(@('Private', 'Private'), @('Ws', 'Ws'), @('Handles', 'Handles'))) {
                    $f = $pair[0]
                    if ($null -eq $total[$f] -or $null -eq $r[$f]) { $total[$f] = $null } else { $total[$f] = $total[$f] + $r[$f] }
                }
            }
            foreach ($r in $tick) {
                if ($null -ne $r.Dt -and $r.Dt -gt $total.Dt) { $total.Dt = $r.Dt }
                if ($null -ne $total.CycDt -and $null -ne $r.CycDt -and $r.CycDt -gt $total.CycDt) { $total.CycDt = $r.CycDt }
            }
            if ($null -ne $Record) {
                $childRow = New-ChildRow $tick $t $total.Dt
                if ($null -ne $childRow) { [void]$childRows.Add($childRow) }
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

            # The size of the window, once, a few seconds in (a bigger window draws more: the comparison needs both apps' sizes).
            if (-not $Warmup -and $null -eq $windowSize -and $t -ge $script:WindowSizeAtSeconds) { $windowSize = Get-WindowSize $proc }

            # Record mode: what the window is doing, at every tick (normal / minimized / hidden). A minimized or hidden window puts the sampler in slow mode
            # (5 s), which is not the user's interval: the report must say so rather than let it pass for a return to the user interval.
            if ($null -ne $Record) {
                $ws = Get-WindowState $proc
                if ($ws -ne $lastWindowState) { [void]$windowStates.Add([ordered]@{ t_s = [math]::Round($t, 1); state = $ws }); $lastWindowState = $ws }
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
                    if ($step.skipped) { $pageSkips++ }
                    else {
                        $pageTries++
                        if ($step.ok) { $pageOk++ }
                        elseif (@($step.keys).Count -eq 0 -or $null -eq $step.to) { $pageRefused++ }
                    }
                    [void]$events.Add([ordered]@{ t_s = [math]::Round($t, 1); kind = 'page'; ok = $step.ok; skipped = $step.skipped; from = $step.from; to = $step.to; count = $step.count; keys = (@($step.keys) -join ','); note = $step.note })
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
    catch {
        Close-AllSeries $main $children $finished
        throw
    }
    finally {
        if ($null -ne $writer) { $writer.Flush(); $writer.Dispose() }
    }

    # close: who ended the process is recorded as closed_by (script: CloseMainWindow ended it; kill: it had to be killed; manual: the person
    # closed it; already_exited). An elevated Task Manager is never closed from here: nothing in this script reaches an elevated window (UIPI
    # drops the message) and nothing elevated is used to force it.
    if ($null -ne $proc) {
        if ($isApp) {
            $close = Close-Politely $proc $script:CloseTimeoutSeconds
            $close['closed_by'] = $(switch ($close.method) { 'CloseMainWindow' { 'script' } 'Kill' { 'kill' } 'already-exited' { 'already_exited' } default { $close.method } })
        }
        else {
            $isHigh = ($main.Token.integrity -eq 'high' -or $main.Token.integrity -eq 'system' -or $main.Token.elevated -eq $true)
            if ($isHigh) {
                $close = [ordered]@{ method = 'not-tried'; waited_seconds = $null; exit_code = $null; note = 'Task Manager is elevated (integrity ' + $main.Token.integrity + '): it cannot be closed from a normal script' }
            }
            else { $close = Close-Politely $proc 15 -NoKill }
            if ($isHigh -or $close.method -eq 'left-running') {
                # Left open (the next run measures the same window). Nothing here waits for a person: the runs go on unattended.
                Write-Host ('  Task Manager (pid {0}) is left open: it cannot be closed from this script. Close it yourself when the runs are done.' -f $proc.Id) -ForegroundColor Yellow
                $close.note = $close.note + '; it was left open'
                $close['closed_by'] = 'left_open'
            }
            else { $close['closed_by'] = $(switch ($close.method) { 'CloseMainWindow' { 'script' } 'already-exited' { 'already_exited' } default { $close.method } }) }
        }
    }
    if ($isApp -and $null -ne $main) {
        $limit = [Diagnostics.Stopwatch]::StartNew()
        foreach ($c in @($children)) {
            $c.Ended = $true
            while ((Test-SeriesAlive $c) -and $limit.Elapsed.TotalSeconds -lt 10) { [Threading.Thread]::Sleep(200) }
            if (Test-SeriesAlive $c) {
                [void]$warnings.Add('a ' + $script:SensorHostName + ' (pid ' + $c.ProcId + ') was still running 10 s after the GUI closed; it was killed')
                try { $c.Proc.Kill() } catch { }
            }
            [void]$finished.Add($c)
        }
        $children.Clear()
    }
    # Every series' handle is closed here, after the last look at the processes.
    Close-AllSeries $main $children $finished
    if ($isApp) { $marks = Read-Marks $markFile }

    $run = [ordered]@{
        label = $Label
        target = $Target
        csv = $(if ($Warmup) { $null } else { $csvName })
        marks_file = $(if ($isApp) { $Label + '-marks.jsonl' } else { $null })
        ended = $ended
        closed = $close
        closed_by = $(if ($null -ne $close) { $close.closed_by } else { $null })
        duration_s = $(if ($mainRows.Count -gt 0) { [math]::Round($mainRows[$mainRows.Count - 1].T, 1) } else { 0 })
        samples = $mainRows.Count
        clock_zero = $(if ($isApp) { 'just before the process was started' } else { 'when the Task Manager process was first seen' })
    }
    if ($Warmup) { $run['warnings'] = $warnings; return $run }

    $run['integrity'] = $(if ($null -ne $main) { $main.Token } else { $null })
    $run['window_size'] = $windowSize
    if (-not $isApp) {
        $run['taskmgr_launch'] = 'as_invoker'
        $run['task_manager'] = $tmInfo
        $run['taskmgr_settings'] = $tmSettings
    }

    $created = $null
    if ($null -ne $main) { $created = $main.StartUtc }
    $run['startup'] = $(if ($isApp) { Get-Startup $marks $created } else { [ordered]@{ note = 'Task Manager has no marks: startup is missing'; first_list_ms = $null; cpu_list_ms = $null } })
    $series = New-Object System.Collections.ArrayList
    if ($null -ne $main) { [void]$series.Add((Get-SeriesSummary $main)) }
    foreach ($c in $finished) { [void]$series.Add((Get-SeriesSummary $c)) }
    $run['series'] = $series
    $run['child_series_count'] = $finished.Count

    $windows = [ordered]@{}
    $recordSection = $null
    if ($LongMode) {
        $windows['all'] = [ordered]@{
            main = (Get-WindowStats $mainRows 0 $DurationSeconds $Cpus)
            total = (Get-WindowStats $totalRows 0 $DurationSeconds $Cpus)
        }
    }
    elseif ($null -ne $Record) {
        # first_30s is the startup, as in warm; then before / recording / after from the record events (the GUI alone, the SensorHost alone, the total).
        $first = [double]$script:FirstWindowSeconds
        $windows['first_30s'] = [ordered]@{
            main = (Get-WindowStats $mainRows 0 $first $Cpus)
            total = (Get-WindowStats $totalRows 0 $first $Cpus)
        }
        $recordSection = Get-RecordSection $Record (Get-RecordEventsPath $markFile) $zeroUtc $DurationSeconds $mainRows $childRows $totalRows $Cpus $warnings $windowStates
        foreach ($k in $recordSection.windows.Keys) { $windows[$k] = $recordSection.windows[$k] }
        $recordSection.Remove('windows')
        $run['record'] = $recordSection
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
                method = 'PostMessage of the shortest key route (HOME/END, then DOWN/UP) to the navigation list box; it never selects the checks page (that page lists the real check history); no focus needed, none taken'
                needs_focus = $false
                checks_page_index = $nav.ChecksIndex
                attempts = $pageTries
                changed_page = $pageOk
                modifier_skips = $pageSkips
                refused = $pageRefused
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
    if ($Run.Contains('integrity') -and $null -ne $Run['integrity']) {
        $tok = $Run['integrity']
        Write-Host ('  integrity: {0}{1}{2}' -f $tok.integrity, $(if ($null -ne $tok.elevated) { ', elevated ' + $tok.elevated } else { '' }), $(if ($null -ne $tok.reason) { ' (' + $tok.reason + ')' } else { '' }))
    }
    if ($Run.Contains('window_size') -and $null -ne $Run['window_size']) {
        $ws = $Run['window_size']
        Write-Host ('  window: {0}' -f $(if ($null -ne $ws.width) { '{0} x {1} px' -f $ws.width, $ws.height } else { 'size unknown (' + $ws.note + ')' }))
    }
    $s = $Run.startup
    if ($Run.target -eq 'pcmieru') {
        Write-Host ('  startup  first list {0} ms   cpu list {1} ms    [target: warm start < 1000 ms]' -f (Fmt $s.first_list_ms '0.0'), (Fmt $s.cpu_list_ms '0.0'))
        if ($null -ne $s.note) { Write-Host ('  startup note: ' + $s.note) -ForegroundColor Yellow }
    }
    foreach ($w in $Run.windows.Keys) {
        foreach ($who in @('main', 'child', 'total')) {
            if (-not $Run.windows[$w].Contains($who)) { continue }
            $x = $Run.windows[$w][$who]
            $label = $(if ($who -eq 'main') { 'GUI alone' } elseif ($who -eq 'child') { 'SensorHost' } else { 'total    ' })
            Write-Host ('  {0,-9} {1}: cpu mean {2} % ({3} ms/s) max {4} % ({5} ms/s); cycles mean {6} Mcycles/s ({7} GHz-equiv) max {8}; private mean {9} max {10} MB; ws mean {11} max {12} MB; handles mean {13} max {14}{15}' -f `
                $w, $label, (Fmt $x.cpu_percent_mean '0.000'), (Fmt $x.cpu_ms_per_s_mean '0.0'), (Fmt $x.cpu_percent_max '0.000'), (Fmt $x.cpu_ms_per_s_max '0.0'),
                (Fmt $(if ($null -ne $x.cycles_per_s_mean) { $x.cycles_per_s_mean / 1.0e6 } else { $null }) '0.0'), (Fmt $x.ghz_equivalent_mean '0.000'), (Fmt $(if ($null -ne $x.cycles_per_s_max) { $x.cycles_per_s_max / 1.0e6 } else { $null }) '0.0'),
                (Fmt $x.private_mb_mean '0.0'), (Fmt $x.private_mb_max '0.0'), (Fmt $x.working_set_mb_mean '0.0'), (Fmt $x.working_set_mb_max '0.0'),
                (Fmt $x.handles_mean '0'), (Fmt $x.handles_max '0'), $(if ($x.complete) { '' } else { '  (window not complete)' }))
        }
    }
    if ($Run.Contains('long') -and $null -ne $Run['long']) {
        $l = $Run['long']
        Write-Host ('  pages: {0} switches tried, {1} changed the page, {2} skipped for a held Shift/Ctrl/Alt/Win key, {3} not pressed; test processes: {4} started, {5} failed' -f $l.page_switching.attempts, $l.page_switching.changed_page, $l.page_switching.modifier_skips, $l.page_switching.refused, $l.test_processes.started, $l.test_processes.failed)
        foreach ($k in $l.trend.Keys) {
            $tr = $l.trend[$k]
            if ($null -eq $tr) { Write-Host ('  trend {0}: missing' -f $k); continue }
            Write-Host ('  trend {0}: first {1} min mean {2}, last mean {3}, change {4}, slope {5} per hour' -f $k, (Fmt ($tr.window_seconds_used / 60.0) '0.0'), (Fmt $tr.first_window_mean '0.000'), (Fmt $tr.last_window_mean '0.000'), (Fmt $tr.change '0.000'), (Fmt $tr.slope_per_hour '0.000'))
        }
    }
    if ($Run.Contains('record') -and $null -ne $Run['record']) { Write-RecordReport $Run['record'] }
    foreach ($m in $Run.missing) { Write-Host ('  missing: {0} {1}: {2}' -f $m.series, $m.counter, $m.reason) -ForegroundColor Yellow }
    foreach ($w in $Run.warnings) { Write-Host ('  warning: ' + $w) -ForegroundColor Yellow }
}

# The record scenario's part of the report (what the GUI said, what the CSV held, the period, the backlog evidence).
function Write-RecordReport($Rec) {
    $tl = $Rec.timeline
    Write-Host ('  record   begin {0} s, start {1} s (took {2} ms, ok {3}), stop {4} s, stopped {5} s (the stop took {6} ms); status {7}, rows {8}, dropped ticks {9}' -f `
        (Fmt $tl.begin_s '0.0'), (Fmt $tl.start_s '0.0'), (Fmt $tl.start_ms '0'), $(if ($null -ne $tl.start_ok) { $tl.start_ok } else { 'missing' }), (Fmt $tl.stop_s '0.0'), (Fmt $tl.stopped_s '0.0'), (Fmt $tl.stop_ms '0'),
        $(if ($null -ne $tl.status) { $tl.status } else { 'missing' }), (Fmt $tl.rows '0'), (Fmt $tl.dropped_ticks '0'))
    $p = $Rec.periods
    foreach ($name in $p.phases.Keys) {
        $x = $p.phases[$name]
        if ($null -eq $x.core_gap_ms) { Write-Host ('  period   {0,-9}: no snapshot reached the window: missing' -f $name) }
        else {
            $g = $x.core_gap_ms
            Write-Host ('  period   {0,-9}: Core measured gap ms min {1} median {2} mean {3} max {4} (last {5}); nearest nominal {6} ms, {7}% of the gaps within 25% of it; settled {8} ms; pump setting {9} ms (the user interval with slow mode applied, not the recording period); {10} period line(s)' -f $name, (Fmt $g.min '0.0'), (Fmt $g.median '0.0'), (Fmt $g.mean '0.0'), (Fmt $g.max '0.0'), (Fmt $g.last '0'), $g.nominal_ms, (Fmt ($g.share_near_nominal * 100) '0'), $g.settled_nominal_ms, $(if ($x.effective_interval_ms.Count -gt 0) { $x.effective_interval_ms -join '/' } else { 'missing' }), $x.period_lines)
        }
    }
    Write-Host ('  period   user interval {0} ms; while recording at {1} ms: {2}; back at the user interval after the stop: {3}{4}' -f (Fmt $p.user_interval_ms '0'), $p.recording_period_ms, $(if ($null -ne $p.during_recording_is_recording_period) { $p.during_recording_is_recording_period } else { 'missing' }), $(if ($null -ne $p.after_stop_returns_to_user_interval) { $p.after_stop_returns_to_user_interval } else { 'missing' }), $(if ($null -ne $p.note) { ' (' + $p.note + ')' } else { '' }))
    foreach ($name in $p.phases.Keys) {
        $x = $p.phases[$name]
        if ($x.loop_lines -gt 0) { Write-Host ('  period   {0,-9}: the loop ran at {1} ms holding {2} lease(s); {3} of {4} line(s) agree with the phase' -f $name, ($x.loop_period_ms -join '/'), ($x.leases -join '/'), $x.loop_lines_agreeing, $x.loop_lines) }
    }
    Write-Host ('  period   from the loop itself: recording at {0} ms with a lease: {1}; no lease and the user setting after the stop: {2}' -f $p.recording_period_ms, $(if ($null -ne $p.during_recording_loop_period_is_recording_period) { $p.during_recording_loop_period_is_recording_period } else { 'missing (an older PCMieru.exe)' }), $(if ($null -ne $p.after_stop_loop_period_is_user_setting_without_lease) { $p.after_stop_loop_period_is_user_setting_without_lease } else { 'missing' }))
    $wsum = $p.window_state
    if ($null -ne $wsum) {
        Write-Host ('  window   state at the start: {0}; at the stop: {1}; after the stop: {2}; {3} change(s) seen{4}' -f $(if ($null -ne $wsum.at_start) { $wsum.at_start } else { 'missing' }), $(if ($null -ne $wsum.at_stop) { $wsum.at_stop } else { 'missing' }), $(if (@($wsum.after).Count -gt 0) { @($wsum.after) -join '/' } else { 'missing' }), @($wsum.changes).Count, $(if ($null -ne $wsum.note) { ' (' + $wsum.note + ')' } else { '' }))
    }
    $c = $Rec.csv
    if (-not $c.read) { Write-Host ('  csv      not analysed: {0}' -f $c.note) -ForegroundColor Yellow }
    else {
        $gap = $c.row_gap_ms
        $gapMin = $null; $gapMean = $null; $gapMax = $null
        if ($null -ne $gap) { $gapMin = $gap.min; $gapMean = $gap.mean; $gapMax = $gap.max }
        Write-Host ('  csv      {0}: {1} rows ({2} bad); row gap ms min {3} mean {4} max {5}; gaps over 1500 ms: {6}' -f $c.file, $c.rows, $c.bad_rows, (Fmt $gapMin '0.0'), (Fmt $gapMean '0.0'), (Fmt $gapMax '0.0'), $c.row_gaps_over_1500_ms)
        foreach ($name in $c.sources.Keys) {
            $s = $c.sources[$name]
            $per = (@($s.period_ms.Keys | ForEach-Object { '{0} ms x{1}' -f $_, $s.period_ms[$_] }) -join ', ')
            $why = (@($s.reasons.Keys | ForEach-Object { '{0} x{1}' -f $_, $s.reasons[$_] }) -join ', ')
            Write-Host ('  csv      {0,-5}: fresh {1} ({2} %), repeat {3} ({4} %), missing {5} ({6} %); period {7}; frame jumps {8} in {9} place(s); age ms mean {10} max {11}{12}' -f `
                $name, $s.fresh, (Fmt $s.fresh_pct '0.0'), $s.repeat, (Fmt $s.repeat_pct '0.0'), $s.missing, (Fmt $s.missing_pct '0.0'), $(if ($per.Length -gt 0) { $per } else { 'missing' }), $s.frame_jumps, $s.frame_jump_events, (Fmt $s.age_ms_mean '0.0'), (Fmt $s.age_ms_max '0.0'), $(if ($why.Length -gt 0) { '; reasons: ' + $why } else { '' }))
        }
        foreach ($key in @('core.cpu_package_w', 'core.ac_connected', 'core.battery_pct', 'core.battery_state', 'gpu1.temp_c', 'gpu1.power_w')) {
            if (-not $c.values.Contains($key)) { continue }
            $v = $c.values[$key]
            $texts = (@($v.texts.Keys | ForEach-Object { '{0} x{1}' -f $_, $v.texts[$_] }) -join ', ')
            Write-Host ('  csv      {0}: {1} rows with a value, {2} empty{3}{4}' -f $key, $v.rows_with_value, $v.rows_empty, $(if ($null -ne $v.min) { ('; min {0} mean {1} max {2}' -f (Fmt $v.min '0.###'), (Fmt $v.mean '0.###'), (Fmt $v.max '0.###')) } else { '' }), $(if ($texts.Length -gt 0) { '; ' + $texts } else { '' }))
        }
    }
    $b = $Rec.backlog
    Write-Host ('  backlog  dropped ticks {0}; rows reported {1}, in the CSV {2}, expected from the duration {3}; frame jumps {4}; longest row gap {5} ms; the stop took {6} ms' -f (Fmt $b.dropped_ticks '0'), (Fmt $b.rows_reported '0'), $b.rows_in_csv, (Fmt $b.rows_expected_from_duration '0'), $b.frame_jumps_total, (Fmt $b.longest_row_gap_ms '0.0'), (Fmt $b.stop_ms '0'))
    $h = $Rec.sensorhost_period
    Write-Host ('  sensorhost  cycles/s before {0} recording {1} after {2} (after/recording {3}); cpu ms/s before {4} recording {5} after {6}' -f (Fmt $h.child_cycles_per_s_before '0'), (Fmt $h.child_cycles_per_s_recording '0'), (Fmt $h.child_cycles_per_s_after '0'), (Fmt $h.cycles_after_over_recording '0.###'), (Fmt $h.child_cpu_ms_per_s_before '0.0'), (Fmt $h.child_cpu_ms_per_s_recording '0.0'), (Fmt $h.child_cpu_ms_per_s_after '0.0'))
}

function Write-Targets {
    Write-Host 'Targets (printed, not judged here): private bytes < 100 MB; steady CPU mean < 0.3 % and < 10 ms/s; warm start < 1 s.' -ForegroundColor DarkGray
}

# ---- main ----

function Save-Summary([System.Collections.IDictionary]$Summary, [string]$Path) {
    $json = $Summary | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($Path, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
}

# A run's warnings go to the top of summary.json too (prefixed with the run's label), so none is found only by opening each run.
function Add-RunWarnings([System.Collections.IDictionary]$Summary, $Run) {
    foreach ($w in @($Run['warnings'])) {
        if ($null -ne $w) { [void]$Summary.warnings.Add(('{0}: {1}' -f $Run.label, $w)) }
    }
    if ($Run.Contains('integrity') -and $null -ne $Run['integrity'] -and $Run['integrity'].integrity -eq 'unknown') {
        [void]$Summary.warnings.Add(('{0}: the integrity level of the measured process is unknown ({1})' -f $Run.label, $Run['integrity'].reason))
    }
}

# After one compare pair (PC Mieru, Task Manager): the fairness warnings for it, printed and kept in summary.json (fairness).
function Add-CompareFairness([System.Collections.IDictionary]$Summary, $PcRun, $TmRun, [int]$Index) {
    $list = @(Get-CompareWarnings $TmRun['taskmgr_settings'] $PcRun['window_size'] $TmRun['window_size'])
    $entry = [ordered]@{
        pair = $Index
        pcmieru_window = $PcRun['window_size']
        taskmgr_window = $TmRun['window_size']
        taskmgr_settings = $TmRun['taskmgr_settings']
        warnings = $list
    }
    [void]$Summary.fairness.Add($entry)
    foreach ($w in $list) {
        Write-Host ('  fairness warning: ' + $w) -ForegroundColor Yellow
        [void]$Summary.warnings.Add(('compare pair {0}: {1}' -f $Index, $w))
    }
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
                cycles_per_s_mean = (Get-Aggregate @($mine | ForEach-Object { $_.windows[$w][$who].cycles_per_s_mean }))
                ghz_equivalent_mean = (Get-Aggregate @($mine | ForEach-Object { $_.windows[$w][$who].ghz_equivalent_mean }))
                private_mb_mean = (Get-Aggregate @($mine | ForEach-Object { $_.windows[$w][$who].private_mb_mean }))
                private_mb_max = (Get-Aggregate @($mine | ForEach-Object { $_.windows[$w][$who].private_mb_max }))
                working_set_mb_mean = (Get-Aggregate @($mine | ForEach-Object { $_.windows[$w][$who].working_set_mb_mean }))
                handles_mean = (Get-Aggregate @($mine | ForEach-Object { $_.windows[$w][$who].handles_mean }))
            }
        }
    }
    $Summary.aggregate[$Target] = $agg
}

# The record scenario's aggregate over its runs (the median and the spread of each figure): the GUI alone, the SensorHost alone and the total in
# the three windows, plus the missing/repeat rate per source. A figure a run did not have is left out of its aggregate, never zero.
function Add-RecordAggregate([System.Collections.IDictionary]$Summary, $RunList) {
    $mine = @($RunList | Where-Object { $_.target -eq 'pcmieru' -and $_.Contains('record') -and $null -ne $_['record'] })
    if ($mine.Count -eq 0) { return }
    $agg = [ordered]@{ runs = $mine.Count }
    foreach ($w in @('before', 'recording', 'after')) {
        foreach ($who in @('main', 'child', 'total')) {
            $stats = @($mine | ForEach-Object { $_.windows[$w][$who] } | Where-Object { $null -ne $_ })
            if ($stats.Count -eq 0) { continue }
            $agg[$w + '_' + $who] = [ordered]@{
                cpu_percent_mean = (Get-Aggregate @($stats | ForEach-Object { $_.cpu_percent_mean }))
                cpu_ms_per_s_mean = (Get-Aggregate @($stats | ForEach-Object { $_.cpu_ms_per_s_mean }))
                cycles_per_s_mean = (Get-Aggregate @($stats | ForEach-Object { $_.cycles_per_s_mean }))
                private_mb_mean = (Get-Aggregate @($stats | ForEach-Object { $_.private_mb_mean }))
                private_mb_max = (Get-Aggregate @($stats | ForEach-Object { $_.private_mb_max }))
                working_set_mb_mean = (Get-Aggregate @($stats | ForEach-Object { $_.working_set_mb_mean }))
            }
        }
    }
    $sources = [ordered]@{}
    foreach ($r in $mine) {
        $csv = $r['record'].csv
        if ($null -eq $csv -or -not $csv.read) { continue }
        foreach ($name in $csv.sources.Keys) {
            if (-not $sources.Contains($name)) { $sources[$name] = @{ fresh = @(); repeat = @(); missing = @() } }
            $sources[$name].fresh += , $csv.sources[$name].fresh_pct
            $sources[$name].repeat += , $csv.sources[$name].repeat_pct
            $sources[$name].missing += , $csv.sources[$name].missing_pct
        }
    }
    $perSource = [ordered]@{}
    foreach ($name in $sources.Keys) {
        $perSource[$name] = [ordered]@{
            fresh_pct = (Get-Aggregate @($sources[$name].fresh))
            repeat_pct = (Get-Aggregate @($sources[$name].repeat))
            missing_pct = (Get-Aggregate @($sources[$name].missing))
        }
    }
    $agg['sources'] = $perSource
    $Summary.aggregate['pcmieru_record'] = $agg
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
        if ($Mode -eq 'record') {
            if ($RecordSeconds -lt 5 -or $RecordSeconds -gt 7200) { throw '-RecordSeconds must be 5 to 7200' }
            if ($AfterSeconds -lt 10 -or $AfterSeconds -gt 3600) { throw '-AfterSeconds must be 10 to 3600' }
            if ($LeadSeconds -lt 0 -or $LeadSeconds -gt 3600) { throw '-LeadSeconds must be 0 to 3600' }
            if (@(0, 500, 1000, 2000, 5000) -notcontains $UserIntervalMs) { throw '-UserIntervalMs must be 0 (the GUI''s own 1000), 500, 1000, 2000 or 5000' }
        }
        $script:ExePath = (Resolve-Path -LiteralPath $Exe).Path
        $script:FakesOn = [bool]$Fakes

        if ([string]::IsNullOrEmpty($Output)) { $Output = Join-Path ([IO.Path]::GetTempPath()) ('pcmieru-measure-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss')) }
        $Output = [IO.Path]::GetFullPath($Output)
        $summaryPath = Join-Path $Output 'summary.json'
        if (Test-Path -LiteralPath $summaryPath) { throw "the output folder already holds a summary.json; use a new folder: $Output" }
        if ($Mode -eq 'record') {
            # The recordings go under <Output>: the GUI refuses a folder inside the user's own folders, so say it here before anything is made or started.
            if (-not (Test-PathResolvable $Output)) { throw ('-Output goes through a link that cannot be followed, so where the recordings would go is not known: ' + $Output) }
            foreach ($root in (Get-UserDataFolders)) {
                if (Test-InsideFolder $root $Output) { throw ('-Output must be outside the user''s own folders in record mode (' + $root + '): the recordings are written under it') }
            }
        }
        # Before anything is started: a folder that cannot be written is one line and nothing else (not a run that fails after the app is up).
        Assert-WritableFolder $Output

        Write-Host ('PC Mieru measure.ps1 - mode {0}{1}' -f $Mode, $(if ($Fakes) { ' - FAKES (a trial: the numbers do not describe the product)' } else { '' })) -ForegroundColor Cyan
        Write-Targets
        $environment = Get-EnvironmentInfo $script:ExePath
        $cpus = [int]$environment.logical_cpus
        Write-Host ('environment: {0} / {1} logical CPUs / {2} GB / power plan {3} / uptime {4} min' -f $environment.os_build, $cpus, (Fmt $environment.total_memory_gb '0.0'), $environment.power_plan, (Fmt $environment.uptime_minutes '0'))

        $scriptToken = Get-ScriptToken
        Write-Host ('script: elevated {0}, integrity {1}' -f $(if ($null -ne $scriptToken.is_elevated) { $scriptToken.is_elevated } else { 'unknown' }), $scriptToken.integrity)

        $summary = [ordered]@{
            tool = 'tools/measure.ps1'
            mode = $Mode
            fakes = [bool]$Fakes
            fakes_note = $(if ($Fakes) { 'the GUI ran with --fakes (fake sensors and data): a trial of the script, not a measurement of the product' } else { $null })
            environment = $environment
            script = $scriptToken
            task_manager = $(if ($Mode -eq 'compare') { Get-TaskMgrInfo } else { $null })
            warnings = (New-Object System.Collections.ArrayList)
            fairness = (New-Object System.Collections.ArrayList)
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

        if ($Mode -eq 'record') {
            $summary.parameters['record'] = [ordered]@{
                lead_seconds = $LeadSeconds
                record_seconds = $RecordSeconds
                after_seconds = $AfterSeconds
                user_interval_ms = $UserIntervalMs
                user_interval_note = $(if ($UserIntervalMs -eq 0) { 'left at the GUI''s own 1000 ms: equal to the recording period, so a return of Core''s period cannot be told apart' } else { 'set in memory with --measure-record-interval (a measured run never saves settings)' })
                guard_seconds = $script:RecordGuardSeconds
                margin_seconds = $script:RecordMarginSeconds
                run_seconds = ($LeadSeconds + $RecordSeconds + $AfterSeconds + $script:RecordMarginSeconds)
                recording_period_ms = $script:RecordingPeriodMs
                switches = '--measure <marks> --measure-record <folder> --measure-record-lead <s> --measure-record-for <s> [--measure-record-interval <ms>]'
                todo = 'DESIGN-B section 10, the update side: the states "waiting for a check" and "a check is due" are not measured here (they need src/PcMieru.Update and a test-only way to put the GUI into them)'
            }
            if ($Fakes) { [void]$summary.warnings.Add('record mode with -Fakes: the recording is in memory and writes no CSV, so the per-source figures are missing') }
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
            Add-RunWarnings $summary $summary.warmup
            Write-Host ('warm-up run: {0}' -f $summary.warmup.ended)
            [Threading.Thread]::Sleep(5000)
            $run = Invoke-MeasuredRun -Target pcmieru -Label 'long-r1-pcmieru' -RunDir $Output -DurationSeconds ($LongHours * 3600.0) -Cpus $cpus -LongMode
            [void]$summary.runs.Add($run)
            Add-RunWarnings $summary $run
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
            Add-RunWarnings $summary $run
            Write-RunReport $run $Mode
            Add-Aggregate $summary 'pcmieru' $summary.runs
            Save-Summary $summary $summaryPath
        }
        elseif ($Mode -eq 'record') {
            $summary.temperature = 'warm (after one unmeasured run, which does not record)'
            $summary.warmup = (Invoke-MeasuredRun -Target pcmieru -Label 'record-warmup' -RunDir $Output -DurationSeconds 0 -Cpus $cpus -Warmup)
            Add-RunWarnings $summary $summary.warmup
            Write-Host ('warm-up run (not measured, not recording): {0}' -f $summary.warmup.ended)
            $duration = [double]($LeadSeconds + $RecordSeconds + $AfterSeconds + $script:RecordMarginSeconds)
            for ($i = 1; $i -le $Runs; $i++) {
                [Threading.Thread]::Sleep(5000)
                $label = 'record-r{0}-pcmieru' -f $i
                # One recording folder per run, under <Output>: the only place the GUI writes the recording into.
                $folder = Join-Path $Output ($label + '-recording')
                Assert-WritableFolder $folder
                $plan = @{ Folder = $folder; Lead = $LeadSeconds; For = $RecordSeconds; Interval = $UserIntervalMs }
                Write-Host ('record run {0} of {1}: lead {2} s, record {3} s, after {4} s, user interval {5} ms ({6} s in all); recording folder {7}' -f $i, $Runs, $LeadSeconds, $RecordSeconds, $AfterSeconds, $(if ($UserIntervalMs -gt 0) { $UserIntervalMs } else { 'default' }), $duration, $folder)
                $run = Invoke-MeasuredRun -Target pcmieru -Label $label -RunDir $Output -DurationSeconds $duration -Cpus $cpus -Record $plan
                [void]$summary.runs.Add($run)
                Add-RunWarnings $summary $run
                Write-RunReport $run $Mode
                Save-Summary $summary $summaryPath
            }
            Add-RecordAggregate $summary $summary.runs
            Save-Summary $summary $summaryPath
        }
        else {
            $summary.temperature = 'warm (after one unmeasured run)'
            if ($Mode -eq 'compare') {
                $open = @([System.Diagnostics.Process]::GetProcessesByName('Taskmgr'))
                if ($open.Count -gt 0) {
                    # An elevated Task Manager cannot be closed from here, and one left open by an earlier compare would stop every later run.
                    # It is measured as it is, and the PC Mieru runs then share the machine with it: both are said, never hidden.
                    $w = 'Task Manager was already open before the runs (pid ' + (($open | ForEach-Object { $_.Id }) -join ', ') + '): its runs measure that window, not a fresh start, and it stays open during the PC Mieru runs'
                    [void]$summary.warnings.Add($w)
                    Write-Host ('  warning: ' + $w) -ForegroundColor Yellow
                }
                foreach ($p in $open) { Dispose-Quietly $p }
            }
            $summary.warmup = (Invoke-MeasuredRun -Target pcmieru -Label ($Mode + '-warmup') -RunDir $Output -DurationSeconds 0 -Cpus $cpus -Warmup)
            Add-RunWarnings $summary $summary.warmup
            Write-Host ('warm-up run (not measured): {0}' -f $summary.warmup.ended)
            $duration = $script:FirstWindowSeconds + $Seconds
            for ($i = 1; $i -le $Runs; $i++) {
                $targets = @('pcmieru')
                if ($Mode -eq 'compare') { $targets = @('pcmieru', 'taskmgr') }
                $pair = @{}
                foreach ($target in $targets) {
                    [Threading.Thread]::Sleep(5000)
                    $run = Invoke-MeasuredRun -Target $target -Label ('{0}-r{1}-{2}' -f $Mode, $i, $target) -RunDir $Output -DurationSeconds $duration -Cpus $cpus
                    [void]$summary.runs.Add($run)
                    $pair[$target] = $run
                    Add-RunWarnings $summary $run
                    Write-RunReport $run $Mode
                    Save-Summary $summary $summaryPath
                }
                if ($Mode -eq 'compare') {
                    Add-CompareFairness $summary $pair['pcmieru'] $pair['taskmgr'] $i
                    Save-Summary $summary $summaryPath
                }
            }
            Add-Aggregate $summary 'pcmieru' $summary.runs
            if ($Mode -eq 'compare') { Add-Aggregate $summary 'taskmgr' $summary.runs }
            Save-Summary $summary $summaryPath
        }

        Write-Targets
        if ($summary.warnings.Count -gt 0) {
            Write-Host ('{0} warning(s):' -f $summary.warnings.Count) -ForegroundColor Yellow
            foreach ($w in $summary.warnings) { Write-Host ('  ' + $w) -ForegroundColor Yellow }
        }
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
