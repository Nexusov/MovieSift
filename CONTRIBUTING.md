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

The real-media suite uses generated fixtures, two checked-in direct FFmpeg-origin fixtures and the pinned backend. Fixture encoding is a development operation; FFmpeg is not a production or CI test-runtime dependency and is excluded from archives. Retained VP8/PCM/SRT and H264/AAC/SRT payload comparisons, chapters/tags, attachment payloads, track order/flags and source hashes are meaningful assertions. Deterministic fake ports/process helpers cover warning/race/failure paths and must be labelled separately from real remux verification.

Phase 2A tests are grouped under Application `Batches`, Infrastructure `BatchInputDiscoveryTests` / `BatchMediaIntegrationTests`, and Desktop batch headless tests. Phase 2B1 adds Core `LanguageRuleMatcherTests`, Application language/selection guard tests, Desktop `BatchLanguageRulesTests`, and real `BatchLanguageRuleIntegrationTests`. Phase 2B2 adds `LanguageTitleFilterTests`, Application/Desktop `BatchTitleFilterTests` and real `BatchTitleFilterIntegrationTests`. The real rule cases must obtain selections through production Preview/Apply before explicit approval/review, compare all retained extracted payloads from reordered inputs, source hashes and owned cleanup. Keep literal-title conjunction tests distinct from language uncertainty and preservation policy; regex-looking input remains literal. Use deterministic signals for lifecycle boundaries. These checks are separate from native desktop acceptance. Preserve the existing required media-test environment; do not turn missing tools/fixtures into a successful skip in CI.

Phase 2B3 adds pure `ForcedSubtitleRuleTests`, Application/Desktop `BatchForcedSubtitleTests`, controlled `MkvForcedFlagParsingTests` and real `BatchForcedSubtitleIntegrationTests`. The real case uses `ForcedSubtitleFixture` to give otherwise matching subtitles different actual flags and payloads across reordered inputs. It must exercise production rule application, compare all retained track/attachment payloads and source hashes, and retain both owned remux handles through exit. Keep JSON parsing, controlled lifecycle, headless and native evidence distinct. Do not bypass conservative language validation using title/forced nonmatches.

Generate the original fixtures without FFmpeg or a production codec dependency:

```powershell
python ./build/generate-fixtures.py --tool-directory ./tools --destination ./artifacts/fixtures
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

CI runs the checked-in inputs without installing FFmpeg. `TRACKTRIM_REQUIRE_MEDIA_TOOLS=1` prevents silent omission of the required MKVToolNix suite; provisioning and generated-fixture creation must succeed first. Record actual counts and skips in `docs/PLAN.md`; do not turn an unexecuted test definition into a passing result. Linux-only FIFO tests must run on Linux; their Windows skips are explicit. Controlled filesystem tests establish allocation-stage and malformed-record behavior; headless close/cleanup tests remain separate from native desktop shutdown acceptance.

## Portable package checks

```powershell
pwsh ./build/package.ps1 -RuntimeIdentifier win-x64 -FixturePath ./artifacts/fixtures/ordinary.mkv
pwsh ./build/verify-package.ps1 -ArchivePath ./artifacts/TrackTrim-win-x64.zip -FixturePath ./artifacts/fixtures/ordinary.mkv
```

Run the Linux equivalents on Linux. Packaging verifies the shipped runtime version, copies the complete normal publish output and tool closure, inventories restored NuGet licenses, records provenance, and checks the packaged entry point. Use `-Overwrite` only to replace an existing generated archive. Staging/relocation directories remain under `artifacts` for inspection.

The relocation test clears development runtime/tool variables, changes the working directory, exercises spaces/Cyrillic/metacharacters, and verifies that the source hash remains unchanged. It cannot prove the host lacks an installed SDK; clean target-system acceptance remains a separate check. Native file pickers, keyboard focus, scaling, shutdown during work, and both-language layouts require a real desktop session.

On a Linux developer/CI host with Docker, run `bash ./build/verify-clean-linux.sh ./artifacts/TrackTrim-linux-x64.tar.gz ./artifacts/fixtures/ordinary.mkv`. The image uses the pinned official Ubuntu 24.04 amd64 digest in `build/portable-smoke.Dockerfile`, installs only native OS libraries during image construction, then runs the packaged CLI without root or networking. It asserts that `dotnet`, MKVToolNix, and FFmpeg commands are not installed separately. The ASCII parent locale (`LANG=C`, `LC_ALL=C`, `LC_CTYPE=C`) deliberately tests that the native worker boundary handles Unicode independently of the inherited locale. The exact distro/package list, user/locale, and workflow log are retained under `artifacts/clean Linux Кириллица.*/evidence`. Docker is a development/CI dependency only; the distributed application does not use containers.

## Protected main and pull requests

Submit changes through a pull request from a review branch. The active [Protect main ruleset](https://github.com/Nexusov/TrackTrim/rules/25401528) applies only to `main`, with no bypass actors. It requires resolved review conversations and both existing checks from GitHub Actions (App ID `15368`):

- `Required media checks and win-x64 package`
- `Required media checks and linux-x64 package`

The branch must be up to date with its base before merging. No approving review, code-owner approval or last-push approval is required. Force pushes and branch deletion are blocked. Merge, squash and rebase remain available; automatic merging is disabled.

The existing verification workflow covers draft and documentation-only pull requests as well as ordinary changes, without path or fork exclusions. Fork runs may await GitHub's contributor approval; pending approval or missing checks do not waive the requirements. Preserve mandatory tool/fixture provisioning, full tests, package/relocation checks and the Linux no-SDK container check; do not add a skip guard or rename a required check without updating the policy deliberately.

The [applied protection record](docs/verification/main-protection.md) links the canonical configuration and readback commands. Green CI establishes the checks that ran; native desktop acceptance and licensing obligations retain their separate status.

## Changes and licensing

Phase 2C1 adds `LanguageRuleConfigurationTests`, Application `Presets` tests, `PortableRulePresetStoreTests` / `RulePresetSpecialFileTests`, Desktop `RulePresetsTests`, and `RulePresetMediaIntegrationTests`. Its review regressions include real-file `RulePresetUnicodeTests`, production-store/headless `RulePresetCatalogInitializationTests`, and `RulePresetDeleteGateTests` for availability and direct execution after write access changes. Keep pure validation, controlled write failures, real filesystem/cross-process locking, headless bindings and native functional evidence distinct. The media case must save/load through fresh production persistence instances before deriving different file-local choices through Preview/Apply; existing mandatory backend cases still cover remux/publication. Never serialize media/workflow/settings state into presets or silently repair a bad catalog. Linux FIFO/symlink cases must run in the required Linux job.

Follow `AGENTS.md` and the project dependency boundaries. Prefer small changes with a specific user-visible behavior or safety invariant. Update the relevant preservation matrix and tests when support expands. Current scope is Phase 2A independent plans/bounded discovery/sequential dispatch, Phase 2B explicit language/title/forced rules, and Phase 2C1 named portable presets with separate explicit Load. Matching uses Core policy and file-local inspections, never another file's IDs. Preview must not mutate approvals, Apply must reject stale state, and only real choice changes invalidate plans. Preset update/import/export, automatic loading, title-only/regex rules, other forced-filter modes, recursion, persistent queues and MP4 require another task. Do not claim unsupported container fidelity.

The Phase 2A review branch targets `review/phase-0-1` and depends on draft PR #1. Keep both reviews unmerged until separately authorized; the unfiltered `pull_request` workflow runs the same complete platform jobs on dependent PRs. Do not change required contexts or protected-main policy to deliver a feature.

The Phase 2B1 branch `review/phase-2b-language-rules` targets `review/phase-2a-batch` and depends on draft PR #2. Preserve both existing draft PRs. The same unfiltered workflow must run both exact platform contexts, including packaging/inventory/relocation and the applicable Linux container check.

The Phase 2B2 branch `review/phase-2b-title-filter` targets `review/phase-2b-language-rules` and depends on draft PR #3. The Phase 2B3 branch `review/phase-2b-forced-subtitles` targets `review/phase-2b-title-filter` and depends on draft PR #4. Preserve all earlier draft reviews; no branch merges or dependency changes are part of these slices.

The Phase 2C1 branch `review/phase-2c-rule-presets` targets `review/phase-2b-forced-subtitles` and depends on draft PR #5. It uses the unchanged complete Windows/Linux pull-request workflow.

Keep code, documentation, and commit messages in English. Keep English/Russian resource keys and formatting arguments aligned. Never log full media probe XML/JSON or collect remote telemetry.

No license for original TrackTrim code has been selected. Do not add an author identity or a public license without an explicit decision. Preserve third-party notices, hashes, source links, and build provenance; complete the corresponding-source distribution review before a public binary release. The [license proposal](docs/license-decision.md) requires the rights holder's decision and does not grant rights over existing contributions.

Packaging generates `artifacts/distribution-reports/<RID>/distribution-<RID>.json` after comparing every staged/archived payload with `SHA256SUMS`, matching NuGet payload/notice bytes to original package members, and validating tool/source/material provenance. Reports include unknowns and `redistributionReady: false`; success is mechanical evidence, not legal approval. Run `python -m unittest discover -s build/tests -v` for the small archive/evidence regressions. Both existing required jobs run these checks and upload only compact reports, manifests, inventories and test evidence. Keep binaries, original NuGet caches, private-path restore graphs and large source archives out of uploads. Consult the [distribution audit](docs/verification/distribution-audit.md) for exact remaining work.
