param([ValidateSet('win-x64','linux-x64')][string]$Runtime='win-x64',[switch]$SkipTests)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$localDotnet=Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet=if(Test-Path -LiteralPath $localDotnet){$localDotnet}else{'dotnet'}
Push-Location $root
try {
    if(-not $SkipTests){
        & $dotnet test tests/Dragonwilds.Core.Tests -c Release
        if($LASTEXITCODE -ne 0){throw 'Core checks failed'}
        & $dotnet test tests/Wyrmwatch.Desktop.Tests -c Release
        if($LASTEXITCODE -ne 0){throw 'UI smoke checks failed'}
    }
    & $dotnet publish src/Wyrmwatch.Desktop -c Release -r $Runtime --self-contained true -o "dist/$Runtime"
    if($LASTEXITCODE -ne 0){throw 'Desktop publish failed'}
    if($Runtime -eq 'win-x64'){
        & $dotnet publish src/Dragonwilds.Signal -c Release -r $Runtime --self-contained true -o "dist/$Runtime"
        if($LASTEXITCODE -ne 0){throw 'Shutdown helper publish failed'}
    }
    Copy-Item -LiteralPath README.md -Destination "dist/$Runtime/README.md"
    Write-Host "Built: $root\dist\$Runtime"
} finally { Pop-Location }
