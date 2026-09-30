param([ValidateSet('win-x64','linux-x64')][string]$Runtime='win-x64',[switch]$SkipTests,[string]$OutputRoot='dist')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$localDotnet=Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet=if(Test-Path -LiteralPath $localDotnet){$localDotnet}else{'dotnet'}
Push-Location $root
try {
    $output=Join-Path $OutputRoot $Runtime
    if(-not $SkipTests){
        & $dotnet test tests/Wyrmwatch.Core.Tests -c Release
        if($LASTEXITCODE -ne 0){throw 'Core checks failed'}
        & $dotnet test tests/Wyrmwatch.Desktop.Tests -c Release
        if($LASTEXITCODE -ne 0){throw 'UI smoke checks failed'}
    }
    & $dotnet publish src/Wyrmwatch.Desktop -c Release -r $Runtime --self-contained true -o $output
    if($LASTEXITCODE -ne 0){throw 'Desktop publish failed'}
    & $dotnet publish src/Wyrmwatch.Agent -c Release -r $Runtime --self-contained true -o "$output/agent"
    if($LASTEXITCODE -ne 0){throw 'Background manager publish failed'}
    if($Runtime -eq 'win-x64'){
        & $dotnet publish src/Wyrmwatch.Signal -c Release -r $Runtime --self-contained true -o $output
        if($LASTEXITCODE -ne 0){throw 'Shutdown helper publish failed'}
    }
    foreach($document in @('README.md','LICENSE','NOTICE','CONTRIBUTING.md','THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath $document -Destination "$output/$document"
    }
    Copy-Item -LiteralPath licenses -Destination $output -Recurse -Force
    Copy-Item -LiteralPath docs -Destination $output -Recurse -Force
    New-Item -ItemType Directory -Path "$output/service" -Force | Out-Null
    Copy-Item -LiteralPath scripts/install-windows-service.ps1,scripts/install-linux-service.sh -Destination "$output/service"
    # Keep notices for the exact runtime selected by the publishing SDK.
    $assets=Get-Content 'src/Wyrmwatch.Desktop/obj/project.assets.json' -Raw | ConvertFrom-Json
    $runtimePack=$assets.project.frameworks.'net10.0'.downloadDependencies | Where-Object { $_.name -eq "Microsoft.NETCore.App.Runtime.$Runtime" } | Select-Object -First 1
    $runtimeNoticesCopied=$false
    if($runtimePack) {
        $runtimeVersion=$runtimePack.version.Trim('[',']').Split(',')[0]
        foreach($packageRoot in $assets.packageFolders.PSObject.Properties.Name) {
            $packPath=Join-Path $packageRoot "microsoft.netcore.app.runtime.$Runtime/$runtimeVersion"
            if(Test-Path -LiteralPath $packPath) {
                Copy-Item -LiteralPath (Join-Path $packPath 'LICENSE.TXT') -Destination "$output/licenses/third-party/dotnet-LICENSE.txt"
                Copy-Item -LiteralPath (Join-Path $packPath 'THIRD-PARTY-NOTICES.TXT') -Destination "$output/licenses/third-party/dotnet-NOTICES.txt"
                $runtimeNoticesCopied=$true
                break
            }
        }
    }
    if(-not $runtimeNoticesCopied){throw 'Could not locate the publishing runtime license notices'}
    $agentAssets=Get-Content 'src/Wyrmwatch.Agent/obj/project.assets.json' -Raw | ConvertFrom-Json
    $webRuntime=$agentAssets.project.frameworks.'net10.0'.downloadDependencies | Where-Object { $_.name -eq "Microsoft.AspNetCore.App.Runtime.$Runtime" } | Select-Object -First 1
    $webNoticesCopied=$false
    if($webRuntime) {
        $webVersion=$webRuntime.version.Trim('[',']').Split(',')[0]
        foreach($packageRoot in $agentAssets.packageFolders.PSObject.Properties.Name) {
            $packPath=Join-Path $packageRoot "microsoft.aspnetcore.app.runtime.$Runtime/$webVersion"
            if(Test-Path -LiteralPath $packPath) {
                Copy-Item -LiteralPath (Join-Path $packPath 'LICENSE.txt') -Destination "$output/licenses/third-party/aspnetcore-LICENSE.txt"
                Copy-Item -LiteralPath (Join-Path $packPath 'THIRD-PARTY-NOTICES.TXT') -Destination "$output/licenses/third-party/aspnetcore-NOTICES.txt"
                $webNoticesCopied=$true
                break
            }
        }
    }
    if(-not $webNoticesCopied){throw 'Could not locate ASP.NET Core runtime license notices'}
    Copy-Item -LiteralPath LICENSE -Destination "$output/LICENSE.txt"
    # Ship the project source used for this build, including local tracked edits.
    $sourceFiles=git -c core.quotepath=false ls-files
    if($LASTEXITCODE -ne 0){throw 'Could not list project source files'}
    $revision=git rev-parse HEAD
    if($LASTEXITCODE -ne 0){throw 'Could not identify the source revision'}
    $modified=git status --porcelain --untracked-files=no
    if($LASTEXITCODE -ne 0){throw 'Could not inspect source modifications'}
    $sourceArchive=Join-Path (Resolve-Path -LiteralPath $output) 'Wyrmwatch-source.zip'
    $sourceStream=[System.IO.File]::Open($sourceArchive,[System.IO.FileMode]::Create)
    $zip=[System.IO.Compression.ZipArchive]::new($sourceStream,[System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($file in $sourceFiles) {
            if(Test-Path -LiteralPath $file -PathType Leaf) {
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $root $file),$file,[System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
    } finally { $zip.Dispose(); $sourceStream.Dispose() }
    $sourceNote=if($modified){'Includes local tracked changes; the bundled archive is the source used for this build.'}else{'Built from the unmodified revision linked above.'}
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
    ) | Set-Content -LiteralPath "$output/SOURCE.md" -Encoding utf8
    Write-Host "Built: $((Resolve-Path -LiteralPath $output).Path)"
} finally { Pop-Location }
