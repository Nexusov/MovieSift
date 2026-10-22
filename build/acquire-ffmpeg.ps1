param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64')]
    [string] $RuntimeIdentifier,
    [Parameter(Mandatory)]
    [string] $Destination,
    [Parameter(Mandatory)]
    [string] $CacheDirectory
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$lockPath = Join-Path $PSScriptRoot 'ffmpeg.lock.json'
$toolLock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$destinationPath = [IO.Path]::GetFullPath($Destination)
$cachePath = [IO.Path]::GetFullPath($CacheDirectory)
$materialDirectory = Join-Path $destinationPath 'licenses/ffmpeg'
$recipePath = Join-Path $PSScriptRoot $toolLock.recipe
$recipeHash = (Get-FileHash -LiteralPath $recipePath -Algorithm SHA256).Hash.ToLowerInvariant()
. (Join-Path $PSScriptRoot 'ffmpeg-build-cache.ps1')
New-Item -ItemType Directory -Path $cachePath, $materialDirectory -Force | Out-Null

## Downloads an immutable source archive and rejects a changed cached payload.
function Get-VerifiedSource {
    param([Parameter(Mandatory)] $Artifact)

    $artifactPath = Join-Path $cachePath $Artifact.fileName

    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        Invoke-WebRequest -Uri $Artifact.url -OutFile $artifactPath -TimeoutSec 240
    }

    $actualHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($actualHash -ne $Artifact.sha256) {
        $actualSize = (Get-Item -LiteralPath $artifactPath).Length
        throw "Source hash mismatch: $($Artifact.url); expected=$($Artifact.sha256); actual=$actualHash; bytes=$actualSize. Preserve and inspect the incorrect cached file explicitly."
    }

    return $artifactPath
}

$sourcePath = Get-VerifiedSource -Artifact $toolLock.source
$zlibPath = Get-VerifiedSource -Artifact $toolLock.zlib
$identity = "$RuntimeIdentifier`n$($toolLock.source.sha256)`n$($toolLock.zlib.sha256)`n$recipeHash"
$identityHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).ToLowerInvariant()
$buildCache = Join-Path $cachePath ("ffmpeg-source-$RuntimeIdentifier-" + $identityHash.Substring(0, 20))

if (-not (Test-Path -LiteralPath $buildCache -PathType Container)) {
    $buildDirectory = Join-Path $cachePath ('ffmpeg-build-' + [Guid]::NewGuid().ToString('N'))
    $bash = 'bash'

    if ($IsWindows) {
        $bash = 'C:/msys64/usr/bin/bash.exe'

        if (-not (Test-Path -LiteralPath $bash -PathType Leaf)) {
            throw 'A source build requires MSYS2 Bash, GNU make, NASM and the documented MinGW-w64 compiler. A verified existing source-build cache may be reused without them.'
        }
    }

    & $bash $recipePath $RuntimeIdentifier $sourcePath $zlibPath $buildDirectory

    if ($LASTEXITCODE) {
        throw "The pinned FFmpeg source build failed. Its owned diagnostics remain at $buildDirectory."
    }

    [IO.Directory]::Move((Join-Path $buildDirectory 'result'), $buildCache)
    Remove-OwnedFfmpegBuild -CacheDirectory $cachePath -BuildDirectory $buildDirectory
}

$receiptPath = Join-Path $buildCache 'build-receipt.json'
$receipt = Read-VerifiedFfmpegBuild -CacheDirectory $buildCache -RuntimeIdentifier $RuntimeIdentifier -ToolLock $toolLock -RecipeHash $recipeHash
$extension = ''

if ($RuntimeIdentifier -eq 'win-x64') {
    $extension = '.exe'
}

$sourceBuildMaterials = Join-Path $materialDirectory 'source-build'
New-Item -ItemType Directory -Path $sourceBuildMaterials -Force | Out-Null
Copy-Item -LiteralPath $receiptPath -Destination $sourceBuildMaterials

foreach ($file in $receipt.materials) {
    Copy-Item -LiteralPath (Join-Path $buildCache $file.path) -Destination $sourceBuildMaterials
}

Copy-Item -LiteralPath $sourcePath, $zlibPath -Destination $materialDirectory
$recipeMaterials = Join-Path $materialDirectory 'build-recipes'
New-Item -ItemType Directory -Path $recipeMaterials -Force | Out-Null
Copy-Item -LiteralPath $recipePath, $lockPath -Destination $recipeMaterials
$provenance = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
$provenance.recipeSha256 = $recipeHash
$provenance.buildReceiptSha256 = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
$provenance | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $destinationPath 'ffmpeg-provenance.json') -Encoding utf8NoBOM

$extractNotices = @'
from pathlib import Path
import sys
import tarfile

source_path, zlib_path, output_path, commit = sys.argv[1:]
output = Path(output_path)
selections = [(source_path, "FFmpeg-" + commit + "/", "source-notices", ("COPYING.LGPLv2.1", "COPYING.LGPLv3", "COPYING.GPLv2", "COPYING.GPLv3", "LICENSE.md")),
              (zlib_path, "zlib-1.3.2/", "zlib-notices", ("README", "zlib.h"))]
for archive_path, prefix, directory, names in selections:
    with tarfile.open(archive_path) as archive:
        for name in names:
            member = archive.getmember(prefix + name)
            if not member.isfile():
                raise ValueError("A required source notice is not regular")
            target = output / directory / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(archive.extractfile(member).read())
(output / "LICENSE.txt").write_bytes((output / "source-notices/COPYING.LGPLv2.1").read_bytes())
'@
& python -c $extractNotices $sourcePath $zlibPath $materialDirectory $toolLock.source.commit

if ($LASTEXITCODE) {
    throw 'Could not collect the pinned FFmpeg/zlib notices.'
}

$members = @()

foreach ($tool in @('ffmpeg', 'ffprobe')) {
    $fileName = $tool + $extension
    $executable = Join-Path $destinationPath $fileName

    if (Test-Path -LiteralPath $executable) {
        throw "Acquisition never overwrites an existing executable: $executable."
    }

    Copy-Item -LiteralPath (Join-Path $buildCache $fileName) -Destination $executable

    if ($IsLinux) {
        & chmod u+x $executable

        if ($LASTEXITCODE) {
            throw "Could not set executable permissions for $tool."
        }
    }

    $versionOutput = @(& $executable -version 2>&1 | ForEach-Object { "$_" })
    $versionExitCode = $LASTEXITCODE
    $configurationLines = @($versionOutput | Where-Object { $_.StartsWith('configuration: ', [StringComparison]::Ordinal) })
    $configuration = ''

    if ($configurationLines.Count -eq 1) {
        $configuration = $configurationLines[0]
    }

    $invalidConfiguration = $configuration.Contains('--enable-nonfree') -or $configuration.Contains('--enable-gpl') -or
        $configuration.Contains('--enable-version3') -or $configuration.Contains('--enable-lib') -or
        -not $configuration.Contains('--disable-autodetect') -or -not $configuration.Contains('--disable-network') -or
        -not $configuration.Contains('--disable-everything') -or -not $configuration.Contains('--enable-zlib')

    $firstVersionLine = '<no version output>'

    if ($versionOutput.Count) {
        $firstVersionLine = $versionOutput[0]
    }

    $hasExpectedVersion = $firstVersionLine.StartsWith("$tool version $($toolLock.version) ", [StringComparison]::Ordinal)

    if ($versionExitCode -or -not $hasExpectedVersion -or $invalidConfiguration) {
        $maximumDiagnosticCharacters = 512
        $diagnosticLine = $firstVersionLine.Substring(0, [Math]::Min($firstVersionLine.Length, $maximumDiagnosticCharacters))
        throw "The source-built $tool failed its exact version/configuration check: exit=$versionExitCode; expectedVersion=$hasExpectedVersion; configurationAccepted=$(-not $invalidConfiguration); firstLine='$diagnosticLine'."
    }

    $versionOutput | Set-Content -LiteralPath (Join-Path $materialDirectory ($tool + '-version.txt')) -Encoding utf8NoBOM
    $configuration | Set-Content -LiteralPath (Join-Path $materialDirectory ($tool + '-buildconf.txt')) -Encoding utf8NoBOM
    $members += [ordered]@{ path = $fileName; artifactKind = 'sourceBuild'; upstreamMember = ''; buildReceiptSha256 = $provenance.buildReceiptSha256 }
}

$members | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $materialDirectory 'acquired-members.json') -Encoding utf8NoBOM
& python (Join-Path $PSScriptRoot 'ffmpeg-runtime-dependencies.py') --directory $destinationPath --runtime-identifier $RuntimeIdentifier --output (Join-Path $materialDirectory 'runtime-dependencies.json')

if ($LASTEXITCODE) {
    throw 'Could not establish the actual source-built FFmpeg runtime dependency closure.'
}

Write-Output "Built FFmpeg/ffprobe $($toolLock.version); exact FFmpeg/zlib sources, local build recipe, toolchain and worker receipts included."
