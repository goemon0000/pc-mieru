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
             comes up elevated (integrity high), the script does not try to close it: it prints one line asking you to close it,
             waits (up to 3 minutes) and records closed_by = "manual". A counter that cannot be read is recorded as missing with
             the reason; the script never elevates, never shows or answers a UAC prompt.
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

# The size of the window of a process (GetWindowRect: width, height). width/height $null with a note when it cannot be measured.
function Get-WindowSize($Proc) {
    $r = [ordered]@{ width = $null; height = $null; note = $null }
    try {
        Initialize-Win32
        $Proc.Refresh()
        $handle = $Proc.MainWindowHandle
        if ($handle -eq [IntPtr]::Zero) { $r.note = 'no main window handle'; return $r }
        $size = [PcmMeasureWin]::WindowSize($handle)
        if ($null -eq $size) { $r.note = 'GetWindowRect failed'; return $r }
        $r.width = [int]$size[0]; $r.height = [int]$size[1]
    }
    catch { $r.note = Get-ShortError $_ }
    return $r
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

# ---- running the app ----

# What the GUI wrote to stderr (it writes one line there and exits 1 when it cannot start a measurement run: a marker file that cannot be made).
# Stderr is read in the background from the start, so a full pipe never blocks the GUI.
$script:AppErrTask = $null

function New-AppStartInfo([string]$MarkFile) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:ExePath
    $psi.WorkingDirectory = Split-Path -Parent $script:ExePath
    $psi.UseShellExecute = $false
    $psi.RedirectStandardError = $true
    $arguments = '--measure "' + $MarkFile + '"'
    if ($script:FakesOn) { $arguments += ' --fakes' }
    $psi.Arguments = $arguments
    return $psi
}

function Start-App([string]$MarkFile) {
    $proc = [System.Diagnostics.Process]::Start((New-AppStartInfo $MarkFile))
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

function Start-TaskMgr {
    $before = @{}
    foreach ($p in [System.Diagnostics.Process]::GetProcessesByName('Taskmgr')) { $before[$p.Id] = $true; $p.Dispose() }
    $started = $null
    try { $started = [System.Diagnostics.Process]::Start((New-TaskMgrStartInfo)) }
    catch { throw ('Task Manager did not start (' + (Get-ShortError $_) + ')') }
    Dispose-Quietly $started
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
        $writer.WriteLine('t_s,role,name,pid,start_utc,dt_s,cpu_s,cpu_delta_s,cycles,cycles_delta,cycles_per_s,ghz_equivalent,private_bytes,working_set_bytes,handles')
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
    $nav = @{ Nav = $null; ChecksIndex = $null }
    $pageTries = 0; $pageOk = 0; $pageSkips = 0; $pageRefused = 0
    $churnStarted = 0; $churnFailed = 0
    $markDoneT = $null
    $windowSize = $null
    $tmSettings = $null
    $tmInfo = $null
    if (-not $isApp) { $tmSettings = Read-TaskMgrSettings; $tmInfo = Get-TaskMgrInfo }

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
                Write-Host '  Task Manager is still open and elevated: please close it yourself (waiting up to 3 minutes).' -ForegroundColor Yellow
                $wait = [Diagnostics.Stopwatch]::StartNew()
                while ((Test-SeriesAlive $main) -and $wait.Elapsed.TotalSeconds -lt 180) { [Threading.Thread]::Sleep(500) }
                $close.waited_seconds = [math]::Round($wait.Elapsed.TotalSeconds, 1)
                if (Test-SeriesAlive $main) { Close-AllSeries $main $children $finished; throw 'Task Manager is still open: close it and run again' }
                $close.note = $close.note + '; it was closed by hand'
                $close['closed_by'] = 'manual'
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
        foreach ($who in @('main', 'total')) {
            $x = $Run.windows[$w][$who]
            $label = $(if ($who -eq 'main') { 'GUI alone' } else { 'total    ' })
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
        else {
            $summary.temperature = 'warm (after one unmeasured run)'
            if ($Mode -eq 'compare') {
                $open = @([System.Diagnostics.Process]::GetProcessesByName('Taskmgr'))
                if ($open.Count -gt 0) { throw 'Task Manager is open: close it and run again (compare starts and ends its own)' }
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
