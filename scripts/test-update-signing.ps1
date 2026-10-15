param([Parameter(Mandatory)][string]$Tool,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($env:SPARKLE_PRIVATE_KEY)){throw 'Signing secret is missing'}
$public=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../src/Wyrmwatch.Desktop/Assets/UpdateSigning.pub')).Trim()
$env:SPARKLE_PUBLIC_KEY=$public
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$proof=Join-Path $Output 'signing-proof.json'
# This schema is deliberately not a software update catalog.
[IO.File]::WriteAllText($proof,([ordered]@{Purpose='signing-verification-only';SourceCommit=(git rev-parse HEAD).Trim();PublicKeySha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($public)))}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$signed=@(& $Tool --generate-signature $proof 2>&1)
if($LASTEXITCODE -ne 0){throw 'Signing proof failed; tool output suppressed'}
$match=[regex]::Match((($signed -join "`n") -replace '\x1b\[[0-9;]*m',''),'Signature:\s*([A-Za-z0-9+/]{86}==)')
if(-not $match.Success){throw 'No valid detached proof signature returned'}
$signature=$match.Groups[1].Value
$verified=@(& $Tool --verify $proof --signature $signature 2>&1)
if($LASTEXITCODE -ne 0 -or (($verified -join "`n") -replace '\x1b\[[0-9;]*m','') -notmatch '(?m)^Signature valid\s*$'){throw 'Proof did not verify against committed public key'}
[IO.File]::WriteAllText($proof+'.sig',$signature,[Text.Encoding]::ASCII)
[IO.File]::WriteAllText((Join-Path $Output 'UpdateSigning.pub'),$public,[Text.Encoding]::ASCII)
Write-Host 'PASS: dedicated GitHub signing secret matches the committed public key. Proof contains no private material and cannot authorize an application update.'
