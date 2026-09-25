<#
.SYNOPSIS
    Sets, removes or lists the PAN RADIUS Accounting Proxy's secrets (RADIUS shared secrets, Firewall API key).

.DESCRIPTION
    Secrets are provided after installation, never through the MSI. Each secret is a file named after it in
    %ProgramData%\PanRaProxy\secrets. This script creates that folder with a protected ACL (SYSTEM and
    Administrators: full control; NT SERVICE\PanRaProxy: read; nothing inherited, so local users can't read it)
    and writes the secret atomically. Names must match the SecretName / ApiKeySecretName values in
    %ProgramData%\PanRaProxy\appsettings.json.

    Runs non-interactively when -Value is given, so a setup tool can call it after msiexec.
    Requires an elevated session. Exits with a non-zero code on any failure.

.EXAMPLE
    .\Set-PanRaProxySecret.ps1 -Name RADIUS_SECRET_NPS1
    Prompts for the value.

.EXAMPLE
    $key = Read-Host -AsSecureString
    .\Set-PanRaProxySecret.ps1 -Name PAN_API_KEY -Value $key -Restart
    Writes the API key, then starts or restarts the service so it picks it up.

.EXAMPLE
    .\Set-PanRaProxySecret.ps1 -List
    Shows which secrets are set (names only, never values).
#>
[CmdletBinding(DefaultParameterSetName = 'Set', SupportsShouldProcess = $true)]
param(
    [Parameter(ParameterSetName = 'Set', Mandatory = $true, Position = 0)]
    [Parameter(ParameterSetName = 'Remove', Mandatory = $true, Position = 0)]
    [ValidatePattern('^[A-Za-z0-9_\-][A-Za-z0-9_.\-]{0,127}$')]
    [string] $Name,

    [Parameter(ParameterSetName = 'Set', ValueFromPipeline = $true)]
    [System.Security.SecureString] $Value,

    [Parameter(ParameterSetName = 'Remove', Mandatory = $true)]
    [switch] $Remove,

    [Parameter(ParameterSetName = 'List', Mandatory = $true)]
    [switch] $List,

    # Start the service, or restart it if it is running, after the change.
    [Parameter(ParameterSetName = 'Set')]
    [Parameter(ParameterSetName = 'Remove')]
    [switch] $Restart
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$ServiceName = 'PanRaProxy'
$ServiceAccount = "NT SERVICE\$ServiceName"
$DataDirectory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'PanRaProxy'
$SecretsDirectory = Join-Path $DataDirectory 'secrets'

function Assert-Elevated {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated (Administrator) session.'
    }
}

function Get-ServiceSid {
    try {
        return (New-Object Security.Principal.NTAccount($ServiceAccount)).Translate([Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "The $ServiceAccount account doesn't exist. Install the PanRaProxy MSI before setting secrets."
    }
}

function Initialize-SecretsDirectory {
    $serviceSid = Get-ServiceSid
    New-Item -ItemType Directory -Path $SecretsDirectory -Force | Out-Null

    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow

    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false) # protected: nothing inherited from ProgramData
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) { # SYSTEM, Administrators
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', $inherit, $none, $allow)))
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($serviceSid, 'ReadAndExecute', $inherit, $none, $allow)))

    Set-Acl -Path $SecretsDirectory -AclObject $acl

    # Files written earlier (e.g. by hand) take the protected ACL too.
    Get-ChildItem -Path $SecretsDirectory -File | ForEach-Object {
        $fileAcl = Get-Acl -Path $_.FullName
        $fileAcl.SetAccessRuleProtection($false, $false)
        foreach ($rule in @($fileAcl.Access | Where-Object { -not $_.IsInherited })) { [void]$fileAcl.RemoveAccessRule($rule) }
        Set-Acl -Path $_.FullName -AclObject $fileAcl
    }
}

function ConvertFrom-SecureStringToPlain([Security.SecureString] $secure) {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Restart-ProxyService {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        throw "Service $ServiceName is not installed."
    }

    if ($service.Status -eq 'Running') {
        Restart-Service -Name $ServiceName
    }
    else {
        Start-Service -Name $ServiceName
    }

    $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    Write-Host "Service $ServiceName is running."
}

try {
    if ($PSCmdlet.ParameterSetName -eq 'List') {
        if (-not (Test-Path $SecretsDirectory)) { return }
        Get-ChildItem -Path $SecretsDirectory -File |
            Where-Object { $_.Name -notlike '*.tmp' } |
            Select-Object @{ n = 'Name'; e = { $_.Name } }, @{ n = 'Set'; e = { $_.LastWriteTime } }
        return
    }

    Assert-Elevated
    $path = Join-Path $SecretsDirectory $Name

    if ($PSCmdlet.ParameterSetName -eq 'Remove') {
        if ((Test-Path $path) -and $PSCmdlet.ShouldProcess($Name, 'Remove secret')) {
            Remove-Item -Path $path -Force
            Write-Host "Removed secret $Name."
        }
    }
    else {
        if ($null -eq $Value) {
            if (-not [Environment]::UserInteractive) {
                throw '-Value is required when running non-interactively.'
            }
            $Value = Read-Host -Prompt "Value for $Name" -AsSecureString
        }

        if ($Value.Length -eq 0) {
            throw 'The secret value is empty.'
        }

        if ($PSCmdlet.ShouldProcess($Name, 'Set secret')) {
            Initialize-SecretsDirectory

            $plain = ConvertFrom-SecureStringToPlain $Value
            $temporary = "$path.$([Guid]::NewGuid().ToString('N')).tmp"
            try {
                # UTF-8 without BOM, no trailing newline; inherits the protected folder ACL.
                [IO.File]::WriteAllText($temporary, $plain, (New-Object Text.UTF8Encoding($false)))
                Move-Item -Path $temporary -Destination $path -Force
            }
            finally {
                $plain = $null
                if (Test-Path $temporary) { Remove-Item -Path $temporary -Force }
            }

            Write-Host "Set secret $Name."
        }
    }

    if ($Restart -and $PSCmdlet.ShouldProcess($ServiceName, 'Start or restart service')) {
        Restart-ProxyService
    }
}
catch {
    Write-Error -ErrorRecord $_ -ErrorAction Continue
    exit 1
}
