## Validates a complete build cache without executing or replacing its contents.
function Read-VerifiedFfmpegBuild {
    param(
        [Parameter(Mandatory)] [string] $CacheDirectory,
        [Parameter(Mandatory)] [string] $RuntimeIdentifier,
        [Parameter(Mandatory)] $ToolLock,
        [Parameter(Mandatory)] [string] $RecipeHash
    )

    $receiptPath = Join-Path $CacheDirectory 'build-receipt.json'
    $readReceipt = @'
import os
from pathlib import Path
import stat
import sys

maximum_bytes = 64 * 1024
path = Path(sys.argv[1])
before = path.lstat()
redirected = getattr(before, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
if not stat.S_ISREG(before.st_mode) or redirected or before.st_size > maximum_bytes:
    raise ValueError("Expected a bounded regular source-build receipt")
descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0))
with os.fdopen(descriptor, "rb") as source:
    opened = os.fstat(source.fileno())
    if not stat.S_ISREG(opened.st_mode) or (opened.st_dev, opened.st_ino) != (before.st_dev, before.st_ino):
        raise ValueError("The source-build receipt changed while opening")
    data = source.read(maximum_bytes + 1)
if len(data) > maximum_bytes:
    raise ValueError("The source-build receipt exceeds its size bound")
print(data.decode("utf-8-sig"))
'@
    $receiptText = & python -c $readReceipt $receiptPath

    if ($LASTEXITCODE) {
        throw 'Could not read a bounded regular source-build receipt; the cache remains unchanged.'
    }

    $receipt = $receiptText | ConvertFrom-Json -Depth 16
    $hasMatchingInputs = $receipt.schemaVersion -eq 1 -and $receipt.runtimeIdentifier -eq $RuntimeIdentifier -and
        $receipt.version -eq $ToolLock.version -and $receipt.sourceSha256 -eq $ToolLock.source.sha256 -and
        $receipt.zlibSha256 -eq $ToolLock.zlib.sha256 -and $receipt.recipeSha256 -eq $RecipeHash

    if (-not $hasMatchingInputs) {
        throw 'The cached source-build receipt does not match the pinned sources, target and current build recipe.'
    }

    $extension = ''

    if ($RuntimeIdentifier -eq 'win-x64') {
        $extension = '.exe'
    }

    $expectedWorkers = @('ffmpeg', 'ffprobe') | ForEach-Object { $_ + $extension }
    $actualWorkers = @($receipt.workers | ForEach-Object path)
    $expectedMaterials = @('configure-arguments.txt', 'configure-output.txt', 'config.h', 'config_components.h', 'config.mak',
        'compiler-version.txt', 'compiler-target.txt', 'compiler-runtime-macros.txt', 'make-version.txt', 'assembler-version.txt', 'build-host.txt')
    $actualMaterials = @($receipt.materials | ForEach-Object path)
    $hasExpectedWorkers = $actualWorkers.Count -eq $expectedWorkers.Count -and -not @(Compare-Object $expectedWorkers $actualWorkers -CaseSensitive).Count
    $hasExpectedMaterials = $actualMaterials.Count -eq $expectedMaterials.Count -and -not @(Compare-Object $expectedMaterials $actualMaterials -CaseSensitive).Count

    if (-not $hasExpectedWorkers -or -not $hasExpectedMaterials) {
        throw 'The source-build cache must contain exactly the intended workers and build materials.'
    }

    foreach ($file in @($receipt.workers) + @($receipt.materials)) {
        $filePath = Join-Path $CacheDirectory $file.path
        $fileInfo = Get-Item -LiteralPath $filePath -Force
        $isRedirected = $fileInfo.Attributes -band [IO.FileAttributes]::ReparsePoint
        $matchesReceipt = -not $fileInfo.PSIsContainer -and -not $isRedirected -and $file.size -gt 0 -and $fileInfo.Length -eq $file.size -and
            (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $file.sha256

        if (-not $matchesReceipt) {
            throw "Source-build cache payload missing or changed: $($file.path)."
        }
    }

    return $receipt
}

## Deletes only this invocation's recognized scratch directory after resolving containment and links.
function Remove-OwnedFfmpegBuild {
    param(
        [Parameter(Mandatory)] [string] $CacheDirectory,
        [Parameter(Mandatory)] [string] $BuildDirectory
    )

    $validateCleanup = @'
from pathlib import Path
import json
import re
import stat
import sys

cache, build = map(Path, sys.argv[1:])
metadata = build.lstat()
redirected = stat.S_ISLNK(metadata.st_mode) or getattr(metadata, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
resolved_cache = cache.resolve(strict=True)
resolved_build = build.resolve(strict=True)
valid_name = re.fullmatch(r"ffmpeg-build-[a-f0-9]{32}", build.name)
if redirected or not stat.S_ISDIR(metadata.st_mode) or not valid_name or resolved_build.parent != resolved_cache:
    raise ValueError("Refusing cleanup of an unrecognized or redirected build directory")
for path in build.rglob("*"):
    metadata = path.lstat()
    redirected = path.is_symlink() or getattr(metadata, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
    if redirected and not path.resolve(strict=True).is_relative_to(resolved_build):
        raise ValueError("Refusing cleanup through a link outside the owned build directory")
print(json.dumps(str(resolved_build)))
'@
    $resolvedBuild = & python -c $validateCleanup $CacheDirectory $BuildDirectory

    if ($LASTEXITCODE) {
        throw 'Generated-build cleanup validation failed; its diagnostics and files remain.'
    }

    $checkedDirectory = $resolvedBuild | ConvertFrom-Json
    Remove-Item -LiteralPath $checkedDirectory -Recurse -Force
}
