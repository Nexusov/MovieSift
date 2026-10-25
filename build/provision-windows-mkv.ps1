[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Destination,
    [Parameter(Mandatory)] [string] $CacheDirectory
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

## Resolves a fresh owned child path beneath the ephemeral runner directory.
function Get-OwnedMkvBuildPath {
    param([Parameter(Mandatory)] [string] $Path)

    $absolute = [IO.Path]::GetFullPath($Path)
    $relative = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($env:RUNNER_TEMP), $absolute)
    $escapesRunner = [IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)

    if ($relative -eq '.' -or $escapesRunner) {
        throw 'MKV source-build paths must be owned child directories beneath RUNNER_TEMP.'
    }

    if (Test-Path -LiteralPath $absolute) {
        $metadata = Get-Item -LiteralPath $absolute -Force

        if (-not $metadata.PSIsContainer -or ($metadata.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'MKV source-build directories must be ordinary, non-redirected directories.'
        }
    }

    return $absolute
}

## Hash-checks a source or binary package without replacing an unexpected cached file.
function Get-VerifiedMkvBuildInput {
    param([Parameter(Mandatory)] $Artifact, [Parameter(Mandatory)] [string] $Directory)

    $path = Join-Path $Directory $Artifact.fileName

    if (-not (Test-Path -LiteralPath $path)) {
        Invoke-WebRequest -Uri $Artifact.url -OutFile $path -TimeoutSec 240
    }

    $metadata = Get-Item -LiteralPath $path -Force
    $regular = -not $metadata.PSIsContainer -and -not ($metadata.Attributes -band [IO.FileAttributes]::ReparsePoint)

    if (-not $regular -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Artifact.sha256) {
        throw "MKV build input hash/regular-file validation failed: $($Artifact.fileName). The cached file is preserved."
    }

    return $path
}

## Verifies the complete pinned target package batch before installation can begin.
function Get-VerifiedMkvTargetPackages {
    param([Parameter(Mandatory)] [object[]] $Artifacts, [Parameter(Mandatory)] [string] $Directory)

    $results = @($Artifacts | ForEach-Object -ThrottleLimit 8 -Parallel {
        $ErrorActionPreference = 'Stop'
        $artifact = $_

        try {
            $path = Join-Path $using:Directory $artifact.fileName

            if (-not (Test-Path -LiteralPath $path)) {
                Invoke-WebRequest -Uri $artifact.url -OutFile $path -TimeoutSec 240
            }

            $metadata = Get-Item -LiteralPath $path -Force
            $regular = -not $metadata.PSIsContainer -and -not ($metadata.Attributes -band [IO.FileAttributes]::ReparsePoint)

            if (-not $regular -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $artifact.sha256) {
                throw "Pinned target package validation failed: $($artifact.fileName)."
            }

            [PSCustomObject]@{ FileName = $artifact.fileName; Failure = '' }
        }
        catch {
            [PSCustomObject]@{ FileName = $artifact.fileName; Failure = $_.Exception.Message }
        }
    })
    $failures = @($results | Where-Object Failure | ForEach-Object { "$($_.FileName): $($_.Failure)" })

    if ($results.Count -ne $Artifacts.Count -or $failures.Count) {
        foreach ($failure in $failures) {
            [Console]::Error.WriteLine($failure)
        }

        throw "Pinned target package batch failed before installation ($($failures.Count) failures among $($Artifacts.Count) inputs)."
    }
}

## Writes the literal archive names consumed by the MSYS2 Bash installer.
function Write-MkvPackageFileList {
    param([Parameter(Mandatory)] [object[]] $Artifacts, [Parameter(Mandatory)] [string] $Path)

    [IO.File]::WriteAllText($Path, ($Artifacts.fileName -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
}

$supportedRunner = $IsWindows -and $env:GITHUB_ACTIONS -ceq 'true' -and $env:RUNNER_OS -ceq 'Windows'

if (-not $supportedRunner -or -not $env:RUNNER_TEMP) {
    throw 'MKV source-build provisioning is restricted to an ephemeral Windows GitHub Actions runner.'
}

$destinationPath = Get-OwnedMkvBuildPath -Path $Destination
$cachePath = Get-OwnedMkvBuildPath -Path $CacheDirectory

if ((Test-Path -LiteralPath $destinationPath) -and @(Get-ChildItem -LiteralPath $destinationPath -Force).Count) {
    & python (Join-Path $PSScriptRoot 'distribution-inventory.py') --validate-windows-mkv-build $destinationPath

    if ($LASTEXITCODE) {
        throw 'The existing MKV source-build cache does not match the current inputs/recipe/complete payload. It remains unchanged.'
    }

    Write-Output "Reused the fully verified MKV source-build cache at $destinationPath."
    return
}

New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
New-Item -ItemType Directory -Path $cachePath -Force | Out-Null
$lockPath = Join-Path $PSScriptRoot 'mkv-source.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$bash = 'C:/msys64/usr/bin/bash.exe'
$pacman = 'C:/msys64/usr/bin/pacman.exe'

foreach ($executable in @($bash, $pacman)) {

    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "The documented hosted MSYS2 prerequisite is missing: $executable. No local replacement is installed."
    }
}

$materialDirectory = Join-Path $destinationPath 'licenses/mkv-source-build'
$sourceMaterials = Join-Path $materialDirectory 'source'
New-Item -ItemType Directory -Path $sourceMaterials -Force | Out-Null
Copy-Item -LiteralPath $lockPath -Destination $materialDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build-mkv-windows.sh') -Destination $materialDirectory
$sourceArchive = Get-VerifiedMkvBuildInput -Artifact $lock.source -Directory $cachePath
Copy-Item -LiteralPath $sourceArchive -Destination $sourceMaterials
$recipeLicense = Get-VerifiedMkvBuildInput -Artifact $lock.recipeSnapshot.license -Directory $cachePath
Copy-Item -LiteralPath $recipeLicense -Destination $materialDirectory

# These host-only tools are not shipped. Their actual installed versions are recorded.
& $pacman -S --needed --noconfirm make diffutils patch grep sed gawk tar xz zstd

if ($LASTEXITCODE) {
    throw 'The ephemeral MSYS2 host prerequisites could not be provisioned.'
}

# Download immutable target archives in parallel, then install the entire locked closure together.
$packageInputs = @($lock.packages | ForEach-Object { $_ })
Get-VerifiedMkvTargetPackages -Artifacts $packageInputs -Directory $cachePath

$packageList = Join-Path $cachePath 'package-files.txt'
Write-MkvPackageFileList -Artifacts $packageInputs -Path $packageList
& $bash --noprofile --norc -c 'set -euo pipefail; export PATH=/usr/bin:$PATH; cd "$(cygpath -u "$1")"; mapfile -t packages < package-files.txt; pacman -U --noconfirm --needed "${packages[@]}"' tracktrim-mkv-install $cachePath

if ($LASTEXITCODE) {
    throw 'The exact locked UCRT64 package closure could not be installed.'
}

$workDirectory = Join-Path $cachePath ('compile-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workDirectory | Out-Null
# The pinned archive has exactly these two optional symlinks. Neither is needed by
# the CLI targets; the complete original source archive is retained unchanged.
& $bash --noprofile --norc -c 'set -euo pipefail; export PATH=/usr/bin:$PATH; tar -xJf "$(cygpath -u "$1")" --exclude=mkvtoolnix-102.0/drake --exclude=mkvtoolnix-102.0/doc/man/mkvtoolnix-doc.css -C "$(cygpath -u "$2")"' tracktrim-mkv-extract $sourceArchive $workDirectory

if ($LASTEXITCODE) {
    throw 'The pinned MKVToolNix source archive could not be extracted.'
}

$sourceDirectory = Join-Path $workDirectory ('mkvtoolnix-' + $lock.version)
$recipe = (Join-Path $PSScriptRoot 'build-mkv-windows.sh').Replace('\', '/')
& $bash --noprofile --norc $recipe $sourceDirectory $destinationPath $materialDirectory

if ($LASTEXITCODE) {
    throw "The source-built MKV candidate failed; its diagnostics remain in $materialDirectory and $workDirectory."
}

& python (Join-Path $PSScriptRoot 'collect-mkv-build.py') --directory $destinationPath --cache $cachePath --prefix 'C:/msys64/ucrt64' --pacman $pacman --lock $lockPath --recipe (Join-Path $PSScriptRoot 'build-mkv-windows.sh')

if ($LASTEXITCODE) {
    throw 'MKV worker dependency/source/material validation failed; the candidate is not accepted.'
}

& python (Join-Path $PSScriptRoot 'distribution-inventory.py') --validate-windows-mkv-build $destinationPath

if ($LASTEXITCODE) {
    throw 'The complete newly built MKV candidate failed independent payload/material validation.'
}

Write-Output "Built the pinned MKVToolNix CLI candidate at $destinationPath; mandatory application/package tests must accept it before adoption."
