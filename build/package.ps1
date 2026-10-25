[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64')]
    [string] $RuntimeIdentifier,
    [string] $OutputDirectory,
    [string] $FixturePath,
    [string] $Mp4FixturePath,
    [string] $ToolCacheDirectory = (Join-Path $PSScriptRoot '../artifacts/downloads'),
    [string] $Dotnet = 'dotnet',
    [string] $WindowsMkvBuildDirectory = $env:TRACKTRIM_WINDOWS_MKV_BUILD_DIRECTORY,
    [switch] $Overwrite
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$desktopProject = Join-Path $repositoryRoot 'src/TrackTrim.Desktop/TrackTrim.Desktop.csproj'
$runtimeVersion = '10.0.13'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts'
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$archiveName = "TrackTrim-$RuntimeIdentifier"
$archiveExtension = '.zip'

if ($RuntimeIdentifier -eq 'linux-x64') {
    $archiveExtension = '.tar.gz'
}

$archivePath = Join-Path $OutputDirectory ($archiveName + $archiveExtension)

if ((Test-Path -LiteralPath $archivePath) -and -not $Overwrite) {
    throw "Archive already exists: $archivePath. Choose another output directory or use -Overwrite."
}

if (($RuntimeIdentifier -eq 'linux-x64') -and -not $IsLinux) {
    throw 'Linux packaging must run on Linux so native closure and executable permissions can be checked.'
}

if (($RuntimeIdentifier -eq 'win-x64') -and -not $IsWindows) {
    throw 'Windows packaging must run on Windows so the bundled executables can be checked.'
}

$stagingRoot = Join-Path $OutputDirectory ('staging-' + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $stagingRoot $archiveName
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Push-Location $repositoryRoot

try {
    & $Dotnet restore $desktopProject --locked-mode

    if ($LASTEXITCODE -ne 0) {
        throw 'Locked runtime restore failed.'
    }

    & $Dotnet publish $desktopProject --configuration Release --runtime $RuntimeIdentifier --self-contained true --no-restore --output $packageRoot "-p:RuntimeFrameworkVersion=$runtimeVersion" -p:PublishTrimmed=false -p:PublishSingleFile=false -p:PublishAot=false

    if ($LASTEXITCODE -ne 0) {
        throw 'Self-contained publish failed.'
    }

    & (Join-Path $PSScriptRoot 'acquire-tools.ps1') -RuntimeIdentifier $RuntimeIdentifier -Destination (Join-Path $packageRoot 'tools') -CacheDirectory $ToolCacheDirectory -WindowsMkvBuildDirectory $WindowsMkvBuildDirectory
    New-Item -ItemType Directory -Path (Join-Path $packageRoot 'data') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $packageRoot 'portable.flag') -Value 'TrackTrim portable data policy version 1' -Encoding utf8NoBOM
    $licenseRoot = Join-Path $packageRoot 'licenses'
    New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null

    foreach ($document in @('LICENSE', 'README.md', 'THIRD_PARTY_NOTICES.md', 'CONTRIBUTING.md')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $document) -Destination $packageRoot
    }

    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs') -Destination $packageRoot -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'tools.lock.json') -Destination (Join-Path $packageRoot 'tool-provenance.json')

    if ($WindowsMkvBuildDirectory) {
        Copy-Item -LiteralPath (Join-Path $packageRoot 'tools/mkv-source-provenance.json') -Destination (Join-Path $packageRoot 'mkv-source-provenance.json')
    }

    Copy-Item -LiteralPath (Join-Path $packageRoot 'tools/ffmpeg-provenance.json') -Destination (Join-Path $packageRoot 'ffmpeg-provenance.json')
    & (Join-Path $PSScriptRoot 'collect-licenses.ps1') -AssetsFile (Join-Path $repositoryRoot 'src/TrackTrim.Desktop/obj/project.assets.json') -Destination (Join-Path $licenseRoot 'nuget') -AdditionalPackages @("Microsoft.NETCore.App.Runtime.$RuntimeIdentifier/$runtimeVersion") -CacheDirectory (Join-Path $ToolCacheDirectory 'license-materials')
    $runtimeConfiguration = Get-Content -LiteralPath (Join-Path $packageRoot 'TrackTrim.runtimeconfig.json') -Raw | ConvertFrom-Json
    $shippedRuntime = $runtimeConfiguration.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.NETCore.App'

    if ($shippedRuntime.version -ne $runtimeVersion) {
        throw "Expected runtime $runtimeVersion; published runtime is $($shippedRuntime.version)."
    }

    $sdkVersion = & $Dotnet --version

    if ($LASTEXITCODE -ne 0) {
        throw 'Could not record the SDK version.'
    }

    $sourceRevision = & git rev-parse HEAD

    if ($LASTEXITCODE -ne 0) {
        throw 'Could not record the source revision.'
    }

    $sourceTree = & git rev-parse 'HEAD^{tree}'

    if ($LASTEXITCODE -ne 0) {
        throw 'Could not record the source tree.'
    }

    # Read the stored commit headers: history traversal hides parents at CI's shallow boundary.
    $sourceCommit = & git cat-file -p HEAD

    if ($LASTEXITCODE -ne 0) {
        throw 'Could not record the source parents.'
    }

    $sourceParents = @($sourceCommit | Where-Object { $_ -match '^parent [a-f0-9]{40}$' } | ForEach-Object { $_.Substring('parent '.Length) })
    $sourceChanges = @(& git status --porcelain --untracked-files=normal)

    if ($LASTEXITCODE -ne 0) {
        throw 'Could not record the worktree state.'
    }

    $provenance = [ordered]@{
        schemaVersion = 1
        runtimeIdentifier = $RuntimeIdentifier
        sdkVersion = $sdkVersion.Trim()
        runtimeVersion = $shippedRuntime.version
        sourceRevision = $sourceRevision.Trim()
        sourceTree = $sourceTree.Trim()
        sourceParents = $sourceParents
        worktreeDirty = $sourceChanges.Count -gt 0
        configuration = 'Release; self-contained; untrimmed; non-single-file; no AOT'
        operatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        artifactPurpose = 'review and acceptance testing; public-release obligations remain'
    }
    $provenance | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'build-provenance.json') -Encoding utf8NoBOM

    if ($RuntimeIdentifier -eq 'linux-x64') {
        & bash (Join-Path $PSScriptRoot 'inspect-native-dependencies.sh') $packageRoot (Join-Path $packageRoot 'native-dependencies.txt')

        if ($LASTEXITCODE -ne 0) {
            throw 'A packaged ELF dependency was unresolved.'
        }
    }

    & (Join-Path $PSScriptRoot 'verify-package.ps1') -PackageDirectory $packageRoot -FixturePath $FixturePath -Mp4FixturePath $Mp4FixturePath

    $generatedDataDirectory = [IO.Path]::GetFullPath((Join-Path $packageRoot 'data'))
    $stagingPrefix = [IO.Path]::GetFullPath($stagingRoot) + [IO.Path]::DirectorySeparatorChar

    if (-not $generatedDataDirectory.StartsWith($stagingPrefix, [StringComparison]::Ordinal)) {
        throw 'Refusing to clean generated package data outside the checked staging root.'
    }

    Remove-Item -LiteralPath $generatedDataDirectory -Recurse
    New-Item -ItemType Directory -Path $generatedDataDirectory | Out-Null

    $manifest = Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $relativePath = [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relativePath"
    }
    $manifest | Set-Content -LiteralPath (Join-Path $packageRoot 'SHA256SUMS') -Encoding utf8NoBOM

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath
    }

    if ($RuntimeIdentifier -eq 'win-x64') {
        [IO.Compression.ZipFile]::CreateFromDirectory($stagingRoot, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
    }

    if ($RuntimeIdentifier -eq 'linux-x64') {
        & tar -czf $archivePath -C $stagingRoot $archiveName

        if ($LASTEXITCODE -ne 0) {
            throw 'tar archive creation failed.'
        }
    }

    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$archiveHash  $([IO.Path]::GetFileName($archivePath))" | Set-Content -LiteralPath ($archivePath + '.sha256') -Encoding utf8NoBOM
    & python (Join-Path $PSScriptRoot 'distribution-inventory.py') --package-root $packageRoot --archive $archivePath --assets (Join-Path $repositoryRoot 'src/TrackTrim.Desktop/obj/project.assets.json') --output (Join-Path $OutputDirectory "distribution-reports/$RuntimeIdentifier")

    if ($LASTEXITCODE -ne 0) {
        throw 'Distribution inventory failed; the archive is not accepted.'
    }

    Write-Output "Package: $archivePath"
    Write-Output "Unpacked test directory: $packageRoot"
}
finally {
    Pop-Location
}
