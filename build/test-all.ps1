<#
.SYNOPSIS
    Runs every test the Proxy has: the unit and module tests (dotnet test), then the end-to-end replay of
    a live capture against the published executable (tools/e2e/live-replay.py). See docs/testing.md.

.DESCRIPTION
    Captures are git-ignored (they hold real usernames and IPs), so the replay runs only where one is
    present: by default the newest *.pcap and detail-* in the repository root. Without them the replay is
    skipped, visibly, and the unit tests alone decide the exit code.

    The RADIUS shared secret of the capture comes from -Secret or from $env:PANRA_REPLAY_SECRET: it
    is never stored in the repository.

    Exit code 0 when every step that ran passed, 1 otherwise.

.EXAMPLE
    .\build\test-all.ps1 -Secret testing123

.EXAMPLE
    .\build\test-all.ps1 -Quiet -Speed 60 -ReplayArgs '--logout-on-stop'

.EXAMPLE
    .\build\test-all.ps1 -SkipReplay
#>
[CmdletBinding()]
param(
    [string] $Pcap,
    [string] $Detail,
    [string] $Secret = $env:PANRA_REPLAY_SECRET,
    [double] $Speed = 30,

    # The site's domain rules, as the replay's options. The default matches docs/testing.md.
    [string[]] $DomainArgs = @('--nt4-domain', 'XDOMAIN', '--upn-suffix', 'xdomain.local=XDOMAIN', '--upn-suffix', 'example.com=XDOMAIN'),

    # Anything else for live-replay.py, e.g. '--logout-on-stop' or '--reject', 'guest'.
    [string[]] $ReplayArgs = @(),

    [switch] $SkipReplay,

    # Show only the replay's RESULT lines; the full transcript goes to artifacts\e2e\replay-console.log.
    [switch] $Quiet
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish\'
$results = [System.Collections.Generic.List[object]]::new()

function Invoke-Step([string] $Name, [scriptblock] $Body) {
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $code = & $Body
    }
    catch {
        Write-Host $_ -ForegroundColor Red
        $code = 'exception'
    }
    $results.Add([pscustomobject]@{ Step = $Name; Result = $(if ($code -eq 0) { 'passed' } else { "FAILED ($code)" }); Seconds = [int]$watch.Elapsed.TotalSeconds })
    return $code -eq 0
}

function Find-Newest([string] $Filter) {
    Get-ChildItem -Path $root -Filter $Filter -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}

# 1. Unit and module tests, in-process end-to-end test included.
$unitPassed = Invoke-Step 'Unit and module tests' {
    & dotnet test (Join-Path $root 'tests\PanRaProxy.Tests\PanRaProxy.Tests.csproj') --nologo | Out-Host
    $LASTEXITCODE
}

# 2. The replay, against the executable that ships.
if (-not $Pcap) { $Pcap = Find-Newest '*.pcap' }
if (-not $Detail) { $Detail = Find-Newest 'detail-*' }

$skipReason = if ($SkipReplay) { '-SkipReplay' }
              elseif (-not $Pcap -or -not $Detail) { 'no capture (*.pcap and detail-*) in the repository root' }
              elseif (-not $Secret) { 'no secret: pass -Secret or set $env:PANRA_REPLAY_SECRET' }

if ($skipReason) {
    Write-Warning "End-to-end replay skipped: $skipReason."
    $results.Add([pscustomobject]@{ Step = 'End-to-end replay'; Result = 'skipped'; Seconds = 0 })
}
elseif ($unitPassed) {
    $published = Invoke-Step 'Publish the executable' {
        & dotnet publish (Join-Path $root 'src\PanRaProxy\PanRaProxy.csproj') -c Release '-p:PublishSingleFile=true' -o $publish --nologo | Out-Host
        $LASTEXITCODE
    }

    if ($published) {
        # 'python', or the py launcher where only that is on the PATH.
        $python, $pythonArgs = if (Get-Command python -ErrorAction SilentlyContinue) { 'python', @() } else { 'py', @('-3') }
        $replay = $pythonArgs + @((Join-Path $root 'tools\e2e\live-replay.py'),
                    '--pcap', $Pcap, '--detail', $Detail, '--secret', $Secret, '--speed', $Speed,
                    '--exe', (Join-Path $publish 'PanRaProxy.exe')) + $DomainArgs + $ReplayArgs

        Write-Host "Capture: $(Split-Path -Leaf $Pcap) + $(Split-Path -Leaf $Detail), speed x$Speed"

        [void](Invoke-Step 'End-to-end replay' {
            if ($Quiet) {
                $log = Join-Path $root 'artifacts\e2e\replay-console.log'
                New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
                & $python @replay *> $log
                $code = $LASTEXITCODE
                Select-String -Path $log -Pattern ' RESULT ' | ForEach-Object { Write-Host $_.Line }
                Write-Host "Full transcript: $log"
                $code
            }
            else {
                & $python @replay | Out-Host
                $LASTEXITCODE
            }
        })
    }
}
else {
    Write-Warning 'End-to-end replay not run: the unit tests failed.'
    $results.Add([pscustomobject]@{ Step = 'End-to-end replay'; Result = 'not run'; Seconds = 0 })
}

Write-Host "`n=== Summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-Host

$failed = @($results | Where-Object { $_.Result -like 'FAILED*' -or $_.Result -eq 'not run' })
exit $(if ($failed.Count -eq 0) { 0 } else { 1 })
