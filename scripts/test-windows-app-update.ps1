$ErrorActionPreference='Stop'
if($env:CI -cne 'true'){throw 'Native app update tests require a disposable Windows CI host'}
$fixture=(Get-Content dist/native-update-fixture-path.txt -Raw).Trim()
if((Split-Path -Leaf $fixture) -notmatch '^wyrmwatch-update-fixture-[a-z0-9_]+$'){throw 'Unexpected fixture root'}
$spec=Get-Content (Join-Path $fixture fixture.json) -Raw | ConvertFrom-Json
$app=Join-Path $fixture app
$workspace=Join-Path $fixture workspace
$game=Join-Path $fixture game
New-Item -ItemType Directory -Path $workspace,$game -Force | Out-Null
$world=Join-Path $game 'RSDragonwilds/Saved/SaveGames/fixture.sav'
$configuration=Join-Path $game 'RSDragonwilds/Saved/Config/WindowsServer/DedicatedServer.ini'
New-Item -ItemType Directory -Path (Split-Path -Parent $world),(Split-Path -Parent $configuration) -Force | Out-Null
Set-Content -LiteralPath $world -Value 'untouched isolated world'
Set-Content -LiteralPath $configuration -Value "[/Script/Dominion.DedicatedServerSettings]`nOwnerId=fixture-owner`nServerName=Upgrade fixture`nDefaultWorldName=Fixture world`nAdminPassword=disposable-fixture-only`nPort=17789"
$worldHash=(Get-FileHash -LiteralPath $world).Hash
$configHash=(Get-FileHash -LiteralPath $configuration).Hash
# This explicitly selected synthetic server runs no game code.
dotnet publish tests/FixtureServer -c Release --no-self-contained -o "$game" | Out-Null
if($LASTEXITCODE -ne 0){throw 'Synthetic server build failed'}
Set-Content -LiteralPath (Join-Path $game window-console) -Value 'fixture'
$profile=@{Id='upgrade-fixture';Name='Upgrade fixture';InstallPath=$game;BackupPath=(Join-Path $fixture backups);Port=17789;AutoUpdate=$false;AutoBackup=$false}
@{Servers=@($profile);SelectedServerId=$profile.Id;CloseToTray=$false;BackgroundMode=$false;FollowLog=$false} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $workspace settings.json) -Encoding utf8

function Start-App([string]$Executable=(Join-Path $app Wyrmwatch.exe),[string[]]$Extra=@()) {
    $start=[Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute=$false
    $start.WorkingDirectory=$app
    $start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($workspace)
    foreach($argument in $Extra){$start.ArgumentList.Add($argument)}
    return [Diagnostics.Process]::Start($start)
}
function Request([string]$Method,[string]$Route,$Body=$null) {
    $endpoint=Get-Content (Join-Path $workspace agent.json) -Raw | ConvertFrom-Json
    $request=@{Method=$Method;Uri=$endpoint.Address+$Route;Headers=@{Authorization='Bearer '+$endpoint.Secret};TimeoutSec=20}
    if($null -ne $Body){$request.Body=ConvertTo-Json -InputObject $Body -Depth 20;$request.ContentType='application/json'}
    Invoke-RestMethod @request
}
function Wait-Agent([string]$Version) {
    $deadline=(Get-Date).AddSeconds(45)
    do {
        try {
            $status=Request Get '/api/status'
            if($status.Version -eq $Version -and @($status.Servers).Count -eq 1 -and $status.Servers[0].Id -eq 'upgrade-fixture'){return $status}
        } catch { }
        Start-Sleep -Milliseconds 200
    } while((Get-Date) -lt $deadline)
    throw "Fixture agent did not reconnect with expected version $Version"
}
function Close-App {
    $matches=@(Get-Process Wyrmwatch -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq (Join-Path $app Wyrmwatch.exe)})
    foreach($process in $matches) {
        if(-not $process.CloseMainWindow() -or -not $process.WaitForExit(20000)){throw 'Owned fixture app did not quit gracefully'}
    }
}
function Install([string]$Setup,[switch]$Reopen) {
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="'+$app+'"'),'/GROUP="Wyrmwatch Update Fixture"','/TASKS=desktopicon',('/LOG="'+(Join-Path $fixture ('setup-'+[Guid]::NewGuid().ToString('N')+'.log'))+'"'))
    if($Reopen){$arguments+=('/UPDATEWORKSPACE="'+$workspace+'"')}
    $process=Start-Process -FilePath $Setup -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if($process.ExitCode -ne 0){throw "Fixture setup exited with $($process.ExitCode)"}
}
function Assert-Data {
    $preferences=Get-Content (Join-Path $workspace settings.json) -Raw | ConvertFrom-Json
    if(@($preferences.Servers).Count -ne 1 -or $preferences.Servers[0].Id -ne 'upgrade-fixture' -or $preferences.FollowLog){throw 'Update lost saved connection or Follow log choice'}
    if((Get-FileHash -LiteralPath $world).Hash -cne $worldHash -or (Get-FileHash -LiteralPath $configuration).Hash -cne $configHash){throw 'Upgrade changed isolated game data'}
    if($script:gameId -and (-not (Get-Process -Id $script:gameId -ErrorAction SilentlyContinue) -or (Test-Path (Join-Path $game shutdown.requested)))){throw 'App update stopped the synthetic game'}
}
function Assert-Shortcuts([string]$Version) {
    $shell=New-Object -ComObject WScript.Shell
    try {
        foreach($path in @((Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) Wyrmwatch.lnk),(Join-Path ([Environment]::GetFolderPath('Programs')) 'Wyrmwatch Update Fixture/Wyrmwatch.lnk'))) {
            $link=$shell.CreateShortcut($path)
            try {
                if($link.TargetPath -ne (Join-Path $app Wyrmwatch.exe) -or -not $link.Arguments.Contains($workspace)){throw 'Shortcut does not preserve installed path and workspace'}
            } finally {[Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null}
            Start-Process -FilePath $path | Out-Null
            Wait-Agent $Version | Out-Null
            $ui=@(Get-Process Wyrmwatch -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq (Join-Path $app Wyrmwatch.exe)})
            if($ui.Count -ne 1 -or -not $ui[0].MainWindowHandle){throw 'Shortcut did not open the updated installed UI'}
            Close-App
        }
    } finally {[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null}
}
function Run-Button([string]$Mode) {
    foreach($path in Get-ChildItem -LiteralPath $spec.Driver -File){Copy-Item -LiteralPath $path.FullName -Destination $app -Force}
    $case=Join-Path $fixture $Mode
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $app Wyrmwatch.UpdateFixture.exe))
    $start.UseShellExecute=$false;$start.WorkingDirectory=$app
    $start.ArgumentList.Add($workspace);$start.ArgumentList.Add($case)
    $process=[Diagnostics.Process]::Start($start)
    if(-not $process.WaitForExit(90000)){throw 'Real download handler did not finish its fixture'}
    if($process.ExitCode -ne 0 -or (Test-Path (Join-Path $case driver-failure.txt))){throw 'Native update driver failed'}
    return $case
}

# The immutable legacy updater needs the documented one-time setup migration.
# Verify the actual released 0.2.7 installer before executing it on this CI host.
$legacy=Join-Path $fixture legacy-0.2.7-setup.exe
gh release download v0.2.7 --repo stephenjarrett/Wyrmwatch --pattern Wyrmwatch-0.2.7-win-x64-setup.exe --output $legacy
if($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $legacy).Hash -ine 'de3e15d9652b31b8a58cbfe3304e1b2cdc7153a083293cf5d660c984db543bd6'){throw 'Released legacy installer integrity check failed'}
Install $legacy
$legacyUi=Start-App
Wait-Agent '0.2.7' | Out-Null
Close-App
# Old automatic updates left the desktop link on a portable workspace copy.
$shell=New-Object -ComObject WScript.Shell
$link=$shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) Wyrmwatch.lnk))
$link.TargetPath=Join-Path $workspace 'app-updates/0.2.7/Wyrmwatch.exe';$link.Arguments='--minimized';$link.Save()
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
Install $spec.Installer -Reopen
Wait-Agent $spec.Current | Out-Null
Close-App
Assert-Shortcuts $spec.Current
Assert-Data

# Start a synthetic server explicitly once; app upgrades must never stop it.
$ui=Start-App
Wait-Agent $spec.Current | Out-Null
Request Post '/api/servers/upgrade-fixture/actions' @{Action='start'} | Out-Null
$deadline=(Get-Date).AddSeconds(20)
do {
    $synthetic=@(Get-Process RSDragonwildsServer -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq (Join-Path $game RSDragonwildsServer.exe)})
    if($synthetic.Count -eq 1){$script:gameId=$synthetic[0].Id;break}
    Start-Sleep -Milliseconds 200
} while((Get-Date) -lt $deadline)
if(-not $script:gameId){throw 'Synthetic server did not start'}
Close-App
$original=(Get-FileHash (Join-Path $app Wyrmwatch.dll)).Hash
foreach($mode in @('bad-digest','bad-signature')) {
    $case=Run-Button $mode
    $result=Get-Content (Join-Path $case driver-result.json) -Raw | ConvertFrom-Json
    if(-not $result.HasError -or (Get-FileHash (Join-Path $app Wyrmwatch.dll)).Hash -cne $original){throw 'Unverified update reached installation'}
    Assert-Data
}
$case=Run-Button failure
Wait-Agent $spec.Current | Out-Null
if((Get-FileHash (Join-Path $app Wyrmwatch.dll)).Hash -cne $original){throw 'Failed native upgrade did not restore previous application'}
$faultObserved=@(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'wyrmwatch-install-*' | Where-Object {
    $journal=Join-Path $_.FullName installation.json
    (Test-Path $journal) -and (Get-Content $journal -Raw | ConvertFrom-Json).Root -eq $app -and (Test-Path (Join-Path $_.FullName fault-observed.txt))
})
if($faultObserved.Count -eq 0){throw 'Failure fixture did not reach real file replacement'}
Assert-Data
Close-App
$case=Run-Button success
Wait-Agent $spec.Future | Out-Null
if((Get-FileHash (Join-Path $app Wyrmwatch.dll)).Hash -ceq $original){throw 'Subsequent update did not replace application version'}
Assert-Data
Close-App
Assert-Shortcuts $spec.Future
$ui=Start-App
Wait-Agent $spec.Future | Out-Null
Request Post '/api/servers/upgrade-fixture/actions' @{Action='stop'} | Out-Null
Close-App
$uninstall=Start-Process (Join-Path $app unins000.exe) -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -WindowStyle Hidden -PassThru -Wait
if($uninstall.ExitCode -ne 0){throw 'Fixture uninstall failed'}
Write-Host 'PASS: released legacy migration; real download/checksum/signature/restart handlers; failed-copy recovery; subsequent version; desktop and Start menu launches; saved connections and live synthetic server preserved.'
