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
    * the output-folder check, the CSV columns, the cycle statistics.
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
