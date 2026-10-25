# Contributing

Use the stable SDK pinned by `global.json`, PowerShell 7, Python 3.9+ with standard-library `tarfile`/XZ support, Git, and an editor with C#/XAML support. Local verification used Python 3.13.0 and PowerShell 7.6.5. Python is required only for explicit source-notice extraction and fixture generation; no Python runtime is bundled or required by the application. Use the same central package versions as the application and restore committed lockfiles. Updating a dependency is a deliberate, independently reviewable change; regenerate the affected locks and rerun verification.

## Source-only checks

```powershell
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --no-restore --logger trx
```

If formatting is required, run `dotnet format --no-restore`, inspect the diff, and repeat the check. Do not mix unrelated formatting into a feature change.

## Explicit media tool acquisition

Commands below run from the repository root in **PowerShell 7** unless marked Bash. Source-only restore/build/tests above do not provision media: optional local media cases report skips, not successful media coverage. Required CI sets `MOVIESIFT_REQUIRE_MEDIA_TOOLS=1` and fails on missing tools or fixtures. Do not use a separately installed FFmpeg or the old product's custom-version workers as a substitute.

Acquisition is explicit and needs network access/disk space. `tools` must be absent or empty; existing tool payloads are never overwritten. MKV pins live in `build/tools.lock.json` / `build/mkv-source.lock.json`; FFmpeg/zlib source and recipe inputs live in `build/ffmpeg.lock.json`. Successful acquisition verifies source, worker and material evidence rather than trusting a directory name.

### Windows media setup

**1. Obtain the complete Windows MKV source-build artifact.** The existing [Windows MKV source-build candidate workflow](.github/workflows/windows-mkv-candidate.yml) produces `windows-mkv-source-candidate`, containing the two workers, DLL closure, `build-receipt.json`, sources, patches, original binary metadata, notices and build/compiler material. A portable app ZIP, test-evidence artifact or the official MKVToolNix ZIP is not this input. This guide does not dispatch a workflow automatically. If no successful unexpired matching artifact exists, request a separately authorized run of that workflow; it is not part of ordinary local setup or this quality pass.

Install/authenticate GitHub CLI for repository-read access, then list the existing successful runs and select one explicitly:

```powershell
gh run list --repo Nexusov/MovieSift --workflow windows-mkv-candidate.yml --status success --limit 10 --json databaseId,headSha,url
$workerRunId = Read-Host 'Successful worker-build run ID with an unexpired artifact'
gh api "repos/Nexusov/MovieSift/actions/runs/$workerRunId/artifacts" --jq '.artifacts[] | {id,name,expired,expires_at}'
$mkvBuildDirectory = Join-Path (Get-Location) 'artifacts/windows-mkv-source-candidate'
gh run download $workerRunId --repo Nexusov/MovieSift --name windows-mkv-source-candidate --dir $mkvBuildDirectory
python ./build/distribution-inventory.py --validate-windows-mkv-build $mkvBuildDirectory
$env:MOVIESIFT_WINDOWS_MKV_BUILD_DIRECTORY = $mkvBuildDirectory
```

Use a **fresh empty extraction directory**, and stop if download or validation fails. The two `.exe` files and receipt must be directly under `$mkvBuildDirectory`, not another nested artifact directory. Validation binds the current recipe/source locks, both actual PE workers and recursively imported DLLs to their complete hashed source/build materials. An expired/old artifact or changed recipe requires a current matching build; do not edit receipts or bypass the check. The workflow builds on its ephemeral Windows runner, never by installing the toolchain on the developer's host. Its reviewed artifacts expire after three days.

**2. Prepare FFmpeg's native prerequisites or reuse an exact build cache.** The worker artifact above supplies MKV only. FFmpeg/ffprobe are built from the pinned upstream source with explicit static zlib. A new Windows build needs:

- [MSYS2](https://www.msys2.org/) Bash at `C:/msys64/usr/bin/bash.exe`, plus GNU Make and tar/XZ utilities in that environment.
- Native x64 MinGW-w64 GCC/binutils from a [MinGW-w64 distribution](https://www.mingw-w64.org/downloads/), on the process PATH, reporting `x86_64-w64-mingw32` from `gcc -dumpmachine`.
- NASM on PATH. CI uses the hash-pinned NASM **3.01** and GNU Make **4.4.1** inputs documented in `build/provision-windows-ffmpeg.ps1`; GCC and MSYS2 Bash come from the Windows runner image. That script deliberately refuses local installation. Install/choose developer prerequisites explicitly; do not fake GitHub runner variables to run it locally.

For an existing toolchain, set its actual directories (not example placeholder paths):

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

Alternatively, reuse an existing **complete** `ffmpeg-source-win-x64-<identity>` cache directory without a compiler. Keep its generated directory name, workers, `build-receipt.json` and all listed materials together under the chosen cache parent. Acquisition computes the identity from RID, source/zlib hashes and the current recipe, then `Read-VerifiedFfmpegBuild` in `build/ffmpeg-build-cache.ps1` verifies the receipt and every worker/material hash. Old-name/version or different-recipe caches do not qualify. Merely copying `ffmpeg.exe`/`ffprobe.exe` from a package is insufficient. Missing pinned source archives are downloaded and checked even when reusing a cache.

**3. Acquire into an empty tools directory.** Choose a writable **whitespace-free** cache parent; FFmpeg's generated Makefiles do not support spaces in the build-cache path. This is a build-only limit, not a restriction on application/media/output paths.

```powershell
$toolCacheDirectory = Read-Host 'Writable whitespace-free FFmpeg cache parent (existing exact cache or new build)'
pwsh ./build/acquire-tools.ps1 -RuntimeIdentifier win-x64 -Destination ./tools -CacheDirectory $toolCacheDirectory -WindowsMkvBuildDirectory $mkvBuildDirectory
```

Stop on any source, recipe, version, import or material failure. The complete source-built MKV input is mandatory; there is no official-binary fallback. Actual compiler/configuration receipts accompany both source-built backends; compiler/Make/NASM are build-only dependencies, not portable application requirements.

### Linux media setup

On Ubuntu 24.04 x64, install the pinned SDK and PowerShell 7 using their official [.NET](https://dotnet.microsoft.com/download/dotnet/10.0) / [PowerShell](https://learn.microsoft.com/powershell/scripting/install/install-ubuntu) instructions. The following are developer/CI native prerequisites, not an instruction for application end users:

```bash
sudo apt-get update
sudo apt-get install --no-install-recommends -y build-essential nasm squashfs-tools file python3 python-is-python3 xz-utils libx11-6 libice6 libsm6 libfontconfig1 libicu74 libssl3t64 libgssapi-krb5-2 libbrotli1 libgmp10 libgl1 libegl1 libxrandr2 libxcursor1 libxi6 libxinerama1
pwsh ./build/acquire-tools.ps1 -RuntimeIdentifier linux-x64 -Destination ./tools -CacheDirectory ./artifacts/downloads
```

The build-time pinned AppImage extraction preserves the approved MKV dependency tree and does not require FUSE. FFmpeg/ffprobe are built with the same pinned source/recipe and explicit static zlib, or verified from the exact `ffmpeg-source-linux-x64-<identity>` cache. Use a whitespace-free repository/cache path for this native build. The acquisition step records executable versions and runtime/material closure; success on a developer host does not establish Ubuntu Desktop acceptance.

The production backend resolves `tools/mkvmerge.exe` and `tools/mkvextract.exe` on Windows, and the native executables under `tools/app-root/usr/bin` on Linux. Linux library paths are set on the worker's process environment; the application never invokes media through a shell. The `tools/mkvmerge`/`mkvextract` Linux wrappers are only build-time acquisition/fixture helpers. Tests accept an explicit tool directory; the live application always uses its app-local bundle. A test override does not establish portable-release acceptance. Both MKV executables must match MKVToolNix 102.0. The separate MP4 adapter uses the exact FFmpeg/ffprobe build in `build/ffmpeg.lock.json`, under `tools` on both targets. There is no backend fallback or container conversion.

The real-media suite uses generated fixtures, two checked-in direct FFmpeg-origin fixtures and the pinned backend. Fixture encoding is a development operation. FFmpeg/ffprobe now ship for MP4 processing; ffplay and external development encoders are excluded. The historical MKV fixtures remain direct original outputs, with their earlier provenance unchanged. Retained VP8/PCM/SRT and H264/AAC/SRT payload comparisons, chapters/tags, attachment payloads, track order/flags and source hashes are meaningful assertions. Deterministic fake ports/process helpers cover warning/race/failure paths and must be labelled separately from real remux verification.

Phase 2A tests are grouped under Application `Batches`, Infrastructure `BatchInputDiscoveryTests` / `BatchMediaIntegrationTests`, and Desktop batch headless tests. Phase 2B1 adds Core `LanguageRuleMatcherTests`, Application language/selection guard tests, Desktop `BatchLanguageRulesTests`, and real `BatchLanguageRuleIntegrationTests`. Phase 2B2 adds `LanguageTitleFilterTests`, Application/Desktop `BatchTitleFilterTests` and real `BatchTitleFilterIntegrationTests`. The real rule cases must obtain selections through production Preview/Apply before explicit approval/review, compare all retained extracted payloads from reordered inputs, source hashes and owned cleanup. Keep literal-title conjunction tests distinct from language uncertainty and preservation policy; regex-looking input remains literal. Use deterministic signals for lifecycle boundaries. These checks are separate from native desktop acceptance. Preserve the existing required media-test environment; do not turn missing tools/fixtures into a successful skip in CI.

Phase 2B3 adds pure `ForcedSubtitleRuleTests`, Application/Desktop `BatchForcedSubtitleTests`, controlled `MkvForcedFlagParsingTests` and real `BatchForcedSubtitleIntegrationTests`. The real case uses `ForcedSubtitleFixture` to give otherwise matching subtitles different actual flags and payloads across reordered inputs. It must exercise production rule application, compare all retained track/attachment payloads and source hashes, and retain both owned remux handles through exit. Keep JSON parsing, controlled lifecycle, headless and native evidence distinct. Do not bypass conservative language validation using title/forced nonmatches.

## Mandatory fixtures and media tests

After either OS setup, generate both original fixture sets in PowerShell 7:

```powershell
python ./build/generate-fixtures.py --tool-directory ./tools --destination ./artifacts/fixtures
$ffmpegName = if ($IsWindows) { 'ffmpeg.exe' } else { 'ffmpeg' }
python ./build/generate-mp4-fixtures.py --ffmpeg (Join-Path ./tools $ffmpegName) --destination ./artifacts/fixtures/mp4
$env:MOVIESIFT_TOOL_DIRECTORY = (Resolve-Path ./tools).Path
$env:MOVIESIFT_FIXTURE_DIRECTORY = (Resolve-Path ./artifacts/fixtures).Path
$env:MOVIESIFT_REQUIRE_MEDIA_TOOLS = '1'
dotnet test --configuration Release --no-build --no-restore --logger trx
```

The generator uses original embedded VP8 frames, synthesized PCM waveforms, subtitles and XML metadata. It also generates a longer-frame-count cancellation input: the real cancellation test must observe an active worker at cancellation, retain its process handle, and await that same worker's exit. Progress buffered from an already exited process cannot pass that regression.

The checked-in H264/two-AAC/SRT inputs are direct Matroska output from development FFmpeg 9.0.2 and 7.1.1. Do not pre-remux them through MKVToolNix: their original language aliases and missing AAC duration header are the regression triggers. [Fixture provenance](tests/MovieSift.Infrastructure.Tests/Fixtures/README.md) records exact input hashes, executable/archive provenance and commands. Regeneration is optional and requires explicitly supplying the matching development encoder:

```powershell
python ./build/generate-ffmpeg-fixture.py --ffmpeg /absolute/path/to/ffmpeg-9.0.2 --version 9.0.2 --destination ./tests/MovieSift.Infrastructure.Tests/Fixtures/h264-aac-aliases.mkv
python ./build/generate-ffmpeg-fixture.py --ffmpeg /absolute/path/to/ffmpeg-7.1.1 --version 7.1.1 --destination ./tests/MovieSift.Infrastructure.Tests/Fixtures/h264-aac-no-default-duration.mkv
```

The 7.1.1 cases are positive missing-duration/alias/payload regressions. The 9.0.2 cases must reject a known final AAC duration change; do not weaken timestamp/end-boundary checks to make every AAC source pass. The only terminal-boundary exception is the pinned extractor's duplicate-last-frame sentinel when the bounded AAC-LC missing-duration proof succeeds. Real chapter/tag alias cases use separately generated metadata fixtures.

CI uses the pinned bundled FFmpeg/ffprobe pair and checked-in original H.264/AAC source to generate small MP4 fixtures; use `./tools/ffmpeg` on Linux. `MOVIESIFT_REQUIRE_MEDIA_TOOLS=1` prevents silent omission of the required MKV/MP4 backend suites; provisioning and generated-fixture creation must succeed first. Record actual counts and skips in `docs/PLAN.md`; do not turn an unexecuted test definition into a passing result. Linux-only FIFO tests must run on Linux; their Windows skips are explicit. Controlled filesystem tests establish allocation-stage and malformed-record behavior; headless close/cleanup tests remain separate from native desktop shutdown acceptance.

## Portable package checks

```powershell
pwsh ./build/package.ps1 -RuntimeIdentifier win-x64 -WindowsMkvBuildDirectory $mkvBuildDirectory -ToolCacheDirectory $toolCacheDirectory -FixturePath ./artifacts/fixtures/ordinary.mkv -Mp4FixturePath ./artifacts/fixtures/mp4/ordinary.mp4
pwsh ./build/verify-package.ps1 -ArchivePath ./artifacts/MovieSift-win-x64.zip -FixturePath ./artifacts/fixtures/ordinary.mkv -Mp4FixturePath ./artifacts/fixtures/mp4/ordinary.mp4
```

Run the Linux equivalents on Linux. Packaging verifies the shipped runtime version, copies the complete normal publish output and tool closure, inventories restored NuGet licenses, records provenance, and checks the packaged entry point. Use `-Overwrite` only to replace an existing generated archive. Staging/relocation directories remain under `artifacts` for inspection.

The root `LICENSE` must match the operative MIT license byte-for-byte; the obsolete application-license-pending marker is rejected. After packaging, run `python ./build/prepare-review-materials.py --repository . --archive ./artifacts/MovieSift-win-x64.zip --inventory ./artifacts/distribution-reports/win-x64/distribution-win-x64.json --output ./artifacts/review-materials-win-x64` (use the corresponding Linux paths on Linux). This copies inventory-verified notices/source/build materials and the exact clean commit's source into a matching bundle. CI prepares both platform sets and uploads compact evidence by default. An explicitly authorized manual run with `retain_candidates: true` also uploads each verified binary/material pair. These artifacts are publicly accessible under GitHub's repository-read rules and expire after seven days; permanent release distribution still requires separate publication authorization.

The relocation test clears development runtime/tool variables, changes the working directory, exercises spaces/Cyrillic/metacharacters, and verifies that the source hash remains unchanged. It cannot prove the host lacks an installed SDK; clean target-system acceptance remains a separate check. Native file pickers, keyboard focus, scaling, shutdown during work, and both-language layouts require a real desktop session.

On a Linux developer/CI host with Docker, run `bash ./build/verify-clean-linux.sh ./artifacts/MovieSift-linux-x64.tar.gz ./artifacts/fixtures/ordinary.mkv ./artifacts/fixtures/mp4/ordinary.mp4`. The image uses the pinned official Ubuntu 24.04 amd64 digest in `build/portable-smoke.Dockerfile`, installs only native OS libraries during image construction, then runs the packaged CLI without root or networking. It asserts that `dotnet`, MKVToolNix, and FFmpeg commands are not installed separately. The ASCII parent locale (`LANG=C`, `LC_ALL=C`, `LC_CTYPE=C`) deliberately tests that the native worker boundary handles Unicode independently of the inherited locale. The exact distro/package list, user/locale, and workflow log are retained under `artifacts/clean Linux Кириллица.*/evidence`. Docker is a development/CI dependency only; the distributed application does not use containers.

## Protected main and pull requests

Submit changes through a pull request from a review branch. The active [Protect main ruleset](https://github.com/Nexusov/MovieSift/rules/25401528) applies only to `main`, with no bypass actors. It requires resolved review conversations and both existing checks from GitHub Actions (App ID `15368`):

- `Required media checks and win-x64 package`
- `Required media checks and linux-x64 package`

The branch must be up to date with its base before merging. No approving review, code-owner approval or last-push approval is required. Force pushes and branch deletion are blocked. Merge, squash and rebase remain available; automatic merging is disabled.

The existing verification workflow covers draft and documentation-only pull requests as well as ordinary changes, without path or fork exclusions. Fork runs may await GitHub's contributor approval; pending approval or missing checks do not waive the requirements. Preserve mandatory tool/fixture provisioning, full tests, package/relocation checks and the Linux no-SDK container check; do not add a skip guard or rename a required check without updating the policy deliberately.

The [applied protection record](docs/verification/main-protection.md) links the canonical configuration and readback commands. Green CI establishes the checks that ran; native desktop acceptance and licensing obligations retain their separate status.

## Changes and licensing

Phase 2C1 adds `LanguageRuleConfigurationTests`, Application `Presets` tests, `PortableRulePresetStoreTests` / `RulePresetSpecialFileTests`, Desktop `RulePresetsTests`, and `RulePresetMediaIntegrationTests`. Its review regressions include real-file `RulePresetUnicodeTests`, production-store/headless `RulePresetCatalogInitializationTests`, and `RulePresetDeleteGateTests` for availability and direct execution after write access changes. Keep pure validation, controlled write failures, real filesystem/cross-process locking, headless bindings and native functional evidence distinct. The media case must save/load through fresh production persistence instances before deriving different file-local choices through Preview/Apply; existing mandatory backend cases still cover remux/publication. Never serialize media/workflow/settings state into presets or silently repair a bad catalog. Linux FIFO/symlink cases must run in the required Linux job.

Follow `AGENTS.md` and the project dependency boundaries. Prefer small changes with a specific user-visible behavior or safety invariant. Update the relevant preservation matrix and tests when support expands. Current scope is Phase 2A independent plans/bounded discovery/sequential dispatch, Phase 2B explicit language/title/forced rules, Phase 2C1 named portable presets with separate explicit Load, and Phase 2D1 optional recursive folder input with relative output directories. Matching uses Core policy and file-local inspections, never another file's IDs. Preview must not mutate approvals, Apply must reject stale state, and only real choice changes invalidate plans. Preset update/import/export, automatic loading, title-only/regex rules, other forced-filter modes, live job restoration and byte-offset resume require another task. Phase 3 adds MP4 using the same workflow and a separate documented preservation contract. Do not claim unsupported container fidelity.

The reviewed PR stack, including #10 and #11, has been integrated through ordinary protected merges; the review branches remain available. Subsequent changes use a new pull request against `main`. Preserve both required contexts and the complete workflow when correcting a demonstrated candidate defect. Public tags and release assets require separate authorization; the original-code license remains MIT.

Recursive-input checks live in `OutputPathPolicyTests`, `RecursiveBatchDiscoveryTests`, `BatchFolderProvenanceTests`, `DestinationPreparationTests`, `NestedDestinationTests` and headless `BatchRecursiveFolderTests`. The mandatory `RecursiveBatchMediaIntegrationTests` case begins with missing season output directories and compares independently retained payloads/metadata, original hashes and cleanup after sequential publication. Keep global bounds, transactional discovery acceptance, native separator rules and actual ownership exclusions intact; discovery/Review must never create directories. Controlled preparation failures and headless bindings remain distinct from actual filesystem/media and native desktop evidence.

Queue-list coverage lives in Application `BatchQueueListTests`, Infrastructure `PortableQueueListStoreTests` / `QueueListSpecialFileTests` / required `QueueListMediaIntegrationTests`, and headless `QueueRecoveryTests`. Controlled mount classification tests require no privileged NFS/CIFS mounts. Fresh-store restoration must exercise real inspection, independent explicit approval/Review and the existing sequential pipeline; compare retained payloads, source hashes and owned cleanup. CAS conflicts, malformed files, failed writes and old journal preservation must stay distinguishable from media-ownership failures. Do not serialize track indexes or turn Restore into resume. No native automation adapter is part of this delivery.

Keep code, documentation, and commit messages in English. Keep English/Russian resource keys and formatting arguments aligned. Never log full media probe XML/JSON or collect remote telemetry.

Original MovieSift code is licensed under [MIT](LICENSE), copyright (c) 2026 Nexusov, following the [explicit license decision](docs/license-decision.md). By intentionally submitting an original contribution for inclusion, you agree to license it under these terms; retain applicable third-party notices and identify separately licensed material. This does not transfer copyright or grant rights you do not hold. Preserve third-party notices, hashes, source links and build provenance; actual covered-source/material delivery and final release authorization remain separate requirements.

Packaging generates `artifacts/distribution-reports/<RID>/distribution-<RID>.json` after comparing every staged/archived payload with `SHA256SUMS`, matching NuGet payload/notice bytes to original package members, and validating tool/source/material provenance. Reports include unknowns and `redistributionReady: false`; success is mechanical evidence, not legal approval. Run `python -m unittest discover -s build/tests -v` for the small archive/evidence regressions. Both existing required jobs run these checks and default to uploading only compact reports, manifests, inventories and test evidence. The separately authorized manual `retain_candidates: true` path described in the [release checklist](docs/release-checklist.md#optional-candidate-handoff) can retain each exact verified binary/material pair and its identities for seven days, after all platform gates succeed. These public-repository workflow artifacts are not private. Keep original NuGet caches, private-path restore graphs and unrelated work directories out of every upload. Consult the [current distribution requirements](docs/verification/release-distribution-closure.md) and [historical evidence index](docs/verification/README.md) for their distinct scopes.

Unified workspace coverage uses Core `TrackGroupingPolicyTests`, Application `BatchAggregateTrackTests` / `BatchSelectionReviewTests`, Desktop `AggregateWorkspaceTests` and required `AggregateTrackMediaIntegrationTests`. The UI and tests use the same `ReviewSelectionsAsync` boundary: edit selection/destination intent, then explicitly Review to create independent plans. There is no alternate per-file approval API. Preserve union scope, concrete member identities, changed-only invalidation, per-file zero-audio consent and NoChange. The real aggregate case removes only a second-file Spanish occurrence with a conflicting local ID, then independently compares retained payloads, supported metadata, sources and owned cleanup. Headless keyboard/control tests are not native desktop acceptance.

Soft workspace coverage uses `WorkspacePresentationTests`, `AppearanceTests`, `SoftWorkspaceRenderTests` and the portable appearance-store/special-file tests. Set `MOVIESIFT_SOFT_RENDER_OUTPUT` to an owned development directory to export the actual Avalonia/Skia views; fixtures use controlled, nonprivate paths. The 1×/1.5×/2× offscreen densities do not change or verify desktop scaling. Preserve separate Review/Run, empty-batch preset access, active-job/theme isolation, malformed cosmetic records and warning/cleanup visibility. Filee-derived style patterns retain the shipped MIT notice under `docs/third-party`.
