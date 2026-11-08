<#
.SYNOPSIS
  Builds the PC Mieru release folder, zip and SHA256.txt.

.DESCRIPTION
  1. Publishes Gui, Cli and SensorHost one by one (self-contained, ReadyToRun) into separate work folders.
  2. Merges them into one folder. A file that exists in more than one work folder must be byte-identical
     (SHA256); if it is not, the script stops. Nothing is overwritten silently.
  3. Publishes the Helper with NativeAOT and checks the exe itself: DependentLoadFlags must contain
     LOAD_LIBRARY_SEARCH_SYSTEM32 (0x800) and the static imports must be system DLLs only.
  4. Zips the folder and writes SHA256.txt next to it.
  5. With -Verify, runs the merged folder: pcmi version / ps / hw, the MCP handshake, and PCMieru.exe --smoke.

  Works in Windows PowerShell 5.1 and pwsh. ASCII only on purpose (5.1 reads BOM-less files as ANSI).

.EXAMPLE
  powershell -File tools\publish.ps1 -Version 0.1.0 -Verify
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$Output = '',
    [string]$Runtime = 'win-x64',
    [switch]$Verify,
    [switch]$SkipZip,
    [int]$HelperMaxKB = 8192
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($Output)) { $Output = Join-Path $root 'dist' }
$Output = [IO.Path]::GetFullPath($Output)

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Version must look like 1.2.3 (got '$Version')" }

# Imports a NativeAOT Helper may have. Everything in here is resolved from System32 once DependentLoadFlags has 0x800.
# Extend it when the Helper really needs another system DLL, and say why in the commit.
$AllowedHelperImports = @(
    'kernel32.dll', 'kernelbase.dll', 'ntdll.dll', 'advapi32.dll', 'user32.dll', 'shell32.dll', 'ole32.dll',
    'oleaut32.dll', 'ws2_32.dll', 'bcrypt.dll', 'crypt32.dll', 'ucrtbase.dll', 'rpcrt4.dll', 'sechost.dll',
    'version.dll', 'wtsapi32.dll', 'setupapi.dll', 'cfgmgr32.dll'
)
$AllowedHelperImportPatterns = @('^api-ms-win-[a-z0-9-]+-l\d+-\d+-\d+\.dll$', '^ext-ms-win-[a-z0-9-]+-l\d+-\d+-\d+\.dll$')

function Write-Step([string]$Text) { Write-Host ('== ' + $Text) -ForegroundColor Cyan }

function Invoke-Dotnet([string[]]$DotnetArgs) {
    Write-Host ('dotnet ' + ($DotnetArgs -join ' '))
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($DotnetArgs[0]) failed with exit code $LASTEXITCODE" }
}

# NativeAOT links with the MSVC toolchain, and the ILCompiler package finds it through vswhere.exe.
function Add-VswhereToPath {
    if (Get-Command vswhere.exe -ErrorAction SilentlyContinue) { return }
    $pf86 = ${env:ProgramFiles(x86)}
    if (-not $pf86) { return }
    $installer = Join-Path $pf86 'Microsoft Visual Studio\Installer'
    if (Test-Path -LiteralPath (Join-Path $installer 'vswhere.exe')) {
        $env:PATH = $installer + ';' + $env:PATH
        Write-Host ('vswhere added to PATH: ' + $installer)
    }
}

if (-not ('PcMieru.Build.PeInfo' -as [type])) {
    # C# 5 only: Windows PowerShell 5.1 compiles this with the old csc.
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PcMieru.Build
{
    public sealed class PeInfo
    {
        public bool HasLoadConfig;
        public int DependentLoadFlags;
        public bool IsManaged;
        public List<string> Imports = new List<string>();
        public List<string> DelayImports = new List<string>();

        private byte[] b;
        private int sectionTable;
        private int sectionCount;

        public static PeInfo Read(string path)
        {
            PeInfo info = new PeInfo();
            info.Parse(File.ReadAllBytes(path));
            return info;
        }

        private void Parse(byte[] data)
        {
            b = data;
            if (b.Length < 0x40 || b[0] != (byte)'M' || b[1] != (byte)'Z') throw new InvalidDataException("not a PE file (MZ)");
            int pe = BitConverter.ToInt32(b, 0x3C);
            if (pe < 0 || pe + 24 > b.Length || BitConverter.ToUInt32(b, pe) != 0x00004550u) throw new InvalidDataException("not a PE file (PE signature)");
            sectionCount = BitConverter.ToUInt16(b, pe + 6);
            int optionalSize = BitConverter.ToUInt16(b, pe + 20);
            int opt = pe + 24;
            if (BitConverter.ToUInt16(b, opt) != 0x20B) throw new InvalidDataException("expected a PE32+ (64-bit) image");
            sectionTable = opt + optionalSize;
            int dirCount = BitConverter.ToInt32(b, opt + 108);
            int dirs = opt + 112;

            uint importRva = Dir(dirs, dirCount, 1);
            if (importRva != 0)
            {
                int off = Offset(importRva);
                while (off + 20 <= b.Length)
                {
                    uint nameRva = BitConverter.ToUInt32(b, off + 12);
                    uint thunk = BitConverter.ToUInt32(b, off + 16);
                    if (nameRva == 0 && thunk == 0) break;
                    Imports.Add(Ascii(Offset(nameRva)));
                    off += 20;
                }
            }

            uint delayRva = Dir(dirs, dirCount, 13);
            if (delayRva != 0)
            {
                int off = Offset(delayRva);
                while (off + 32 <= b.Length)
                {
                    uint nameRva = BitConverter.ToUInt32(b, off + 4);
                    if (nameRva == 0) break;
                    DelayImports.Add(Ascii(Offset(nameRva)));
                    off += 32;
                }
            }

            IsManaged = Dir(dirs, dirCount, 14) != 0;

            uint cfgRva = Dir(dirs, dirCount, 10);
            if (cfgRva != 0)
            {
                int off = Offset(cfgRva);
                uint size = BitConverter.ToUInt32(b, off);
                // IMAGE_LOAD_CONFIG_DIRECTORY64: DependentLoadFlags is the WORD at offset 78.
                if (size >= 80)
                {
                    HasLoadConfig = true;
                    DependentLoadFlags = BitConverter.ToUInt16(b, off + 78);
                }
            }
        }

        private uint Dir(int dirs, int count, int index)
        {
            if (index >= count) return 0;
            return BitConverter.ToUInt32(b, dirs + 8 * index);
        }

        private int Offset(uint rva)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                int s = sectionTable + 40 * i;
                uint virtualSize = BitConverter.ToUInt32(b, s + 8);
                uint virtualAddress = BitConverter.ToUInt32(b, s + 12);
                uint rawSize = BitConverter.ToUInt32(b, s + 16);
                uint rawPointer = BitConverter.ToUInt32(b, s + 20);
                uint span = Math.Max(virtualSize, rawSize);
                if (rva >= virtualAddress && rva < virtualAddress + span) return (int)(rva - virtualAddress + rawPointer);
            }
            throw new InvalidDataException("RVA 0x" + rva.ToString("X") + " is outside every section");
        }

        private string Ascii(int offset)
        {
            int end = offset;
            while (end < b.Length && b[end] != 0) end++;
            return Encoding.ASCII.GetString(b, offset, end - offset);
        }
    }
}
'@
}

function Test-HelperExe([string]$Path) {
    $pe = [PcMieru.Build.PeInfo]::Read($Path)
    $problems = New-Object System.Collections.Generic.List[string]
    if ($pe.IsManaged) { $problems.Add('the exe has a CLR header: it is not a NativeAOT image') }
    if (-not $pe.HasLoadConfig) {
        $problems.Add('no load configuration directory: /DEPENDENTLOADFLAG was not applied')
    } elseif (($pe.DependentLoadFlags -band 0x800) -ne 0x800) {
        $problems.Add(('DependentLoadFlags is 0x{0:X}, LOAD_LIBRARY_SEARCH_SYSTEM32 (0x800) is missing' -f $pe.DependentLoadFlags))
    }
    $all = @($pe.Imports) + @($pe.DelayImports)
    foreach ($name in $all) {
        $lower = $name.ToLowerInvariant()
        $ok = $AllowedHelperImports -contains $lower
        if (-not $ok) { foreach ($p in $AllowedHelperImportPatterns) { if ($lower -match $p) { $ok = $true; break } } }
        if (-not $ok) { $problems.Add("unexpected import: $name (extend `$AllowedHelperImports only if it is a System32 DLL the Helper truly needs)") }
    }
    Write-Host ('Helper imports : ' + ((@($pe.Imports) | Sort-Object) -join ', '))
    if (@($pe.DelayImports).Count -gt 0) { Write-Host ('Helper delay   : ' + ((@($pe.DelayImports) | Sort-Object) -join ', ')) }
    Write-Host ('Helper load flags: 0x{0:X}' -f $pe.DependentLoadFlags)
    if ($problems.Count -gt 0) { throw ("Helper check failed:`n  " + ($problems -join "`n  ")) }
}

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Merge-Folders([string[]]$Sources, [string]$Destination, [string[]]$FirstWins) {
    $seen = @{}
    $conflicts = New-Object System.Collections.Generic.List[string]
    $first = @($FirstWins | ForEach-Object { $_.ToLowerInvariant() })
    foreach ($source in $Sources) {
        $base = (Resolve-Path -LiteralPath $source).Path.TrimEnd('\')
        foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File) {
            $relative = $file.FullName.Substring($base.Length + 1)
            if ($relative -like '*.pdb') { continue }
            $hash = Get-Sha256 $file.FullName
            $key = $relative.ToLowerInvariant()
            if ($seen.ContainsKey($key)) {
                if ($seen[$key].Hash -ne $hash) {
                    if ($first -contains $key) {
                        Write-Host ("kept {0} from {1} (differs from the {2} copy, allowed)" -f $relative, $seen[$key].From, (Split-Path -Leaf $base))
                    } else {
                        $conflicts.Add("$relative differs: $($seen[$key].From) vs $(Split-Path -Leaf $base)")
                    }
                }
                continue
            }
            $seen[$key] = @{ Hash = $hash; From = (Split-Path -Leaf $base) }
            $target = Join-Path $Destination $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
    if ($conflicts.Count -gt 0) {
        throw ("Same-named files with different content; refusing to merge:`n  " + ($conflicts -join "`n  "))
    }
    return $seen.Count
}

function Invoke-VerifyRun([string]$Folder) {
    $pcmi = Join-Path $Folder 'pcmi.exe'
    $gui = Join-Path $Folder 'PCMieru.exe'

    $text = & $pcmi version
    if ($LASTEXITCODE -ne 0 -or -not $text) { throw 'pcmi version failed' }
    Write-Host ('pcmi version   : ' + (@($text) -join ' '))

    foreach ($sub in @('ps', 'hw')) {
        $json = (& $pcmi $sub --json) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "pcmi $sub --json exited with $LASTEXITCODE" }
        $parsed = $json | ConvertFrom-Json
        if ($null -eq $parsed) { throw "pcmi $sub --json printed nothing parseable" }
        Write-Host ("pcmi $sub --json : {0} bytes, valid JSON" -f $json.Length)
    }

    $requests = @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"publish-verify","version":"0"}}}',
        '{"jsonrpc":"2.0","method":"notifications/initialized"}',
        '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
    )
    $lines = @($requests | & $pcmi mcp)
    if ($LASTEXITCODE -ne 0) { throw "pcmi mcp exited with $LASTEXITCODE" }
    $responses = @($lines | Where-Object { $_ -and $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    if ($responses.Count -ne 2) { throw "pcmi mcp answered $($responses.Count) messages, expected 2" }
    $tools = @($responses[1].result.tools)
    if ($tools.Count -lt 1) { throw 'pcmi mcp listed no tools' }
    Write-Host ('pcmi mcp       : initialize ok, {0} tools' -f $tools.Count)

    $smoke = Join-Path $Folder 'smoke-verify.json'
    $process = Start-Process -FilePath $gui -ArgumentList @('--smoke', ('"' + $smoke + '"')) -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "PCMieru.exe --smoke exited with $($process.ExitCode); see $smoke" }
    $report = Get-Content -LiteralPath $smoke -Raw | ConvertFrom-Json
    if (-not $report.ok) { throw "PCMieru.exe --smoke reported failures; see $smoke" }
    Write-Host ('PCMieru smoke  : ok, {0} checks, {1} dpi' -f @($report.checks).Count, $report.dpi)
    Remove-Item -LiteralPath $smoke -Force
}

# ---------------------------------------------------------------------------------------------------------------------

$name = "PCMieru-$Version-$Runtime"
$work = Join-Path $Output 'work'
$folder = Join-Path $Output $name
$zip = Join-Path $Output ($name + '.zip')

Write-Step "Clean $Output"
foreach ($path in @($work, $folder, $zip, (Join-Path $Output 'SHA256.txt'))) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $work, $folder | Out-Null

$common = @('-c', 'Release', '-r', $Runtime, "-p:Version=$Version", '-p:DebugType=none', '-p:DebugSymbols=false', '--nologo')

$apps = @(
    @{ Name = 'Gui'; Project = 'src\PcMieru.Gui\PcMieru.Gui.csproj' },
    @{ Name = 'Cli'; Project = 'src\PcMieru.Cli\PcMieru.Cli.csproj' },
    @{ Name = 'SensorHost'; Project = 'src\PcMieru.SensorHost\PcMieru.SensorHost.csproj' }
)
foreach ($app in $apps) {
    Write-Step ("Publish " + $app.Name)
    Invoke-Dotnet (@('publish', (Join-Path $root $app.Project)) + $common + @('-p:SelfContained=true', '-p:PublishReadyToRun=true', '-o', (Join-Path $work $app.Name)))
}

Write-Step 'Merge (same-named files must be identical)'
# Only the Gui references the Windows Desktop runtime, so its System.Drawing.dll and Microsoft.VisualBasic.dll are the
# Desktop pack's type-forward facades; the base pack's copies (what Cli and SensorHost publish) forward fewer types.
# Checked on the string tables: the Gui copy adds forwards to System.Windows.Forms and the VB forms types and drops only
# the obsolete PrintingPermission forwards, which nothing here uses. The Gui is merged first, so for these two names
# its copy wins. Every other same-named file must still be byte-identical.
$desktopFacades = @('System.Drawing.dll', 'Microsoft.VisualBasic.dll')
$count = Merge-Folders -Sources @($apps | ForEach-Object { Join-Path $work $_.Name }) -Destination $folder -FirstWins $desktopFacades
Write-Host ("merged $count files from $($apps.Count) publishes")

Write-Step 'Publish Helper (NativeAOT)'
Add-VswhereToPath
Invoke-Dotnet (@('publish', (Join-Path $root 'src\PcMieru.Helper\PcMieru.Helper.csproj')) + $common + @('-o', (Join-Path $work 'Helper')))
$helperExe = Join-Path (Join-Path $work 'Helper') 'PCMieru.Helper.exe'
if (-not (Test-Path -LiteralPath $helperExe)) { throw 'PCMieru.Helper.exe was not produced' }
$helperKB = [int]((Get-Item -LiteralPath $helperExe).Length / 1KB)
Write-Host "Helper size    : $helperKB KB"
if ($helperKB -gt $HelperMaxKB) { throw "Helper is $helperKB KB, over the $HelperMaxKB KB limit: something large got linked in" }
Test-HelperExe $helperExe
Copy-Item -LiteralPath $helperExe -Destination (Join-Path $folder 'PCMieru.Helper.exe')

foreach ($extra in @('LICENSE', 'NOTICE.txt')) {
    $source = Join-Path $root $extra
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $folder $extra) }
}

$expected = @('PCMieru.exe', 'pcmi.exe', 'PCMieru.SensorHost.exe', 'PCMieru.Helper.exe', 'PCMieru.Core.dll')
foreach ($file in $expected) {
    if (-not (Test-Path -LiteralPath (Join-Path $folder $file))) { throw "missing from the release folder: $file" }
}
if (Test-Path -LiteralPath (Join-Path $folder 'PCMieru.Helper.dll')) { throw 'the Helper must not ship as a managed dll' }

if ($Verify) {
    Write-Step 'Run the published folder'
    Invoke-VerifyRun $folder
}

$files = @(Get-ChildItem -LiteralPath $folder -Recurse -File)
$bytes = ($files | Measure-Object -Property Length -Sum).Sum
Write-Step 'Result'
Write-Host ('folder : {0}' -f $folder)
Write-Host ('files  : {0}' -f $files.Count)
Write-Host ('size   : {0:N1} MB' -f ($bytes / 1MB))

if (-not $SkipZip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $zipHash = Get-Sha256 $zip
    ('{0}  {1}' -f $zipHash, (Split-Path -Leaf $zip)) | Set-Content -LiteralPath (Join-Path $Output 'SHA256.txt') -Encoding ASCII
    Write-Host ('zip    : {0} ({1:N1} MB)' -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))
    Write-Host ('sha256 : {0}' -f $zipHash)
}

Remove-Item -LiteralPath $work -Recurse -Force
