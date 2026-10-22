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
$toolLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ffmpeg.lock.json') -Raw | ConvertFrom-Json
$distribution = $toolLock.$RuntimeIdentifier
$destinationPath = [IO.Path]::GetFullPath($Destination)
$cachePath = [IO.Path]::GetFullPath($CacheDirectory)
$materialDirectory = Join-Path $destinationPath 'licenses/ffmpeg'
New-Item -ItemType Directory -Path $cachePath, $materialDirectory -Force | Out-Null

## Downloads one immutable artifact and rejects a changed cached or upstream payload.
function Get-VerifiedArtifact {
    param([Parameter(Mandatory)] $Artifact)

    $fileName = [IO.Path]::GetFileName(([Uri] $Artifact.url).AbsolutePath)

    if ($Artifact.PSObject.Properties.Name -contains 'fileName') {
        $fileName = $Artifact.fileName
    }

    $artifactPath = Join-Path $cachePath $fileName

    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        Invoke-WebRequest -Uri $Artifact.url -OutFile $artifactPath -TimeoutSec 240
    }

    $actualHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($actualHash -ne $Artifact.sha256) {
        throw "FFmpeg artifact hash mismatch: $($Artifact.url); expected $($Artifact.sha256), received $actualHash."
    }

    return $artifactPath
}

$archivePath = Get-VerifiedArtifact -Artifact $distribution
$checksumPath = Get-VerifiedArtifact -Artifact $toolLock.checksums
$sourcePath = Get-VerifiedArtifact -Artifact $toolLock.source
$recipePath = Get-VerifiedArtifact -Artifact $toolLock.buildRecipes
$archiveName = [IO.Path]::GetFileName($archivePath)
$checksumLine = "$($distribution.sha256)  $archiveName"

if (-not ((Get-Content -LiteralPath $checksumPath) -ccontains $checksumLine)) {
    throw 'The pinned FFmpeg artifact is absent from the verified publisher checksum list.'
}

$extractPayload = @'
import json
from pathlib import Path
import sys
import tarfile
import zipfile

archive_path, source_path, recipe_path, output_path, root_name, source_commit, recipe_commit, runtime_identifier = sys.argv[1:]
output = Path(output_path)
materials = output / "licenses/ffmpeg"
origins = []

def store(name, data, archive_kind, member):
    """Write only explicitly selected regular members, without archive path extraction."""
    destination = output / name
    if destination.exists():
        raise ValueError("FFmpeg acquisition never overwrites an existing file: " + name)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(data)
    origins.append({"path": name, "artifactKind": archive_kind, "upstreamMember": member})

extension = ".exe" if runtime_identifier == "win-x64" else ""
selected = {f"{root_name}/bin/{tool}{extension}": tool + extension for tool in ("ffmpeg", "ffprobe")}
selected[f"{root_name}/LICENSE.txt"] = "licenses/ffmpeg/LICENSE.txt"
for name in ("ffmpeg.html", "ffprobe.html", "ffmpeg-formats.html", "ffmpeg-codecs.html", "ffmpeg-protocols.html", "default.css", "style.min.css", "bootstrap.min.css"):
    selected[f"{root_name}/doc/{name}"] = "licenses/ffmpeg/doc/" + name
found = set()
if zipfile.is_zipfile(archive_path):
    with zipfile.ZipFile(archive_path) as archive:
        for member in archive.infolist():
            if member.filename in selected and not member.is_dir():
                if member.filename in found:
                    raise ValueError("Duplicate selected FFmpeg archive member")
                store(selected[member.filename], archive.read(member), "binary", member.filename)
                found.add(member.filename)
else:
    with tarfile.open(archive_path, "r:xz") as archive:
        for member in archive.getmembers():
            if member.name in selected:
                if not member.isfile() or member.name in found:
                    raise ValueError("Unsafe or duplicate selected FFmpeg archive member")
                store(selected[member.name], archive.extractfile(member).read(), "binary", member.name)
                found.add(member.name)
if found != set(selected):
    raise ValueError("A selected FFmpeg executable, license or documentation member is absent")

with tarfile.open(source_path, "r:gz") as archive:
    prefix = "FFmpeg-" + source_commit + "/"
    required = ("COPYING.LGPLv3", "COPYING.LGPLv2.1", "COPYING.GPLv2", "COPYING.GPLv3", "LICENSE.md", "configure")
    for name in required:
        member = archive.getmember(prefix + name)
        if not member.isfile():
            raise ValueError("A required FFmpeg source material is not regular")
        store("licenses/ffmpeg/source-notices/" + name, archive.extractfile(member).read(), "source", member.name)

with tarfile.open(recipe_path, "r:gz") as archive:
    prefix = "FFmpeg-Builds-" + recipe_commit + "/"
    recipes = []
    required = ("LICENSE", "README.md", "build.sh", "generate.sh", "download.sh", "variants/defaults-lgpl.sh")
    names = {member.name: member for member in archive.getmembers() if member.isfile()}
    for name in required:
        if prefix + name not in names:
            raise ValueError("A required pinned build recipe is absent: " + name)
    for full_name, member in names.items():
        name = full_name.removeprefix(prefix)
        if not full_name.startswith(prefix):
            continue
        if name in ("LICENSE", "README.md", "build.sh", "generate.sh", "download.sh") or name.startswith(("scripts.d/", "variants/", "addins/", "patches/", "images/", "util/", ".github/workflows/")):
            data = archive.extractfile(member).read()
            store("licenses/ffmpeg/build-recipes/" + name, data, "buildRecipes", member.name)
            if name.startswith("scripts.d/") and name.endswith(".sh"):
                recipes.append({"path": name, "declarations": [line.strip() for line in data.decode("utf-8").splitlines() if line.startswith(("SCRIPT_REPO=", "SCRIPT_COMMIT=", "SCRIPT_VERSION=", "SCRIPT_SHA256=", "SCRIPT_NAME="))]})
(materials / "dependency-recipes.json").write_text(json.dumps({"purpose": "Pinned recipe declarations, not assertions of statically linked component versions", "buildRecipesCommit": recipe_commit, "recipes": recipes}, indent=2) + "\n", encoding="utf-8")
(materials / "acquired-members.json").write_text(json.dumps(origins, indent=2) + "\n", encoding="utf-8")
'@
& python -c $extractPayload $archivePath $sourcePath $recipePath $destinationPath $distribution.archiveRoot $toolLock.source.commit $toolLock.buildRecipes.commit $RuntimeIdentifier

if ($LASTEXITCODE) {
    throw 'Could not extract the selected FFmpeg tools and pinned source/build materials.'
}

Copy-Item -LiteralPath $sourcePath, $recipePath -Destination $materialDirectory
Copy-Item -LiteralPath $checksumPath -Destination (Join-Path $materialDirectory 'checksums.sha256')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ffmpeg.lock.json') -Destination (Join-Path $destinationPath 'ffmpeg-provenance.json')
$extension = ''

if ($IsWindows) {
    $extension = '.exe'
}

foreach ($tool in @('ffmpeg', 'ffprobe')) {
    $executable = Join-Path $destinationPath ($tool + $extension)

    if ($IsLinux) {
        & chmod u+x $executable

        if ($LASTEXITCODE) {
            throw "Could not set executable permissions for $tool."
        }
    }

    $versionOutput = @(& $executable -version 2>&1 | ForEach-Object { "$_" })

    if ($LASTEXITCODE -or -not $versionOutput[0].StartsWith("$tool version $($toolLock.version) ", [StringComparison]::Ordinal)) {
        throw "The acquired $tool failed its pinned version check."
    }

    $configuration = $versionOutput | Where-Object { $_.StartsWith('configuration: ', [StringComparison]::Ordinal) }
    $hasProhibitedBuild = $configuration.Contains('--enable-nonfree') -or $configuration.Contains('--enable-gpl')

    if ($hasProhibitedBuild -or -not $configuration.Contains('--enable-version3')) {
        throw 'The acquired FFmpeg configuration is not the pinned LGPL version3 build.'
    }

    $versionOutput | Set-Content -LiteralPath (Join-Path $materialDirectory ($tool + '-version.txt')) -Encoding utf8NoBOM
    $configuration | Set-Content -LiteralPath (Join-Path $materialDirectory ($tool + '-buildconf.txt')) -Encoding utf8NoBOM
}

& python (Join-Path $PSScriptRoot 'ffmpeg-runtime-dependencies.py') --directory $destinationPath --runtime-identifier $RuntimeIdentifier --output (Join-Path $materialDirectory 'runtime-dependencies.json')

if ($LASTEXITCODE) {
    throw 'Could not establish the acquired FFmpeg runtime dependency closure.'
}

Write-Output "Acquired FFmpeg/ffprobe $($toolLock.version); exact source and complete pinned build recipes included. Static dependency source/build reconciliation remains a release gate."
