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
# Simulate files from the old layout; upgrades remove exact packaged paths only.
$obsoleteFiles=@('libSkiaSharp.pdb','agent/System.Private.CoreLib.dll','agent/Wyrmwatch.Agent.runtimeconfig.json','Microsoft.AspNetCore.Mvc.Core.dll')
foreach($oldFile in $obsoleteFiles) {
    Set-Content -LiteralPath (Join-Path $app $oldFile) -Value 'obsolete package fixture'
}
$unrelated=Join-Path $app 'agent/preserve-user-notes.txt'
Set-Content -LiteralPath $unrelated -Value 'preserve-unrelated-file'
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
    if(-not (Test-Path -LiteralPath (Join-Path $app 'libSkiaSharp.pdb'))){throw 'Blocked upgrade removed a file'}
    $headers=@{Authorization='Bearer '+$endpoint.Secret}
    Invoke-RestMethod -Method Post -Uri ($endpoint.Address+'/admin/shutdown') -Headers $headers | Out-Null
    if(-not $agent.WaitForExit(15000)){throw 'Agent did not shut down normally'}
} finally {
    # Keep failed fixtures available; do not terminate an agent with unknown work.
    $headers=$null
}
if((Install-Package) -ne 0){throw 'Reinstallation after clean shutdown failed'}
foreach($oldFile in $obsoleteFiles) {
    if(Test-Path -LiteralPath (Join-Path $app $oldFile)){throw "Upgrade left obsolete package file: $oldFile"}
}
if((Get-Content -LiteralPath $unrelated -Raw).Trim() -ne 'preserve-unrelated-file'){throw 'Upgrade changed an unrelated file'}
$uninstall=Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -WindowStyle Hidden -PassThru -Wait
if($uninstall.ExitCode -ne 0){throw 'Uninstallation failed'}
if(Test-Path -LiteralPath (Join-Path $app 'Wyrmwatch.exe')){throw 'Uninstaller left packaged executable'}
if((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'preserve-user-data'){throw 'Uninstaller modified user data'}
Write-Host "PASS: fresh install, shared-runtime agent, running-agent refusal, obsolete-file cleanup, reinstall, uninstall, unrelated-file and user-data preservation. Fixture: $testRoot"
