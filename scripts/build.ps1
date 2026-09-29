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
    foreach($document in @('README.md','LICENSE','NOTICE','CONTRIBUTING.md','THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath $document -Destination "dist/$Runtime/$document"
    }
    Copy-Item -LiteralPath licenses -Destination "dist/$Runtime" -Recurse -Force
    # Keep notices for the exact runtime selected by the publishing SDK.
    $assets=Get-Content 'src/Wyrmwatch.Desktop/obj/project.assets.json' -Raw | ConvertFrom-Json
    $runtimePack=$assets.project.frameworks.'net10.0'.downloadDependencies | Where-Object { $_.name -eq "Microsoft.NETCore.App.Runtime.$Runtime" } | Select-Object -First 1
    $runtimeNoticesCopied=$false
    if($runtimePack) {
        $runtimeVersion=$runtimePack.version.Trim('[',']').Split(',')[0]
        foreach($packageRoot in $assets.packageFolders.PSObject.Properties.Name) {
            $packPath=Join-Path $packageRoot "microsoft.netcore.app.runtime.$Runtime/$runtimeVersion"
            if(Test-Path -LiteralPath $packPath) {
                Copy-Item -LiteralPath (Join-Path $packPath 'LICENSE.TXT') -Destination "dist/$Runtime/licenses/third-party/dotnet-LICENSE.txt"
                Copy-Item -LiteralPath (Join-Path $packPath 'THIRD-PARTY-NOTICES.TXT') -Destination "dist/$Runtime/licenses/third-party/dotnet-NOTICES.txt"
                $runtimeNoticesCopied=$true
                break
            }
        }
    }
    if(-not $runtimeNoticesCopied){throw 'Could not locate the publishing runtime license notices'}
    Copy-Item -LiteralPath LICENSE -Destination "dist/$Runtime/LICENSE.txt"
    # Ship the project source used for this build, including local tracked edits.
    $sourceFiles=git -c core.quotepath=false ls-files
    if($LASTEXITCODE -ne 0){throw 'Could not list project source files'}
    $revision=git rev-parse HEAD
    if($LASTEXITCODE -ne 0){throw 'Could not identify the source revision'}
    $modified=git status --porcelain --untracked-files=no
    if($LASTEXITCODE -ne 0){throw 'Could not inspect source modifications'}
    $sourceArchive=Join-Path $root "dist/$Runtime/Wyrmwatch-source.zip"
    $sourceStream=[System.IO.File]::Open($sourceArchive,[System.IO.FileMode]::Create)
    $zip=[System.IO.Compression.ZipArchive]::new($sourceStream,[System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($file in $sourceFiles) {
            if(Test-Path -LiteralPath $file -PathType Leaf) {
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $root $file),$file,[System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
    } finally { $zip.Dispose(); $sourceStream.Dispose() }
    $sourceNote=if($modified){'Includes local tracked changes; the bundled archive is the source used for this build.'}else{'Built from the unmodified revision linked below.'}
    @(
        '# Wyrmwatch source',
        '',
        'License: GNU AGPL version 3 (AGPL-3.0-only). See LICENSE and NOTICE.',
        '',
        'Extract Wyrmwatch-source.zip for the project source, assets, and build scripts. See README.md inside for build instructions.',
        '',
        "Revision: https://github.com/stephenjarrett/Wyrmwatch/tree/$revision",
        $sourceNote,
        '',
        'Third-party components retain their own licenses. THIRD-PARTY-NOTICES.md lists their versions, notices, and upstream sources.'
    ) | Set-Content -LiteralPath "dist/$Runtime/SOURCE.md" -Encoding utf8
    Write-Host "Built: $root\dist\$Runtime"
} finally { Pop-Location }
