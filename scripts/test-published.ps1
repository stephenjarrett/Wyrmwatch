# Exercise the actual packaged apphost and shared runtime with disposable game files.
param([Parameter(Mandatory)][string]$ApplicationDirectory)
$ErrorActionPreference='Stop'
$app=(Resolve-Path -LiteralPath $ApplicationDirectory).Path
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('wyrmwatch-package-'+[Guid]::NewGuid().ToString('N'))
$workspace=Join-Path $testRoot 'workspace'
New-Item -ItemType Directory -Path $workspace -Force | Out-Null
$launcher=Join-Path $app $(if($IsWindows){'agent/Wyrmwatch.Agent.exe'}else{'agent/Wyrmwatch.Agent'})
$start=[Diagnostics.ProcessStartInfo]::new($launcher)
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
$start.ArgumentList.Add('--workspace')
$start.ArgumentList.Add($workspace)
$start.Environment['DOTNET_ROOT']=Join-Path $testRoot 'no-installed-dotnet'
$start.Environment['DOTNET_ROOT_X64']=Join-Path $testRoot 'no-installed-dotnet'
$start.Environment['COREHOST_TRACE']='1'
$trace=Join-Path $testRoot 'host-trace.txt'
$start.Environment['COREHOST_TRACEFILE']=$trace
$process=[Diagnostics.Process]::Start($start)
$stdout=$process.StandardOutput.ReadToEndAsync()
$stderr=$process.StandardError.ReadToEndAsync()
$endpoint=$null
function Wait-Endpoint {
    $deadline=(Get-Date).AddSeconds(30)
    do {
        if($process.HasExited){throw ('Packaged agent exited during startup: '+$stderr.GetAwaiter().GetResult())}
        try { $candidate=Get-Content -LiteralPath (Join-Path $workspace 'agent.json') -Raw -ErrorAction Stop | ConvertFrom-Json } catch { $candidate=$null }
        if($candidate -and $candidate.ProcessId -eq $process.Id){return $candidate}
        Start-Sleep -Milliseconds 100
    } while((Get-Date) -lt $deadline)
    throw 'Packaged agent did not publish its own endpoint'
}
function Request-Json([string]$method,[string]$route,$body=$null) {
    $request=@{Method=$method;Uri=$endpoint.Address+$route;Headers=$headers;TimeoutSec=30}
    if($null -ne $body){$request.Body=ConvertTo-Json -InputObject $body -Depth 30;$request.ContentType='application/json'}
    Invoke-RestMethod @request
}
try {
    $endpoint=Wait-Endpoint
    if(-not ([Uri]$endpoint.Address).IsLoopback){throw 'Packaged agent exposed a nonlocal endpoint'}
    $headers=@{Authorization='Bearer '+$endpoint.Secret}
    $state=Invoke-RestMethod ($endpoint.Address+'/api/status') -Headers $headers
    if(@($state.servers).Count -ne 0){throw 'Empty package fixture adopted a server'}
    $runtime=Join-Path $app $(if($IsWindows){'hostfxr.dll'}else{'libhostfxr.so'})
    if(-not (Get-Content -LiteralPath $trace -Raw).Contains($runtime)){throw 'Packaged launcher did not resolve the bundled runtime'}

    $install=Join-Path $testRoot 'dummy-server'
    $saved=Join-Path $install 'RSDragonwilds/Saved'
    $config=Join-Path $saved $(if($IsWindows){'Config/WindowsServer/DedicatedServer.ini'}else{'Config/Linux/DedicatedServer.ini'})
    $world=Join-Path $saved 'SaveGames/dummy.sav'
    $gameLauncher=Join-Path $install $(if($IsWindows){'RSDragonwildsServer.exe'}else{'RSDragonwildsServer.sh'})
    New-Item -ItemType Directory -Path (Split-Path -Parent $config),(Split-Path -Parent $world) -Force | Out-Null
    # Non-executable text sentinel: the package test never starts a game process.
    Set-Content -LiteralPath $gameLauncher -Value 'dummy launcher, never execute'
    Set-Content -LiteralPath $config -Value "[/Script/Dominion.DedicatedServerSettings]`nServerName=Package fixture`nPort=7799"
    Set-Content -LiteralPath $world -Value 'original dummy world'
    $originalWorld=(Get-FileHash -LiteralPath $world).Hash
    $originalConfig=(Get-FileHash -LiteralPath $config).Hash
    $profile=@{Id='package-fixture';Name='Package fixture';InstallPath=$install;LauncherPath=$gameLauncher;BackupPath=(Join-Path $testRoot 'backups');Port=7799;AutoUpdate=$true;AutoBackup=$true;UpdateMinutes=30;WindowStart='03:15:00';WindowEnd='05:45:00'}
    $connected=Request-Json Post '/admin/import' $profile
    if($connected.AutoUpdate -or $connected.AutoBackup -or $connected.Port -ne 7799){throw 'Packaged import lost detected configuration or enabled automation'}
    if((Get-FileHash -LiteralPath $world).Hash -ne $originalWorld -or (Get-FileHash -LiteralPath $config).Hash -ne $originalConfig){throw 'Packaged import changed dummy game files'}
    $connected.Name='Renamed package fixture'
    $edited=Request-Json Put '/admin/profiles' $connected
    if($edited.Name -ne $connected.Name -or $edited.UpdateMinutes -ne 30 -or $edited.WindowStart -ne '03:15:00'){throw 'Packaged profile edit lost values'}
    Request-Json Post '/api/servers/package-fixture/actions' @{Action='backup'} | Out-Null
    $backups=@(Request-Json Get '/api/servers/package-fixture/backups')
    if($backups.Count -ne 1 -or $backups[0].Files -lt 2){throw 'Packaged backup omitted dummy game files'}
    Request-Json Post '/api/servers/package-fixture/actions' @{Action='verify';Archive=$backups[0].Id} | Out-Null
    Set-Content -LiteralPath $world -Value 'replacement dummy world'
    Request-Json Post '/api/servers/package-fixture/actions' @{Action='restore';Archive=$backups[0].Id;Confirmation=$connected.Name} | Out-Null
    if((Get-FileHash -LiteralPath $world).Hash -ne $originalWorld -or (Get-FileHash -LiteralPath $config).Hash -ne $originalConfig){throw 'Packaged restore did not recover the original dummy files'}
    Request-Json Post '/admin/shutdown' | Out-Null
    if(-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0){throw 'Packaged agent did not shut down cleanly'}
    $process.Dispose()
    $process=[Diagnostics.Process]::Start($start)
    $stdout=$process.StandardOutput.ReadToEndAsync()
    $stderr=$process.StandardError.ReadToEndAsync()
    $endpoint=Wait-Endpoint
    $headers=@{Authorization='Bearer '+$endpoint.Secret}
    $settings=Request-Json Get '/admin/settings'
    if(@($settings.Servers).Count -ne 1 -or $settings.Servers[0].Name -ne $connected.Name -or $settings.Servers[0].UpdateMinutes -ne 30){throw 'Packaged restart lost the saved profile'}
    Request-Json Delete '/admin/profiles/package-fixture' | Out-Null
    $state=Request-Json Get '/api/status'
    if(@($state.Servers).Count -ne 0 -or (Get-FileHash -LiteralPath $world).Hash -ne $originalWorld){throw 'Packaged disconnect changed dummy world files'}
    if(-not (Test-Path -LiteralPath (Join-Path $profile.BackupPath $backups[0].Id))){throw 'Packaged disconnect removed a backup'}
    Request-Json Post '/admin/shutdown' | Out-Null
    if(-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0){throw 'Packaged agent did not shut down cleanly'}
    Write-Host 'PASS: packaged launcher, bundled runtime, empty workspace, import/edit, backup/verify/restore, persisted restart, disconnect preservation, clean shutdown.'
} finally {
    # This process owns only the disposable workspace and non-executable dummy launcher.
    if(-not $process.HasExited){$process.Kill();$process.WaitForExit()}
    $process.Dispose()
    $headers=$null
}
