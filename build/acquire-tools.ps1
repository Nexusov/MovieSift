param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64')]
    [string] $RuntimeIdentifier,
    [Parameter(Mandatory)]
    [string] $Destination,
    [string] $CacheDirectory = (Join-Path $PSScriptRoot '../artifacts/downloads')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$toolLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tools.lock.json') -Raw | ConvertFrom-Json
$distribution = $toolLock.$RuntimeIdentifier
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$cachePath = [System.IO.Path]::GetFullPath($CacheDirectory)
New-Item -ItemType Directory -Path $cachePath -Force | Out-Null

if (Test-Path -LiteralPath $destinationPath) {
    $existingItems = Get-ChildItem -LiteralPath $destinationPath -Force

    if ($existingItems.Count) {
        throw "Tool destination must be empty; existing tools are never overwritten: $destinationPath"
    }
}

New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
$archiveName = [System.IO.Path]::GetFileName(([Uri] $distribution.url).AbsolutePath)
$archivePath = Join-Path $cachePath $archiveName

if (!(Test-Path -LiteralPath $archivePath)) {
    Invoke-WebRequest -Uri $distribution.url -OutFile $archivePath -TimeoutSec 180
}

$actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()

if ($actualHash -ne $distribution.sha256) {
    throw "Tool archive hash mismatch: expected $($distribution.sha256), received $actualHash. Remove the incorrect cached archive explicitly."
}

$licenseDirectory = Join-Path $destinationPath 'licenses'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
$sourceArchiveName = [System.IO.Path]::GetFileName(([Uri] $toolLock.source.url).AbsolutePath)
$sourceArchivePath = Join-Path $cachePath $sourceArchiveName

if (!(Test-Path -LiteralPath $sourceArchivePath)) {
    Invoke-WebRequest -Uri $toolLock.source.url -OutFile $sourceArchivePath -TimeoutSec 180
}

$sourceHash = (Get-FileHash -LiteralPath $sourceArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()

if ($sourceHash -ne $toolLock.source.sha256) {
    throw "MKVToolNix source archive hash mismatch: expected $($toolLock.source.sha256), received $sourceHash."
}

$correspondingSourceDirectory = Join-Path $licenseDirectory 'corresponding-source'
New-Item -ItemType Directory -Path $correspondingSourceDirectory -Force | Out-Null
Copy-Item -LiteralPath $sourceArchivePath -Destination $correspondingSourceDirectory
$extractSourceNotices = @'
import pathlib
import sys
import tarfile

archive_path, output_path, version = sys.argv[1:]
output_directory = pathlib.Path(output_path)
prefix = "mkvtoolnix-" + version + "/"
build_materials = ("Building.for.Windows.md", "packaging/windows/setup_cross_compilation_env.sh", "packaging/appimage/build.sh", "configure.ac")
with tarfile.open(archive_path, "r:xz") as archive:
    source_members = {member.name for member in archive.getmembers()}
    required_materials = build_materials + ("lib/fmt/LICENSE", "lib/libebml/LICENSE.LGPL", "lib/libmatroska/LICENSE.LGPL", "lib/librmff/COPYING")
    if any(prefix + name not in source_members for name in required_materials):
        raise ValueError("A required pinned source license/build material is absent")
    for member in archive.getmembers():
        relative_name = member.name.removeprefix(prefix)
        vendor_notices = ("lib/fmt/LICENSE", "lib/libebml/LICENSE.LGPL", "lib/libmatroska/LICENSE.LGPL", "lib/librmff/COPYING")
        is_notice = relative_name in ("COPYING", "README.md") or relative_name.startswith("doc/licenses/") or relative_name in vendor_notices
        is_build_material = relative_name in build_materials or relative_name.startswith("ac/") and relative_name.endswith(".m4")
        if not member.isfile() or not member.name.startswith(prefix) or not (is_notice or is_build_material):
            continue
        destination_path = output_directory / relative_name
        if is_build_material:
            destination_path = output_directory.parent / "source-build-materials" / relative_name
        destination_path.parent.mkdir(parents=True, exist_ok=True)
        destination_path.write_bytes(archive.extractfile(member).read())
'@
& python -c $extractSourceNotices $sourceArchivePath (Join-Path $licenseDirectory 'source-notices') $toolLock.version

if ($LASTEXITCODE) {
    throw 'Python could not extract pinned source notices. Python 3.9+ is a build prerequisite, not an application runtime dependency.'
}

if ($RuntimeIdentifier -eq 'win-x64') {

    if (!$IsWindows) {
        throw 'Windows tool acquisition/version acceptance must run on Windows.'
    }

    $stageDirectory = Join-Path $cachePath ('extract-' + [Guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $archivePath -DestinationPath $stageDirectory
    $distributionDirectory = Join-Path $stageDirectory 'mkvtoolnix'
    Copy-Item -LiteralPath (Join-Path $distributionDirectory 'mkvmerge.exe') -Destination $destinationPath
    Copy-Item -LiteralPath (Join-Path $distributionDirectory 'mkvextract.exe') -Destination $destinationPath
    Copy-Item -LiteralPath (Join-Path $distributionDirectory 'doc') -Destination $licenseDirectory -Recurse
    Copy-Item -LiteralPath (Join-Path $distributionDirectory 'data') -Destination $licenseDirectory -Recurse
    $mergeExecutable = Join-Path $destinationPath 'mkvmerge.exe'
    $extractExecutable = Join-Path $destinationPath 'mkvextract.exe'
}

if ($RuntimeIdentifier -eq 'linux-x64') {

    if (!$IsLinux) {
        throw 'Linux AppImage extraction/dependency acceptance requires a Linux build host.'
    }

    & chmod u+x $archivePath

    if ($LASTEXITCODE) {
        throw 'Could not set the build-time AppImage executable permission.'
    }

    $stageDirectory = Join-Path $cachePath ('extract-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stageDirectory | Out-Null
    Push-Location $stageDirectory
    try {
        # Type-2 AppImage extraction needs no FUSE mount and happens only at build time.
        & $archivePath --appimage-extract | Out-Null

        if ($LASTEXITCODE) {
            throw 'The pinned upstream AppImage could not be extracted.'
        }
    }
    finally {
        Pop-Location
    }

    $extractedDirectory = Join-Path $stageDirectory 'squashfs-root'
    $applicationRoot = Join-Path $destinationPath 'app-root'
    $binaryDirectory = Join-Path $applicationRoot 'usr/bin'
    New-Item -ItemType Directory -Path $binaryDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $extractedDirectory 'usr/bin/mkvmerge') -Destination $binaryDirectory
    Copy-Item -LiteralPath (Join-Path $extractedDirectory 'usr/bin/mkvextract') -Destination $binaryDirectory
    # Preserve the upstream library closure conservatively, including its Qt-linked CLI dependencies.
    Copy-Item -LiteralPath (Join-Path $extractedDirectory 'usr/lib') -Destination (Join-Path $applicationRoot 'usr') -Recurse

    foreach ($toolName in @('mkvmerge', 'mkvextract')) {
        $wrapper = @'
#!/bin/sh
set -eu
tool_directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
application_root="$tool_directory/app-root"
export LD_LIBRARY_PATH="$application_root/usr/lib:$application_root/usr/lib/x86_64-linux-gnu:$application_root/lib:$application_root/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$application_root/usr/bin/TOOL_NAME" "$@"
'@
        $wrapperPath = Join-Path $destinationPath $toolName
        [System.IO.File]::WriteAllText($wrapperPath, $wrapper.Replace('TOOL_NAME', $toolName).Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
        & chmod u+x $wrapperPath (Join-Path $binaryDirectory $toolName)

        if ($LASTEXITCODE) {
            throw "Could not set executable permissions for $toolName."
        }
    }

    $mergeExecutable = Join-Path $destinationPath 'mkvmerge'
    $extractExecutable = Join-Path $destinationPath 'mkvextract'
}

$mergeVersion = & $mergeExecutable --version

if ($LASTEXITCODE -or !$mergeVersion.StartsWith("mkvmerge v$($toolLock.version) ")) {
    throw "The acquired mkvmerge failed its actual version check: $mergeVersion"
}

$extractVersion = & $extractExecutable --version

if ($LASTEXITCODE -or !$extractVersion.StartsWith("mkvextract v$($toolLock.version) ")) {
    throw "The acquired mkvextract failed its actual version check: $extractVersion"
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'tools.lock.json') -Destination (Join-Path $destinationPath 'provenance.json')
$materialLockPath = Join-Path $PSScriptRoot 'tool-materials.lock.json'
$materialLock = Get-Content -LiteralPath $materialLockPath -Raw | ConvertFrom-Json
$applicableMaterials = @($materialLock.materials | Where-Object runtimeIdentifier -EQ $RuntimeIdentifier)

foreach ($material in $applicableMaterials) {
    $materialCache = Join-Path $cachePath $material.sha256

    if (-not (Test-Path -LiteralPath $materialCache -PathType Leaf)) {
        Invoke-WebRequest -Uri $material.url -OutFile $materialCache -TimeoutSec 120
    }

    if ((Get-FileHash -LiteralPath $materialCache -Algorithm SHA256).Hash.ToLowerInvariant() -ne $material.sha256) {
        throw "Upstream terms/header material hash mismatch: $($material.url)."
    }

    $materialTarget = Join-Path $licenseDirectory (Join-Path 'upstream-terms' $material.path)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($materialTarget)) -Force | Out-Null
    Copy-Item -LiteralPath $materialCache -Destination $materialTarget
}

ConvertTo-Json -InputObject $applicableMaterials -Depth 5 | Set-Content -LiteralPath (Join-Path $licenseDirectory 'upstream-materials.json') -Encoding utf8NoBOM
& (Join-Path $PSScriptRoot 'acquire-ffmpeg.ps1') -RuntimeIdentifier $RuntimeIdentifier -Destination $destinationPath -CacheDirectory $cachePath
$ffmpegLock = Get-Content -LiteralPath (Join-Path $destinationPath 'ffmpeg-provenance.json') -Raw | ConvertFrom-Json
$ffmpegMembers = @{}

foreach ($member in Get-Content -LiteralPath (Join-Path $licenseDirectory 'ffmpeg/acquired-members.json') -Raw | ConvertFrom-Json) {
    $ffmpegMembers[$member.path] = $member
}

$fileProvenance = @(Get-ChildItem -LiteralPath $destinationPath -File -Recurse -Force | ForEach-Object {
    $relativePath = [IO.Path]::GetRelativePath($destinationPath, $_.FullName).Replace('\', '/')
    $origin = 'generated by build/acquire-tools.ps1 or build/tools.lock.json'
    $originArchive = ''
    $originArchiveHash = ''
    $originMember = ''

    if ($relativePath.StartsWith('licenses/upstream-terms/')) {
        $materialPath = $relativePath.Substring('licenses/upstream-terms/'.Length)
        $material = $applicableMaterials | Where-Object path -EQ $materialPath
        $origin = $material.relationship
        $originArchive = $material.url
        $originArchiveHash = $material.sha256
    }

    if ($relativePath.StartsWith('licenses/source-notices/')) {
        $origin = 'member extracted from the pinned source archive'
        $originArchive = $toolLock.source.url
        $originArchiveHash = $sourceHash
        $originMember = "mkvtoolnix-$($toolLock.version)/" + $relativePath.Substring('licenses/source-notices/'.Length)
    }

    if ($relativePath.StartsWith('licenses/source-build-materials/')) {
        $origin = 'build recipe extracted from the pinned source archive; not proof of published binary build inputs'
        $originArchive = $toolLock.source.url
        $originArchiveHash = $sourceHash
        $originMember = "mkvtoolnix-$($toolLock.version)/" + $relativePath.Substring('licenses/source-build-materials/'.Length)
    }

    if ($relativePath.StartsWith('licenses/corresponding-source/')) {
        $origin = 'complete pinned source archive'
        $originArchive = $toolLock.source.url
        $originArchiveHash = $sourceHash
    }

    $isWindowsPayload = $RuntimeIdentifier -eq 'win-x64' -and ($relativePath -in @('mkvmerge.exe', 'mkvextract.exe') -or $relativePath.StartsWith('licenses/doc/') -or $relativePath.StartsWith('licenses/data/'))

    if ($isWindowsPayload) {
        $origin = 'unchanged member of the pinned upstream binary ZIP'
        $originArchive = $distribution.url
        $originArchiveHash = $actualHash
        $originMember = 'mkvtoolnix/' + $relativePath.Replace('licenses/', '')
    }

    if ($relativePath.StartsWith('app-root/')) {
        $origin = 'unchanged retained file from the pinned upstream AppImage'
        $originArchive = $distribution.url
        $originArchiveHash = $actualHash
        $originMember = $relativePath.Substring('app-root/'.Length)
    }

    if ($ffmpegMembers.ContainsKey($relativePath)) {
        $member = $ffmpegMembers[$relativePath]
        $artifact = $ffmpegLock.$RuntimeIdentifier

        if ($member.artifactKind -ne 'binary') {
            $artifact = $ffmpegLock.($member.artifactKind)
        }

        $origin = 'unchanged selected member of the pinned FFmpeg ' + $member.artifactKind + ' artifact'
        $originArchive = $artifact.url
        $originArchiveHash = $artifact.sha256
        $originMember = $member.upstreamMember
    }

    foreach ($materialKind in @('source', 'buildRecipes')) {

        if ($relativePath -eq ('licenses/ffmpeg/' + $ffmpegLock.$materialKind.fileName)) {
            $origin = 'complete pinned FFmpeg ' + $materialKind + ' archive'
            $originArchive = $ffmpegLock.$materialKind.url
            $originArchiveHash = $ffmpegLock.$materialKind.sha256
        }
    }

    if ($relativePath -eq 'licenses/ffmpeg/checksums.sha256') {
        $origin = 'published FFmpeg binary checksum list'
        $originArchive = $ffmpegLock.checksums.url
        $originArchiveHash = $ffmpegLock.checksums.sha256
    }

    $isGeneratedFfmpegMaterial = ($relativePath.StartsWith('licenses/ffmpeg/') -and -not $originArchiveHash) -or $relativePath -eq 'ffmpeg-provenance.json'

    if ($isGeneratedFfmpegMaterial) {
        $origin = 'generated by build/acquire-ffmpeg.ps1 and build/ffmpeg.lock.json; exact executed version/configuration or pinned recipe declarations'
    }

    [ordered]@{
        path = $relativePath
        size = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        origin = $origin
        upstreamArchive = $originArchive
        upstreamArchiveSha256 = $originArchiveHash
        upstreamMember = $originMember
    }
})
$fileProvenance | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destinationPath 'file-provenance.json') -Encoding utf8NoBOM
Write-Output "$mergeVersion; $extractVersion; verified archive SHA-256 $actualHash"
Write-Output "Acquired pinned tools at $destinationPath. Public redistribution obligations for all bundled components still require review."
