# Contributing

Use the stable SDK pinned by `global.json`, PowerShell 7, Python 3.9+ with standard-library `tarfile`/XZ support, Git, and an editor with C#/XAML support. Local verification used Python 3.13.0 and PowerShell 7.6.5. Python is required only for explicit source-notice extraction and fixture generation; no Python runtime is bundled or required by the application. Use the same central package versions as the application and restore committed lockfiles. Updating a dependency is a deliberate, independently reviewable change; regenerate the affected locks and rerun verification.

## Local checks

```powershell
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --no-restore --logger trx
```

If formatting is required, run `dotnet format --no-restore`, inspect the diff, and repeat the check. Do not mix unrelated formatting into a feature change.

## Explicit media tool acquisition

```powershell
pwsh ./build/acquire-tools.ps1 -RuntimeIdentifier win-x64 -Destination ./tools
```

Use `linux-x64` on Linux. Downloads and SHA256 hashes are pinned in `build/tools.lock.json`; the tools are not checked into Git. Linux extraction occurs at build time and preserves the dependency tree, without requiring FUSE on users' systems. The build requires network access, adequate disk space, and Linux archive extraction tools; acquisition failures are real failures.

The production backend resolves `tools/mkvmerge.exe` and `tools/mkvextract.exe` on Windows, and the native executables under `tools/app-root/usr/bin` on Linux. Linux library paths are set on the worker's process environment; the application never invokes media through a shell. The `tools/mkvmerge`/`mkvextract` Linux wrappers are only build-time acquisition/fixture helpers. Tests accept an explicit tool directory; the live application always uses its app-local bundle. A test override does not establish portable-release acceptance. Both executables must match the pinned MKVToolNix version. Do not add an FFmpeg fallback.

The real-media suite uses small generated fixtures and the pinned backend. Fixture generation is a development operation; FFmpeg, if needed for encoding fixtures, is not a production dependency and is excluded from archives. Retained payload comparisons, chapters/tags, attachment payloads, track order/flags, and source hashes are meaningful assertions. Deterministic fake ports/process helpers cover warning/race/failure paths and must be labelled separately from real remux verification.

Generate the original fixtures without FFmpeg or a production codec dependency:

```powershell
python ./build/generate-fixtures.py --tool-directory ./tools --destination ./artifacts/fixtures
$env:TRACKTRIM_TOOL_DIRECTORY = (Resolve-Path ./tools).Path
$env:TRACKTRIM_REQUIRE_MEDIA_TOOLS = '1'
dotnet test --configuration Release --no-build --no-restore --logger trx
```

The generator uses original embedded VP8 frames, synthesized PCM waveforms, subtitles, and XML metadata. In CI `TRACKTRIM_REQUIRE_MEDIA_TOOLS=1` prevents silent omission of the required backend suite; provisioning and fixture generation must succeed first. Record the actual test counts and any local skips in `docs/PLAN.md`; do not turn an unexecuted test definition into a passing result.

## Portable package checks

```powershell
pwsh ./build/package.ps1 -RuntimeIdentifier win-x64 -FixturePath ./artifacts/fixtures/ordinary.mkv
pwsh ./build/verify-package.ps1 -ArchivePath ./artifacts/TrackTrim-win-x64.zip -FixturePath ./artifacts/fixtures/ordinary.mkv
```

Run the Linux equivalents on Linux. Packaging verifies the shipped runtime version, copies the complete normal publish output and tool closure, inventories restored NuGet licenses, records provenance, and checks the packaged entry point. Use `-Overwrite` only to replace an existing generated archive. Staging/relocation directories remain under `artifacts` for inspection.

The relocation test clears development runtime/tool variables, changes the working directory, exercises spaces/Cyrillic/metacharacters, and verifies that the source hash remains unchanged. It cannot prove the host lacks an installed SDK; clean target-system acceptance remains a separate check. Native file pickers, keyboard focus, scaling, shutdown during work, and both-language layouts require a real desktop session.

On a Linux developer/CI host with Docker, run `bash ./build/verify-clean-linux.sh ./artifacts/TrackTrim-linux-x64.tar.gz ./artifacts/fixtures/ordinary.mkv`. The image uses the pinned official Ubuntu 24.04 amd64 digest in `build/portable-smoke.Dockerfile`, installs only native OS libraries during image construction, then runs the packaged CLI without root or networking. It asserts that `dotnet`, MKVToolNix, and FFmpeg commands are not installed separately. The ASCII parent locale (`LANG=C`, `LC_ALL=C`, `LC_CTYPE=C`) deliberately tests that the native worker boundary handles Unicode independently of the inherited locale. The exact distro/package list, user/locale, and workflow log are retained under `artifacts/clean Linux Кириллица.*/evidence`. Docker is a development/CI dependency only; the distributed application does not use containers.

## Changes and licensing

Follow `AGENTS.md` and the project dependency boundaries. Prefer small changes with a specific user-visible behavior or safety invariant. Update the relevant preservation matrix and tests when support expands. Do not add future phase modules or claim unsupported container fidelity.

Keep code, documentation, and commit messages in English. Keep English/Russian resource keys and formatting arguments aligned. Never log full media probe XML/JSON or collect remote telemetry.

No license for original TrackTrim code has been selected. Do not add an author identity or a public license without an explicit decision. Preserve third-party notices, hashes, source links, and build provenance; complete the corresponding-source distribution review before a public binary release.
