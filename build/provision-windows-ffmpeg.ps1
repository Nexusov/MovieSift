[CmdletBinding()]
param(
    [string] $CacheDirectory = '',
    [string] $EvidencePath = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

## Describes only the pinned build prerequisites used by the Windows FFmpeg recipe.
function Get-WindowsFfmpegPrerequisitePlan {
    param([Parameter(Mandatory)] [string] $CacheDirectory)

    return [ordered]@{
        Bash = 'C:/msys64/usr/bin/bash.exe'
        Compiler = 'C:/mingw64/bin/gcc.exe'
        CompilerDirectory = 'C:/mingw64/bin'
        Pacman = 'C:/msys64/usr/bin/pacman.exe'
        Make = 'C:/msys64/usr/bin/make.exe'
        MsysDirectory = 'C:/msys64/usr/bin'
        PreflightArguments = @('-Q', 'bash', 'msys2-runtime', 'libintl')
        InstallArguments = @('-U', '--needed', '--noconfirm', (Join-Path $CacheDirectory 'make-4.4.1-3-x86_64.pkg.tar.zst'))
        MakePackage = [ordered]@{
            FileName = 'make-4.4.1-3-x86_64.pkg.tar.zst'
            Url = 'https://repo.msys2.org/msys/x86_64/make-4.4.1-3-x86_64.pkg.tar.zst'
            Sha256 = 'af0bdba17f06fe037f0194069adaa31a8fe45f1a11381501896aea1fae37bd5d'
        }
        NasmPackage = [ordered]@{
            FileName = 'nasm-3.01-win64.zip'
            Url = 'https://www.nasm.us/pub/nasm/releasebuilds/3.01/win64/nasm-3.01-win64.zip'
            Sha256 = 'e0ba5157007abc7b1a65118a96657a961ddf55f7e3f632ee035366dfce039ca4'
        }
    }
}

## Downloads a build-only archive and preserves a changed cached file for inspection.
function Get-VerifiedFfmpegPrerequisite {
    param(
        [Parameter(Mandatory)] [string] $CacheDirectory,
        [Parameter(Mandatory)] $Artifact
    )

    New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null
    $archive = Join-Path $CacheDirectory $Artifact.FileName

    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        Invoke-WebRequest -Uri $Artifact.Url -OutFile $archive -TimeoutSec 120
    }

    $metadata = Get-Item -LiteralPath $archive -Force
    $redirected = $metadata.Attributes -band [IO.FileAttributes]::ReparsePoint

    if ($metadata.PSIsContainer -or $redirected -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Artifact.Sha256) {
        throw "Build prerequisite hash or regular-file check failed: $($Artifact.FileName). The cached file remains unchanged."
    }

    return $archive
}

## Extracts the verified NASM archive into a new owned directory without deleting existing files.
function Expand-OwnedFfmpegAssembler {
    param(
        [Parameter(Mandatory)] [string] $ArchivePath,
        [Parameter(Mandatory)] [string] $CacheDirectory
    )

    $extraction = Join-Path $CacheDirectory ('nasm-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $extraction | Out-Null
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $extraction
    $executable = Join-Path $extraction 'nasm-3.01/nasm.exe'

    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "The pinned NASM executable is absent; extraction remains at $extraction."
    }

    $metadata = Get-Item -LiteralPath $executable -Force

    if ($metadata.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The extracted NASM executable must not be redirected.'
    }

    return $executable
}

## Provisions only an ephemeral Windows Actions runner and records the actual build tools.
function Initialize-WindowsFfmpegPrerequisites {
    param([string] $CacheDirectory, [string] $EvidencePath)

    $supportedRunner = $IsWindows -and $env:GITHUB_ACTIONS -ceq 'true' -and $env:RUNNER_OS -ceq 'Windows'
    $hasRunnerFiles = $env:RUNNER_TEMP -and $env:GITHUB_WORKSPACE -and $env:GITHUB_ENV -and $env:GITHUB_PATH

    if (-not $supportedRunner -or -not $hasRunnerFiles) {
        throw 'This prerequisite installation is restricted to an ephemeral Windows GitHub Actions runner.'
    }

    if (-not $CacheDirectory) {
        $CacheDirectory = Join-Path $env:RUNNER_TEMP 'tracktrim-ffmpeg-prerequisites'
    }

    if (-not $EvidencePath) {
        $EvidencePath = Join-Path $env:GITHUB_WORKSPACE 'artifacts/distribution-reports/win-x64/build-prerequisites.json'
    }

    $cache = [IO.Path]::GetFullPath($CacheDirectory)
    $relativeCache = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($env:RUNNER_TEMP), $cache)
    $outsideRunner = [IO.Path]::IsPathRooted($relativeCache) -or $relativeCache -eq '..' -or $relativeCache.StartsWith('..' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)

    if ($relativeCache -eq '.' -or $outsideRunner) {
        throw 'Use an owned prerequisite subdirectory beneath RUNNER_TEMP.'
    }

    $plan = Get-WindowsFfmpegPrerequisitePlan -CacheDirectory $cache

    foreach ($required in @($plan.Bash, $plan.Compiler, $plan.Pacman, $env:GITHUB_ENV, $env:GITHUB_PATH)) {

        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "A documented runner prerequisite is missing: $required. No replacement toolchain is installed automatically."
        }
    }

    $preflightArguments = $plan.PreflightArguments
    $basePackages = @(& $plan.Pacman @preflightArguments 2>&1 | ForEach-Object { "$_" })

    if ($LASTEXITCODE) {
        throw 'The existing MSYS2 bash, msys2-runtime and libintl packages are required. No floating MSYS2 update is performed.'
    }

    $makeArchive = Get-VerifiedFfmpegPrerequisite -CacheDirectory $cache -Artifact $plan.MakePackage
    $nasmArchive = Get-VerifiedFfmpegPrerequisite -CacheDirectory $cache -Artifact $plan.NasmPackage
    $installArguments = $plan.InstallArguments
    & $plan.Pacman @installArguments

    if ($LASTEXITCODE) {
        throw "Installing the verified GNU Make package failed: $makeArchive."
    }

    $installedMake = @(& $plan.Pacman -Q make 2>&1 | ForEach-Object { "$_" })

    if ($LASTEXITCODE -or $installedMake.Count -ne 1 -or $installedMake[0] -cne 'make 4.4.1-3') {
        throw 'The installed GNU Make package does not match the pinned build prerequisite.'
    }

    $nasm = Expand-OwnedFfmpegAssembler -ArchivePath $nasmArchive -CacheDirectory $cache
    $nasmDirectory = [IO.Path]::GetDirectoryName($nasm)
    $env:PATH = "$nasmDirectory;$($plan.CompilerDirectory);$($plan.MsysDirectory);$env:PATH"
    $compilerTarget = @(& $plan.Compiler -dumpmachine 2>&1 | ForEach-Object { "$_" })

    if ($LASTEXITCODE -or $compilerTarget.Count -ne 1 -or $compilerTarget[0] -cne 'x86_64-w64-mingw32') {
        throw 'The documented compiler must target native x64 MinGW-w64.'
    }

    $versions = [ordered]@{}

    foreach ($tool in @(
        @{ Name = 'bash'; Path = $plan.Bash; Arguments = @('--version') },
        @{ Name = 'gcc'; Path = $plan.Compiler; Arguments = @('--version') },
        @{ Name = 'make'; Path = $plan.Make; Arguments = @('--version') },
        @{ Name = 'nasm'; Path = $nasm; Arguments = @('-v') }
    )) {
        $arguments = $tool.Arguments
        $versions[$tool.Name] = @(& $tool.Path @arguments 2>&1 | ForEach-Object { "$_" })

        if ($LASTEXITCODE) {
            throw "Could not execute the required $($tool.Name) build tool."
        }
    }

    if (-not $versions.nasm[0].StartsWith('NASM version 3.01 ', [StringComparison]::Ordinal)) {
        throw 'The extracted assembler does not report the pinned NASM version.'
    }

    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) -Force | Out-Null
    [ordered]@{
        schemaVersion = 1
        compilerTarget = $compilerTarget[0]
        makeArchive = $plan.MakePackage
        nasmArchive = $plan.NasmPackage
        installedMsysPackages = @($basePackages) + @($installedMake)
        actualVersions = $versions
        scope = 'Build-only ephemeral runner tools; no compiler, Make or NASM is included in portable application payloads.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidencePath -Encoding utf8NoBOM

    # Actions prepends GITHUB_PATH entries in reverse order on subsequent steps.
    @($plan.MsysDirectory, $plan.CompilerDirectory, $nasmDirectory) | Add-Content -LiteralPath $env:GITHUB_PATH -Encoding utf8NoBOM
    ('TRACKTRIM_BUILD_MAKE=' + $plan.Make) | Add-Content -LiteralPath $env:GITHUB_ENV -Encoding utf8NoBOM
    Write-Output 'Pinned Windows FFmpeg build prerequisites are ready; actual versions recorded.'
}

Initialize-WindowsFfmpegPrerequisites -CacheDirectory $CacheDirectory -EvidencePath $EvidencePath
