<#
.SYNOPSIS
  Builds the harness that runs the OLD configuration code (parts-life 0.7.4, pl-cloud commit 25e4571) for the PCB1 goldens.
.DESCRIPTION
  Makes a `git archive` of the old revision's src/PartsLife.Checks and src/Shared in a temporary folder, copies this harness into it and builds it there.
  Nothing is built inside this repository or inside the old repository's working tree (only `git archive` reads the old one).
  The result is <Out>\out\Pcb1Golden.exe, where the RealMachine tests look for it (or in the PCMIERU_PCB1_HARNESS variable).
.PARAMETER OldRepo
  The git repository that holds the old revision (the owner's pl-cloud).
.PARAMETER Commit
  The old revision. Default: 25e4571 (parts-life 0.7.4).
.PARAMETER Out
  Where to put the copy and the build. Default: %TEMP%\pcmieru-pcb1-harness. It is deleted first, and can be deleted when you are done.
#>
param(
    [Parameter(Mandatory = $true)][string]$OldRepo,
    [string]$Commit = '25e4571',
    [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) 'pcmieru-pcb1-harness')
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$work = Join-Path $Out 'src'

if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null

$tar = Join-Path $Out 'old.tar'
# Only the two folders the harness needs (the old tree also holds Japanese file names that tar cannot extract).
& git -C $OldRepo archive --format=tar -o $tar $Commit src/PartsLife.Checks src/Shared
if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
# The Windows tar (bsdtar) on purpose: GNU tar from Git Bash reads "C:\..." as host:path.
$tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path -LiteralPath $tarExe)) { $tarExe = 'tar' }
& $tarExe -xf $tar -C $work
if ($LASTEXITCODE -ne 0) { throw 'tar failed' }
Remove-Item -LiteralPath $tar -Force

$dest = Join-Path $work 'tools\pcb1-golden'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -LiteralPath (Join-Path $here 'Program.cs'), (Join-Path $here 'Pcb1Golden.csproj') -Destination $dest

& dotnet build (Join-Path $dest 'Pcb1Golden.csproj') -c Release -o (Join-Path $Out 'out') --nologo
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

Write-Host ('harness: ' + (Join-Path $Out 'out\Pcb1Golden.exe'))
