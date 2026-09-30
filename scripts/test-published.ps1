# Exercise the actual packaged apphost and shared runtime using an empty workspace.
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
try {
    $deadline=(Get-Date).AddSeconds(30)
    do {
        if($process.HasExited){throw ('Packaged agent exited during startup: '+$stderr.GetAwaiter().GetResult())}
        try { $endpoint=Get-Content -LiteralPath (Join-Path $workspace 'agent.json') -Raw -ErrorAction Stop | ConvertFrom-Json } catch { }
        if($endpoint){break}
        Start-Sleep -Milliseconds 100
    } while((Get-Date) -lt $deadline)
    if(-not $endpoint -or $endpoint.ProcessId -ne $process.Id){throw 'Packaged agent did not publish its own endpoint'}
    if(-not ([Uri]$endpoint.Address).IsLoopback){throw 'Packaged agent exposed a nonlocal endpoint'}
    $headers=@{Authorization='Bearer '+$endpoint.Secret}
    $state=Invoke-RestMethod ($endpoint.Address+'/api/status') -Headers $headers
    if(@($state.servers).Count -ne 0){throw 'Empty package fixture adopted a server'}
    $runtime=Join-Path $app $(if($IsWindows){'hostfxr.dll'}else{'libhostfxr.so'})
    if(-not (Get-Content -LiteralPath $trace -Raw).Contains($runtime)){throw 'Packaged launcher did not resolve the bundled runtime'}
    Invoke-RestMethod -Method Post -Uri ($endpoint.Address+'/admin/shutdown') -Headers $headers | Out-Null
    if(-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0){throw 'Packaged agent did not shut down cleanly'}
    Write-Host 'PASS: packaged compatibility launcher, bundled shared runtime, authenticated local status, empty workspace, clean shutdown.'
} finally {
    # This process owns only the new, empty test workspace; no game is configured.
    if(-not $process.HasExited){$process.Kill();$process.WaitForExit()}
    $process.Dispose()
    $headers=$null
}
