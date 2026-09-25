<#
.SYNOPSIS
    Builds artifacts\msi\PanRaProxy.msi: tests, publishes the Proxy as one self-contained win-x64
    executable, then builds the WiX v5 package around it.

.EXAMPLE
    .\build\build-msi.ps1 -Version 1.0.1
#>
[CmdletBinding()]
param(
    [string] $Version = '1.0.0',
    [string] $Configuration = 'Release',
    [switch] $SkipTests
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish\'
$msi = Join-Path $root 'artifacts\msi\'

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

if (-not $SkipTests) {
    Invoke-Dotnet test (Join-Path $root 'PanRaProxy.sln') -c $Configuration
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Invoke-Dotnet publish (Join-Path $root 'src\PanRaProxy\PanRaProxy.csproj') `
    -c $Configuration "-p:PublishSingleFile=true" "-p:Version=$Version" -o $publish

# MSI validation (ICEs) runs VBScript, which newer Windows builds no longer ship (error 2738).
# Validate wherever VBScript exists (build servers); skip it, visibly, where it doesn't.
$validation = @()
if (-not (Test-Path (Join-Path $env:SystemRoot 'System32bscript.dll'))) {
    Write-Warning 'VBScript is not installed on this host: MSI ICE validation is skipped. Build release MSIs on a host with VBScript.'
    $validation = @('-p:SuppressValidation=true')
}

Invoke-Dotnet build (Join-Path $root 'src\PanRaProxy.Setup\PanRaProxy.Setup.wixproj') `
    -c $Configuration "-p:PayloadDir=$publish" -o $msi @validation

Get-ChildItem $msi -Filter *.msi | ForEach-Object { Write-Host "Built $($_.FullName)" }
