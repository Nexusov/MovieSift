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
pwsh ./build/acquire-tools.ps1 -RuntimeIdentifier win-x64 -Destination ./tools -WindowsMkvBuildDirectory /path/to/verified/windows-mkv-build
```

Use `linux-x64` on Linux. MKVToolNix artifacts and the FFmpeg/zlib source hashes are pinned in `build/tools.lock.json` and `build/ffmpeg.lock.json`; the tools are not checked into Git. Linux extraction occurs at build time and preserves the dependency tree, without requiring FUSE on users' systems. The build requires network access, adequate disk space, and Linux archive extraction tools; acquisition failures are real failures.

The Windows MKV pair is built from the same 102.0 release source using `build/build-mkv-windows.sh` and the exact UCRT64 binary/source package pins in `build/mkv-source.lock.json`. `build/provision-windows-mkv.ps1` runs only on an ephemeral Windows GitHub Actions runner, never as a local prerequisite installer. It builds only `mkvmerge`/`mkvextract`, retains required QtCore and the actual recursively imported DLLs, and collects complete source packages, original patches/PKGBUILDs, binary metadata, notices and configuration/compiler evidence. Each binary's BUILDINFO recipe hash must match its collected source PKGBUILD. GMP, ICU and Vorbis notices come from exact pinned upstream source members where their binary packages omit notices. Host-only packages are recorded, not shipped.

The required Windows job constructs or reuses only an exact-input verified build. Local acquisition/packaging can consume that complete worker/material directory through the parameter above or `TRACKTRIM_WINDOWS_MKV_BUILD_DIRECTORY`; no MSYS2 installation is then needed. `python build/distribution-inventory.py --validate-windows-mkv-build /path/to/build` independently checks the pair, all actual PE imports and complete material hashes. Review worker artifacts expire and do not substitute for delivering the covered source/materials with an eventual release. There is no automatic fallback to the historical official ZIP.

FFmpeg/ffprobe are built from the same pinned source with explicit static zlib, no autodetected optional codec libraries and no GPL/version3/nonfree configuration. Build-only prerequisites are GCC, binutils, GNU Make and NASM; native Windows builds also require MSYS2 Bash and MinGW-w64. CI uses `build/provision-windows-ffmpeg.ps1` only on its ephemeral Windows runner, with hash-pinned Make/NASM archives and no floating MSYS2 update. It refuses local-host installation. On Linux, install the documented compiler/make/NASM development prerequisites before acquisition. A verified existing source-build cache can be reused without a local compiler; source/RID/recipe and every worker/material hash must match. Actual compiler/configuration receipts accompany the package. Use a whitespace-free build-cache directory; packaged application, media and output paths still support spaces/Cyrillic. End users install neither compilers, .NET nor media tools.

The production backend resolves `tools/mkvmerge.exe` and `tools/mkvextract.exe` on Windows, and the native executables under `tools/app-root/usr/bin` on Linux. Linux library paths are set on the worker's process environment; the application never invokes media through a shell. The `tools/mkvmerge`/`mkvextract` Linux wrappers are only build-time acquisition/fixture helpers. Tests accept an explicit tool directory; the live application always uses its app-local bundle. A test override does not establish portable-release acceptance. Both MKV executables must match MKVToolNix 102.0. The separate MP4 adapter uses the exact FFmpeg/ffprobe build in `build/ffmpeg.lock.json`, under `tools` on both targets. There is no backend fallback or container conversion.

The real-media suite uses generated fixtures, two checked-in direct FFmpeg-origin fixtures and the pinned backend. Fixture encoding is a development operation. FFmpeg/ffprobe now ship for MP4 processing; ffplay and external development encoders are excluded. The historical MKV fixtures remain direct original outputs, with their earlier provenance unchanged. Retained VP8/PCM/SRT and H264/AAC/SRT payload comparisons, chapters/tags, attachment payloads, track order/flags and source hashes are meaningful assertions. Deterministic fake ports/process helpers cover warning/race/failure paths and must be labelled separately from real remux verification.

Phase 2A tests are grouped under Application `Batches`, Infrastructure `BatchInputDiscoveryTests` / `BatchMediaIntegrationTests`, and Desktop batch headless tests. Phase 2B1 adds Core `LanguageRuleMatcherTests`, Application language/selection guard tests, Desktop `BatchLanguageRulesTests`, and real `BatchLanguageRuleIntegrationTests`. Phase 2B2 adds `LanguageTitleFilterTests`, Application/Desktop `BatchTitleFilterTests` and real `BatchTitleFilterIntegrationTests`. The real rule cases must obtain selections through production Preview/Apply before explicit approval/review, compare all retained extracted payloads from reordered inputs, source hashes and owned cleanup. Keep literal-title conjunction tests distinct from language uncertainty and preservation policy; regex-looking input remains literal. Use deterministic signals for lifecycle boundaries. These checks are separate from native desktop acceptance. Preserve the existing required media-test environment; do not turn missing tools/fixtures into a successful skip in CI.

Phase 2B3 adds pure `ForcedSubtitleRuleTests`, Application/Desktop `BatchForcedSubtitleTests`, controlled `MkvForcedFlagParsingTests` and real `BatchForcedSubtitleIntegrationTests`. The real case uses `ForcedSubtitleFixture` to give otherwise matching subtitles different actual flags and payloads across reordered inputs. It must exercise production rule application, compare all retained track/attachment payloads and source hashes, and retain both owned remux handles through exit. Keep JSON parsing, controlled lifecycle, headless and native evidence distinct. Do not bypass conservative language validation using title/forced nonmatches.

Generate both original fixture sets before running the mandatory media suite:

```powershell
python ./build/generate-fixtures.py --tool-directory ./tools --destination ./artifacts/fixtures
python ./build/generate-mp4-fixtures.py --ffmpeg ./tools/ffmpeg.exe --destination ./artifacts/fixtures/mp4
$env:TRACKTRIM_TOOL_DIRECTORY = (Resolve-Path ./tools).Path
$env:TRACKTRIM_REQUIRE_MEDIA_TOOLS = '1'
dotnet test --configuration Release --no-build --no-restore --logger trx
```

The generator uses original embedded VP8 frames, synthesized PCM waveforms, subtitles and XML metadata. It also generates a longer-frame-count cancellation input: the real cancellation test must observe an active worker at cancellation, retain its process handle, and await that same worker's exit. Progress buffered from an already exited process cannot pass that regression.

The checked-in H264/two-AAC/SRT inputs are direct Matroska output from development FFmpeg 9.0.2 and 7.1.1. Do not pre-remux them through MKVToolNix: their original language aliases and missing AAC duration header are the regression triggers. [Fixture provenance](tests/TrackTrim.Infrastructure.Tests/Fixtures/README.md) records exact input hashes, executable/archive provenance and commands. Regeneration is optional and requires explicitly supplying the matching development encoder:

```powershell
python ./build/generate-ffmpeg-fixture.py --ffmpeg /absolute/path/to/ffmpeg-9.0.2 --version 9.0.2 --destination ./tests/TrackTrim.Infrastructure.Tests/Fixtures/h264-aac-aliases.mkv
python ./build/generate-ffmpeg-fixture.py --ffmpeg /absolute/path/to/ffmpeg-7.1.1 --version 7.1.1 --destination ./tests/TrackTrim.Infrastructure.Tests/Fixtures/h264-aac-no-default-duration.mkv
```

The 7.1.1 cases are positive missing-duration/alias/payload regressions. The 9.0.2 cases must reject a known final AAC duration change; do not weaken timestamp/end-boundary checks to make every AAC source pass. The only terminal-boundary exception is the pinned extractor's duplicate-last-frame sentinel when the bounded AAC-LC missing-duration proof succeeds. Real chapter/tag alias cases use separately generated metadata fixtures.

CI uses the pinned bundled FFmpeg/ffprobe pair and checked-in original H.264/AAC source to generate small MP4 fixtures; use `./tools/ffmpeg` on Linux. `TRACKTRIM_REQUIRE_MEDIA_TOOLS=1` prevents silent omission of the required MKV/MP4 backend suites; provisioning and generated-fixture creation must succeed first. Record actual counts and skips in `docs/PLAN.md`; do not turn an unexecuted test definition into a passing result. Linux-only FIFO tests must run on Linux; their Windows skips are explicit. Controlled filesystem tests establish allocation-stage and malformed-record behavior; headless close/cleanup tests remain separate from native desktop shutdown acceptance.

## Portable package checks

```powershell
pwsh ./build/package.ps1 -RuntimeIdentifier win-x64 -FixturePath ./artifacts/fixtures/ordinary.mkv -Mp4FixturePath ./artifacts/fixtures/mp4/ordinary.mp4
pwsh ./build/verify-package.ps1 -ArchivePath ./artifacts/TrackTrim-win-x64.zip -FixturePath ./artifacts/fixtures/ordinary.mkv -Mp4FixturePath ./artifacts/fixtures/mp4/ordinary.mp4
```

Run the Linux equivalents on Linux. Packaging verifies the shipped runtime version, copies the complete normal publish output and tool closure, inventories restored NuGet licenses, records provenance, and checks the packaged entry point. Use `-Overwrite` only to replace an existing generated archive. Staging/relocation directories remain under `artifacts` for inspection.

The root `LICENSE` must match the operative MIT license byte-for-byte; the obsolete application-license-pending marker is rejected. After packaging, run `python ./build/prepare-review-materials.py --repository . --archive ./artifacts/TrackTrim-win-x64.zip --inventory ./artifacts/distribution-reports/win-x64/distribution-win-x64.json --output ./artifacts/review-materials-win-x64` (use the corresponding Linux paths on Linux). This copies inventory-verified notices/source/build materials and the exact clean commit's source into a private matching bundle. Existing CI prepares both platform sets and uploads only their compact identity summaries, not the binary or material archives. Actual public covered-source delivery still requires separate publication authorization.

The relocation test clears development runtime/tool variables, changes the working directory, exercises spaces/Cyrillic/metacharacters, and verifies that the source hash remains unchanged. It cannot prove the host lacks an installed SDK; clean target-system acceptance remains a separate check. Native file pickers, keyboard focus, scaling, shutdown during work, and both-language layouts require a real desktop session.

On a Linux developer/CI host with Docker, run `bash ./build/verify-clean-linux.sh ./artifacts/TrackTrim-linux-x64.tar.gz ./artifacts/fixtures/ordinary.mkv ./artifacts/fixtures/mp4/ordinary.mp4`. The image uses the pinned official Ubuntu 24.04 amd64 digest in `build/portable-smoke.Dockerfile`, installs only native OS libraries during image construction, then runs the packaged CLI without root or networking. It asserts that `dotnet`, MKVToolNix, and FFmpeg commands are not installed separately. The ASCII parent locale (`LANG=C`, `LC_ALL=C`, `LC_CTYPE=C`) deliberately tests that the native worker boundary handles Unicode independently of the inherited locale. The exact distro/package list, user/locale, and workflow log are retained under `artifacts/clean Linux Кириллица.*/evidence`. Docker is a development/CI dependency only; the distributed application does not use containers.

## Protected main and pull requests

Submit changes through a pull request from a review branch. The active [Protect main ruleset](https://github.com/Nexusov/TrackTrim/rules/25401528) applies only to `main`, with no bypass actors. It requires resolved review conversations and both existing checks from GitHub Actions (App ID `15368`):

- `Required media checks and win-x64 package`
- `Required media checks and linux-x64 package`

The branch must be up to date with its base before merging. No approving review, code-owner approval or last-push approval is required. Force pushes and branch deletion are blocked. Merge, squash and rebase remain available; automatic merging is disabled.

The existing verification workflow covers draft and documentation-only pull requests as well as ordinary changes, without path or fork exclusions. Fork runs may await GitHub's contributor approval; pending approval or missing checks do not waive the requirements. Preserve mandatory tool/fixture provisioning, full tests, package/relocation checks and the Linux no-SDK container check; do not add a skip guard or rename a required check without updating the policy deliberately.

The [applied protection record](docs/verification/main-protection.md) links the canonical configuration and readback commands. Green CI establishes the checks that ran; native desktop acceptance and licensing obligations retain their separate status.

## Changes and licensing

Phase 2C1 adds `LanguageRuleConfigurationTests`, Application `Presets` tests, `PortableRulePresetStoreTests` / `RulePresetSpecialFileTests`, Desktop `RulePresetsTests`, and `RulePresetMediaIntegrationTests`. Its review regressions include real-file `RulePresetUnicodeTests`, production-store/headless `RulePresetCatalogInitializationTests`, and `RulePresetDeleteGateTests` for availability and direct execution after write access changes. Keep pure validation, controlled write failures, real filesystem/cross-process locking, headless bindings and native functional evidence distinct. The media case must save/load through fresh production persistence instances before deriving different file-local choices through Preview/Apply; existing mandatory backend cases still cover remux/publication. Never serialize media/workflow/settings state into presets or silently repair a bad catalog. Linux FIFO/symlink cases must run in the required Linux job.

Follow `AGENTS.md` and the project dependency boundaries. Prefer small changes with a specific user-visible behavior or safety invariant. Update the relevant preservation matrix and tests when support expands. Current scope is Phase 2A independent plans/bounded discovery/sequential dispatch, Phase 2B explicit language/title/forced rules, Phase 2C1 named portable presets with separate explicit Load, and Phase 2D1 optional recursive folder input with relative output directories. Matching uses Core policy and file-local inspections, never another file's IDs. Preview must not mutate approvals, Apply must reject stale state, and only real choice changes invalidate plans. Preset update/import/export, automatic loading, title-only/regex rules, other forced-filter modes, live job restoration and byte-offset resume require another task. Phase 3 adds MP4 using the same workflow and a separate documented preservation contract. Do not claim unsupported container fidelity.

The reviewed PR stack #1–#9 has been integrated through ordinary protected merges; the review branches remain available. The final `review/release-candidate` PR targets `main` and remains draft/unmerged pending owner review. Preserve both required contexts and the complete workflow when correcting a demonstrated candidate defect. Do not create public tags/assets or select the original-code license as an incidental code change.

Recursive-input checks live in `OutputPathPolicyTests`, `RecursiveBatchDiscoveryTests`, `BatchFolderProvenanceTests`, `DestinationPreparationTests`, `NestedDestinationTests` and headless `BatchRecursiveFolderTests`. The mandatory `RecursiveBatchMediaIntegrationTests` case begins with missing season output directories and compares independently retained payloads/metadata, original hashes and cleanup after sequential publication. Keep global bounds, transactional discovery acceptance, native separator rules and actual ownership exclusions intact; discovery/Review must never create directories. Controlled preparation failures and headless bindings remain distinct from actual filesystem/media and native desktop evidence.

Queue-list coverage lives in Application `BatchQueueListTests`, Infrastructure `PortableQueueListStoreTests` / `QueueListSpecialFileTests` / required `QueueListMediaIntegrationTests`, and headless `QueueRecoveryTests`. Controlled mount classification tests require no privileged NFS/CIFS mounts. Fresh-store restoration must exercise real inspection, independent explicit approval/Review and the existing sequential pipeline; compare retained payloads, source hashes and owned cleanup. CAS conflicts, malformed files, failed writes and old journal preservation must stay distinguishable from media-ownership failures. Do not serialize track indexes or turn Restore into resume. No native automation adapter is part of this delivery.

Keep code, documentation, and commit messages in English. Keep English/Russian resource keys and formatting arguments aligned. Never log full media probe XML/JSON or collect remote telemetry.

Original TrackTrim code is licensed under [MIT](LICENSE), copyright (c) 2026 Nexusov, following the [explicit license decision](docs/license-decision.md). By intentionally submitting an original contribution for inclusion, you agree to license it under these terms; retain applicable third-party notices and identify separately licensed material. This does not transfer copyright or grant rights you do not hold. Preserve third-party notices, hashes, source links and build provenance; actual covered-source/material delivery and final release authorization remain separate requirements.

Packaging generates `artifacts/distribution-reports/<RID>/distribution-<RID>.json` after comparing every staged/archived payload with `SHA256SUMS`, matching NuGet payload/notice bytes to original package members, and validating tool/source/material provenance. Reports include unknowns and `redistributionReady: false`; success is mechanical evidence, not legal approval. Run `python -m unittest discover -s build/tests -v` for the small archive/evidence regressions. Both existing required jobs run these checks and default to uploading only compact reports, manifests, inventories and test evidence. The separately authorized manual `retain_candidates: true` path described in the [release checklist](docs/release-checklist.md#optional-candidate-handoff) can retain each exact verified binary/material pair and its identities for seven days, after all platform gates succeed. These public-repository workflow artifacts are not private. Keep original NuGet caches, private-path restore graphs and unrelated work directories out of every upload. Consult the [distribution audit](docs/verification/distribution-audit.md) for exact remaining work.

Unified workspace coverage uses Core `TrackGroupingPolicyTests`, Application `BatchAggregateTrackTests` / `BatchSelectionReviewTests`, Desktop `AggregateWorkspaceTests` and required `AggregateTrackMediaIntegrationTests`. The ordinary UI has one Review action; tests may use the lower-level per-file approval API to establish specific pre-existing authority. Preserve union scope, concrete member identities, changed-only invalidation, per-file zero-audio consent and NoChange. The real aggregate case removes only a second-file Spanish occurrence with a conflicting local ID, then independently compares retained payloads, supported metadata, sources and owned cleanup. Headless keyboard/control tests are not native desktop acceptance.

Soft workspace coverage uses `WorkspacePresentationTests`, `AppearanceTests`, `SoftWorkspaceRenderTests` and the portable appearance-store/special-file tests. Set `TRACKTRIM_SOFT_RENDER_OUTPUT` to an owned development directory to export the actual Avalonia/Skia views; fixtures use controlled, nonprivate paths. The 1×/1.5×/2× offscreen densities do not change or verify desktop scaling. Preserve separate Review/Run, empty-batch preset access, active-job/theme isolation, malformed cosmetic records and warning/cleanup visibility. Filee-derived style patterns retain the shipped MIT notice under `docs/third-party`.
