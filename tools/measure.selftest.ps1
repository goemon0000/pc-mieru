<#
.SYNOPSIS
  Self-test of tools/measure.ps1 (its helpers, not a measurement). Prints PASS / FAIL / SKIP per check and exits 1 when one failed.

.DESCRIPTION
  Dot-sources measure.ps1 (which then runs nothing) and checks, without touching any app:
    * the page cycle of the long run never selects the checks page, from every page, for several list sizes;
    * a parent-process or creation-time lookup that fails is tried again (10 times) before a process is given up, and a warning names the pid;
    * the reader (limited-rights handle: creation time, CPU time, cycles, memory, handles, integrity) on this shell and on a ping child;
    * Task Manager is started with __COMPAT_LAYER=RunAsInvoker on the child's start info only (this script's environment is unchanged);
    * Task Manager's settings are read and never written; the compare fairness warnings;
    * the output-folder check, the CSV columns, the cycle statistics;
    * the record scenario (-Mode record), on synthetic text only: the recording CSV read from outside (fresh / repeat / missing, skipped
      frames, row gaps, value columns), the GUI's event lines, the period summary, the windows around the start and the stop, the
      --measure-record switches on the app's command line, and the user-folder test.
  With -Exe (a published PCMieru.exe) it also starts the GUI twice: with a marker path that cannot be made (exit 1, one stderr line, no
  window) and normally (reads the navigation list, the checks page index and one page step). That part starts PCMieru.exe: do not run it while
  a game is on.
  ASCII only on purpose (Windows PowerShell 5.1 reads a file without a BOM as ANSI).
.PARAMETER Exe
  A published PCMieru.exe (optional; the GUI part is skipped without it).
.PARAMETER ScriptUnderTest
  The measure.ps1 to test (default: the one next to this file). A mutated copy is tested this way.
.PARAMETER WorkDir
  A folder for the test's own files (made and removed). Default: a new folder under the temp folder.
#>
[CmdletBinding()]
param(
    [string]$Exe = '',
    [string]$ScriptUnderTest = '',
    [string]$WorkDir = ''
)

if ([string]::IsNullOrEmpty($ScriptUnderTest)) { $ScriptUnderTest = Join-Path $PSScriptRoot 'measure.ps1' }
# measure.ps1 has its own parameters (-Exe, -Mode, -Output ...): dot-sourcing it binds their defaults in this scope, so what this script was
# given is kept under other names first.
$GuiExe = $Exe
. $ScriptUnderTest
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Passed = 0
$script:Failed = 0
$script:Skipped = 0

function Assert-That($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Check([string]$Name, [scriptblock]$Test) {
    try {
        $r = & $Test
        if ($r -is [string] -and $r.StartsWith('SKIP')) { $script:Skipped++; Write-Host ('SKIP  ' + $Name + ' (' + $r + ')') -ForegroundColor Yellow }
        else { $script:Passed++; Write-Host ('PASS  ' + $Name) -ForegroundColor Green }
    }
    catch {
        $script:Failed++
        Write-Host ('FAIL  ' + $Name + ': ' + $_.Exception.Message) -ForegroundColor Red
    }
}

function Info([string]$Text) { Write-Host ('      ' + $Text) -ForegroundColor DarkGray }

if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path ([IO.Path]::GetTempPath()) ('pcmieru-measure-selftest-' + [Guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

function New-WorkSubDir([string]$Name) {
    $d = Join-Path $WorkDir $Name
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    return $d
}

# A child that runs for a while and does nothing: its parent is this shell.
function Start-Ping([int]$Count = 20) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $env:SystemRoot 'System32\PING.EXE'
    $psi.Arguments = '-n ' + $Count + ' 127.0.0.1'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    return [System.Diagnostics.Process]::Start($psi)
}

function Stop-Quietly($Proc) {
    try { if ($null -ne $Proc) { if (-not $Proc.HasExited) { $Proc.Kill() }; $Proc.Dispose() } } catch { }
}

Write-Host ('selftest of ' + $ScriptUnderTest) -ForegroundColor Cyan

# ---------------------------------------------------------------- the page cycle

Check 'nav: the plan from every page never selects the checks page and ends on the next page of the cycle' {
    foreach ($count in 3..12) {
        for ($checks = 0; $checks -lt $count; $checks++) {
            for ($from = 0; $from -lt $count; $from++) {
                if ($from -eq $checks) { continue }   # the selection is never on the checks page: the plan never goes there
                $plan = Get-NavPlan $count $from $checks
                $target = Get-NavTarget $count $from $checks
                if ($target -eq $from) { continue }
                Assert-That $plan.ok ('no plan: count ' + $count + ' from ' + $from + ' checks ' + $checks + ': ' + $plan.note)
                Assert-That ($plan.target -eq $target) 'wrong target'
                $pos = $from
                foreach ($step in $plan.steps) {
                    $next = Get-NavKeyResult $step.key $pos $count
                    Assert-That ($next -ne $pos) ('a key that does not move: ' + $step.key + ' at ' + $pos)
                    Assert-That ($next -ne $checks) ('the plan selects the checks page: count ' + $count + ' from ' + $from + ' checks ' + $checks)
                    Assert-That ($next -eq [int]$step.expect) 'expect does not follow the key'
                    $pos = $next
                }
                Assert-That ($pos -eq $target) 'the route does not end on the target'
            }
        }
    }
}

Check 'nav: ten pages, checks at 7: hardware goes END, UP (visits settings, never checks); settings goes HOME; processes goes DOWN' {
    $hw = Get-NavPlan 10 6 7
    Assert-That $hw.ok 'hardware: no plan'
    Assert-That ((@($hw.steps | ForEach-Object { $_.key }) -join ',') -eq 'END,UP') ('hardware: ' + (@($hw.steps | ForEach-Object { $_.key }) -join ','))
    Assert-That ((@($hw.visits) -join ',') -eq '9,8') 'hardware: visits'
    $st = Get-NavPlan 10 9 7
    Assert-That ((@($st.steps | ForEach-Object { $_.key }) -join ',') -eq 'HOME') 'settings: not HOME'
    $pr = Get-NavPlan 10 1 7
    Assert-That ((@($pr.steps | ForEach-Object { $_.key }) -join ',') -eq 'DOWN') 'processes: not DOWN'
}

Check 'nav: a checks page that was not found means no page is switched' {
    $plan = Get-NavPlan 10 1 $null
    Assert-That (-not $plan.ok) 'a plan without a checks index'
    Assert-That (@($plan.steps).Count -eq 0) 'steps without a checks index'
}

Check 'nav: the checks title is matched in ja and en, trimmed, and nothing else' {
    Assert-That (Test-ChecksTitle 'Checks') 'en'
    Assert-That (Test-ChecksTitle ' checks ') 'en trimmed'
    Assert-That (Test-ChecksTitle ([string][char]0x70B9 + [string][char]0x691C)) 'ja'
    Assert-That (-not (Test-ChecksTitle 'Check')) 'Check'
    Assert-That (-not (Test-ChecksTitle $null)) 'null'
    Assert-That (-not (Test-ChecksTitle 'Settings')) 'Settings'
}

# ---------------------------------------------------------------- the lookups that are retried

Check 'retry: a parent lookup that fails three times and then answers still finds the child (never marked "other" for good)' {
    $ping = Start-Ping 40
    try {
        $script:PingPid = [int]$ping.Id
        $script:ProviderCalls = 0
        $script:ParentIdProvider = { param($p) $script:ProviderCalls++; if ($script:ProviderCalls -le 3) { return $null }; return $PID }
        $script:ProcessLister = { param($n) return [System.Diagnostics.Process]::GetProcessById($script:PingPid) }
        $main = @{ ProcId = [int]$PID; StartUtc = $null }
        $seen = @{}
        $warnings = New-Object System.Collections.ArrayList
        $found = @()
        for ($round = 1; $round -le 3; $round++) {
            $found = @(Find-NewChildren $main $seen 1.0 ([DateTime]::UtcNow) $warnings)
            Assert-That ($found.Count -eq 0) ('round ' + $round + ': a child was claimed before the lookup answered')
        }
        $found = @(Find-NewChildren $main $seen 4.0 ([DateTime]::UtcNow) $warnings)
        try {
            Assert-That ($found.Count -eq 1) 'round 4: the child was not found after the lookup answered'
            Assert-That ($found[0].Role -eq 'child') 'role'
            Assert-That ($found[0].ProcId -eq $script:PingPid) 'pid'
        }
        finally { foreach ($s in $found) { Close-Series $s } }
        Assert-That ($warnings.Count -eq 0) ('warnings: ' + ($warnings -join '; '))
        $again = @(Find-NewChildren $main $seen 5.0 ([DateTime]::UtcNow) $warnings)
        Assert-That ($again.Count -eq 0) 'a child that was taken is taken again'
        Assert-That ($script:ProviderCalls -eq 4) ('the lookup ran ' + $script:ProviderCalls + ' times, not 4')
    }
    finally { $script:ParentIdProvider = $null; $script:ProcessLister = $null; Stop-Quietly $ping }
}

Check 'retry: a parent lookup that never answers is given up after 10 tries, with one warning naming the pid, and not asked again' {
    $ping = Start-Ping 40
    try {
        $script:PingPid = [int]$ping.Id
        $script:ProviderCalls = 0
        $script:ParentIdProvider = { param($p) $script:ProviderCalls++; return $null }
        $script:ProcessLister = { param($n) return [System.Diagnostics.Process]::GetProcessById($script:PingPid) }
        $main = @{ ProcId = [int]$PID; StartUtc = $null }
        $seen = @{}
        $warnings = New-Object System.Collections.ArrayList
        for ($round = 1; $round -le 14; $round++) {
            $found = @(Find-NewChildren $main $seen ([double]$round) ([DateTime]::UtcNow) $warnings)
            Assert-That ($found.Count -eq 0) 'a process was claimed on a lookup that never answered'
        }
        Assert-That ($script:ProviderCalls -eq $script:LookupMaxTries) ('the lookup ran ' + $script:ProviderCalls + ' times, not ' + $script:LookupMaxTries)
        Assert-That ($warnings.Count -eq 1) ('warnings: ' + $warnings.Count)
        Assert-That ($warnings[0] -match ('pid ' + $script:PingPid + '\b')) ('the warning does not name the pid: ' + $warnings[0])
    }
    finally { $script:ParentIdProvider = $null; $script:ProcessLister = $null; Stop-Quietly $ping }
}

Check 'retry: a SensorHost whose creation time cannot be read is cached as failed after 10 tries, with a warning naming the pid' {
    try {
        $fake = New-Object PSObject -Property @{ Id = 999999 }
        Add-Member -InputObject $fake -MemberType ScriptMethod -Name Dispose -Value { }
        $script:FakeProc = $fake
        $script:ProviderCalls = 0
        $script:ParentIdProvider = { param($p) $script:ProviderCalls++; return $PID }
        $script:ProcessLister = { param($n) return $script:FakeProc }
        $main = @{ ProcId = [int]$PID; StartUtc = $null }
        $seen = @{}
        $warnings = New-Object System.Collections.ArrayList
        for ($round = 1; $round -le 14; $round++) {
            $found = @(Find-NewChildren $main $seen ([double]$round) ([DateTime]::UtcNow) $warnings)
            Assert-That ($found.Count -eq 0) 'a process that cannot be opened was claimed'
            if ($round -lt $script:LookupMaxTries) { Assert-That ($warnings.Count -eq 0) ('a warning came at round ' + $round + ', before ' + $script:LookupMaxTries + ' tries') }
        }
        Assert-That ($warnings.Count -eq 1) ('warnings: ' + $warnings.Count)
        Assert-That ($warnings[0] -match 'pid 999999') ('the warning does not name the pid: ' + $warnings[0])
        Assert-That ($seen['pid:999999'] -eq 'failed') 'not cached as failed'
        Assert-That ($script:ProviderCalls -eq 0) 'the parent was asked for a process that could not be opened'
    }
    finally { $script:ParentIdProvider = $null; $script:ProcessLister = $null }
}

Check 'retry: the real parent lookup (no seam) gives this shell as the parent of a child' {
    $ping = Start-Ping 20
    try {
        $parent = $null
        for ($i = 0; $i -lt 5 -and $null -eq $parent; $i++) { $parent = Get-ParentId ([int]$ping.Id) }
        Assert-That ($null -ne $parent) 'the lookup did not answer in 5 tries'
        Assert-That ([int]$parent -eq [int]$PID) ('parent ' + $parent + ' is not this shell ' + $PID)
    }
    finally { Stop-Quietly $ping }
}

# ---------------------------------------------------------------- the reader

Check 'reader: this shell and a ping child through one limited-rights handle (creation time, CPU time, cycles, memory, handles, integrity)' {
    $ping = Start-Ping 20
    try {
        [Threading.Thread]::Sleep(700)
        foreach ($case in @(@('shell', [int]$PID), @('ping', [int]$ping.Id))) {
            $open = Open-ProcHandle $case[1]
            Assert-That ($open.Handle -ne [IntPtr]::Zero) ('OpenProcess failed for the ' + $case[0] + ': ' + $open.Error)
            try {
                $c = [PcmProc]::Read($open.Handle)
                $tok = Get-ProcessToken $open.Handle
                $p = [System.Diagnostics.Process]::GetProcessById($case[1])
                $start = $p.StartTime.ToUniversalTime()
                $cpu = $p.TotalProcessorTime.TotalSeconds
                $p.Dispose()
                Assert-That $c.HasCreation 'no creation time'
                Assert-That ([math]::Abs(($c.CreationUtc - $start).TotalSeconds) -lt 1.0) 'the creation time is not the one Process.StartTime gives'
                Assert-That ([math]::Abs($c.CpuSeconds - $cpu) -lt 1.0) ('CPU time ' + $c.CpuSeconds + ' against Process.TotalProcessorTime ' + $cpu)
                Assert-That ($c.CyclesError -eq 0 -and $c.Cycles -gt 0) 'no cycle time'
                Assert-That ($c.MemoryError -eq 0 -and $c.PrivateBytes -gt 0 -and $c.WorkingSet -gt 0) 'no memory counters'
                Assert-That ($c.HandlesError -eq 0 -and $c.Handles -gt 0) 'no handle count'
                Assert-That ($tok.integrity -ne 'unknown') ('integrity unknown: ' + $tok.reason)
                Info ('{0} pid {1}: created {2} UTC, cpu {3} s, cycles {4}, private {5} MB, handles {6}, integrity {7}, elevated {8}' -f $case[0], $case[1], $c.CreationUtc.ToString('HH:mm:ss.fff'), (Fmt $c.CpuSeconds '0.000'), (Fmt $c.Cycles '0'), (Fmt ($c.PrivateBytes / 1048576.0) '0.0'), $c.Handles, $tok.integrity, $tok.elevated)
            }
            finally { Close-ProcHandle $open.Handle }
        }
    }
    finally { Stop-Quietly $ping }
}

Check 'reader: a series holds its handle, sees a delta on the second read, sees the process end, and says so after the handle is closed' {
    $ping = Start-Ping 40
    try {
        [Threading.Thread]::Sleep(500)
        $series = New-Series $ping 'child' 0.0 ([DateTime]::UtcNow) $false
        try {
            Assert-That ($null -ne $series.Handle) 'no handle'
            Assert-That ($series.Token.integrity -ne 'unknown') 'integrity unknown'
            Assert-That ($null -ne $series.StartUtc) 'no creation time'
            $row1 = Read-Series $series 1.0
            [Threading.Thread]::Sleep(1100)
            $row2 = Read-Series $series 2.1
            Assert-That ($null -ne $row1.CyclesDelta) 'no cycle delta on the first read (it runs from the creation)'
            Assert-That ($null -ne $row2.CyclesDelta -and $null -ne $row2.CycDt) 'no cycle delta on the second read'
            Assert-That ($null -ne $row2.CpuDelta -and $null -ne $row2.Private -and $null -ne $row2.Handles) 'a counter is missing'
            Assert-That (Test-SeriesAlive $series) 'alive reported as ended'
            $ping.Kill()
            [void]$ping.WaitForExit(5000)
            Assert-That (-not (Test-SeriesAlive $series)) 'an ended process reported as alive (through the handle)'
        }
        finally { Close-Series $series }
        Assert-That ($null -eq $series.Handle) 'the handle was not closed'
        $row3 = Read-Series $series 3.0
        Assert-That ($null -eq $row3.Cycles -and $series.Missing.ContainsKey('cycles')) 'a closed series did not say its counters are missing'
    }
    finally { Stop-Quietly $ping }
}

# Soft: which higher-integrity processes of this machine the non-elevated reader can open at all. Only printed; Windows decides.
Check 'reader: higher-integrity system processes (soft: printed, what Windows refuses is recorded as unknown with its reason)' {
    $seenNames = @{}
    $opened = 0
    foreach ($name in @('svchost', 'dwm', 'winlogon', 'csrss', 'wininit', 'services', 'lsass', 'spoolsv', 'fontdrvhost', 'RuntimeBroker')) {
        $procs = @([System.Diagnostics.Process]::GetProcessesByName($name))
        if ($procs.Count -eq 0) { continue }
        $ids = @($procs | ForEach-Object { [int]$_.Id } | Select-Object -First 6)
        foreach ($pr in $procs) { $pr.Dispose() }
        foreach ($id in $ids) {
            $open = Open-ProcHandle $id
            if ($open.Handle -eq [IntPtr]::Zero) {
                if (-not $seenNames.ContainsKey($name)) { $seenNames[$name] = 'refused: ' + (Get-Win32Reason 'OpenProcess' $open.Error) }
                continue
            }
            try {
                $c = [PcmProc]::Read($open.Handle)
                $tok = Get-ProcessToken $open.Handle
                $opened++
                $viaProcess = 'allowed'
                try { $p = [System.Diagnostics.Process]::GetProcessById($id); [void]$p.StartTime; $p.Dispose() } catch { $viaProcess = 'refused' }
                if (-not $seenNames.ContainsKey($name) -or $seenNames[$name] -like 'refused*') {
                    $seenNames[$name] = ('read: pid {0}, creation {1} UTC, cycles {2}, integrity {3} ({4}); Process.StartTime {5}' -f $id, $(if ($c.HasCreation) { $c.CreationUtc.ToString('HH:mm:ss') } else { 'unreadable' }), (Fmt $c.Cycles '0'), $tok.integrity, $tok.reason, $viaProcess)
                }
            }
            finally { Close-ProcHandle $open.Handle }
        }
    }
    foreach ($k in ($seenNames.Keys | Sort-Object)) { Info ($k + ' -> ' + $seenNames[$k]) }
    Info ('opened ' + $opened + ' system processes without elevation')
}

Check 'reader: the script token (is_elevated, integrity) is read' {
    $t = Get-ScriptToken
    Assert-That ($t.integrity -ne 'unknown') ('integrity unknown: ' + $t.reason)
    Assert-That ($null -ne $t.is_elevated) 'is_elevated unknown'
    Info ('script: is_elevated ' + $t.is_elevated + ', integrity ' + $t.integrity)
}

# ---------------------------------------------------------------- Task Manager: RunAsInvoker on the child only; settings read only

Check 'taskmgr: the child is started with __COMPAT_LAYER=RunAsInvoker on its start info only; the script environment is unchanged' {
    $beforeEnv = [Environment]::GetEnvironmentVariable('__COMPAT_LAYER', 'Process')
    $beforeAll = (Get-ChildItem Env: | Sort-Object Name | ForEach-Object { $_.Name + '=' + $_.Value }) -join "`n"
    $psi = New-TaskMgrStartInfo
    Assert-That ($psi.EnvironmentVariables['__COMPAT_LAYER'] -eq 'RunAsInvoker') 'the child does not get RunAsInvoker'
    Assert-That (-not $psi.UseShellExecute) 'UseShellExecute must be false (the environment of a ShellExecute start is not used)'
    Assert-That ($psi.FileName -like '*\System32\taskmgr.exe') ('the file: ' + $psi.FileName)
    $afterEnv = [Environment]::GetEnvironmentVariable('__COMPAT_LAYER', 'Process')
    $afterAll = (Get-ChildItem Env: | Sort-Object Name | ForEach-Object { $_.Name + '=' + $_.Value }) -join "`n"
    Assert-That ($beforeEnv -eq $afterEnv) 'the script''s own __COMPAT_LAYER changed'
    Assert-That ($beforeAll -eq $afterAll) 'the script''s environment changed'
    Assert-That ($afterEnv -ne 'RunAsInvoker') 'the script''s own environment has RunAsInvoker'
    $savedExe = $script:ExePath
    try {
        if ([string]::IsNullOrEmpty($script:ExePath)) { $script:ExePath = Join-Path $WorkDir 'nowhere\PCMieru.exe' }
        $app = New-AppStartInfo (Join-Path $WorkDir 'marks.jsonl')
    }
    finally { $script:ExePath = $savedExe }
    $appCompat = $app.EnvironmentVariables['__COMPAT_LAYER']
    Assert-That ($appCompat -eq $beforeEnv) 'the GUI start info carries a changed __COMPAT_LAYER'
    $text = [IO.File]::ReadAllText($ScriptUnderTest)
    Assert-That ($text -notmatch '\$env:__COMPAT_LAYER') 'the script sets $env:__COMPAT_LAYER'
    Assert-That ($text -notmatch 'SetEnvironmentVariable') 'the script calls SetEnvironmentVariable'
    Assert-That ($text -match 'New-TaskMgrStartInfo\)\)') 'Start-TaskMgr does not start the child from New-TaskMgrStartInfo'
}

Check 'taskmgr: the file version is recorded' {
    $info = Get-TaskMgrInfo
    Assert-That (-not [string]::IsNullOrEmpty($info.file_version)) ('no file version: ' + $info.note)
    Assert-That ($info.launch -eq 'as_invoker') 'launch'
    Info ('taskmgr.exe ' + $info.file_version)
}

Check 'taskmgr: the settings are read (start page, small view), the update speed is unknown, and the file is not changed' {
    $dir = New-WorkSubDir 'tm-settings'
    $file = Join-Path $dir 'settings.json'
    [IO.File]::WriteAllText($file, '{"CurrentView":0,"SmallView":false,"Other":1}')
    $hashBefore = (Get-FileHash -LiteralPath $file).Hash
    $r = Read-TaskMgrSettings $file
    Assert-That ($r.read -and $r.start_page -eq 0 -and $r.start_page_name -eq 'Processes') 'start page'
    Assert-That ($r.small_view -eq $false) 'small view'
    Assert-That ($null -eq $r.update_speed) 'the update speed must stay unknown'
    [IO.File]::WriteAllText($file, '{"CurrentView":3}')
    $r2 = Read-TaskMgrSettings $file
    Assert-That ($r2.start_page -eq 3) 'start page 3'
    [IO.File]::WriteAllText($file, 'not json')
    $r3 = Read-TaskMgrSettings $file
    Assert-That (-not $r3.read -and $null -ne $r3.note) 'bad json'
    $r4 = Read-TaskMgrSettings (Join-Path $dir 'missing.json')
    Assert-That (-not $r4.read -and $null -ne $r4.note) 'missing file'
    [IO.File]::WriteAllText($file, '{"CurrentView":0,"SmallView":false,"Other":1}')
    Assert-That ((Get-FileHash -LiteralPath $file).Hash -eq $hashBefore) 'the settings file changed'
    $text = [IO.File]::ReadAllText($ScriptUnderTest)
    Assert-That ($text -notmatch 'settings\.json[^\r\n]*(Set-Content|WriteAllText|Out-File)') 'the script writes a settings file'
    $real = Read-TaskMgrSettings
    Info ('this machine: read ' + $real.read + ', start page ' + $real.start_page_name + ', small view ' + $real.small_view + ', update speed ' + $real.update_speed)
}

Check 'compare: the fairness warnings (start page, update speed, window sizes)' {
    $ok = [ordered]@{ read = $true; start_page = 0; start_page_name = 'Processes'; small_view = $false; update_speed = $null; note = $null }
    $same = [ordered]@{ width = 1000; height = 700; note = $null }
    $w = @(Get-CompareWarnings $ok $same $same)
    Assert-That ($w.Count -eq 1 -and $w[0] -match 'update speed') ('only the standing update-speed warning was expected: ' + ($w -join ' | '))
    $page = [ordered]@{ read = $true; start_page = 1; start_page_name = 'page 1'; small_view = $false; update_speed = $null; note = $null }
    $w = @(Get-CompareWarnings $page $same $same)
    Assert-That (@($w | Where-Object { $_ -match 'does not start on Processes' }).Count -eq 1) 'a start page other than Processes was not warned'
    $big = [ordered]@{ width = 1200; height = 700; note = $null }
    $w = @(Get-CompareWarnings $ok $same $big)
    Assert-That (@($w | Where-Object { $_ -match 'window sizes differ' }).Count -eq 1) 'a 20 percent wider window was not warned'
    $near = [ordered]@{ width = 1050; height = 720; note = $null }
    $w = @(Get-CompareWarnings $ok $same $near)
    Assert-That (@($w | Where-Object { $_ -match 'window sizes differ' }).Count -eq 0) 'a 5 percent difference was warned'
    $none = [ordered]@{ width = $null; height = $null; note = 'no main window handle' }
    $w = @(Get-CompareWarnings $ok $same $none)
    Assert-That (@($w | Where-Object { $_ -match 'could not be read' }).Count -eq 1) 'an unreadable window size was not warned'
    $bad = [ordered]@{ read = $false; start_page = $null; start_page_name = $null; small_view = $null; update_speed = $null; note = 'x' }
    $w = @(Get-CompareWarnings $bad $same $same)
    Assert-That (@($w | Where-Object { $_ -match 'settings could not be read' }).Count -eq 1) 'unreadable settings were not warned'
}

# ---------------------------------------------------------------- folder, CSV, statistics

Check 'folder: a folder that cannot be written is one line and nothing else; a writable one is left without a probe file' {
    $dir = New-WorkSubDir 'writable'
    Assert-WritableFolder $dir
    Assert-That (@(Get-ChildItem -LiteralPath $dir -Force).Count -eq 0) 'the probe file was left behind'
    $file = Join-Path $WorkDir 'a-file'
    [IO.File]::WriteAllText($file, 'x')
    $msg = $null
    try { Assert-WritableFolder (Join-Path $file 'sub') } catch { $msg = $_.Exception.Message }
    Assert-That ($null -ne $msg -and $msg -match '^the output folder cannot be written') ('a folder under a file was accepted: ' + $msg)
    Assert-That ($msg -notmatch '[\r\n]') 'the message has more than one line'
    $blocked = New-WorkSubDir 'blocked'
    New-Item -ItemType Directory -Force -Path (Join-Path $blocked 'probe') | Out-Null
    $msg = $null
    try { Assert-WritableFolder $blocked 'probe' } catch { $msg = $_.Exception.Message }
    Assert-That ($null -ne $msg -and $msg -match '^the output folder cannot be written') ('a probe that cannot be written was accepted: ' + $msg)
}

Check 'csv: a row has as many fields as the header has columns, and the cycle columns are there' {
    $text = [IO.File]::ReadAllText($ScriptUnderTest)
    $m = [regex]::Match($text, "WriteLine\('(t_s,[a-z_,]+)'\)")
    Assert-That $m.Success 'the CSV header was not found'
    $header = $m.Groups[1].Value.Split(',')
    foreach ($col in @('cycles', 'cycles_delta', 'cycles_per_s', 'ghz_equivalent')) { Assert-That ($header -contains $col) ('the header has no ' + $col) }
    $row = @{ T = 2.0; Role = 'main'; Name = 'x'; ProcId = 4; StartUtc = [DateTime]::UtcNow; Dt = 1.0; Cpu = 1.5; CpuDelta = 0.01; Cycles = 3.0e9; CyclesDelta = 2.0e7; CycDt = 1.0; Private = 100L; Ws = 200L; Handles = 5 }
    $sw = New-Object System.IO.StringWriter
    Write-CsvRow $sw $row
    $fields = $sw.ToString().TrimEnd().Split(',')
    Assert-That ($fields.Count -eq $header.Count) ('row has ' + $fields.Count + ' fields, header ' + $header.Count)
    Assert-That ($fields[$header.IndexOf('cycles_per_s')] -eq '20000000') 'cycles_per_s'
    Assert-That ($fields[$header.IndexOf('ghz_equivalent')] -eq '0.0200') 'ghz_equivalent'
    $empty = @{ T = 2.0; Role = 'total'; Name = 'total'; ProcId = $null; StartUtc = $null; Dt = $null; Cpu = $null; CpuDelta = $null; Cycles = $null; CyclesDelta = $null; CycDt = $null; Private = $null; Ws = $null; Handles = $null }
    $sw = New-Object System.IO.StringWriter
    Write-CsvRow $sw $empty
    Assert-That ($sw.ToString().TrimEnd().Split(',').Count -eq $header.Count) 'an empty row has the wrong field count'
}

Check 'stats: cycles per second, its GHz-equivalent, and a missing cycle figure is missing, not 0' {
    $rows = @()
    foreach ($t in 1..4) { $rows += , @{ T = [double]$t; CpuDelta = 0.01; Dt = 1.0; CyclesDelta = 2.0e8; CycDt = 1.0; Private = 1048576L; Ws = 1048576L; Handles = 10 } }
    $s = Get-WindowStats $rows 0 4 8
    Assert-That ($s.cycles_per_s_mean -eq 2.0e8) ('mean ' + $s.cycles_per_s_mean)
    Assert-That ($s.ghz_equivalent_mean -eq 0.2) ('ghz ' + $s.ghz_equivalent_mean)
    Assert-That ($s.cycles_intervals -eq 4) 'intervals'
    $none = @()
    foreach ($t in 1..3) { $none += , @{ T = [double]$t; CpuDelta = 0.01; Dt = 1.0; CyclesDelta = $null; CycDt = $null; Private = 1L; Ws = 1L; Handles = 1 } }
    $s2 = Get-WindowStats $none 0 3 8
    Assert-That ($null -eq $s2.cycles_per_s_mean -and $null -eq $s2.ghz_equivalent_mean) 'a missing cycle figure became a number'
}

# ---------------------------------------------------------------- the record scenario (-Mode record): helpers on synthetic text, no app

# A recording CSV as the app writes it (header, then rows): one frame that was skipped on core (c-3), one repeat and one missing row on gpu1
# (the reason has a comma, so it is quoted), one row 1600 ms after the one before it, and one line that is not a row.
function New-RecordCsvText {
    $lines = @(
        'offsetUs,utc,core.frame,core.acquiredOffsetUs,core.periodMs,core.state,core.reason,gpu1.frame,gpu1.acquiredOffsetUs,gpu1.periodMs,gpu1.state,gpu1.reason,core.cpu_usage_pct,core.cpu_package_w,core.ac_connected,gpu1.temp_c',
        '1000000,2026-11-22T00:00:01Z,c-1,900000,1000,fresh,,g1-1,950000,1000,fresh,,10.5,,1,60',
        '2000000,2026-11-22T00:00:02Z,c-2,1900000,1000,fresh,,g1-1,950000,1000,repeat,,11.5,,1,61',
        '3000000,2026-11-22T00:00:03Z,c-4,2900000,1000,fresh,,g1-2,2950000,1000,fresh,,12.5,,1,62',
        'not a row',
        '4600000,2026-11-22T00:00:04Z,c-5,4500000,1000,fresh,,,,1000,missing,"sensor, gone",13.5,,1,'
    )
    return ($lines -join "`n") + "`n"
}

function New-RecordZero { return [DateTime]::SpecifyKind((New-Object DateTime 2026, 11, 22, 0, 0, 0), [DateTimeKind]::Utc) }

function ConvertTo-EventLine([hashtable]$Fields, [DateTime]$Zero, [double]$AtSeconds) {
    $o = [ordered]@{ event = $Fields.event; utc = $Zero.AddSeconds($AtSeconds).ToString('o') }
    foreach ($k in $Fields.Keys) { if ($k -ne 'event') { $o[$k] = $Fields[$k] } }
    return ($o | ConvertTo-Json -Compress)
}

Check 'record: Split-CsvLine reads a quoted cell with a comma, a doubled quote, an empty cell and a trailing empty cell' {
    $f = Split-CsvLine 'a,"b,c","d""e",,f,'
    Assert-That ($f.Count -eq 6) ('fields: ' + $f.Count)
    Assert-That ($f[0] -eq 'a' -and $f[1] -eq 'b,c' -and $f[2] -eq 'd"e' -and $f[3] -eq '' -and $f[4] -eq 'f' -and $f[5] -eq '') ('fields: ' + ($f -join '|'))
    $one = Split-CsvLine 'abc'
    Assert-That ($one.Count -eq 1 -and $one[0] -eq 'abc') 'one cell'
}

Check 'record: the CSV is read from outside - fresh / repeat / missing per source, skipped frames, period, a quoted reason, row gaps, a bad line and the value columns' {
    $path = Join-Path $WorkDir 'rec-stats.csv'
    [IO.File]::WriteAllText($path, (New-RecordCsvText))
    $s = Get-RecordCsvStats $path
    Assert-That $s.read ('not read: ' + $s.note)
    Assert-That ($s.rows -eq 4 -and $s.bad_rows -eq 1) ('rows ' + $s.rows + ' bad ' + $s.bad_rows)
    Assert-That (@($s.sources.Keys).Count -eq 2 -and $s.sources.Contains('core') -and $s.sources.Contains('gpu1')) ('sources: ' + (@($s.sources.Keys) -join ','))
    $c = $s.sources['core']; $g = $s.sources['gpu1']
    Assert-That ($c.fresh -eq 4 -and $c.repeat -eq 0 -and $c.missing -eq 0) 'core fresh/repeat/missing'
    Assert-That ($g.fresh -eq 2 -and $g.repeat -eq 1 -and $g.missing -eq 1) ('gpu1 fresh/repeat/missing: ' + $g.fresh + '/' + $g.repeat + '/' + $g.missing)
    Assert-That ($g.fresh_pct -eq 50 -and $g.repeat_pct -eq 25 -and $g.missing_pct -eq 25) ('gpu1 percent: ' + $g.fresh_pct + '/' + $g.repeat_pct + '/' + $g.missing_pct)
    Assert-That ($c.frame_jumps -eq 1 -and $c.frame_jump_events -eq 1 -and $c.max_frame_jump -eq 1) ('core jumps ' + $c.frame_jumps + ' in ' + $c.frame_jump_events)
    Assert-That ($g.frame_jumps -eq 0) 'a repeat of g1-1 and then g1-2 is not a jump'
    Assert-That ($g.reasons.Contains('sensor, gone') -and $g.reasons['sensor, gone'] -eq 1) ('the quoted reason: ' + (@($g.reasons.Keys) -join '|'))
    Assert-That ($g.period_ms.Contains('1000') -and $g.period_ms['1000'] -eq 4) 'gpu1 period'
    Assert-That ($c.age_ms_mean -eq 100 -and $g.age_ms_mean -eq 50) ('age: ' + $c.age_ms_mean + ' / ' + $g.age_ms_mean)
    Assert-That ($s.row_gap_ms.min -eq 1000 -and $s.row_gap_ms.max -eq 1600 -and $s.row_gap_ms.mean -eq 1200) ('gaps: ' + $s.row_gap_ms.min + '/' + $s.row_gap_ms.mean + '/' + $s.row_gap_ms.max)
    Assert-That ($s.row_gaps_over_1500_ms -eq 1) 'gaps over 1500 ms'
    $pkg = $s.values['core.cpu_package_w']
    Assert-That ($pkg.rows_with_value -eq 0 -and $pkg.rows_empty -eq 4 -and $null -eq $pkg.min) 'an empty column must stay empty (power missing), not 0'
    $ac = $s.values['core.ac_connected']
    Assert-That ($ac.rows_with_value -eq 4 -and $ac.min -eq 1 -and $ac.max -eq 1) 'ac_connected'
    $t = $s.values['gpu1.temp_c']
    Assert-That ($t.rows_with_value -eq 3 -and $t.rows_empty -eq 1 -and $t.min -eq 60 -and $t.max -eq 62 -and $t.mean -eq 61) ('temp: ' + $t.min + '/' + $t.mean + '/' + $t.max)
    Assert-That (-not $s.values.Contains('core.state') -and -not $s.values.Contains('gpu1.frame')) 'a source column was counted as a value column'
}

Check 'record: a CSV that is missing, empty or not a recording is said, never analysed' {
    $none = Get-RecordCsvStats (Join-Path $WorkDir 'no-such.csv')
    Assert-That (-not $none.read -and $none.note -match 'not found') ('missing: ' + $none.note)
    $blank = Get-RecordCsvStats ''
    Assert-That (-not $blank.read -and $null -eq $blank.file) 'no path'
    $emptyFile = Join-Path $WorkDir 'rec-empty.csv'
    [IO.File]::WriteAllText($emptyFile, '')
    $e = Get-RecordCsvStats $emptyFile
    Assert-That (-not $e.read -and $e.note -match 'empty') ('empty: ' + $e.note)
    $other = Join-Path $WorkDir 'rec-other.csv'
    [IO.File]::WriteAllText($other, "a,b,c`n1,2,3`n")
    $o = Get-RecordCsvStats $other
    Assert-That (-not $o.read -and $o.note -match 'not a recording header') ('other: ' + $o.note)
}

Check 'record: the events are read (a line that is not an event is counted) and the timeline is in seconds from the stopwatch zero' {
    $zero = New-RecordZero
    $path = Join-Path $WorkDir 'ev.record.jsonl'
    $lines = @(
        (ConvertTo-EventLine @{ event = 'begin'; folder = 'x'; leadSeconds = 60; recordSeconds = 180; userIntervalMs = 2000; effectiveIntervalMs = 2000 } $zero 1.5),
        'this is not json',
        '{"nothing":1}',
        (ConvertTo-EventLine @{ event = 'start'; ok = $true; startMs = 12; runId = 'r1'; json = 'C:\x\run-r1.json'; csv = 'C:\x\run-r1.csv' } $zero 61),
        (ConvertTo-EventLine @{ event = 'stop'; reason = 'user'; selfEnded = $false } $zero 241),
        (ConvertTo-EventLine @{ event = 'stopped'; stopMs = 480; saved = $true; status = 'completed'; stopReason = 'user'; rows = 180; droppedTicks = 0; durationUs = 180000000; json = 'C:\x\run-r1.json'; csv = 'C:\x\run-r1.csv' } $zero 241.5)
    )
    [IO.File]::WriteAllText($path, (($lines -join "`n") + "`n"))
    $ev = Read-RecordEvents $path
    Assert-That $ev.found 'the events file was not found'
    Assert-That ($ev.events.Count -eq 4) ('events: ' + $ev.events.Count)
    Assert-That ($ev.bad_lines -eq 2) ('bad lines: ' + $ev.bad_lines)
    $t = Get-RecordTimeline $ev.events $zero
    Assert-That ($t.begin_s -eq 1.5 -and $t.start_s -eq 61 -and $t.stop_s -eq 241 -and $t.stopped_s -eq 241.5) ('times: ' + $t.begin_s + '/' + $t.start_s + '/' + $t.stop_s + '/' + $t.stopped_s)
    Assert-That ($t.start_ok -eq $true -and $t.start_ms -eq 12 -and $t.run_id -eq 'r1') 'start'
    Assert-That ($t.stop_ms -eq 480 -and $t.saved -eq $true -and $t.status -eq 'completed' -and $t.rows -eq 180 -and $t.dropped_ticks -eq 0) 'stopped'
    Assert-That ($t.user_interval_ms -eq 2000 -and $t.lead_seconds -eq 60 -and $t.record_seconds -eq 180) 'begin'
    Assert-That ($t.csv -eq 'C:\x\run-r1.csv') 'the csv path'
    $evPath = Get-RecordEventsPath 'C:\m\marks.jsonl'
    Assert-That ($evPath -eq 'C:\m\marks.record.jsonl') ('the events path (the GUI writes <marks name>.record.jsonl next to the marks): ' + $evPath)
    $evDots = Get-RecordEventsPath 'C:\m\run.1.jsonl'
    Assert-That ($evDots -eq 'C:\m\run.1.record.jsonl') ('the events path of a marks name with a dot: ' + $evDots)
    $none = Read-RecordEvents (Join-Path $WorkDir 'no-events.jsonl')
    Assert-That (-not $none.found -and $none.events.Count -eq 0) 'a missing events file'
    $t0 = Get-RecordTimeline $none.events $zero
    Assert-That ($null -eq $t0.begin_s -and $null -eq $t0.start_s -and $null -eq $t0.stopped_s) 'a timeline from no events must be empty'
    $failed = @(
        (ConvertTo-EventLine @{ event = 'start'; ok = $false; problem = 'folder'; detail = 'no write' } $zero 61),
        (ConvertTo-EventLine @{ event = 'error'; message = 'boom' } $zero 62)
    )
    $t2 = Get-RecordTimeline @($failed | ForEach-Object { $_ | ConvertFrom-Json }) $zero
    Assert-That ($t2.start_ok -eq $false -and $t2.start_problem -eq 'folder no write' -and $t2.errors.Count -eq 1) ('a start that failed: ' + $t2.start_problem)
}

Check 'record: the period summary - Core''s measured gap jitters; the recording period while recording, the user interval after the stop (told apart only when they differ)' {
    $zero = New-RecordZero
    $jitter = @(-9, 0, 6, 3, -2, 12, 1, -5, 4, 8)
    # One period line per snapshot, with a gap that jitters around the nominal period as the real Core's does (the gap is measured, never exactly 1000).
    # After the stop the first two gaps are still the recording's: the return is judged on the settled half and the last line.
    function New-PeriodLine($Phase, $Gap, $Eff, $Count, $User) {
        return ((ConvertTo-EventLine @{ event = 'period'; phase = $Phase; coreIntervalMs = [int]$Gap; userIntervalMs = $User; effectiveIntervalMs = $Eff; snapshotsInPhase = $Count } $zero 10) | ConvertFrom-Json)
    }
    function New-Periods([int]$User, [int]$Before, [int]$Rec, [int]$After, $RecOverride = $null) {
        $e = @()
        $c = 0
        foreach ($j in $jitter[0..5]) { $c++; $e += , (New-PeriodLine 'before' ($Before + $j) $Before $c $User) }
        $c = 0
        $recGaps = $(if ($null -ne $RecOverride) { $RecOverride } else { @($jitter | ForEach-Object { $Rec + $_ }) })
        foreach ($g in $recGaps) { $c++; $e += , (New-PeriodLine 'recording' $g $Rec $c $User) }
        $c = 0
        foreach ($g in @(($Rec + 7), ($Rec - 3))) { $c++; $e += , (New-PeriodLine 'after' $g $After $c $User) }
        foreach ($j in $jitter[0..5]) { $c++; $e += , (New-PeriodLine 'after' ($After + $j) $After $c $User) }
        return $e
    }
    $ok = Get-PeriodSummary (New-Periods 2000 2000 1000 2000) $null
    Assert-That ($ok.user_interval_ms -eq 2000) ('user interval from the events: ' + $ok.user_interval_ms)
    Assert-That ($ok.during_recording_is_recording_period -eq $true) 'during the recording'
    Assert-That ($ok.after_stop_returns_to_user_interval -eq $true -and $ok.return_observable -eq $true -and $null -eq $ok.note) ('after the stop: ' + $ok.after_stop_returns_to_user_interval + ' ' + $ok.note)
    $rp = $ok.phases['recording']; $ap = $ok.phases['after']; $bp = $ok.phases['before']
    Assert-That ($rp.period_lines -eq 10 -and $rp.nominal_period_ms -eq 1000 -and $ap.period_lines -eq 8 -and $ap.nominal_period_ms -eq 2000 -and $bp.nominal_period_ms -eq 2000) ('phases: ' + $rp.period_lines + '/' + $rp.nominal_period_ms + '/' + $ap.period_lines + '/' + $ap.nominal_period_ms + '/' + $bp.nominal_period_ms)
    Assert-That ($rp.core_gap_ms.min -eq 991 -and $rp.core_gap_ms.max -eq 1012 -and $rp.core_gap_ms.min -ne $rp.core_gap_ms.max -and $rp.core_gap_ms.median -eq 1002 -and $rp.core_gap_ms.share_near_nominal -eq 1) ('the measured gap is a distribution: ' + $rp.core_gap_ms.min + '/' + $rp.core_gap_ms.median + '/' + $rp.core_gap_ms.max)
    Assert-That ($rp.last_core_interval_ms -eq 1008 -and $ap.last_core_interval_ms -eq 2012 -and $rp.snapshots_at_last_line -eq 10) ('last gaps: ' + $rp.last_core_interval_ms + '/' + $ap.last_core_interval_ms)
    Assert-That ($ap.core_gap_ms.settled_nominal_ms -eq 2000 -and $ap.core_gap_ms.last_nominal_ms -eq 2000 -and $ap.core_gap_ms.min -eq 997) ('after: ' + $ap.core_gap_ms.settled_nominal_ms + '/' + $ap.core_gap_ms.last_nominal_ms + '/' + $ap.core_gap_ms.min)
    $same = Get-PeriodSummary (New-Periods 1000 1000 1000 1000) 1000
    Assert-That ($same.return_observable -eq $false -and $same.note -match 'equals the recording period') ('same interval: ' + $same.note)
    Assert-That ($same.during_recording_is_recording_period -eq $true) 'with equal periods the recording is still read as 1000 ms'
    $stuck = Get-PeriodSummary (New-Periods 2000 2000 1000 1000) 2000
    Assert-That ($stuck.after_stop_returns_to_user_interval -eq $false) 'a period that stays at 1000 after the stop must say it did not return'
    $slow = Get-PeriodSummary (New-Periods 2000 2000 2000 2000) 2000
    # Mostly back at 2000 ms but the last gap is the recording's again: not returned. Mostly at the recording's 1000 ms but the last gap is 2000: not returned.
    $flipEv = New-Periods 2000 2000 1000 2000
    $flipEv[$flipEv.Count - 1].coreIntervalMs = 1003
    Assert-That ((Get-PeriodSummary $flipEv 2000).after_stop_returns_to_user_interval -eq $false) 'a phase that ends at the recording period has not returned'
    $lateEv = New-Periods 2000 2000 1000 1000
    $lateEv[$lateEv.Count - 1].coreIntervalMs = 2003
    Assert-That ((Get-PeriodSummary $lateEv 2000).after_stop_returns_to_user_interval -eq $false) 'one late gap at 2000 ms after a phase at 1000 ms is not a return'
    # Slow to settle: the first half of the after phase is still at 1000 ms (the median of the whole phase is not the user's), the second half is back: returned.
    $slowEv = New-Periods 2000 2000 1000 2000
    $afterIx = @(0..($slowEv.Count - 1) | Where-Object { $slowEv[$_].phase -eq 'after' })
    foreach ($k in 0..4) { $slowEv[$afterIx[$k]].coreIntervalMs = 1000 + $jitter[$k] }
    foreach ($k in 5..7) { $slowEv[$afterIx[$k]].coreIntervalMs = 2000 + $jitter[$k] }
    $settle = Get-PeriodSummary $slowEv 2000
    Assert-That ($settle.after_stop_returns_to_user_interval -eq $true -and $settle.phases['after'].core_gap_ms.nominal_ms -eq 1000) ('a late settling: ' + $settle.after_stop_returns_to_user_interval + ' whole-phase class ' + $settle.phases['after'].core_gap_ms.nominal_ms)
    Assert-That ($slow.during_recording_is_recording_period -eq $false) 'a recording that did not hold 1000 ms must say so'
    # Nine gaps of ten near 1000 hold it (a first gap after the lease can be long); two of ten do not.
    $one = Get-PeriodSummary (New-Periods 2000 2000 1000 2000 @(1996, 1003, 998, 1004, 1001, 997, 1010, 1002, 999, 1005)) 2000
    Assert-That ($one.during_recording_is_recording_period -eq $true -and $one.phases['recording'].core_gap_ms.share_near_nominal -eq 0.9) ('one stray gap of ten: ' + $one.phases['recording'].core_gap_ms.share_near_nominal)
    $two = Get-PeriodSummary (New-Periods 2000 2000 1000 2000 @(1996, 2004, 998, 1004, 1001, 997, 1010, 1002, 999, 1005)) 2000
    Assert-That ($two.during_recording_is_recording_period -eq $false -and $two.phases['recording'].core_gap_ms.share_near_nominal -eq 0.8) ('two stray gaps of ten: ' + $two.phases['recording'].core_gap_ms.share_near_nominal)
    Assert-That ((Get-NominalPeriod 1018) -eq 1000 -and (Get-NominalPeriod 991) -eq 1000 -and (Get-NominalPeriod 2003) -eq 2000 -and (Get-NominalPeriod 520) -eq 500 -and (Get-NominalPeriod 4800) -eq 5000 -and (Get-NominalPeriod 1700) -eq 2000 -and $null -eq (Get-NominalPeriod $null)) 'nearest nominal period'
    $noAfter = Get-PeriodSummary @((New-Periods 2000 2000 1000 2000)[0..15]) 2000
    Assert-That ($null -eq $noAfter.after_stop_returns_to_user_interval -and $noAfter.note -match 'after the stop is missing') ('no after line: ' + $noAfter.note)
    $none = Get-PeriodSummary @() $null
    Assert-That ($null -eq $none.during_recording_is_recording_period -and $null -eq $none.after_stop_returns_to_user_interval -and $null -eq $none.user_interval_ms) 'no events: everything missing, nothing invented'
}

Check 'record: a path inside one of the user''s folders is found; a sibling, a parent and an empty path are not' {
    Assert-That (Test-InsideFolder 'C:\Users\x\Documents' 'C:\Users\x\Documents') 'the folder itself'
    Assert-That (Test-InsideFolder 'C:\Users\x\Documents' 'c:\users\X\documents\PCMieru\runs\') 'inside, any case, trailing slash'
    Assert-That (Test-InsideFolder 'C:\Users\x\Documents\' 'C:\Users\x\Documents\a') 'a root with a trailing slash'
    Assert-That (-not (Test-InsideFolder 'C:\Users\x\Documents' 'C:\Users\x\DocumentsOther\a')) 'a sibling that starts with the same letters'
    Assert-That (-not (Test-InsideFolder 'C:\Users\x\Documents' 'C:\Users\x')) 'the parent'
    Assert-That (-not (Test-InsideFolder '' 'C:\a')) 'an empty root'
    Assert-That (-not (Test-InsideFolder 'C:\a' '')) 'an empty path'
    $roots = @(Get-UserDataFolders)
    Assert-That ($roots.Count -eq 3) ('folders: ' + $roots.Count)
    Assert-That (@($roots | Where-Object { $_ -match 'PCMieru$' }).Count -eq 1) 'the PCMieru folder under LocalAppData is not listed'
    foreach ($r in $roots) { Assert-That (-not [string]::IsNullOrWhiteSpace($r)) 'an empty user folder' }
}

Check 'record: the app start info has the --measure-record switches only with a plan, and the interval only when it was asked for' {
    $oldExe = $script:ExePath; $oldFakes = $script:FakesOn
    try {
        $script:ExePath = 'C:\x\PCMieru.exe'
        $script:FakesOn = $false
        $plain = (New-AppStartInfo 'C:\m\marks.jsonl').Arguments
        Assert-That ($plain -eq '--measure "C:\m\marks.jsonl"') ('plain: ' + $plain)
        $plan = @{ Folder = 'D:\out\rec\'; Lead = 60; For = 180; Interval = 0 }
        $a = (New-AppStartInfo 'C:\m\marks.jsonl' $plan).Arguments
        Assert-That ($a -eq '--measure "C:\m\marks.jsonl" --measure-record "D:\out\rec" --measure-record-lead 60 --measure-record-for 180') ('plan: ' + $a)
        $plan.Interval = 2000
        $b = (New-AppStartInfo 'C:\m\marks.jsonl' $plan).Arguments
        Assert-That ($b.EndsWith(' --measure-record-interval 2000')) ('interval: ' + $b)
        $script:FakesOn = $true
        $c = (New-AppStartInfo 'C:\m\marks.jsonl' $plan).Arguments
        Assert-That ($c -match ' --fakes ' -and $c -match '--measure-record ') ('fakes: ' + $c)
        Assert-That ($c.IndexOf('--measure ') -lt $c.IndexOf('--measure-record ')) '--measure comes first'
    }
    finally { $script:ExePath = $oldExe; $script:FakesOn = $oldFakes }
}

Check 'record: the SensorHost alone in a tick is the sum of the child rows, and no child means no row' {
    $tick = @(
        @{ T = 5.0; Role = 'main'; CpuDelta = 0.5; Dt = 1.0; CyclesDelta = 5.0e8; CycDt = 1.0; Private = 1000L; Ws = 2000L; Handles = 50 },
        @{ T = 5.0; Role = 'child'; CpuDelta = 0.01; Dt = 1.0; CyclesDelta = 1.0e7; CycDt = 1.0; Private = 100L; Ws = 200L; Handles = 5 },
        @{ T = 5.0; Role = 'child'; CpuDelta = 0.02; Dt = 1.0; CyclesDelta = 2.0e7; CycDt = 1.0; Private = 300L; Ws = 400L; Handles = 7 }
    )
    $row = New-ChildRow $tick 5.0 1.0
    Assert-That ($null -ne $row) 'no child row'
    Assert-That ([math]::Abs($row.CpuDelta - 0.03) -lt 1e-9) ('cpu: ' + $row.CpuDelta)
    Assert-That ($row.CyclesDelta -eq 3.0e7 -and $row.Private -eq 400 -and $row.Ws -eq 600 -and $row.Handles -eq 12) ('sums: ' + $row.CyclesDelta + '/' + $row.Private + '/' + $row.Ws + '/' + $row.Handles)
    Assert-That ($row.Role -eq 'child' -and $row.T -eq 5.0) 'role and time'
    Assert-That ($null -eq (New-ChildRow @($tick[0]) 5.0 1.0)) 'a tick without a child made a row'
    $half = @(
        @{ T = 6.0; Role = 'child'; CpuDelta = 0.01; Dt = 1.0; CyclesDelta = $null; CycDt = $null; Private = 100L; Ws = 200L; Handles = 5 }
    )
    $r2 = New-ChildRow $half 6.0 1.0
    Assert-That ($null -eq $r2.CyclesDelta) 'a missing cycle figure became a number'
}

Check 'record: the windows and the whole section from synthetic rows, events and a CSV (before, recording, after; the period and the backlog)' {
    $zero = New-RecordZero
    $csvPath = Join-Path $WorkDir 'rec-section.csv'
    [IO.File]::WriteAllText($csvPath, (New-RecordCsvText))
    $events = Join-Path $WorkDir 'section.record.jsonl'
    $lines = @(
        (ConvertTo-EventLine @{ event = 'begin'; leadSeconds = 40; recordSeconds = 60; userIntervalMs = 2000; effectiveIntervalMs = 2000 } $zero 1),
        (ConvertTo-EventLine @{ event = 'period'; phase = 'before'; coreIntervalMs = 2000; userIntervalMs = 2000; effectiveIntervalMs = 2000; snapshotsInPhase = 10 } $zero 20),
        (ConvertTo-EventLine @{ event = 'start'; ok = $true; startMs = 20; runId = 'r1'; json = ($csvPath + '.json'); csv = $csvPath } $zero 41),
        (ConvertTo-EventLine @{ event = 'period'; phase = 'recording'; coreIntervalMs = 1000; userIntervalMs = 2000; effectiveIntervalMs = 1000; snapshotsInPhase = 10 } $zero 60),
        (ConvertTo-EventLine @{ event = 'stop'; reason = 'user'; selfEnded = $false } $zero 101),
        (ConvertTo-EventLine @{ event = 'stopped'; stopMs = 300; saved = $true; status = 'completed'; stopReason = 'user'; rows = 60; droppedTicks = 2; durationUs = 60000000; csv = $csvPath } $zero 101.3),
        (ConvertTo-EventLine @{ event = 'period'; phase = 'after'; coreIntervalMs = 2000; userIntervalMs = 2000; effectiveIntervalMs = 2000; snapshotsInPhase = 10 } $zero 120)
    )
    [IO.File]::WriteAllText($events, (($lines -join "`n") + "`n"))
    $main = @(); $child = @(); $total = @()
    foreach ($t in 1..130) {
        $mainCpu = $(if ($t -le 40) { 0.001 } elseif ($t -le 101) { 0.004 } else { 0.0005 })
        $kidCyc = $(if ($t -gt 40 -and $t -le 101) { 2.0e7 } else { 1.0e7 })
        $main += , @{ T = [double]$t; Role = 'main'; CpuDelta = $mainCpu; Dt = 1.0; CyclesDelta = 1.0e8; CycDt = 1.0; Private = 52428800L; Ws = 104857600L; Handles = 300 }
        $child += , @{ T = [double]$t; Role = 'child'; CpuDelta = 0.002; Dt = 1.0; CyclesDelta = $kidCyc; CycDt = 1.0; Private = 10485760L; Ws = 20971520L; Handles = 40 }
        $total += , @{ T = [double]$t; Role = 'total'; CpuDelta = $mainCpu + 0.002; Dt = 1.0; CyclesDelta = 1.0e8 + $kidCyc; CycDt = 1.0; Private = 62914560L; Ws = 125829120L; Handles = 340 }
    }
    $warnings = New-Object System.Collections.ArrayList
    $plan = @{ Folder = $WorkDir; Lead = 40; For = 60; Interval = 2000 }
    $sec = Get-RecordSection $plan $events $zero 130.0 $main $child $total 8 $warnings
    Assert-That ($sec.events_found -and $sec.events_count -eq 7) ('events: ' + $sec.events_count)
    $w = $sec.windows
    Assert-That (@($w.Keys) -join ',' -eq 'before,recording,after') ('windows: ' + (@($w.Keys) -join ','))
    Assert-That ($w['before'].main.window_from_s -eq 30 -and $w['before'].main.window_to_s -eq 39) ('before: ' + $w['before'].main.window_from_s + '-' + $w['before'].main.window_to_s)
    Assert-That ($w['recording'].main.window_from_s -eq 43 -and $w['recording'].main.window_to_s -eq 99) ('recording: ' + $w['recording'].main.window_from_s + '-' + $w['recording'].main.window_to_s)
    Assert-That ([math]::Abs($w['after'].main.window_from_s - 103.3) -lt 1e-6 -and $w['after'].main.window_to_s -eq 130) ('after: ' + $w['after'].main.window_from_s + '-' + $w['after'].main.window_to_s)
    Assert-That ($w['before'].main.cpu_ms_per_s_mean -eq 1 -and $w['recording'].main.cpu_ms_per_s_mean -eq 4 -and $w['after'].main.cpu_ms_per_s_mean -eq 0.5) ('main ms/s: ' + $w['before'].main.cpu_ms_per_s_mean + '/' + $w['recording'].main.cpu_ms_per_s_mean + '/' + $w['after'].main.cpu_ms_per_s_mean)
    Assert-That ($w['recording'].child.cycles_per_s_mean -eq 2.0e7 -and $w['after'].child.cycles_per_s_mean -eq 1.0e7) 'child cycles'
    Assert-That ($w['recording'].total.private_mb_mean -eq 60 -and $w['recording'].main.private_mb_mean -eq 50) ('private: ' + $w['recording'].total.private_mb_mean + ' / ' + $w['recording'].main.private_mb_mean)
    Assert-That ($sec.sensorhost_period.cycles_after_over_recording -eq 0.5) ('ratio: ' + $sec.sensorhost_period.cycles_after_over_recording)
    Assert-That ($sec.periods.after_stop_returns_to_user_interval -eq $true -and $sec.periods.during_recording_is_recording_period -eq $true) 'periods'
    Assert-That ($sec.csv.read -and $sec.csv.rows -eq 4) 'csv'
    Assert-That ($sec.backlog.dropped_ticks -eq 2 -and $sec.backlog.rows_in_csv -eq 4 -and $sec.backlog.rows_reported -eq 60 -and $sec.backlog.rows_expected_from_duration -eq 60 -and $sec.backlog.frame_jumps_total -eq 1 -and $sec.backlog.stop_ms -eq 300 -and $sec.backlog.longest_row_gap_ms -eq 1600) 'backlog'
    Assert-That (@($warnings | Where-Object { $_ -match 'dropped 2 tick' }).Count -eq 1) ('no dropped-tick warning: ' + ($warnings -join ' | '))
    Assert-That (@($warnings | Where-Object { $_ -match 'frame-number jumps' }).Count -eq 1) ('no frame-jump warning: ' + ($warnings -join ' | '))
    Assert-That ($warnings.Count -eq 2) ('other warnings: ' + ($warnings -join ' | '))
    # No events at all (an old PCMieru.exe): the windows are empty and one warning says why; nothing is invented.
    $w2 = New-Object System.Collections.ArrayList
    $s2 = Get-RecordSection $plan (Join-Path $WorkDir 'none.record.jsonl') $zero 130.0 $main $child $total 8 $w2
    Assert-That (-not $s2.events_found) 'events found'
    Assert-That ($s2.windows['recording'].main.samples -eq 0 -and $s2.windows['after'].main.samples -eq 0) 'a recording window without a start event must be empty'
    Assert-That (@($w2 | Where-Object { $_ -match 'events file was not written' }).Count -eq 1) ('no warning: ' + ($w2 -join ' | '))
}

# ---------------------------------------------------------------- the GUI (only with -Exe)

if ([string]::IsNullOrEmpty($GuiExe)) {
    Write-Host 'SKIP  GUI checks (no -Exe)' -ForegroundColor Yellow
}
else {
    $script:ExePath = (Resolve-Path -LiteralPath $GuiExe).Path
    $script:FakesOn = $true

    Check 'gui: a marker path that cannot be made is exit 1 and one stderr line, found by the script, and no marker file' {
        $dir = New-WorkSubDir 'badmark'
        $file = Join-Path $dir 'a-file'
        [IO.File]::WriteAllText($file, 'x')
        $bad = Join-Path $file 'marks.jsonl'
        $proc = Start-App $bad
        try {
            [void]$proc.WaitForExit(20000)
            $msg = $null
            try { Assert-AppStillRunning $proc } catch { $msg = $_.Exception.Message }
            Assert-That ($null -ne $msg) 'the script did not notice that the GUI had ended'
            Assert-That ($msg -match 'exit code 1') ('exit code: ' + $msg)
            Assert-That ($msg -match '--measure:') ('no stderr line: ' + $msg)
            Assert-That ($msg -notmatch '[\r\n]') 'more than one line'
            Assert-That ($proc.HasExited) 'the GUI is still running (a message box?)'
            Assert-That (-not (Test-Path -LiteralPath $bad)) 'a marker file exists'
            Info $msg
        }
        finally { Stop-Quietly $proc }
    }

    Check 'gui: the navigation list is read, the checks page is found, one page step is made, the window size is read and the window closes politely' {
        $dir = New-WorkSubDir 'navread'
        $proc = Start-App (Join-Path $dir 'marks.jsonl')
        try {
            $nav = [IntPtr]::Zero
            $count = 0
            $limit = [Diagnostics.Stopwatch]::StartNew()
            Initialize-Win32
            while ($limit.Elapsed.TotalSeconds -lt 40) {
                $proc.Refresh()
                if ($proc.HasExited) { throw 'the GUI ended by itself' }
                $mainWindow = Get-MainWindow $proc
                if ($mainWindow -ne [IntPtr]::Zero) {
                    $nav = [PcmMeasureWin]::FindNavList($mainWindow)
                    if ($nav -ne [IntPtr]::Zero) { $count = [PcmMeasureWin]::Count($nav); if ($count -ge 8) { break } }
                }
                [Threading.Thread]::Sleep(250)
            }
            Assert-That ($nav -ne [IntPtr]::Zero -and $count -ge 8) 'the navigation list box was not found'
            $idx = Find-ChecksIndex $nav $count
            Assert-That ($null -ne $idx) 'the checks page was not found by its title'
            $titles = @(0..($count - 1) | ForEach-Object { [PcmMeasureWin]::Text($nav, $_) })
            Info ('items: ' + ($titles -join ' | ') + '; checks index ' + $idx)
            $from = [PcmMeasureWin]::Current($nav)
            Assert-That ($from -ge 0 -and $from -ne $idx) 'the start selection is unknown or on the checks page'
            $plan = Get-NavPlan $count $from $idx
            Assert-That $plan.ok ('no plan from the start page: ' + $plan.note)
            $state = @{ Nav = $null; ChecksIndex = $null }
            $step = Step-NavPage $proc $state
            Assert-That ($step.ok) ('the step failed: ' + $step.note)
            Assert-That ($step.to -eq $plan.target -and $step.to -ne $idx) 'the step did not end on the planned page'
            $size = Get-WindowSize $proc
            Assert-That ($null -ne $size.width -and $size.width -gt 200 -and $size.height -gt 200) ('window size: ' + $size.note)
            Info ('step: ' + $from + ' -> ' + $step.to + ' by ' + (@($step.keys) -join ',') + '; window ' + $size.width + ' x ' + $size.height)
            # The close the measurement uses: WM_CLOSE to the window Get-MainWindow chose ends the GUI without a kill (a tooltip under a
            # resting mouse pointer once made Process.MainWindowHandle the tooltip's shadow and every run ended in a kill).
            $close = Close-Politely $proc 20
            Assert-That ($close.method -eq 'CloseMainWindow') ('the GUI was not closed politely: ' + $close.method + ' ' + $close.note)
        }
        finally {
            try { [void]$proc.CloseMainWindow(); if (-not $proc.WaitForExit(15000)) { $proc.Kill() } } catch { }
            Stop-Quietly $proc
        }
    }
}

try { Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction Stop } catch { }

Write-Host ('selftest: {0} passed, {1} failed, {2} skipped' -f $script:Passed, $script:Failed, $script:Skipped) -ForegroundColor $(if ($script:Failed -eq 0) { 'Green' } else { 'Red' })
if ($script:Failed -gt 0) { exit 1 }
exit 0
