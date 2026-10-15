param([string]$OutputRoot='dist',[Parameter(Mandatory)][string]$Tool)
$ErrorActionPreference='Stop'
[xml]$props=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props')
$version=$props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if([version]$version -lt [version]'0.2.8'){return} # Preserve immutable legacy inventories.
if([string]::IsNullOrWhiteSpace($env:SPARKLE_PRIVATE_KEY)){throw 'Dedicated update-signing secret is required; unsigned publication is blocked'}
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$output=(Resolve-Path -LiteralPath (Join-Path $root $OutputRoot)).Path
$publicPath=Join-Path $root 'src/Wyrmwatch.Desktop/Assets/UpdateSigning.pub'
$env:SPARKLE_PUBLIC_KEY=[IO.File]::ReadAllText($publicPath).Trim()
if([Convert]::FromBase64String($env:SPARKLE_PUBLIC_KEY).Length -ne 32){throw 'Invalid committed update verification key'}
$source=(git -C $root rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or $source -cnotmatch '^[0-9a-f]{40}$'){throw 'Cannot identify exact release source'}
$artifacts=@()
foreach($name in @('Wyrmwatch-win-x64.zip',"Wyrmwatch-$version-win-x64-setup.exe")) {
    $file=Get-Item -LiteralPath (Join-Path $output $name)
    $artifacts += [ordered]@{Name=$name;Bytes=$file.Length;Sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$catalog=Join-Path $output 'Wyrmwatch-update.json'
[IO.File]::WriteAllText($catalog,([ordered]@{Schema=1;Version=$version;SourceCommit=$source;Artifacts=$artifacts}|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
# The private seed is provided only through the protected environment. Never use
# console key export, command-line key overrides, or a key file in the checkout.
$signed=@(& $Tool --generate-signature $catalog 2>&1)
if($LASTEXITCODE -ne 0){throw 'Update catalog signing failed; tool output suppressed'}
$text=($signed -join "`n") -replace '\x1b\[[0-9;]*m',''
$match=[regex]::Match($text,'Signature:\s*([A-Za-z0-9+/]{86}==)')
if(-not $match.Success){throw 'Signing tool did not return a valid detached signature'}
$signature=$match.Groups[1].Value
$verified=@(& $Tool --verify $catalog --signature $signature 2>&1)
if($LASTEXITCODE -ne 0 -or (($verified -join "`n") -replace '\x1b\[[0-9;]*m','') -notmatch '(?m)^Signature valid\s*$') {
    throw 'Catalog signature does not verify with the committed public key; publication is blocked'
}
[IO.File]::WriteAllText($catalog+'.sig',$signature,[Text.Encoding]::ASCII)
foreach($path in @($catalog,($catalog+'.sig'))) {
    $hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($path+'.sha256',"$hash  $([IO.Path]::GetFileName($path))`n",[Text.Encoding]::ASCII)
}
Write-Host "Signed and verified Windows update catalog for $version at $source. No private key is included in artifacts."
