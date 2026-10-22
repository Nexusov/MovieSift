[CmdletBinding(DefaultParameterSetName = 'Directory')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Directory')]
    [string] $PackageDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Archive')]
    [string] $ArchivePath,
    [string] $FixturePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

## Runs the packaged entry point from an unrelated working directory without an SDK environment.
function Invoke-PackagedApplication {
    param(
        [Parameter(Mandatory)]
        [hashtable] $Options
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new($Options.Executable)
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    $startInfo.WorkingDirectory = [IO.Path]::GetTempPath()
    $startInfo.Environment.Remove('DOTNET_ROOT') | Out-Null
    $startInfo.Environment.Remove('DOTNET_ROOT_X64') | Out-Null
    $startInfo.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $startInfo.Environment.Remove('TRACKTRIM_TOOL_DIRECTORY') | Out-Null

    foreach ($argument in $Options.Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    try {

        if (-not $process.Start()) {
            throw 'Could not start the packaged entry point.'
        }

        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        $smokeTimeoutMilliseconds = 120000

        if (-not $process.WaitForExit($smokeTimeoutMilliseconds)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'The packaged smoke operation timed out.'
        }

        $outputText = $standardOutput.GetAwaiter().GetResult()
        $errorText = $standardError.GetAwaiter().GetResult()
        Write-Output $outputText

        if ($errorText) {
            Write-Output $errorText
        }

        if ($process.ExitCode -ne 0) {
            throw "Packaged application exited with $($process.ExitCode)."
        }
    }
    finally {
        $process.Dispose()
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Archive') {
    $ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
    $relocationRoot = Join-Path ([IO.Path]::GetDirectoryName($ArchivePath)) ('relocated check Кириллица ' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $relocationRoot | Out-Null

    if ($ArchivePath.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        [IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $relocationRoot)
    }

    if ($ArchivePath.EndsWith('.tar.gz', [StringComparison]::OrdinalIgnoreCase)) {
        & tar -xzf $ArchivePath -C $relocationRoot

        if ($LASTEXITCODE -ne 0) {
            throw 'Archive extraction failed.'
        }
    }

    $packageDirectories = @(Get-ChildItem -LiteralPath $relocationRoot -Directory)

    if ($packageDirectories.Count -ne 1) {
        throw 'Expected exactly one application directory in the archive.'
    }

    $PackageDirectory = $packageDirectories[0].FullName
}

$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$executableName = 'TrackTrim'

if ($IsWindows) {
    $executableName += '.exe'
}

$executablePath = Join-Path $PackageDirectory $executableName
$runtimeFiles = @('System.Private.CoreLib.dll', 'hostfxr.dll', 'coreclr.dll')

if ($IsLinux) {
    $runtimeFiles = @('System.Private.CoreLib.dll', 'libhostfxr.so', 'libcoreclr.so')
}

foreach ($requiredPath in @($executablePath, (Join-Path $PackageDirectory 'portable.flag'), (Join-Path $PackageDirectory 'TrackTrim.runtimeconfig.json'), (Join-Path $PackageDirectory 'tool-provenance.json'), (Join-Path $PackageDirectory 'licenses/nuget/inventory.json')) + @($runtimeFiles | ForEach-Object { Join-Path $PackageDirectory $_ })) {

    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required packaged file is missing: $requiredPath"
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Archive') {
    $manifestPath = Join-Path $PackageDirectory 'SHA256SUMS'
    $packagePrefix = $PackageDirectory + [IO.Path]::DirectorySeparatorChar
    $manifestPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

    foreach ($entry in Get-Content -LiteralPath $manifestPath) {

        if ($entry -notmatch '^([a-f0-9]{64})  (.+)$') {
            throw 'The package integrity manifest contains an invalid entry.'
        }

        $expectedHash = $Matches[1]
        $relativePath = $Matches[2]
        $checkedPath = [IO.Path]::GetFullPath((Join-Path $PackageDirectory $relativePath))

        if (-not $checkedPath.StartsWith($packagePrefix, [StringComparison]::Ordinal)) {
            throw 'The package integrity manifest points outside the application directory.'
        }

        if (-not $manifestPaths.Add($relativePath)) {
            throw "Duplicate package integrity entry: $relativePath"
        }

        if ((Get-FileHash -LiteralPath $checkedPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
            throw "The relocated file failed its SHA256 check: $relativePath"
        }
    }

    $unexpectedFiles = @(Get-ChildItem -LiteralPath $PackageDirectory -File -Recurse -Force | Where-Object {
        $relativePath = [IO.Path]::GetRelativePath($PackageDirectory, $_.FullName).Replace('\', '/')
        $relativePath -ne 'SHA256SUMS' -and -not $manifestPaths.Contains($relativePath)
    })

    if ($unexpectedFiles.Count) {
        throw 'The relocated package contains files absent from its integrity manifest.'
    }

    Write-Output "Relocated package integrity passed: $($manifestPaths.Count) files."
}

Invoke-PackagedApplication -Options @{ Executable = $executablePath; Arguments = @('--verify-tools') }

if ($FixturePath) {
    $FixturePath = [IO.Path]::GetFullPath($FixturePath)
    $checkRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
    New-Item -ItemType Directory -Path $checkRoot -Force | Out-Null
    $smokeRoot = Join-Path $checkRoot ('media smoke Кириллица ' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $smokeRoot | Out-Null
    $sourcePath = Join-Path $smokeRoot '--source with spaces; & Кириллица.mkv'
    $destinationPath = Join-Path $smokeRoot 'output with spaces; & Кириллица.mkv'
    Copy-Item -LiteralPath $FixturePath -Destination $sourcePath
    $originalHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    Invoke-PackagedApplication -Options @{ Executable = $executablePath; Arguments = @('--verify-portable', $sourcePath, $destinationPath) }

    if (-not (Test-Path -LiteralPath $destinationPath -PathType Leaf)) {
        throw 'The full packaged workflow did not publish its destination.'
    }

    if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $originalHash) {
        throw 'The full packaged workflow modified the source.'
    }

    Write-Output "Full packaged workflow passed; original SHA256 $originalHash."
}

Write-Output "Packaged application checks passed: $PackageDirectory"
