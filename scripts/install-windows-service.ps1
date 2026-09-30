# Run from an elevated PowerShell session. No game files are changed.
param(
    [Parameter(Mandatory)][string]$ApplicationDirectory,
    [Parameter(Mandatory)][string]$Workspace,
    [Parameter(Mandatory)][System.Management.Automation.PSCredential]$Credential
)
$ErrorActionPreference='Stop'
$appRoot=(Resolve-Path -LiteralPath $ApplicationDirectory).Path
$workspaceRoot=(Resolve-Path -LiteralPath $Workspace).Path
$executable=Join-Path $appRoot 'agent\Wyrmwatch.Agent.exe'
if(-not (Test-Path -LiteralPath $executable)){throw 'Extract a complete Windows portable build first.'}
if($executable.Contains('"') -or $workspaceRoot.Contains('"')){throw 'Paths cannot contain quote characters.'}
if(Get-Service -Name Wyrmwatch -ErrorAction SilentlyContinue){throw 'The Wyrmwatch service already exists. Stop it safely before updating its registration.'}
New-Service -Name Wyrmwatch -DisplayName 'Wyrmwatch background manager' -Description 'Opt-in Dragonwilds maintenance.' -BinaryPathName ('"'+$executable+'" --workspace "'+$workspaceRoot+'"') -StartupType Automatic -Credential $Credential
Write-Host 'Service registered but not started. Quit the desktop manager with background mode off, then run Start-Service Wyrmwatch.'
Write-Host 'Use the same Windows account as your server files. The account needs the Log on as a service right.'
