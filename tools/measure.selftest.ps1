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
    * the forms (-Form party / legacy / both): the alternating pair order, the old-app gate (legacy-gate.cs; exe size, missing dll, junk dll, missing checker),
      its two PARTSLIFE_ variables, Party's --measure command line, new unique folders and files that are never overwritten, first-value.txt, the totals,
      the incomplete-run rule and the aggregate, the window-difference warning, the WM_CLOSE-then-Kill close (on a windowless powershell child), the argument
      checks (a child powershell that is refused before anything is made) and the source pins. None of these starts an app.
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
    # $Loop / $Leases: what the GUI's period line says the sampler loop ran at and how many recordings held it (loopPeriodMs / leases); $null leaves the
    # fields out, as an older PCMieru.exe does.
    function New-PeriodLine($Phase, $Gap, $Eff, $Count, $User, $Loop = $null, $Leases = $null) {
        $f = @{ event = 'period'; phase = $Phase; coreIntervalMs = [int]$Gap; userIntervalMs = $User; effectiveIntervalMs = $Eff; snapshotsInPhase = $Count }
        if ($null -ne $Loop) { $f['loopPeriodMs'] = $Loop; $f['leases'] = $Leases }
        return ((ConvertTo-EventLine $f $zero 10) | ConvertFrom-Json)
    }
    # $WithLoop: the lines carry the loop fields as the real GUI writes them (no lease before and after at the pump's period, one lease while recording at 1 s).
    function New-Periods([int]$User, [int]$Before, [int]$Rec, [int]$After, $RecOverride = $null, [switch]$WithLoop, [int]$AfterLines = 8) {
        $e = @()
        $c = 0
        foreach ($j in $jitter[0..5]) { $c++; $e += , (New-PeriodLine 'before' ($Before + $j) $Before $c $User $(if ($WithLoop) { $Before }) $(if ($WithLoop) { 0 })) }
        $c = 0
        $recGaps = $(if ($null -ne $RecOverride) { $RecOverride } else { @($jitter | ForEach-Object { $Rec + $_ }) })
        foreach ($g in $recGaps) { $c++; $e += , (New-PeriodLine 'recording' $g $Rec $c $User $(if ($WithLoop) { $Rec }) $(if ($WithLoop) { 1 })) }
        $c = 0
        $afterGaps = @(($Rec + 7), ($Rec - 3)) + @($jitter[0..5] | ForEach-Object { $After + $_ })
        foreach ($g in @($afterGaps[0..($AfterLines - 1)])) { $c++; $e += , (New-PeriodLine 'after' $g $After $c $User $(if ($WithLoop) { $After }) $(if ($WithLoop) { 0 })) }
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
    Assert-That ($null -eq $none.during_recording_loop_period_is_recording_period -and $null -eq $none.after_stop_loop_period_is_user_setting_without_lease) 'no events: the loop checks are missing too'

    # The loop's own reading (loopPeriodMs and leases): exact, no jitter. An older PCMieru.exe writes neither, and the checks are then null, not false.
    $plain = Get-PeriodSummary (New-Periods 2000 2000 1000 2000) 2000
    Assert-That ($null -eq $plain.during_recording_loop_period_is_recording_period -and $null -eq $plain.after_stop_loop_period_is_user_setting_without_lease -and $plain.phases['recording'].loop_lines -eq 0) 'lines without the loop fields: the loop checks are null'
    $loopOk = Get-PeriodSummary (New-Periods 2000 2000 1000 2000 -WithLoop) 2000
    Assert-That ($loopOk.during_recording_loop_period_is_recording_period -eq $true -and $loopOk.after_stop_loop_period_is_user_setting_without_lease -eq $true) ('the loop fields as the GUI writes them: ' + $loopOk.during_recording_loop_period_is_recording_period + '/' + $loopOk.after_stop_loop_period_is_user_setting_without_lease)
    $lp = $loopOk.phases['recording']; $lb = $loopOk.phases['before']
    Assert-That ((@($lp.loop_period_ms) -join ',') -eq '1000' -and (@($lp.leases) -join ',') -eq '1' -and $lp.loop_lines -eq 10 -and $lp.loop_lines_agreeing -eq 10 -and (@($lb.leases) -join ',') -eq '0' -and (@($lb.loop_period_ms) -join ',') -eq '2000') ('loop per phase: ' + (@($lp.loop_period_ms) -join ',') + ' / ' + (@($lp.leases) -join ',') + ' / ' + $lp.loop_lines_agreeing + ' of ' + $lp.loop_lines)
    # A recording whose loop did not go to 1 s (or whose lease was never taken) is not at the recording period, whatever the gaps say.
    $slowLoop = New-Periods 2000 2000 1000 2000 -WithLoop
    $slowLoop[8].loopPeriodMs = 2000
    Assert-That ((Get-PeriodSummary $slowLoop 2000).during_recording_loop_period_is_recording_period -eq $false) 'one recording line at the loop period of 2000 ms must fail the check'
    $noLease = New-Periods 2000 2000 1000 2000 -WithLoop
    $noLease[8].leases = 0
    Assert-That ((Get-PeriodSummary $noLease 2000).during_recording_loop_period_is_recording_period -eq $false) 'a recording line that holds no lease must fail the check'
    # A lease still held after the stop (a leak), or a loop still at 1 s after it, fails the after check.
    $leak = New-Periods 2000 2000 1000 2000 -WithLoop
    $leak[$leak.Count - 1].leases = 1
    Assert-That ((Get-PeriodSummary $leak 2000).after_stop_loop_period_is_user_setting_without_lease -eq $false) 'a lease held after the stop must fail the check'
    $stuckLoop = New-Periods 2000 2000 1000 2000 -WithLoop
    $stuckLoop[$stuckLoop.Count - 1].loopPeriodMs = 1000
    Assert-That ((Get-PeriodSummary $stuckLoop 2000).after_stop_loop_period_is_user_setting_without_lease -eq $false) 'a loop still at 1 s after the stop must fail the check'

    # "Back at the user interval" is judged only with enough lines in the after phase: five say little, six are judged.
    $five = Get-PeriodSummary (New-Periods 2000 2000 1000 2000 -AfterLines 5) 2000
    Assert-That ($null -eq $five.after_stop_returns_to_user_interval -and $five.note -match 'only 5 period line' -and $five.note -match '6 or more') ('five after lines are not judged: ' + $five.after_stop_returns_to_user_interval + ' / ' + $five.note)
    $six = Get-PeriodSummary (New-Periods 2000 2000 1000 2000 -AfterLines 6) 2000
    Assert-That ($six.after_stop_returns_to_user_interval -eq $true -and $null -eq $six.note) ('six after lines are judged: ' + $six.after_stop_returns_to_user_interval + ' / ' + $six.note)
    $stuck5 = Get-PeriodSummary (New-Periods 2000 2000 1000 1000 -AfterLines 5) 2000
    Assert-That ($null -eq $stuck5.after_stop_returns_to_user_interval) 'five lines that stay at 1 s are not judged either (not "false" on thin evidence)'
}

Check 'record: the window state - normal, minimized and hidden at the start, at the stop and after it; slow mode is not read as the user interval' {
    $states = @(
        [ordered]@{ t_s = 1.0; state = 'normal' },
        [ordered]@{ t_s = 50.0; state = 'minimized' },
        [ordered]@{ t_s = 60.0; state = 'normal' }
    )
    Assert-That ($null -eq (Get-WindowStateAt $states 0.5)) 'before the first look nothing is known'
    Assert-That ((Get-WindowStateAt $states 1.0) -eq 'normal' -and (Get-WindowStateAt $states 49.9) -eq 'normal' -and (Get-WindowStateAt $states 50.0) -eq 'minimized' -and (Get-WindowStateAt $states 200) -eq 'normal') 'the state at a moment is the last look at or before it'
    Assert-That ($null -eq (Get-WindowStateAt @() 10) -and $null -eq (Get-WindowStateAt $null 10) -and $null -eq (Get-WindowStateAt $states $null)) 'no looks, or no moment: null'
    $timeline = [ordered]@{ start_s = 20.0; stop_s = 80.0; stopped_s = 81.0 }
    $calm = Get-WindowStateSummary $states $timeline 140.0
    Assert-That ($calm.at_start -eq 'normal' -and $calm.at_stop -eq 'normal' -and (@($calm.after) -join ',') -eq 'normal' -and -not $calm.slow_mode_at_start -and -not $calm.slow_mode_at_stop -and -not $calm.slow_mode_after_the_stop -and $null -eq $calm.note) ('a calm run: ' + $calm.at_start + '/' + $calm.at_stop + '/' + (@($calm.after) -join ',') + ' ' + $calm.note)
    $minimizedAtStop = @([ordered]@{ t_s = 1.0; state = 'normal' }, [ordered]@{ t_s = 70.0; state = 'minimized' }, [ordered]@{ t_s = 90.0; state = 'hidden' })
    $slowRun = Get-WindowStateSummary $minimizedAtStop $timeline 140.0
    Assert-That ($slowRun.at_stop -eq 'minimized' -and $slowRun.slow_mode_at_stop -and $slowRun.slow_mode_after_the_stop -and (@($slowRun.after) -join ',') -eq 'minimized,hidden' -and $null -ne $slowRun.note) ('a minimized window: ' + $slowRun.at_stop + '/' + (@($slowRun.after) -join ',') + ' ' + $slowRun.note)
    $nothing = Get-WindowStateSummary @() $timeline 140.0
    Assert-That ($nothing.looks -eq 0 -and $null -eq $nothing.at_start -and $nothing.note -match 'not read') ('no looks: ' + $nothing.note)
    $unfinished = Get-WindowStateSummary $states ([ordered]@{ start_s = 20.0; stop_s = $null; stopped_s = $null }) 140.0
    Assert-That ($null -eq $unfinished.at_stop -and @($unfinished.after).Count -eq 0) 'a recording that never stopped has no stop state and no after states'
    # In slow mode the return to the user interval is not judged, even with enough lines that would otherwise say it is back.
    $zero = New-RecordZero
    $ev = @()
    foreach ($k in 1..8) { $ev += , ((ConvertTo-EventLine @{ event = 'period'; phase = 'recording'; coreIntervalMs = 1000; userIntervalMs = 2000; effectiveIntervalMs = 1000; snapshotsInPhase = $k } $zero 10) | ConvertFrom-Json) }
    foreach ($k in 1..8) { $ev += , ((ConvertTo-EventLine @{ event = 'period'; phase = 'after'; coreIntervalMs = 2000; userIntervalMs = 2000; effectiveIntervalMs = 2000; snapshotsInPhase = $k } $zero 10) | ConvertFrom-Json) }
    $judged = Get-PeriodSummary $ev 2000 $calm
    Assert-That ($judged.after_stop_returns_to_user_interval -eq $true -and $judged.window_state.at_stop -eq 'normal') 'a calm window leaves the judgement as it was'
    $slowJudged = Get-PeriodSummary $ev 2000 $slowRun
    Assert-That ($null -eq $slowJudged.after_stop_returns_to_user_interval -and $slowJudged.note -match 'slow mode' -and $slowJudged.note -match 'minimized/hidden') ('slow mode after the stop is not judged: ' + $slowJudged.after_stop_returns_to_user_interval + ' / ' + $slowJudged.note)
    # The window of a process id that does not exist has no window at all; the answer for a real process is one of the four words.
    Initialize-Win32
    Assert-That ([PcmMeasureWin]::WindowStateOf(2147483000) -eq 'none') 'a process that does not exist has no window'
    Assert-That (@('normal', 'minimized', 'hidden', 'none') -contains (Get-WindowState ([Diagnostics.Process]::GetCurrentProcess()))) 'this process: one of the four states'
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
    Assert-That ($roots.Count -ge 7) ('folders: ' + $roots.Count)
    Assert-That (@($roots | Where-Object { $_ -match 'PCMieru$' }).Count -eq 1) 'the PCMieru folder under LocalAppData is not listed'
    foreach ($r in $roots) { Assert-That (-not [string]::IsNullOrWhiteSpace($r)) 'an empty user folder' }
    $profileDocuments = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Documents'
    Assert-That ($roots -contains [Environment]::GetFolderPath('MyDocuments')) 'the Documents folder as Windows says it (redirected or not) is not listed'
    Assert-That ($roots -contains $profileDocuments) 'the plain Documents of the profile is not listed'
    Assert-That (Test-InsideFolder $profileDocuments (Join-Path $profileDocuments 'runs')) 'a folder under the plain Documents of the profile'
}

Check 'record: the same folder under another spelling is the same folder (short name, junction), and a link that leads nowhere is refused' {
    $base = Join-Path $WorkDir 'guard'
    $documents = Join-Path $base 'Documents'
    $elsewhere = Join-Path $base 'Elsewhere'
    New-Item -ItemType Directory -Path $documents -Force | Out-Null
    New-Item -ItemType Directory -Path $elsewhere -Force | Out-Null
    $intoDocuments = Join-Path $base 'scratch-link'
    $intoElsewhere = Join-Path $base 'other-link'
    $dangling = Join-Path $base 'dangling'
    $gone = Join-Path $base 'gone'
    New-Item -ItemType Directory -Path $gone -Force | Out-Null
    foreach ($pair in @(@($intoDocuments, $documents), @($intoElsewhere, $elsewhere), @($dangling, $gone))) {
        & cmd.exe /c ('mklink /J "' + $pair[0] + '" "' + $pair[1] + '"') | Out-Null
        Assert-That ($LASTEXITCODE -eq 0) ('mklink failed for ' + $pair[0])
    }
    Remove-Item -LiteralPath $gone -Force
    try {
        # A junction into a user folder is that folder; one into a harmless place is not; a folder that does not exist yet follows the part that does.
        Assert-That (Test-InsideFolder $documents $intoDocuments) 'the junction itself is inside the folder it leads to'
        Assert-That (Test-InsideFolder $documents (Join-Path $intoDocuments 'runs')) 'under the junction'
        Assert-That (Test-InsideFolder $documents (Join-Path $intoDocuments 'a\b\c')) 'under the junction, several levels that do not exist yet'
        Assert-That (-not (Test-InsideFolder $documents (Join-Path $intoElsewhere 'runs'))) 'a junction into a harmless place'
        Assert-That (Test-PathResolvable (Join-Path $intoElsewhere 'runs')) 'a good junction can be followed'
        Assert-That (Test-PathResolvable (Join-Path $base 'new\deeper')) 'a plain folder that is not there yet'
        # A link that leads nowhere: where it goes is not known.
        Assert-That (-not (Test-PathResolvable $dangling)) 'a junction whose target is gone'
        Assert-That (-not (Test-PathResolvable (Join-Path $dangling 'runs'))) 'under a junction whose target is gone'
        # The 8.3 short spelling of a folder is the folder.
        $short = (New-Object -ComObject Scripting.FileSystemObject).GetFolder($documents).ShortPath
        if ($short -ine $documents) {
            Assert-That (Test-InsideFolder $documents (Join-Path $short 'runs')) 'the short spelling of the folder'
            Assert-That (Test-InsideFolder $short (Join-Path $documents 'runs')) 'the short spelling of the root'
        }
        Assert-That (-not (Test-InsideFolder $documents $elsewhere)) 'a sibling is not inside'
    }
    finally {
        foreach ($link in @($intoDocuments, $intoElsewhere, $dangling)) {
            # Test-Path follows a link and says "no" for one that leads nowhere, so the delete is tried for every link (it removes only the link).
            try { [IO.Directory]::Delete($link) } catch { }
        }
    }
    Assert-That ((Test-Path -LiteralPath $documents) -and (Test-Path -LiteralPath $elsewhere)) 'removing the links removed what they led to'
}

Check 'record: only a drive letter, a colon and a backslash with no other colon is a plain local path' {
    foreach ($plain in @('C:\a', 'c:\a\b\c', 'E:\MovedFromC\Documents\x', 'C:\')) { Assert-That (Test-PlainLocalPath $plain) ('plain: ' + $plain) }
    $notPlain = @('\\.\C:\a', '\\?\C:\a', '\\?\UNC\localhost\C$\a', '\\localhost\C$\a', '\\127.0.0.1\E$\a', '\\server\share\a', 'C:a', 'C:', 'C:/a', '1:\a',
        'C:\a::$INDEX_ALLOCATION\x', 'C:\a\b:stream', 'C:\a:b', '')
    foreach ($bad in $notPlain) { Assert-That (-not (Test-PlainLocalPath $bad)) ('not plain: ' + $bad) }
}

Check 'record: a device path, a loopback admin share and a stream spelling that lead into a user folder are refused, and so is any spelling that is not a drive letter' {
    $base = Join-Path $WorkDir 'spelling'
    $documents = Join-Path $base 'Documents'
    $elsewhere = Join-Path $base 'Elsewhere'
    New-Item -ItemType Directory -Path $documents -Force | Out-Null
    New-Item -ItemType Directory -Path $elsewhere -Force | Out-Null
    $roots = @($documents)
    function Share([string]$HostName, [string]$Path) { return ('\\' + $HostName + '\' + $Path.Substring(0, 1) + '$' + $Path.Substring(2)) }
    $into = @(
        ('\\.\' + (Join-Path $documents 'x'))
        ('\\.\' + $documents)
        ('\\?\' + (Join-Path $documents 'x'))
        (Share 'localhost' (Join-Path $documents 'x'))
        (Share '127.0.0.1' (Join-Path $documents 'x'))
        (Share '127.0.0.1' $documents)
        (Share $env:COMPUTERNAME (Join-Path $documents 'x'))
    )
    foreach ($spelling in $into) {
        $why = Get-RecordFolderProblem $spelling $roots
        Assert-That ($null -ne $why -and $why -match 'local drive') ('not refused as a spelling: ' + $spelling + ' -> ' + $why)
    }
    # A folder outside the user's own ones is refused as well when it is not spelled with a letter: where it leads cannot be told from its words.
    foreach ($spelling in @(('\\.\' + (Join-Path $elsewhere 'x')), (Share 'localhost' (Join-Path $elsewhere 'x')), '\\server\share\runs')) {
        $why = Get-RecordFolderProblem $spelling $roots
        Assert-That ($null -ne $why -and $why -match 'local drive') ('not refused as a spelling: ' + $spelling + ' -> ' + $why)
    }
    # A stream spelling of the folder.
    foreach ($spelling in @((Join-Path $base 'Documents::$INDEX_ALLOCATION\x'), (Join-Path $base 'Documents::$INDEX_ALLOCATION'), (Join-Path $base 'Documents:name\x'), (Join-Path $documents 'x:stream'))) {
        $why = Get-RecordFolderProblem $spelling $roots
        Assert-That ($null -ne $why) ('not refused as a stream spelling: ' + $spelling)
    }
    # The same folders with their letter: the user's own one is refused as before, a harmless one is allowed.
    Assert-That ($null -ne (Get-RecordFolderProblem (Join-Path $documents 'x') $roots)) 'a folder inside the user''s folder'
    Assert-That ($null -eq (Get-RecordFolderProblem (Join-Path $elsewhere 'x') $roots)) ('a plain folder outside them: ' + (Get-RecordFolderProblem (Join-Path $elsewhere 'x') $roots))
}

Check 'record: the Desktop, the Downloads and the install folder are among the user''s folders and a record folder under each is refused' {
    $profileFolder = [Environment]::GetFolderPath('UserProfile')
    $downloads = [PcmRealPath]::KnownDownloads()
    Assert-That (-not [string]::IsNullOrWhiteSpace($downloads)) 'Windows does not say where Downloads is'
    Assert-That (Test-PlainLocalPath $downloads) ('Downloads is not a plain local path: ' + $downloads)
    $install = Join-Path $WorkDir 'install-folder'
    $roots = @(Get-UserDataFolders $install)
    $expected = @(
        [Environment]::GetFolderPath('DesktopDirectory'), (Join-Path $profileFolder 'Desktop'),
        $downloads, (Join-Path $profileFolder 'Downloads'), $install
    )
    foreach ($e in $expected) { Assert-That ($roots -contains $e) ('not among the folders: ' + $e) }
    $oldExe = $script:ExePath
    try {
        $script:ExePath = Join-Path $install 'PCMieru.exe'
        foreach ($e in $expected) {
            foreach ($inside in @($e, (Join-Path $e 'runs'), (Join-Path $e 'a\b'))) {
                $why = Get-RecordFolderProblem $inside
                Assert-That ($null -ne $why -and $why -match 'outside the user') ('not refused: ' + $inside + ' -> ' + $why)
            }
        }
    }
    finally { $script:ExePath = $oldExe }
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
        (ConvertTo-EventLine @{ event = 'period'; phase = 'after'; coreIntervalMs = 2000; userIntervalMs = 2000; effectiveIntervalMs = 2000; snapshotsInPhase = 10 } $zero 120),
        (ConvertTo-EventLine @{ event = 'period'; phase = 'after'; coreIntervalMs = 2003; userIntervalMs = 2000; effectiveIntervalMs = 2000; snapshotsInPhase = 11 } $zero 122),
        (ConvertTo-EventLine @{ event = 'period'; phase = 'after'; coreIntervalMs = 1998; userIntervalMs = 2000; effectiveIntervalMs = 2000; snapshotsInPhase = 12 } $zero 124),
        (ConvertTo-EventLine @{ event = 'period'; phase = 'after'; coreIntervalMs = 2001; userIntervalMs = 2000; effectiveIntervalMs = 2000; snapshotsInPhase = 13 } $zero 126),
        (ConvertTo-EventLine @{ event = 'period'; phase = 'after'; coreIntervalMs = 2004; userIntervalMs = 2000; effectiveIntervalMs = 2000; snapshotsInPhase = 14 } $zero 128),
        (ConvertTo-EventLine @{ event = 'period'; phase = 'after'; coreIntervalMs = 1996; userIntervalMs = 2000; effectiveIntervalMs = 2000; snapshotsInPhase = 15 } $zero 129)
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
    Assert-That ($sec.events_found -and $sec.events_count -eq 12) ('events: ' + $sec.events_count)
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
    # The window states the run loop noted go through the section: a window minimized at the stop is a warning and the return is not judged; a calm one is reported and not warned about.
    $calmStates = New-Object System.Collections.ArrayList
    [void]$calmStates.Add([ordered]@{ t_s = 1.0; state = 'normal' })
    $wCalm = New-Object System.Collections.ArrayList
    $secCalm = Get-RecordSection $plan $events $zero 130.0 $main $child $total 8 $wCalm $calmStates
    Assert-That ($secCalm.periods.window_state.at_start -eq 'normal' -and $secCalm.periods.window_state.at_stop -eq 'normal' -and $secCalm.periods.after_stop_returns_to_user_interval -eq $true -and $wCalm.Count -eq 2) ('a calm window: ' + $secCalm.periods.window_state.at_stop + ' ' + ($wCalm -join ' | '))
    $slowStates = New-Object System.Collections.ArrayList
    [void]$slowStates.Add([ordered]@{ t_s = 1.0; state = 'normal' })
    [void]$slowStates.Add([ordered]@{ t_s = 90.0; state = 'minimized' })
    $wSlow = New-Object System.Collections.ArrayList
    $secSlow = Get-RecordSection $plan $events $zero 130.0 $main $child $total 8 $wSlow $slowStates
    Assert-That ($secSlow.periods.window_state.at_stop -eq 'minimized' -and $null -eq $secSlow.periods.after_stop_returns_to_user_interval -and $secSlow.periods.note -match 'slow mode') ('a minimized window: ' + $secSlow.periods.window_state.at_stop + ' / ' + $secSlow.periods.note)
    Assert-That (@($wSlow | Where-Object { $_ -match 'minimized or hidden during the run' }).Count -eq 1) ('no slow-mode warning: ' + ($wSlow -join ' | '))
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
            $winState = Get-WindowState $proc
            Assert-That ($winState -eq 'normal') ('the shown GUI window must read as normal, not ' + $winState)
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

# ---- the forms (C35, C36): -Form party | legacy | both. None of these checks starts PCMieru, PartsLife or PartsParty. ----

function Invoke-ChildMeasure([string[]]$Arguments, [string]$Tag) {
    $o = Join-Path $WorkDir ($Tag + '.out')
    $e = Join-Path $WorkDir ($Tag + '.err')
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $ScriptUnderTest + '"')) + $Arguments
    $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -RedirectStandardOutput $o -RedirectStandardError $e -Wait -PassThru -WindowStyle Hidden
    $err = ''
    if (Test-Path -LiteralPath $e) { $err = [IO.File]::ReadAllText($e) }
    return @{ Code = $p.ExitCode; Err = $err }
}

Check 'forms: the pair order alternates (party legacy, legacy party, ...); every other form repeats itself' {
    $a = @(Get-FormPlan 'party' $true 3)
    Assert-That (($a -join ',') -eq 'party,legacy,legacy,party,party,legacy') ('party + legacy: ' + ($a -join ','))
    $b = @(Get-FormPlan 'party' $false 2)
    Assert-That (($b -join ',') -eq 'party,party') ('party alone: ' + ($b -join ','))
    $c = @(Get-FormPlan 'both' $false 2)
    Assert-That (($c -join ',') -eq 'both,both') ('both: ' + ($c -join ','))
    $d = @(Get-FormPlan 'legacy' $true 2)
    Assert-That (($d -join ',') -eq 'legacy,legacy') ('legacy: ' + ($d -join ','))
    $e = @(Get-FormPlan 'party' $true 1)
    Assert-That (($e -join ',') -eq 'party,legacy') ('one pair: ' + ($e -join ','))
}

Check 'forms: the old-app gate refuses a single-file bundle, a missing dll, a dll that is not the old app, and a missing checker' {
    $d = New-WorkSubDir 'legacy-gate'
    $exe = Join-Path $d 'PartsLife.exe'
    [IO.File]::WriteAllBytes($exe, (New-Object byte[] 2200000))
    $big = Test-LegacyGate $exe
    Assert-That ((-not $big.ok) -and ($big.line -like 'legacy-gate refuse:*above 2 MB*')) ('a big exe: ' + $big.line)
    [IO.File]::WriteAllBytes($exe, [byte[]](1, 2, 3))
    $noDll = Test-LegacyGate $exe
    Assert-That ((-not $noDll.ok) -and ($noDll.line -like 'legacy-gate refuse:*PartsLife.dll is missing*')) ('no dll: ' + $noDll.line)
    [IO.File]::WriteAllBytes((Join-Path $d 'PartsLife.dll'), [byte[]](4, 5, 6))
    $keep = $script:LegacyGatePath
    try {
        $script:LegacyGatePath = Join-Path $d 'no-such-checker.cs'
        $noChecker = Test-LegacyGate $exe
        Assert-That ((-not $noChecker.ok) -and ($noChecker.line -like 'legacy-gate refuse:*checker was not found*')) ('no checker: ' + $noChecker.line)
    }
    finally { $script:LegacyGatePath = $keep }
    $threw = $false
    try { $null = Assert-LegacySafe $exe } catch { $threw = ($_.Exception.Message -like 'refused*') }
    Assert-That $threw 'a folder that does not pass the gate must be refused'
    if (-not (Test-Path -LiteralPath $script:LegacyGatePath -PathType Leaf)) { return 'SKIP the checker file is not next to this script' }
    # three bytes are not a managed assembly: the real checker must say refuse, never ok
    $junk = Test-LegacyGate $exe
    Assert-That ((-not $junk.ok) -and ($junk.line -like 'legacy-gate refuse:*')) ('junk dll: ' + $junk.line)
}

Check 'forms: the old app gets exactly two PARTSLIFE_ variables, whatever this process has' {
    $old = $env:PARTSLIFE_SELFTEST_EXTRA
    $env:PARTSLIFE_SELFTEST_EXTRA = 'must not reach the old app'
    try {
        $psi = New-LegacyStartInfo 'C:\fake\PartsLife.exe' 'C:\fake\data'
        $names = @($psi.EnvironmentVariables.Keys | ForEach-Object { [string]$_ } | Where-Object { $_ -like 'PARTSLIFE_*' } | Sort-Object)
        Assert-That (($names -join ',') -eq 'PARTSLIFE_TEST_DATA_DIR,PARTSLIFE_UPDATE_TEST_FEED') ('names: ' + ($names -join ','))
        Assert-That ($psi.EnvironmentVariables['PARTSLIFE_TEST_DATA_DIR'] -eq 'C:\fake\data') 'the data folder variable'
        Assert-That ($psi.EnvironmentVariables['PARTSLIFE_UPDATE_TEST_FEED'] -eq 'http://127.0.0.1:9/') 'the feed variable'
        Assert-That (-not $psi.UseShellExecute) 'no shell execute (the variables only work without it)'
        Assert-That ($env:PARTSLIFE_SELFTEST_EXTRA -eq 'must not reach the old app') 'this script''s own environment must not change'
    }
    finally {
        if ($null -eq $old) { Remove-Item -LiteralPath Env:PARTSLIFE_SELFTEST_EXTRA -ErrorAction SilentlyContinue } else { $env:PARTSLIFE_SELFTEST_EXTRA = $old }
    }
}

Check 'forms: the old app unpacks into the measurement''s own folder (DOTNET_BUNDLE_EXTRACT_BASE_DIR), never an inherited one' {
    $old = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = 'C:\inherited\must-not-reach-the-old-app'
    try {
        $with = New-LegacyStartInfo 'C:\fake\PartsLife.exe' 'C:\fake\data' 'C:\out\bundle-extract\'
        Assert-That ($with.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] -eq 'C:\out\bundle-extract') ('with a base: ' + $with.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'])
        $without = New-LegacyStartInfo 'C:\fake\PartsLife.exe' 'C:\fake\data'
        Assert-That (-not $without.EnvironmentVariables.ContainsKey('DOTNET_BUNDLE_EXTRACT_BASE_DIR')) 'without a base the inherited value must be removed, not passed on'
        $names = @($with.EnvironmentVariables.Keys | ForEach-Object { [string]$_ } | Where-Object { $_ -like 'PARTSLIFE_*' } | Sort-Object)
        Assert-That (($names -join ',') -eq 'PARTSLIFE_TEST_DATA_DIR,PARTSLIFE_UPDATE_TEST_FEED') 'still exactly two PARTSLIFE_ variables'
    }
    finally {
        if ($null -eq $old) { Remove-Item -LiteralPath Env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -ErrorAction SilentlyContinue } else { $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $old }
    }
    $listing = @(Get-TempDotNetListing)
    Assert-That ($listing.Count -ge 0) 'the %TEMP%\.net listing must be readable (empty when there is none)'
}

# A fake checker that prints the line it is told to (FAKE_GATE_LINE): it tests what measure.ps1 does with each answer, not the checker.
Check 'forms: an exe above 2 MB is allowed only when the checker read it as a single-file bundle (fail closed)' {
    $d = New-WorkSubDir 'gate-answers'
    $fake = Join-Path $d 'fake-gate.cs'
    [IO.File]::WriteAllText($fake, 'var line = Environment.GetEnvironmentVariable("FAKE_GATE_LINE") ?? "legacy-gate refuse: no line"; Console.WriteLine(line); return line.StartsWith("legacy-gate ok") ? 0 : 1;')
    $big = Join-Path $d 'big\PartsLife.exe'
    New-Item -ItemType Directory -Path (Split-Path -Parent $big) | Out-Null
    [IO.File]::WriteAllBytes($big, (New-Object byte[] 2200000))
    $small = Join-Path $d 'small\PartsLife.exe'
    New-Item -ItemType Directory -Path (Split-Path -Parent $small) | Out-Null
    [IO.File]::WriteAllBytes($small, [byte[]](1, 2, 3))
    [IO.File]::WriteAllBytes((Join-Path (Split-Path -Parent $small) 'PartsLife.dll'), [byte[]](4, 5, 6))
    $keepPath = $script:LegacyGatePath
    $keepLine = $env:FAKE_GATE_LINE
    try {
        $script:LegacyGatePath = $fake
        $env:FAKE_GATE_LINE = 'legacy-gate ok'
        $a = Test-LegacyGate $big
        Assert-That ((-not $a.ok) -and ($a.line -like 'legacy-gate refuse:*above 2 MB*')) ('a big exe the checker did not read as a bundle must be refused: ' + $a.line)
        $env:FAKE_GATE_LINE = 'legacy-gate ok (single-file bundle)'
        $b = Test-LegacyGate $big
        Assert-That ($b.ok -and $b.mode -eq 'bundle') ('a big exe read as a bundle is allowed: ' + $b.line)
        $env:FAKE_GATE_LINE = 'legacy-gate refuse: the bundle has 2 entries named PartsLife.dll (exactly one is needed)'
        $c = Test-LegacyGate $big
        Assert-That ((-not $c.ok) -and ($c.line -like '*above 2 MB*') -and ($c.line -like '*exactly one is needed*')) ('a refused big exe: ' + $c.line)
        $env:FAKE_GATE_LINE = 'legacy-gate OK (single-file bundle)'
        $d1 = Test-LegacyGate $big
        Assert-That (-not $d1.ok) ('the ok line is compared exactly (case): ' + $d1.line)
        $env:FAKE_GATE_LINE = 'legacy-gate ok (single-file bundle) and more'
        $d2 = Test-LegacyGate $big
        Assert-That (-not $d2.ok) ('the ok line is compared exactly (nothing after it): ' + $d2.line)
        $env:FAKE_GATE_LINE = 'legacy-gate ok'
        $e = Test-LegacyGate $small
        Assert-That ($e.ok -and $e.mode -eq 'folder') ('a small exe with its dll beside it passes in the folder form: ' + $e.line)
        $env:FAKE_GATE_LINE = 'something else entirely'
        $f = Test-LegacyGate $small
        Assert-That (-not $f.ok) ('an answer that is not an ok line refuses: ' + $f.line)
    }
    finally {
        $script:LegacyGatePath = $keepPath
        if ($null -eq $keepLine) { Remove-Item -LiteralPath Env:FAKE_GATE_LINE -ErrorAction SilentlyContinue } else { $env:FAKE_GATE_LINE = $keepLine }
    }
}

# Fake single-file bundles (the layout of bundle version 6) built here, and the real checker run on them: every broken shape must refuse.
function New-FakeBundle([string]$Path, $Entries, [uint32]$Major = 6, [int]$Signatures = 1, [switch]$BadOffset) {
    $sig = [byte[]](0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32, 0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae)
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter($ms)
    $w.Write((New-Object byte[] 64))
    $marker = $ms.Position
    $w.Write([long]0)
    $w.Write($sig)
    if ($Signatures -gt 1) { $w.Write([long]0); $w.Write($sig) }
    $w.Write((New-Object byte[] 16))
    $placed = @()
    foreach ($e in $Entries) {
        $bytes = [byte[]]$e.Bytes
        $stored = $bytes
        $compressed = [long]0
        if ($e["Deflate"]) {
            $zs = New-Object IO.MemoryStream
            $ds = New-Object IO.Compression.DeflateStream($zs, [IO.Compression.CompressionMode]::Compress, $true)
            $ds.Write($bytes, 0, $bytes.Length); $ds.Dispose()
            $stored = $zs.ToArray()
            $compressed = [long]$stored.Length
        }
        $at = $ms.Position
        $w.Write($stored)
        $size = [long]$bytes.Length
        if ($null -ne $e["ClaimSize"]) { $size = [long]$e["ClaimSize"] }
        $placed += , @{ Offset = $at; Size = $size; Compressed = $compressed; Type = $(if ($null -ne $e["Type"]) { [byte]$e["Type"] } else { [byte]1 }); Path = $e.Path }
    }
    $header = $ms.Position
    $w.Write([uint32]$Major); $w.Write([uint32]0); $w.Write([int]$placed.Count); $w.Write('selftest-bundle-id')
    $w.Write([long]0); $w.Write([long]0); $w.Write([long]0); $w.Write([long]0); $w.Write([uint64]0)
    foreach ($p in $placed) { $w.Write([long]$p.Offset); $w.Write([long]$p.Size); $w.Write([long]$p.Compressed); $w.Write([byte]$p.Type); $w.Write([string]$p.Path) }
    $w.Flush()
    $ms.Position = $marker
    $w.Write($(if ($BadOffset) { [long]($ms.Length + 1000) } else { [long]$header }))
    $w.Flush()
    [IO.File]::WriteAllBytes($Path, $ms.ToArray())
}

function Invoke-GateChecker([string]$Target) {
    $out = & dotnet run --file $script:LegacyGatePath -- $Target 2>$null
    return ([string](@($out) | Select-Object -Last 1)).Trim()
}

Check 'legacy-gate.cs: a single-file bundle is read, and every broken shape of it is refused' {
    if (-not (Test-Path -LiteralPath $script:LegacyGatePath -PathType Leaf)) { return 'SKIP the checker file is not next to this script' }
    if ($null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)) { return 'SKIP dotnet was not found' }
    $d = New-WorkSubDir 'fake-bundles'
    $junk = [byte[]](1..200 | ForEach-Object { $_ % 251 })
    $two = @(@{ Path = 'PartsLife.dll'; Bytes = $junk }, @{ Path = 'PartsLife.Update.dll'; Bytes = $junk })
    $cases = @(
        @{ Name = 'two junk entries (not assemblies)'; Entries = $two; Expect = 'legacy-gate refuse:*' ; Not = '*bundle*entries*' },
        @{ Name = 'two junk entries, deflated'; Entries = @(@{ Path = 'PartsLife.dll'; Bytes = $junk; Deflate = $true }, @{ Path = 'PartsLife.Update.dll'; Bytes = $junk; Deflate = $true }); Expect = 'legacy-gate refuse:*'; Not = '*inflates to*' },
        @{ Name = 'PartsLife.dll twice'; Entries = @($two[0], $two[0], $two[1]); Expect = '*2 entries named PartsLife.dll*' },
        @{ Name = 'PartsLife.Update.dll missing'; Entries = @($two[0]); Expect = '*0 entries named PartsLife.Update.dll*' },
        @{ Name = 'an entry that inflates to another size'; Entries = @(@{ Path = 'PartsLife.dll'; Bytes = $junk; Deflate = $true; ClaimSize = 500 }, $two[1]); Expect = '*inflates to*' },
        @{ Name = 'an unfamiliar entry kind'; Entries = @(@{ Path = 'PartsLife.dll'; Bytes = $junk; Type = 9 }, $two[1]); Expect = '*unfamiliar bundle entry kind*' },
        @{ Name = 'an entry that is not an assembly'; Entries = @(@{ Path = 'PartsLife.dll'; Bytes = $junk; Type = 2 }, $two[1]); Expect = '*is not an assembly*' },
        @{ Name = 'an unfamiliar bundle version'; Entries = $two; Major = 7; Expect = '*unfamiliar bundle version*' },
        @{ Name = 'the signature twice'; Entries = $two; Signatures = 2; Expect = '*in the file 2 times*' },
        @{ Name = 'a header offset outside the file'; Entries = $two; BadOffset = $true; Expect = '*outside the file*' }
    )
    $n = 0
    foreach ($c in $cases) {
        $n++
        $path = Join-Path $d ('b' + $n + '.exe')
        $major = $(if ($null -ne $c["Major"]) { [uint32]$c["Major"] } else { [uint32]6 })
        $sigs = $(if ($null -ne $c["Signatures"]) { [int]$c["Signatures"] } else { 1 })
        New-FakeBundle $path $c.Entries $major $sigs -BadOffset:([bool]$c["BadOffset"])
        $line = Invoke-GateChecker $path
        Assert-That (($line -like 'legacy-gate refuse:*') -and ($line -like $c.Expect)) ($c.Name + ': ' + $line)
        if ($c["Not"]) { Assert-That ($line -notlike $c["Not"]) ($c.Name + ' stopped too early: ' + $line) }
    }
    # a folder whose PartsLife.exe is a bundle is refused (the exe must be given itself); a missing target is refused
    $bundleFolder = New-WorkSubDir 'bundle-folder'
    New-FakeBundle (Join-Path $bundleFolder 'PartsLife.exe') $two
    $line = Invoke-GateChecker $bundleFolder
    Assert-That ($line -like 'legacy-gate refuse:*give the exe itself*') ('a folder with a bundle exe: ' + $line)
    $line = Invoke-GateChecker (Join-Path $d 'no-such.exe')
    Assert-That ($line -like 'legacy-gate refuse:*not found*') ('a missing target: ' + $line)
}

# The positive path needs the real old app: set MEASURE_SELFTEST_LEGACY_FOLDER to a Debug build folder of v0.7.4 (PartsLife.dll and PartsLife.Update.dll in it). Fake bundles are
# made of those two files, plain and deflated, and must be read as "ok (single-file bundle)"; the same with the Release build must refuse.
Check 'legacy-gate.cs: a bundle made of the real Debug build passes, plain and deflated (optional: MEASURE_SELFTEST_LEGACY_FOLDER)' {
    $folder = $env:MEASURE_SELFTEST_LEGACY_FOLDER
    if ([string]::IsNullOrEmpty($folder) -or -not (Test-Path -LiteralPath (Join-Path $folder 'PartsLife.dll'))) { return 'SKIP MEASURE_SELFTEST_LEGACY_FOLDER is not set' }
    if (-not (Test-Path -LiteralPath $script:LegacyGatePath -PathType Leaf)) { return 'SKIP the checker file is not next to this script' }
    $d = New-WorkSubDir 'real-bundles'
    $main = [IO.File]::ReadAllBytes((Join-Path $folder 'PartsLife.dll'))
    $upd = [IO.File]::ReadAllBytes((Join-Path $folder 'PartsLife.Update.dll'))
    $extra = [byte[]](1..50)
    New-FakeBundle (Join-Path $d 'plain.exe') @(@{ Path = 'Other.dll'; Bytes = $extra }, @{ Path = 'PartsLife.dll'; Bytes = $main }, @{ Path = 'PartsLife.Update.dll'; Bytes = $upd })
    New-FakeBundle (Join-Path $d 'deflated.exe') @(@{ Path = 'PartsLife.dll'; Bytes = $main; Deflate = $true }, @{ Path = 'Other.dll'; Bytes = $extra; Deflate = $true }, @{ Path = 'PartsLife.Update.dll'; Bytes = $upd; Deflate = $true })
    foreach ($n in @('plain', 'deflated')) {
        $line = Invoke-GateChecker (Join-Path $d ($n + '.exe'))
        Assert-That ($line -ceq 'legacy-gate ok (single-file bundle)') ($n + ': ' + $line)
    }
}

Check 'forms: Party is started with --measure and the run folder, and nothing else' {
    $psi = New-PartyStartInfo 'C:\fake\Party\PCMieru.Party.exe' 'C:\fake data\run1\'
    Assert-That ($psi.Arguments -eq '--measure "C:\fake data\run1"') ('arguments: ' + $psi.Arguments)
    Assert-That ((-not $psi.UseShellExecute) -and $psi.RedirectStandardError) 'no shell execute, stderr kept'
}

Check 'forms: a run data folder is new, unique and under the temp folder; a folder or a file that exists is never reused or overwritten' {
    $one = New-FormDataDir 'party-01'
    try {
        Assert-That (Test-Path -LiteralPath $one) 'the folder was not made'
        $temp = [IO.Path]::GetTempPath()
        Assert-That ($one.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) ('not under the temp folder: ' + $one)
        $two = New-FormDataDir 'party-01'
        Assert-That ($one -ne $two) 'two folders with one label must differ'
        [void](Remove-FormDataDir $two)
        [IO.File]::WriteAllText((Join-Path $one 'state.json'), '{}')
        Assert-That (Remove-FormDataDir $one) 'the removal did not report success'
        Assert-That (-not (Test-Path -LiteralPath $one)) 'the folder is still there'
    }
    finally { try { if (Test-Path -LiteralPath $one) { Remove-Item -LiteralPath $one -Recurse -Force } } catch { } }
    $d = New-WorkSubDir 'reserve'
    $run = Join-Path $d 'party-01'
    $null = New-ReservedFolder $run
    $threw = $false
    try { $null = New-ReservedFolder $run } catch { $threw = $true }
    Assert-That $threw 'a run folder that exists must be refused'
    $csv = Join-Path $run 'party-01.csv'
    $w = New-ExclusiveWriter $csv
    $w.Write('first'); $w.Dispose()
    $threw2 = $false
    try { $w2 = New-ExclusiveWriter $csv; $w2.Dispose() } catch { $threw2 = $true }
    Assert-That $threw2 'an existing CSV must not be overwritten'
    Assert-That (([IO.File]::ReadAllText($csv)) -eq 'first') 'the CSV was changed'
    $sum = Join-Path $d 'summary.json'
    Save-SummaryNew ([ordered]@{ a = 1 }) $sum
    $threw3 = $false
    try { Save-SummaryNew ([ordered]@{ a = 2 }) $sum } catch { $threw3 = $true }
    Assert-That $threw3 'an existing summary must not be overwritten'
}

Check 'forms: first-value.txt is read as milliseconds; a missing or unreadable one is null with a note' {
    $d = New-WorkSubDir 'first-value'
    $m = Read-FirstValueMs $d
    Assert-That (($null -eq $m.ms) -and ($m.note -like '*did not appear*')) 'a missing file must be null with a note'
    [IO.File]::WriteAllText((Join-Path $d 'first-value.txt'), "1234`n")
    $ok = Read-FirstValueMs $d
    Assert-That (($ok.ms -eq 1234) -and ($null -eq $ok.note)) ('one number: ' + $ok.ms)
    [IO.File]::WriteAllText((Join-Path $d 'first-value.txt'), 'abc')
    $bad = Read-FirstValueMs $d
    Assert-That (($null -eq $bad.ms) -and ($null -ne $bad.note)) 'text must be null with a note'
}

Check 'forms: the total is the sum of the process window means; a figure nobody had stays null' {
    $by = @{
        party = [ordered]@{ all = [ordered]@{ cpu_percent_mean = 0.5; cpu_ms_per_s_mean = 4; private_mb_mean = 30; working_set_mb_mean = 40; handles_mean = 200 } }
        gui = [ordered]@{ all = [ordered]@{ cpu_percent_mean = 0.25; cpu_ms_per_s_mean = $null; private_mb_mean = 50; working_set_mb_mean = $null; handles_mean = 100 } }
    }
    $t = Get-FormTotal $by 'all'
    Assert-That (($t.cpu_percent_mean -eq 0.75) -and ($t.cpu_ms_per_s_mean -eq 4) -and ($t.private_mb_mean -eq 80) -and ($t.working_set_mb_mean -eq 40) -and ($t.handles_mean -eq 300)) 'the sums'
    $none = Get-FormTotal $by 'steady'
    foreach ($k in $script:FormMetrics) { Assert-That ($null -eq $none[$k]) ('a window nobody has must be null: ' + $k) }
}

Check 'forms: a process that ended early or has a short steady window makes the run incomplete' {
    $full = [ordered]@{ samples = 30; complete = $true; cpu_intervals = 30; private_mb_mean = 20.0 }
    $ok = @{ Role = 'party'; EarlyExit = $null; Close = [ordered]@{ method = 'WM_CLOSE' } }
    Assert-That ($null -eq (Get-FormIncomplete $ok $full 30)) 'a full run is complete'
    Assert-That ($null -ne (Get-FormIncomplete $null $full 30)) 'a process that was never started is incomplete'
    $early = @{ Role = 'party'; EarlyExit = 12.0; Close = [ordered]@{ method = 'already-exited' } }
    Assert-That ((Get-FormIncomplete $early $full 30) -like '*ended by itself*') 'an early exit is incomplete'
    $late = @{ Role = 'party'; EarlyExit = $null; Close = [ordered]@{ method = 'already-exited' } }
    Assert-That ((Get-FormIncomplete $late $full 30) -like '*before the close*') 'gone before the close is incomplete'
    $short = [ordered]@{ samples = 20; complete = $true; cpu_intervals = 20; private_mb_mean = 20.0 }
    Assert-That ((Get-FormIncomplete $ok $short 30) -like '*20 of 30*') 'a short steady window is incomplete'
    $notEnd = [ordered]@{ samples = 30; complete = $false; cpu_intervals = 30; private_mb_mean = 20.0 }
    Assert-That ($null -ne (Get-FormIncomplete $ok $notEnd 30)) 'a window that stops short of the end is incomplete'
    $noCpu = [ordered]@{ samples = 30; complete = $true; cpu_intervals = 3; private_mb_mean = 20.0 }
    Assert-That ($null -ne (Get-FormIncomplete $ok $noCpu 30)) 'missing CPU readings are incomplete'
    Assert-That ($null -ne (Get-FormIncomplete $ok $null 30)) 'no steady window is incomplete'
}

Check 'forms: the aggregate keeps a form apart, leaves out incomplete runs and missing figures, and never makes a missing figure 0' {
    $win = { param($cpu, $priv) [ordered]@{ cpu_percent_mean = $cpu; cpu_ms_per_s_mean = $cpu * 10; private_mb_mean = $priv; working_set_mb_mean = $priv + 10; handles_mean = 100 } }
    $r1 = @{ form = 'party'; label = 'party-01'; complete = $true; processes = [ordered]@{ party = [ordered]@{ first_window_ms = 300; first_value_ms = 1500; windows = [ordered]@{ all = (& $win 1 20) } } }; total = [ordered]@{ all = (& $win 1 20) } }
    $r2 = @{ form = 'party'; label = 'party-02'; complete = $true; processes = [ordered]@{ party = [ordered]@{ first_window_ms = 500; first_value_ms = 1700; windows = [ordered]@{ all = (& $win 3 40) } } }; total = [ordered]@{ all = (& $win 3 40) } }
    $r2b = @{ form = 'party'; label = 'party-03'; complete = $false; processes = [ordered]@{ party = [ordered]@{ first_window_ms = 1; first_value_ms = 1; windows = [ordered]@{ all = (& $win 99 999) } } }; total = [ordered]@{ all = (& $win 99 999) } }
    $r3 = @{ form = 'legacy'; label = 'legacy-01'; complete = $true; processes = [ordered]@{ legacy = [ordered]@{ first_window_ms = 900; first_value_ms = $null; windows = [ordered]@{ all = (& $win 2 60) } } }; total = [ordered]@{ all = (& $win 2 60) } }
    $summary = [ordered]@{ aggregate = [ordered]@{} }
    Add-FormAggregate $summary @($r1, $r2, $r2b, $r3)
    $p = $summary.aggregate['party']
    Assert-That (($p.runs -eq 2) -and ($p.runs_started -eq 3)) ('two complete party runs of three: ' + $p.runs + ' of ' + $p.runs_started)
    Assert-That ((@($p.incomplete) -join ',') -eq 'party-03') 'the incomplete run is named'
    Assert-That ($p.processes['party'].first_value_ms.median -eq 1600) ('first-value median: ' + $p.processes['party'].first_value_ms.median)
    Assert-That ($p.processes['party'].all.private_mb_mean.median -eq 30) 'private median (the incomplete run is out)'
    Assert-That ($p.total['all'].cpu_ms_per_s_mean.median -eq 20) 'total cpu median'
    $l = $summary.aggregate['legacy']
    Assert-That (($l.processes['legacy'].first_value_ms.present -eq 0) -and ($null -eq $l.processes['legacy'].first_value_ms.median)) 'the old app has no first value: null, not 0'
}

Check 'forms: a differing window of Party and the old app is warned about' {
    $mk = { param($form, $w, $h, $state) @{ form = $form; processes = [ordered]@{ $form = [ordered]@{ window = [ordered]@{ end = [ordered]@{ width = $w; height = $h; state = $state } } } } } }
    $same = @(Get-WindowDiffWarnings @((& $mk 'party' 500 300 'normal'), (& $mk 'legacy' 500 300 'normal')))
    Assert-That ($same.Count -eq 0) 'equal windows: no warning'
    $diff = @(Get-WindowDiffWarnings @((& $mk 'party' 500 300 'normal'), (& $mk 'legacy' 800 600 'minimized')))
    Assert-That (($diff.Count -eq 1) -and ($diff[0] -like '*differ*')) 'different windows: one warning'
    $one = @(Get-WindowDiffWarnings @((& $mk 'party' 500 300 'normal')))
    Assert-That ($one.Count -eq 0) 'one form alone: no warning'
}

Check 'forms: a process with no window is closed by the wait and then killed, and one that is gone is recorded as such (no app is started)' {
    $p = [System.Diagnostics.Process]::Start((New-Object System.Diagnostics.ProcessStartInfo -Property @{ FileName = 'powershell.exe'; Arguments = '-NoProfile -Command Start-Sleep -Seconds 60'; UseShellExecute = $false; CreateNoWindow = $true }))
    try {
        $info = Close-ByWindows $p 1
        Assert-That ($info.method -eq 'Kill') ('a process with no window: ' + $info.method)
        Assert-That (-not (Test-Alive $p)) 'the process must be gone'
        Assert-That ($info.windows_posted -eq 0) 'nothing to post to'
        $again = Close-ByWindows $p 1
        Assert-That ($again.method -eq 'already-exited') ('a process that is gone: ' + $again.method)
    }
    finally { try { if (-not $p.HasExited) { $p.Kill() } } catch { }; Stop-Quietly $p }
    $g = Get-WindowGeometry ([int]$PID)
    Assert-That (@('normal', 'minimized', 'hidden', 'none') -contains $g.state) ('the window state of this shell: ' + $g.state)
}

Check 'forms: -Form arguments are checked before anything is started or made' {
    $out = Join-Path $WorkDir 'child-out'
    $a = Invoke-ChildMeasure @('-Form', 'party', '-Output', ('"' + $out + '"')) 'child-a'
    Assert-That (($a.Code -ne 0) -and ($a.Err -like '*PartyExe is required*')) ('party without -PartyExe: ' + $a.Code + ' ' + $a.Err)
    $b = Invoke-ChildMeasure @('-Form', 'both', '-Exe', ('"' + (Join-Path $WorkDir 'no-such.exe') + '"'), '-Output', ('"' + $out + '"')) 'child-b'
    Assert-That (($b.Code -ne 0) -and ($b.Err -like '*Exe not found*')) ('both with a missing -Exe: ' + $b.Code + ' ' + $b.Err)
    $c = Invoke-ChildMeasure @('-Form', 'main', '-Output', ('"' + $out + '"')) 'child-c'
    Assert-That (($c.Code -ne 0) -and ($c.Err -like '*Exe is required*')) ('main without -Exe is unchanged: ' + $c.Code + ' ' + $c.Err)
    $d = Invoke-ChildMeasure @('-Form', 'bogus') 'child-d'
    Assert-That ($d.Code -ne 0) 'an unknown form must be refused'
    $e = Invoke-ChildMeasure @('-Form', 'party', '-Mode', 'cold', '-Output', ('"' + $out + '"')) 'child-e'
    Assert-That (($e.Code -ne 0) -and ($e.Err -like '*warm*')) ('party with another mode: ' + $e.Code + ' ' + $e.Err)
    # the old app from a folder that does not pass the gate: refused, and no output folder was made
    $d2 = New-WorkSubDir 'child-legacy'
    $exe = Join-Path $d2 'PartsLife.exe'
    [IO.File]::WriteAllBytes($exe, [byte[]](1, 2, 3))
    [IO.File]::WriteAllBytes((Join-Path $d2 'PartsLife.dll'), [byte[]](4, 5, 6))
    $f = Invoke-ChildMeasure @('-Form', 'legacy', '-LegacyExe', ('"' + $exe + '"'), '-Output', ('"' + $out + '"')) 'child-f'
    Assert-That (($f.Code -ne 0) -and ($f.Err -like '*refused*')) ('legacy without the gate: ' + $f.Code + ' ' + $f.Err)
    Assert-That (-not (Test-Path -LiteralPath $out)) 'nothing may be made before the gate has passed'
}

Check 'forms: source pins (gate before the old app, registration right after Start, WM_CLOSE by windows, new folders, ASCII)' {
    $src = [IO.File]::ReadAllText($ScriptUnderTest)
    $from = $src.IndexOf('function Invoke-FormRun', [StringComparison]::Ordinal)
    $to = $src.IndexOf('function Write-FormRunReport', [StringComparison]::Ordinal)
    Assert-That ($from -gt 0 -and $to -gt $from) 'Invoke-FormRun was not found'
    $body = $src.Substring($from, $to - $from)
    $gate = $body.IndexOf('Assert-LegacySafe', [StringComparison]::Ordinal)
    $start = $body.IndexOf('New-LegacyStartInfo', [StringComparison]::Ordinal)
    Assert-That ($gate -gt 0 -and $start -gt $gate) 'the gate must come before the old app is started'
    Assert-That ($body.IndexOf('New-FormDataDir', [StringComparison]::Ordinal) -lt $start) 'the data folder is made before the old app is started'
    Assert-That ($body.IndexOf('New-ReservedFolder', [StringComparison]::Ordinal) -lt $start) 'the run folder is reserved before the old app is started'
    Assert-That ($body.IndexOf('Close-Politely', [StringComparison]::Ordinal) -lt 0) 'the forms close with Close-ByWindows (WM_CLOSE to every window of the pid), not CloseMainWindow'
    Assert-That ($body.IndexOf('Close-ByWindows', [StringComparison]::Ordinal) -gt 0) 'Close-ByWindows is not used'
    Assert-That ($body.IndexOf('Remove-FormDataDir', [StringComparison]::Ordinal) -gt $body.LastIndexOf('Test-Alive', [StringComparison]::Ordinal)) 'the data folder is removed only after every process was checked for exit'
    $sf = $src.IndexOf('function Start-FormProcess', [StringComparison]::Ordinal)
    $sfEnd = $src.IndexOf('function Add-FormChildren', [StringComparison]::Ordinal)
    $sfBody = $src.Substring($sf, $sfEnd - $sf)
    Assert-That ($sfBody.IndexOf('$Registry.Add', [StringComparison]::Ordinal) -gt 0 -and $sfBody.IndexOf('$Registry.Add', [StringComparison]::Ordinal) -lt $sfBody.IndexOf('New-Series', [StringComparison]::Ordinal)) 'a started process is registered before its series is made'
    Assert-That ($sfBody.IndexOf('Get-ClockPair', [StringComparison]::Ordinal) -gt 0) 'the series starts from one clock pair and the OS creation time'
    Assert-That ($src.IndexOf('PostCloseAll', [StringComparison]::Ordinal) -gt 0 -and $src.IndexOf('EnumWindows(delegate', [StringComparison]::Ordinal) -gt 0) 'PostCloseAll (EnumWindows by pid) is missing'
    Assert-That ($body.IndexOf('New-LegacyStartInfo $LegacyPath $dataDir $script:LegacyExtractBase', [StringComparison]::Ordinal) -gt 0) 'the old app is started with the bundle extract base of this invocation'
    $fm = $src.IndexOf('function Invoke-FormMain', [StringComparison]::Ordinal)
    $fmEnd = $src.IndexOf('function Invoke-Main', [StringComparison]::Ordinal)
    Assert-That ($fm -gt 0 -and $fmEnd -gt $fm) 'Invoke-FormMain was not found'
    $fmBody = $src.Substring($fm, $fmEnd - $fm)
    $mk = $fmBody.IndexOf('bundle-extract', [StringComparison]::Ordinal)
    $before = $fmBody.IndexOf('Get-TempDotNetListing', [StringComparison]::Ordinal)
    $after = $fmBody.LastIndexOf('Get-TempDotNetListing', [StringComparison]::Ordinal)
    $loop = $fmBody.IndexOf('Invoke-FormRun', [StringComparison]::Ordinal)
    Assert-That ($mk -gt 0 -and $mk -lt $loop) 'the bundle extract folder is made inside the output before the first run'
    Assert-That ($before -gt 0 -and $before -lt $loop -and $after -gt $loop) '%TEMP%\.net is listed before the first run and after the last'
    Assert-That ($fmBody.IndexOf('Remove-FormDataDir $extractBase', [StringComparison]::Ordinal) -gt $loop) 'the bundle extract folder is removed at the end'
    $gb = $src.IndexOf('function Test-LegacyGate', [StringComparison]::Ordinal)
    $gbEnd = $src.IndexOf('function Assert-LegacySafe', [StringComparison]::Ordinal)
    $gateBody = $src.Substring($gb, $gbEnd - $gb)
    Assert-That ($gateBody.IndexOf("-ceq 'legacy-gate ok (single-file bundle)'", [StringComparison]::Ordinal) -gt 0 -and $gateBody.IndexOf('LegacyGateMaxExeBytes', [StringComparison]::Ordinal) -gt 0) 'the 2 MB rule and the exact bundle line are both in the gate'
    foreach ($ch in $src.ToCharArray()) { if ([int]$ch -gt 127) { throw 'measure.ps1 must be ASCII only' } }
}

try { Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction Stop } catch { }

Write-Host ('selftest: {0} passed, {1} failed, {2} skipped' -f $script:Passed, $script:Failed, $script:Skipped) -ForegroundColor $(if ($script:Failed -eq 0) { 'Green' } else { 'Red' })
if ($script:Failed -gt 0) { exit 1 }
exit 0
