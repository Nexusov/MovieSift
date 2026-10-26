# Contributing

This guide covers building, testing and packaging MovieSift from source. For application use, see the [user guide](docs/USAGE.md). Read the [architecture](docs/ARCHITECTURE.md) before changing workflow or filesystem behavior.

## Prerequisites

- The .NET SDK pinned in [global.json](global.json); SDK roll-forward is disabled.
- PowerShell 7, Git and Python 3.9+ with standard-library tar/XZ support.
- The platform-specific native prerequisites below for real media tests.
- GitHub CLI when downloading the Windows MKV build artifact; Docker only for the clean Linux package check.

Commands run from the repository root in **PowerShell 7**, except blocks marked Bash. Native tool builds need a whitespace-free cache path. This build restriction does not apply to application, media or output paths.

NuGet versions are centralized in [Directory.Packages.props](Directory.Packages.props), with committed lockfiles. Treat dependency updates as separate, reviewable changes and regenerate affected locks.

## Build and source checks

```powershell
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --no-restore --logger trx
```

Use `dotnet format --no-restore` when needed, inspect its diff, then repeat the check. Linux UI/headless tests also need the native libraries listed in [Linux media setup](#linux-media-setup).

These commands do not provision media tools. Optional local C# media tests skip when tools/fixtures are absent; those skips are not media coverage. The [verification workflow](.github/workflows/verify.yml) sets `MOVIESIFT_REQUIRE_MEDIA_TOOLS=1` and fails when mandatory provisioning is missing.

## Media tool acquisition

Acquisition requires network access and disk space. Its destination must be absent or empty; existing tools are not overwritten. Downloads and complete build receipts are verified against these inputs:

| Input | Location |
| --- | --- |
| MKVToolNix distribution and source | [tools.lock.json](build/tools.lock.json), [mkv-source.lock.json](build/mkv-source.lock.json) |
| FFmpeg/zlib source and recipe | [ffmpeg.lock.json](build/ffmpeg.lock.json) |
| Windows worker/source validation | [distribution-inventory.py](build/distribution-inventory.py) |

The application uses the bundled matching tools. A separately installed executable or two copied workers without their materials do not satisfy acquisition.

### Windows media setup

#### 1. Download the complete MKV build

The [Windows MKV workflow](.github/workflows/windows-mkv-candidate.yml) produces the `windows-mkv-source-candidate` artifact: workers, DLL closure, receipts, source, patches, notices and compiler/build materials. Select an unexpired successful build matching the current recipe. A portable MovieSift ZIP or official MKVToolNix ZIP is not this input.

Authenticate GitHub CLI, then download into a fresh directory:

```powershell
gh run list --repo Nexusov/MovieSift --workflow windows-mkv-candidate.yml --status success --limit 10 --json databaseId,headSha,url
$workerRunId = Read-Host 'Successful worker-build run ID with an unexpired artifact'
gh api "repos/Nexusov/MovieSift/actions/runs/$workerRunId/artifacts" --jq '.artifacts[] | {id,name,expired,expires_at}'
$mkvBuildDirectory = Join-Path (Get-Location) 'artifacts/windows-mkv-source-candidate'
gh run download $workerRunId --repo Nexusov/MovieSift --name windows-mkv-source-candidate --dir $mkvBuildDirectory
python ./build/distribution-inventory.py --validate-windows-mkv-build $mkvBuildDirectory
$env:MOVIESIFT_WINDOWS_MKV_BUILD_DIRECTORY = $mkvBuildDirectory
```

The two executables and `build-receipt.json` must be directly inside that directory. Validation checks worker/DLL hashes and the complete matching source/build materials. Stop on failure; changing receipts does not make an old build compatible. Artifacts expire after three days. If none matches, ask a maintainer to run the source-build workflow.

#### 2. Prepare FFmpeg's build prerequisites

A fresh Windows FFmpeg/ffprobe build requires:

- [MSYS2](https://www.msys2.org/) Bash at `C:/msys64/usr/bin/bash.exe`, GNU Make and tar/XZ utilities.
- Native x64 [MinGW-w64](https://www.mingw-w64.org/downloads/) GCC/binutils reporting `x86_64-w64-mingw32`.
- NASM on PATH. CI's pinned Make/NASM inputs are defined in [provision-windows-ffmpeg.ps1](build/provision-windows-ffmpeg.ps1). That provisioner is for ephemeral CI runners; local prerequisites must already be installed.

Select the actual installed directories:

```powershell
$compilerDirectory = Read-Host 'Existing native x64 MinGW-w64 bin directory'
$assemblerDirectory = Read-Host 'Existing NASM directory'
$env:PATH = "$compilerDirectory;$assemblerDirectory;C:/msys64/usr/bin;$env:PATH"
$env:MOVIESIFT_BUILD_MAKE = 'C:/msys64/usr/bin/make.exe'
& 'C:/msys64/usr/bin/bash.exe' --version
gcc -dumpmachine
& $env:MOVIESIFT_BUILD_MAKE --version
nasm -v
```

Alternatively, reuse a complete `ffmpeg-source-win-x64-<identity>` cache under a chosen cache parent. Keep its generated name, workers, receipt and all listed materials together. Acquisition derives the identity from the runtime, source/zlib hashes and recipe, then verifies every file. It downloads any missing pinned source archives even when using a cache.

#### 3. Acquire tools

```powershell
$toolCacheDirectory = Read-Host 'Writable whitespace-free FFmpeg cache parent'
pwsh ./build/acquire-tools.ps1 -RuntimeIdentifier win-x64 -Destination ./tools -CacheDirectory $toolCacheDirectory -WindowsMkvBuildDirectory $mkvBuildDirectory
```

A complete source-built MKV input is required; there is no official-binary fallback. Compiler, Make and NASM are build dependencies, not requirements for running the portable application.

### Linux media setup

On Ubuntu 24.04 x64, install the pinned [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/install-ubuntu), then the build/test prerequisites:

```bash
sudo apt-get update
sudo apt-get install --no-install-recommends -y build-essential nasm squashfs-tools file python3 python-is-python3 xz-utils libx11-6 libice6 libsm6 libfontconfig1 libicu74 libssl3t64 libgssapi-krb5-2 libbrotli1 libgmp10 libgl1 libegl1 libxrandr2 libxcursor1 libxi6 libxinerama1
pwsh ./build/acquire-tools.ps1 -RuntimeIdentifier linux-x64 -Destination ./tools -CacheDirectory ./artifacts/downloads
```

Use a whitespace-free repository/cache path for native compilation. MKVToolNix is extracted from the pinned AppImage at build time without FUSE. FFmpeg/ffprobe use the pinned source recipe and static zlib, or a verified `ffmpeg-source-linux-x64-<identity>` cache.

Production MKV workers live under `tools/app-root/usr/bin`; their native library environment is set by the application. The `tools/mkvmerge` and `tools/mkvextract` wrappers serve build/fixture tooling only. FFmpeg/ffprobe live directly under `tools` on both platforms.

## Fixtures and real media tests

After either platform's acquisition, generate the MKV and MP4 fixtures:

```powershell
python ./build/generate-fixtures.py --tool-directory ./tools --destination ./artifacts/fixtures
$ffmpegName = if ($IsWindows) { 'ffmpeg.exe' } else { 'ffmpeg' }
python ./build/generate-mp4-fixtures.py --ffmpeg (Join-Path ./tools $ffmpegName) --destination ./artifacts/fixtures/mp4
$env:MOVIESIFT_TOOL_DIRECTORY = (Resolve-Path ./tools).Path
$env:MOVIESIFT_FIXTURE_DIRECTORY = (Resolve-Path ./artifacts/fixtures).Path
$env:MOVIESIFT_REQUIRE_MEDIA_TOOLS = '1'
dotnet test --configuration Release --no-build --no-restore --logger trx
```

The Python build/distribution regressions require PowerShell 7 on PATH and the complete acquired source/material tree, including the pinned FFmpeg and zlib source archives. After acquisition and with `MOVIESIFT_TOOL_DIRECTORY` set as above, run:

```powershell
python -m unittest discover -s build/tests -v
```

The real-media suite uses original synthetic media and checked-in H.264/AAC/SRT fixtures with distinguishable tracks, chapters, tags and attachments. It checks retained payloads and metadata, source preservation in Copy mode, verified source replacement, and owned cleanup. Cancellation tests must observe a live worker and await that same process's exit; buffered progress after exit is insufficient.

Keep the two direct FFmpeg-origin MKV fixtures unchanged. Their language aliases and missing AAC duration header trigger regressions; pre-remuxing them removes those conditions. [Fixture provenance](tests/MovieSift.Infrastructure.Tests/Fixtures/README.md) records hashes and generation commands. Optional regeneration needs the matching development encoder:

```powershell
$ffmpeg902 = Read-Host 'Full path to the FFmpeg 9.0.2 development executable'
$ffmpeg711 = Read-Host 'Full path to the FFmpeg 7.1.1 development executable'
python ./build/generate-ffmpeg-fixture.py --ffmpeg $ffmpeg902 --version 9.0.2 --destination ./tests/MovieSift.Infrastructure.Tests/Fixtures/h264-aac-aliases.mkv
python ./build/generate-ffmpeg-fixture.py --ffmpeg $ffmpeg711 --version 7.1.1 --destination ./tests/MovieSift.Infrastructure.Tests/Fixtures/h264-aac-no-default-duration.mkv
```

The 7.1.1 fixture exercises accepted missing-duration/alias behavior; the 9.0.2 fixture must reject its known final AAC duration change. Do not relax timestamps or known terminal boundaries to make every AAC input pass. See [supported formats](docs/SUPPORTED_FORMATS.md) for the narrow exception.

Controlled ports/process helpers cover deterministic failures and races. Headless tests check actual Avalonia bindings/input/rendering. Neither substitutes for real-media or native desktop checks. Linux-only filesystem tests run on Linux and explicitly skip on Windows.

### Run the desktop from source

The live desktop uses application-relative tools; `MOVIESIFT_TOOL_DIRECTORY` is a test override. After the Release build and acquisition, copy the verified tools into a fresh output location:

```powershell
$desktopTools = Join-Path (Get-Location) 'src/MovieSift.Desktop/bin/Release/net10.0/tools'
if (Test-Path -LiteralPath $desktopTools) { throw 'The desktop tools directory already exists; inspect it before replacing it.' }
Copy-Item -LiteralPath ./tools -Destination $desktopTools -Recurse
dotnet run --project ./src/MovieSift.Desktop/MovieSift.Desktop.csproj --configuration Release --no-build --no-restore
```

Copy tools once per output directory/tool version; later launches need only the `dotnet run` command. A source run uses portable `data` alongside its build output. Testing installation of application updates requires a complete portable package with its helper, not this development layout.

## Portable package checks

Build on the target OS so native workers and executable permissions can be checked. The Windows commands reuse the variables set during acquisition:

```powershell
pwsh ./build/package.ps1 -RuntimeIdentifier win-x64 -WindowsMkvBuildDirectory $mkvBuildDirectory -ToolCacheDirectory $toolCacheDirectory -FixturePath ./artifacts/fixtures/ordinary.mkv -Mp4FixturePath ./artifacts/fixtures/mp4/ordinary.mp4
pwsh ./build/verify-package.ps1 -ArchivePath ./artifacts/MovieSift-win-x64.zip -FixturePath ./artifacts/fixtures/ordinary.mkv -Mp4FixturePath ./artifacts/fixtures/mp4/ordinary.mp4
```

On Linux:

```powershell
pwsh ./build/package.ps1 -RuntimeIdentifier linux-x64 -FixturePath ./artifacts/fixtures/ordinary.mkv -Mp4FixturePath ./artifacts/fixtures/mp4/ordinary.mp4
pwsh ./build/verify-package.ps1 -ArchivePath ./artifacts/MovieSift-linux-x64.tar.gz -FixturePath ./artifacts/fixtures/ordinary.mkv -Mp4FixturePath ./artifacts/fixtures/mp4/ordinary.mp4
```

Packaging includes the complete self-contained runtime, app/helper and tool closure, notices, provenance and checksums. It verifies the shipped runtime, license inventory and packaged workflows. Use `-Overwrite` only when replacing an existing generated archive. Staging and relocation directories remain under `artifacts`.

Relocation clears development runtime/tool variables, changes the working directory and exercises spaces, Cyrillic and metacharacters. It checks real MKV/MP4 workflows; a host with an SDK still does not prove SDK-free deployment.

### Matching source and build materials

From the clean committed source revision used to build the package, prepare its matching material bundle:

```powershell
# Windows
python ./build/prepare-review-materials.py --repository . --archive ./artifacts/MovieSift-win-x64.zip --inventory ./artifacts/distribution-reports/win-x64/distribution-win-x64.json --output ./artifacts/review-materials-win-x64
# Linux
python ./build/prepare-review-materials.py --repository . --archive ./artifacts/MovieSift-linux-x64.tar.gz --inventory ./artifacts/distribution-reports/linux-x64/distribution-linux-x64.json --output ./artifacts/review-materials-linux-x64
```

Run only the command for the package being prepared. The bundle binds exact application source, tool sources, notices and build materials to the archive/inventory. See [distribution requirements](docs/DISTRIBUTION.md) and the [release checklist](docs/release-checklist.md) before publishing; successful inventory checks alone are not a complete licensing determination.

### Clean Linux container

With Docker available on a Linux development host:

```bash
bash ./build/verify-clean-linux.sh ./artifacts/MovieSift-linux-x64.tar.gz ./artifacts/fixtures/ordinary.mkv ./artifacts/fixtures/mp4/ordinary.mp4
```

The [pinned Ubuntu image](build/portable-smoke.Dockerfile) includes native OS libraries, but no separately installed SDK or media tools. The check runs unprivileged and without networking, with an ASCII parent locale to exercise Unicode handling at the worker boundary. Evidence is retained beneath `artifacts/clean Linux Кириллица.*/evidence`. Docker is not an application dependency.

Native file dialogs, keyboard focus, OS scaling and window closure during work still need a real desktop session. Container/CLI success does not establish those behaviors.

## Changes and licensing

Keep pull requests focused on a concrete behavior or defect. Include reproduction steps, what changed, checks actually run and material limitations. For media support changes, update the preservation matrix and relevant real-media tests.

Project conventions:

- Keep code, documentation and commit messages in English; align English/Russian UI resource keys and formatting arguments.
- Respect the [project boundaries](docs/ARCHITECTURE.md#project-boundaries). Use explicit constructor injection, nullable reference types and immutable workflow snapshots.
- Prefer cohesive types and readable control flow. Introduce interfaces for useful boundaries; comments explain reasons and invariants.
- Keep stable error codes below Desktop and localize their presentation. Preserve primary errors, publication facts and secondary cleanup issues separately.
- Use deterministic signals for race tests and test important behavior rather than mirroring implementation details.
- Keep diagnostics bounded and local. Do not log complete media probe XML/JSON or add remote telemetry.
- Retain dependency lockfiles, source provenance and third-party notices. Keep downloaded binaries and local work directories out of Git.

The [checked-in main ruleset](.github/rulesets/protect-main.json) requires an up-to-date branch, resolved review conversations and both checks:

- `Required media checks and win-x64 package`
- `Required media checks and linux-x64 package`

The workflow includes draft and documentation-only pull requests. Keep required provisioning, media tests, packaging, relocation and the Linux container check intact. Default runs retain compact evidence; manual `retain_candidates: true` also retains verified binary/material sets for seven days. Actions artifacts in this public repository are not private or permanent release storage.

Original MovieSift code is [MIT licensed](LICENSE), copyright (c) 2026 Nexusov. By submitting original work for inclusion, you license it under these terms without transferring copyright. Identify separately licensed material and retain its notices; only submit work you have the right to contribute.
