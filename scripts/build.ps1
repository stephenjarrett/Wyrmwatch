param([ValidateSet('win-x64','linux-x64')][string]$Runtime='win-x64',[switch]$SkipTests,[string]$OutputRoot='dist')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$localDotnet=Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet=if(Test-Path -LiteralPath $localDotnet){$localDotnet}else{'dotnet'}
Push-Location $root
try {
    $output=[IO.Path]::GetFullPath((Join-Path $root (Join-Path $OutputRoot $Runtime)))
    if(-not $output.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Build output must be inside the repository'}
    if((Test-Path -LiteralPath $output) -and (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)){throw 'Choose an empty output folder; existing builds are preserved'}
    if(-not $SkipTests){
        & $dotnet test tests/Wyrmwatch.Core.Tests -c Release
        if($LASTEXITCODE -ne 0){throw 'Core checks failed'}
        & $dotnet test tests/Wyrmwatch.Desktop.Tests -c Release
        if($LASTEXITCODE -ne 0){throw 'UI smoke checks failed'}
    }
    $staging=Join-Path (Split-Path -Parent $output) ('.publish-'+$Runtime+'-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging,$output -Force | Out-Null
    & $dotnet publish src/Wyrmwatch.Desktop -c Release -r $Runtime --self-contained true -o "$staging/desktop"
    if($LASTEXITCODE -ne 0){throw 'Desktop publish failed'}
    & $dotnet publish src/Wyrmwatch.Agent -c Release -r $Runtime --self-contained true -p:SharedRuntimeLauncher=true -o "$staging/agent"
    if($LASTEXITCODE -ne 0){throw 'Background manager publish failed'}
    if($Runtime -eq 'win-x64'){
        & $dotnet publish src/Wyrmwatch.Signal -c Release -r $Runtime --self-contained true -o "$staging/signal"
        if($LASTEXITCODE -ne 0){throw 'Shutdown helper publish failed'}
    }
    # Share identical runtime/dependency files. A version mismatch must fail the build.
    $retired=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($component in @('desktop','agent','signal')) {
        $componentPath=Join-Path $staging $component
        if(-not (Test-Path -LiteralPath $componentPath)){continue}
        foreach($file in Get-ChildItem -LiteralPath $componentPath -Recurse -File) {
            $relative=[IO.Path]::GetRelativePath($componentPath,$file.FullName)
            $isAgentRoot=$component -eq 'agent' -and [IO.Path]::GetDirectoryName($relative) -eq ''
            if($isAgentRoot -and $file.Name -notin @('Wyrmwatch.Agent.exe','Wyrmwatch.Agent')) { $null=$retired.Add('agent/'+$file.Name) }
            if($file.Extension -eq '.pdb') {
                if(-not $isAgentRoot){$null=$retired.Add($relative.Replace('\','/'))}
                continue
            }
            if($isAgentRoot -and $file.Name -in @('Wyrmwatch.Agent.exe','Wyrmwatch.Agent')){continue}
            $destination=Join-Path $output $relative
            if(Test-Path -LiteralPath $destination) {
                if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash){throw "Conflicting published dependency: $relative"}
            } else {
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                Copy-Item -LiteralPath $file.FullName -Destination $destination
            }
        }
    }
    # Exact obsolete package paths, used to reclaim space during installer upgrades.
    @($retired | Sort-Object) | ConvertTo-Json | Set-Content -LiteralPath "$output/retired-package-files.json" -Encoding utf8
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
    $bytes=(Get-ChildItem -LiteralPath $output -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host "Application payload: $([math]::Round($bytes/1MB,1)) MiB; one runtime, no debugging-symbol files"
    $resolvedStaging=(Resolve-Path -LiteralPath $staging).Path
    if(-not $resolvedStaging.StartsWith((Split-Path -Parent $output)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $resolvedStaging) -notlike '.publish-*'){throw 'Unexpected staging path; staging files preserved'}
    Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    Write-Host "Built: $((Resolve-Path -LiteralPath $output).Path)"
} finally { Pop-Location }
