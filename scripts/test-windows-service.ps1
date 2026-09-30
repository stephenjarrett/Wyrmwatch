# Disposable hosted Windows runner only. Never registers a service on a user's host.
param([Parameter(Mandatory)][string]$ApplicationDirectory,[Parameter(Mandatory)][string]$FixtureDirectory)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){throw 'Run this check only on a disposable GitHub-hosted runner'}
if(Get-Service Wyrmwatch -ErrorAction SilentlyContinue){throw 'An existing service must not be replaced'}
$app=(Resolve-Path -LiteralPath $ApplicationDirectory).Path
$fixture=(Resolve-Path -LiteralPath $FixtureDirectory).Path
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('wyrmwatch-service-'+[Guid]::NewGuid().ToString('N'))
$workspace=Join-Path $testRoot 'workspace'
$install=Join-Path $testRoot 'fixture'
$saved=Join-Path $testRoot 'Saved'
$backups=Join-Path $testRoot 'backups'
New-Item -ItemType Directory -Path $workspace,(Join-Path $saved 'SaveGames'),(Join-Path $saved 'Config/WindowsServer') -Force | Out-Null
Copy-Item -LiteralPath $fixture -Destination $install -Recurse
Set-Content -LiteralPath (Join-Path $saved 'SaveGames/world.sav') -Value 'service-test-world'
@'
[/Script/Dominion.DedicatedServerSettings]
OwnerId=fixture
ServerName=Service fixture
DefaultWorldName=Service fixture
AdminPassword=fixture-only
Port=49583
'@ | Set-Content -LiteralPath (Join-Path $saved 'Config/WindowsServer/DedicatedServer.ini')
$profile=@{Id='service';Name='Service fixture';InstallPath=$install;DataPath=$saved;BackupPath=$backups;Port=49583}
@{Servers=@($profile);BackgroundMode=$true} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $workspace 'settings.json')
# LocalService is a built-in low-privilege account; no new account or logon-policy change.
& icacls.exe $testRoot /grant '*S-1-5-19:(OI)(CI)M' /T /Q | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not grant fixture-only service access'}
& icacls.exe $app /grant '*S-1-5-19:(OI)(CI)RX' /T /Q | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not grant read-only package access'}
$binary='"'+(Join-Path $app 'agent/Wyrmwatch.Agent.exe')+'" --workspace "'+$workspace+'"'
& sc.exe create Wyrmwatch binPath= $binary obj= 'NT AUTHORITY\LocalService' start= demand | Out-Null
if($LASTEXITCODE -ne 0){throw 'Test service registration failed'}
function Endpoint {
    $deadline=(Get-Date).AddSeconds(30)
    do {
        try {
            $candidate=Get-Content -LiteralPath (Join-Path $workspace 'agent.json') -Raw | ConvertFrom-Json
            $script:headers=@{Authorization='Bearer '+$candidate.Secret}
            $state=Invoke-RestMethod ($candidate.Address+'/api/status') -Headers $script:headers
            $script:endpoint=$candidate
            return $state
        } catch { Start-Sleep -Milliseconds 200 }
    } while((Get-Date) -lt $deadline)
    throw 'Service did not expose a working endpoint'
}
function Action([string]$name) {
    Invoke-RestMethod -Method Post -Uri ($script:endpoint.Address+'/api/servers/service/actions') -Headers $script:headers -ContentType 'application/json' -Body (@{action=$name} | ConvertTo-Json)
}
try {
    Start-Service Wyrmwatch
    $null=Endpoint
    $null=Action 'start'
    $state=Endpoint
    $identity=$state.servers[0].state.processes[0]
    if(-not $identity.id){throw 'Service failed to launch the fixture'}
    Stop-Service Wyrmwatch
    if(-not (Get-Process -Id $identity.id -ErrorAction SilentlyContinue)){throw 'Stopping the service stopped the fixture'}
    Start-Service Wyrmwatch
    $state=Endpoint
    if($state.servers[0].state.processes[0].id -ne $identity.id){throw 'Restart lost fixture ownership'}
    $null=Action 'stop'
    if((Get-Content -LiteralPath (Join-Path $install 'shutdown.requested') -Raw) -ne 'graceful'){throw 'Service failed graceful fixture shutdown'}
    if((Get-Content -LiteralPath (Join-Path $saved 'SaveGames/world.sav') -Raw).Trim() -ne 'service-test-world'){throw 'Service modified the world'}
    Write-Host 'PASS: low-privilege Windows service start, fixture control, stop preservation, restart ownership, graceful shutdown, and save preservation.'
} finally {
    Stop-Service Wyrmwatch -ErrorAction SilentlyContinue
    & sc.exe delete Wyrmwatch | Out-Null
    $script:headers=$null
}
