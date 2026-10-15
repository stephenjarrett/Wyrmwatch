# Disposable hosted Windows runner only. Never registers a service on a user's host.
param([Parameter(Mandatory)][string]$ApplicationDirectory,[Parameter(Mandatory)][string]$FixtureDirectory)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){throw 'Run this check only on a disposable GitHub-hosted runner'}
if(Get-Service Wyrmwatch -ErrorAction SilentlyContinue){throw 'An existing service must not be replaced'}
$app=(Resolve-Path -LiteralPath $ApplicationDirectory).Path
$fixture=(Resolve-Path -LiteralPath $FixtureDirectory).Path
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('wyrmwatch-service-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $app Wyrmwatch.Platform.dll)) | Out-Null
$testRoot=[Wyrmwatch.Platform.WindowsPhysicalPath]::ExistingDirectory($testRoot)
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
    Write-Host "Synthetic service fixture action: $name"
    try {
        Invoke-RestMethod -Method Post -Uri ($script:endpoint.Address+'/api/servers/service/actions') -Headers $script:headers -ContentType 'application/json' -Body (@{action=$name} | ConvertTo-Json)
    } catch {
        $failure=Join-Path $install startup.failure
        if(Test-Path -LiteralPath $failure){Write-Host ('Synthetic startup diagnostic: '+(Get-Content -LiteralPath $failure -Raw))}
        $present=@(Get-Process RSDragonwildsServer -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq (Join-Path $install RSDragonwildsServer.exe)})
        Write-Host "Synthetic fixture processes still present: $($present.Count)"
        throw
    }
}
function Wait-Fixture {
    $deadline=(Get-Date).AddSeconds(15)
    do {
        $state=Endpoint
        if($state.servers[0].state.accessible -and $state.servers[0].state.running -and $state.servers[0].state.processes[0].id){return $state}
        Start-Sleep -Milliseconds 500
    } while((Get-Date) -lt $deadline)
    throw ('Fixture did not appear in the observed service state: '+($state | ConvertTo-Json -Depth 8 -Compress))
}
try {
    Start-Service Wyrmwatch
    $null=Endpoint
    $null=Action 'start'
    $state=Wait-Fixture
    $identity=$state.servers[0].state.processes[0]
    if(-not $identity.id){throw 'Service failed to launch the fixture'}
    Stop-Service Wyrmwatch
    if(-not (Get-Process -Id $identity.id -ErrorAction SilentlyContinue)){throw 'Stopping the service stopped the fixture'}
    Start-Service Wyrmwatch
    $state=Wait-Fixture
    if($state.servers[0].state.processes[0].id -ne $identity.id){throw 'Restart lost fixture ownership'}
    $null=Action 'stop'
    if((Get-Content -LiteralPath (Join-Path $install 'shutdown.requested') -Raw) -ne 'graceful'){throw 'Service failed graceful fixture shutdown'}
    # Synthetic credentials only: verify the real LocalService DPAPI boundary,
    # authenticated owner read, service restart, and reapplication before start.
    $configuration=Join-Path $saved 'Config/WindowsServer/DedicatedServer.ini'
    $rewritten=(Get-Content -LiteralPath $configuration -Raw) -replace '(?m)^AdminPassword=.*\r?\n',''
    Set-Content -LiteralPath $configuration -Value $rewritten -NoNewline -Encoding utf8
    $rewrittenHash=(Get-FileHash -LiteralPath $configuration).Hash
    Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
    $encrypted=(Get-Content -LiteralPath (Join-Path $workspace server-admin-passwords.json) -Raw | ConvertFrom-Json).service
    $unreadable=$false
    try {
        $unexpected=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($encrypted),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
        $unexpected=$null
    } catch {
        if($_.Exception.InnerException -is [Security.Cryptography.CryptographicException]){$unreadable=$true}else{throw 'Synthetic DPAPI boundary probe failed unexpectedly'}
    }
    $encrypted=$null
    if(-not $unreadable){throw 'Service-owned synthetic credential was readable under the unrelated runner account'}
    foreach($attempt in 1..2) {
        if($attempt -eq 2){Restart-Service Wyrmwatch;$null=Endpoint}
        $response=Invoke-WebRequest ($script:endpoint.Address+'/admin/servers/service/configuration') -Headers $script:headers
        $values=$response.Content | ConvertFrom-Json
        if($values.AdminPassword -cne 'fixture-only' -or $response.Headers['Cache-Control'] -notcontains 'no-store'){throw 'Authenticated service-owner credential read failed'}
        $values=$null;$response=$null
        if((Get-FileHash -LiteralPath $configuration).Hash -cne $rewrittenHash){throw 'Reading retained service configuration changed the INI'}
    }
    $null=Action 'start'
    if((Get-Content -LiteralPath $configuration -Raw) -notmatch '(?m)^AdminPassword=fixture-only\r?$'){throw 'Service did not restore its own retained synthetic credential before start'}
    $null=Action 'stop'
    if((Get-Content -LiteralPath (Join-Path $saved 'SaveGames/world.sav') -Raw).Trim() -ne 'service-test-world'){throw 'Service modified the world'}
    Write-Host 'PASS: low-privilege Windows service, fixture preservation, restart ownership, DPAPI account isolation, authenticated no-store credential recovery, reapplication before start, and unchanged world.'
} finally {
    Stop-Service Wyrmwatch -ErrorAction SilentlyContinue
    & sc.exe delete Wyrmwatch | Out-Null
    $script:headers=$null
}
