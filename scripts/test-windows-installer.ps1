param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference='Stop'
$installerPath=(Resolve-Path -LiteralPath $Installer).Path
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('wyrmwatch-installer-'+[Guid]::NewGuid().ToString('N'))
$app=Join-Path $testRoot 'app'
$workspace=Join-Path $testRoot 'workspace'
New-Item -ItemType Directory -Path $workspace -Force | Out-Null
$sentinel=Join-Path $workspace 'preserve.txt'
Set-Content -LiteralPath $sentinel -Value 'preserve-user-data'
function Install-Package {
    $process=Start-Process -FilePath $installerPath -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="'+$app+'"'),'/GROUP="Wyrmwatch Installer Test"' -WindowStyle Hidden -PassThru -Wait
    return $process.ExitCode
}
if((Install-Package) -ne 0){throw 'Fresh installation failed'}
foreach($file in @('Wyrmwatch.exe','agent/Wyrmwatch.Agent.exe','LICENSE','Wyrmwatch-source.zip','unins000.exe')) {
    if(-not (Test-Path -LiteralPath (Join-Path $app $file))){throw "Installer omitted $file"}
}
$hash=(Get-FileHash -LiteralPath (Join-Path $app 'Wyrmwatch.exe')).Hash
$agent=Start-Process -FilePath (Join-Path $app 'agent/Wyrmwatch.Agent.exe') -ArgumentList '--workspace',('"'+$workspace+'"') -WindowStyle Hidden -PassThru
try {
    $deadline=(Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 200
        $endpointFile=Join-Path $workspace 'agent.json'
        if(Test-Path -LiteralPath $endpointFile){$endpoint=Get-Content -LiteralPath $endpointFile -Raw | ConvertFrom-Json}
    } while((-not $endpoint) -and (Get-Date) -lt $deadline)
    if(-not $endpoint -or $endpoint.ProcessId -ne $agent.Id){throw 'Installed agent did not start'}
    if((Install-Package) -eq 0){throw 'Installer replaced a running manager'}
    if((Get-FileHash -LiteralPath (Join-Path $app 'Wyrmwatch.exe')).Hash -ne $hash){throw 'Blocked installation changed the app'}
    $headers=@{Authorization='Bearer '+$endpoint.Secret}
    Invoke-RestMethod -Method Post -Uri ($endpoint.Address+'/admin/shutdown') -Headers $headers | Out-Null
    if(-not $agent.WaitForExit(15000)){throw 'Agent did not shut down normally'}
} finally {
    # Keep failed fixtures available; do not terminate an agent with unknown work.
    $headers=$null
}
if((Install-Package) -ne 0){throw 'Reinstallation after clean shutdown failed'}
$uninstall=Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -WindowStyle Hidden -PassThru -Wait
if($uninstall.ExitCode -ne 0){throw 'Uninstallation failed'}
if(Test-Path -LiteralPath (Join-Path $app 'Wyrmwatch.exe')){throw 'Uninstaller left packaged executable'}
if((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'preserve-user-data'){throw 'Uninstaller modified user data'}
Write-Host "PASS: fresh install, complete payload, running-agent refusal, reinstall, uninstall, user-data preservation. Fixture: $testRoot"
