param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64')]
    [string] $RuntimeIdentifier,
    [Parameter(Mandatory)]
    [string] $Destination,
    [string] $CacheDirectory = (Join-Path $PSScriptRoot '../artifacts/downloads'),
    [string] $WindowsMkvBuildDirectory = $env:TRACKTRIM_WINDOWS_MKV_BUILD_DIRECTORY
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$toolLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tools.lock.json') -Raw | ConvertFrom-Json
$distribution = $toolLock.$RuntimeIdentifier
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$cachePath = [System.IO.Path]::GetFullPath($CacheDirectory)
$sourceBuiltWindows = $RuntimeIdentifier -eq 'win-x64' -and -not [string]::IsNullOrWhiteSpace($WindowsMkvBuildDirectory)
$requiresWindowsSourceBuild = $RuntimeIdentifier -eq 'win-x64' -and $distribution.PSObject.Properties.Name -contains 'kind' -and $distribution.kind -eq 'source-build'

if ($requiresWindowsSourceBuild -and -not $sourceBuiltWindows) {
    throw 'The pinned Windows MKV source build is required. Provide -WindowsMkvBuildDirectory (or TRACKTRIM_WINDOWS_MKV_BUILD_DIRECTORY) pointing to the complete verified worker/material directory; official binaries are not a fallback.'
}

if ($WindowsMkvBuildDirectory -and $RuntimeIdentifier -ne 'win-x64') {
    throw 'A Windows MKV source build cannot be used for another runtime identifier.'
}

New-Item -ItemType Directory -Path $cachePath -Force | Out-Null

if (Test-Path -LiteralPath $destinationPath) {
    $existingItems = Get-ChildItem -LiteralPath $destinationPath -Force

    if ($existingItems.Count) {
        throw "Tool destination must be empty; existing tools are never overwritten: $destinationPath"
    }
}

New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
$archivePath = $null
$actualHash = $null

if (-not $sourceBuiltWindows) {
    $archiveName = [System.IO.Path]::GetFileName(([Uri] $distribution.url).AbsolutePath)
    $archivePath = Join-Path $cachePath $archiveName

    if (!(Test-Path -LiteralPath $archivePath)) {
        Invoke-WebRequest -Uri $distribution.url -OutFile $archivePath -TimeoutSec 180
    }

    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($actualHash -ne $distribution.sha256) {
        throw "Tool archive hash mismatch: expected $($distribution.sha256), received $actualHash. Remove the incorrect cached archive explicitly."
    }
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

    if ($sourceBuiltWindows) {
        $WindowsMkvBuildDirectory = [IO.Path]::GetFullPath($WindowsMkvBuildDirectory)
        & python (Join-Path $PSScriptRoot 'distribution-inventory.py') --validate-windows-mkv-build $WindowsMkvBuildDirectory

        if ($LASTEXITCODE) {
            throw 'The Windows MKV source-build worker/material directory failed its pinned validation.'
        }

        $mkvProvenancePath = Join-Path $WindowsMkvBuildDirectory 'mkv-source-provenance.json'
        $mkvSourceProvenance = Get-Content -LiteralPath $mkvProvenancePath -Raw | ConvertFrom-Json

        if ($mkvSourceProvenance.source.sha256 -cne $toolLock.source.sha256) {
            throw 'The MKV source build and the application must use the same pinned 102.0 source archive.'
        }

        $mkvSourceReceipt = Get-Content -LiteralPath (Join-Path $WindowsMkvBuildDirectory $mkvSourceProvenance.receiptPath) -Raw | ConvertFrom-Json
        $mkvBuildLock = Get-Content -LiteralPath (Join-Path $WindowsMkvBuildDirectory 'licenses/mkv-source-build/mkv-source.lock.json') -Raw | ConvertFrom-Json
        $mkvDependencyPackages = Get-Content -LiteralPath (Join-Path $WindowsMkvBuildDirectory 'licenses/mkv-source-build/dependency-packages.json') -Raw | ConvertFrom-Json
        $mkvBuildFiles = @('mkv-source-provenance.json', $mkvSourceProvenance.receiptPath) +
            @($mkvSourceReceipt.workers.path) + @($mkvSourceReceipt.dependencies.path) +
            @($mkvSourceReceipt.materials | ForEach-Object { 'licenses/mkv-source-build/' + $_.path })

        foreach ($relativePath in $mkvBuildFiles) {
            $target = Join-Path $destinationPath $relativePath

            if (Test-Path -LiteralPath $target) {
                throw "Acquisition never overwrites an existing source-built member: $target."
            }

            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $WindowsMkvBuildDirectory $relativePath) -Destination $target
        }
    }

    if (-not $sourceBuiltWindows) {
        $stageDirectory = Join-Path $cachePath ('extract-' + [Guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $archivePath -DestinationPath $stageDirectory
        $distributionDirectory = Join-Path $stageDirectory 'mkvtoolnix'
        Copy-Item -LiteralPath (Join-Path $distributionDirectory 'mkvmerge.exe') -Destination $destinationPath
        Copy-Item -LiteralPath (Join-Path $distributionDirectory 'mkvextract.exe') -Destination $destinationPath
        Copy-Item -LiteralPath (Join-Path $distributionDirectory 'doc') -Destination $licenseDirectory -Recurse
        & python (Join-Path $PSScriptRoot 'select-tool-payload.py') --runtime-identifier $RuntimeIdentifier --source $distributionDirectory --destination $destinationPath

        if ($LASTEXITCODE) {
            throw 'The pinned Windows GUI-asset omission policy did not match the upstream payload.'
        }
    }
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
    # Select only the unchanged, hash-verified CLI closure; every omitted upstream file is receipted.
    & python (Join-Path $PSScriptRoot 'select-tool-payload.py') --runtime-identifier $RuntimeIdentifier --source $extractedDirectory --destination $destinationPath

    if ($LASTEXITCODE) {
        throw 'The pinned Linux CLI library selection did not match the upstream payload.'
    }

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

if ($sourceBuiltWindows) {
    $applicableMaterials = @($applicableMaterials | Where-Object { $_.component -notin @('Qt Core', 'FLAC library') })
}

foreach ($material in $applicableMaterials) {
    $materialCache = Join-Path $cachePath $material.sha256

    if ($material.PSObject.Properties.Name -contains 'localFile') {
        $materialCache = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $material.localFile))
        $buildPrefix = [IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar

        if (-not $materialCache.StartsWith($buildPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A collected notice must remain inside the repository build directory.'
        }
    }
    elseif (-not (Test-Path -LiteralPath $materialCache -PathType Leaf)) {
        Invoke-WebRequest -Uri $material.url -OutFile $materialCache -TimeoutSec 240
    }

    if ((Get-FileHash -LiteralPath $materialCache -Algorithm SHA256).Hash.ToLowerInvariant() -ne $material.sha256) {
        throw "Upstream source/notice material hash mismatch: $($material.path)."
    }

    $correspondingPayloads = @()

    if ($material.PSObject.Properties.Name -contains 'correspondsTo') {
        $correspondingPayloads = @($material.correspondsTo)
    }

    foreach ($payload in $correspondingPayloads) {
        $payloadPath = Join-Path $destinationPath $payload.path
        $matchesMaterial = (Test-Path -LiteralPath $payloadPath -PathType Leaf) -and
            (Get-Item -LiteralPath $payloadPath).Length -eq $payload.size -and
            (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $payload.sha256

        if (-not $matchesMaterial) {
            throw "A source material no longer corresponds to the actual shipped library: $($payload.path)."
        }
    }

    $materialDirectory = 'upstream-terms'

    if ($material.PSObject.Properties.Name -contains 'outputDirectory') {

        if ($material.outputDirectory -ne 'corresponding-source') {
            throw 'Unsupported included-material directory.'
        }

        $materialDirectory = $material.outputDirectory
    }

    $materialTarget = Join-Path $licenseDirectory (Join-Path $materialDirectory $material.path)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($materialTarget)) -Force | Out-Null
    Copy-Item -LiteralPath $materialCache -Destination $materialTarget
}

ConvertTo-Json -InputObject $applicableMaterials -Depth 12 | Set-Content -LiteralPath (Join-Path $licenseDirectory 'upstream-materials.json') -Encoding utf8NoBOM
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

    $material = $applicableMaterials | Where-Object {
        $directory = 'upstream-terms'

        if ($_.PSObject.Properties.Name -contains 'outputDirectory') {
            $directory = $_.outputDirectory
        }

        $relativePath -eq ('licenses/' + $directory + '/' + $_.path)
    }

    if ($material) {
        $origin = $material.relationship
        $originArchiveHash = $material.sha256

        if ($material.PSObject.Properties.Name -contains 'url') {
            $originArchive = $material.url
        }

        if ($material.PSObject.Properties.Name -contains 'sourceArtifact') {
            $originArchive = $material.sourceArtifact.url
            $originArchiveHash = $material.sourceArtifact.sha256
            $originMember = $material.sourceArtifact.member
        }
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

    if ($relativePath -eq ('licenses/corresponding-source/' + $sourceArchiveName)) {
        $origin = 'complete pinned source archive'
        $originArchive = $toolLock.source.url
        $originArchiveHash = $sourceHash
    }

    $isWindowsPayload = $RuntimeIdentifier -eq 'win-x64' -and -not $sourceBuiltWindows -and ($relativePath -in @('mkvmerge.exe', 'mkvextract.exe') -or $relativePath.StartsWith('licenses/doc/'))

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

    $isMkvSourceBuildMember = $sourceBuiltWindows -and $relativePath -in $mkvBuildFiles

    if ($isMkvSourceBuildMember) {
        $origin = 'source-built MKVToolNix worker/dependency/material; exact pinned sources, local recipe, configuration, complete worker/library hashes and PE imports in licenses/mkv-source-build/build-receipt.json'
        $originArchive = ''
        $originArchiveHash = ''
        $originMember = ''

        if ($relativePath -cin @('mkvmerge.exe', 'mkvextract.exe')) {
            $originArchive = $toolLock.source.url
            $originArchiveHash = $sourceHash
        }

        $dependency = @($mkvSourceReceipt.dependencies | Where-Object path -CEQ $relativePath)

        if ($dependency.Count -eq 1) {
            $package = $mkvBuildLock.packages | Where-Object name -CEQ $dependency[0].packageName
            $origin = 'unchanged runtime DLL from the exact pinned MSYS2 binary package; complete corresponding source/recipe/notices are included and bound by the MKV source-build receipt'
            $originArchive = $package.url
            $originArchiveHash = $package.sha256
            $originMember = $dependency[0].upstreamMember
        }

        $sourceArtifacts = @($mkvBuildLock.source) + @($mkvBuildLock.packages | Where-Object {
            $_.PSObject.Properties.Name -contains 'sourcePackage'
        } | ForEach-Object sourcePackage)

        foreach ($artifact in $sourceArtifacts) {

            if ($relativePath -ceq ('licenses/mkv-source-build/source/' + $artifact.fileName)) {
                $origin = 'complete hash-pinned source archive used by the Windows MKV source-build closure'
                $originArchive = $artifact.url
                $originArchiveHash = $artifact.sha256
            }
        }

        $recipeLicense = $mkvBuildLock.recipeSnapshot.license

        if ($relativePath -ceq ('licenses/mkv-source-build/' + $recipeLicense.fileName)) {
            $origin = 'unchanged author notice for the exact pinned MSYS2 MINGW-packages recipe snapshot'
            $originArchive = $recipeLicense.url
            $originArchiveHash = $recipeLicense.sha256
        }

        foreach ($packageEvidence in $mkvDependencyPackages) {

            if ($packageEvidence.PSObject.Properties.Name -notcontains 'sourceNoticeEvidence') {
                continue
            }

            foreach ($notice in $packageEvidence.sourceNoticeEvidence.members) {

                if ($relativePath -ceq ('licenses/mkv-source-build/' + $notice.path)) {
                    $origin = 'exact dependency notice from the pinned nested source archive; sourceNoticeEvidence binds the outer package, nested archive and notice member hashes'
                    $originArchive = $packageEvidence.sourcePackage.url
                    $originArchiveHash = $packageEvidence.sourcePackage.sha256
                    $originMember = $packageEvidence.sourceNoticeEvidence.sourcePackageMember
                }
            }
        }
    }

    if ($ffmpegMembers.ContainsKey($relativePath)) {
        $origin = 'source-built worker; exact inputs, local recipe, compiler and complete worker hash in licenses/ffmpeg/source-build/build-receipt.json'
        $originArchive = $ffmpegLock.source.url
        $originArchiveHash = $ffmpegLock.source.sha256
    }

    foreach ($materialKind in @('source', 'zlib')) {

        if ($relativePath -eq ('licenses/ffmpeg/' + $ffmpegLock.$materialKind.fileName)) {
            $origin = 'complete pinned FFmpeg ' + $materialKind + ' source archive'
            $originArchive = $ffmpegLock.$materialKind.url
            $originArchiveHash = $ffmpegLock.$materialKind.sha256
        }
    }

    $isGeneratedFfmpegMaterial = ($relativePath.StartsWith('licenses/ffmpeg/') -and -not $originArchiveHash) -or $relativePath -eq 'ffmpeg-provenance.json'

    if ($isGeneratedFfmpegMaterial) {
        $origin = 'generated by build/acquire-ffmpeg.ps1 and build/ffmpeg.lock.json; exact executed version/configuration, local source-build recipe and bound source/compiler/worker receipt'
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
Write-Output "$mergeVersion; $extractVersion; verified MKV source SHA-256 $sourceHash"

if (-not $sourceBuiltWindows) {
    Write-Output "Verified upstream binary archive SHA-256 $actualHash."
}
Write-Output "Acquired pinned tools at $destinationPath. Public redistribution obligations for all bundled components still require review."
