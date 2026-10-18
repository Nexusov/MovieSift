# Phase 0 and Phase 1 acceptance

Scope stops before Phase 2. Implemented code, an executed test, and acceptance on a clean desktop are different evidence. The review corrections passed final locked restore, format verification, Release build and complete platform CI: **Windows 222 passed + 5 Linux-only skips; Linux 227/227 passed**. Both archives, relocated workflows and the no-SDK Linux container passed. Native target-desktop and licensing acceptance remain open.

## Current review corrections

| Finding | Implemented behavior | Current evidence |
| --- | --- | --- |
| 1. Capacity after allocation | Initial advice requests source length; publication requests zero additional media bytes while rechecking paths, aliases and collisions. Actual allocation/tool failures still fail. | Controlled coordinator and real-filesystem regressions pass in the current Application suite. |
| 2. AAC timing | Only unchanged bounded AAC-LC ASC/rate can justify a new 1024-sample DefaultDuration within 1 ns integer rounding. All actual frame counts/timestamps and known terminal boundaries remain checked. Only the matching duplicate-last-frame synthetic end sentinel can use the inferred duration. | Direct FFmpeg 7.1.1 positives, shifted-AAC negative and FFmpeg 9.0.2 known-final-duration negatives passed on both runners. |
| 3. Language aliases | One explicit ISO 639 B/T/alpha-2 table serves tracks, chapters and tags; regional/script/private/unknown distinctions remain exact. Ordinary chapter additions and independent tag additions are separate cases. | Pure alias/XML and real-tool chapter/tag metadata regressions passed on both runners. |
| 4. Close during warning review | Explicit remain/discard-and-close choice, one awaited cleanup, repeated close requests gated; failure keeps the window open. | Current Desktop suite **32/32 passed**; native OS window acceptance remains pending. |
| 5. Invalid ownership records | Validate nullable shape, version, stage, owner identity, GUID/token, canonical adjacent paths, exact record filename and marker. JSON uses regular-file handles before size/read operations. InvalidRecord notice retains evidence and authorizes no cleanup; settings failure does not hide the scan. | Initial red reproduction produced 18 failures. Corrected Application suite: Windows **69 + 4 OS skips**, Linux **73/73**; EN/RU headless notices and real record/marker/settings FIFO cases pass. |
| 6. SegmentFamily | Bounded Segment/Info traversal rejects actual family/previous/next linkage before planning. Ambiguous boundaries/unknown-sized children fail closed; regular-file handles prevent FIFO blocking. No extra production tool. | Actual family input/ordinary publish control and bounded reader cases passed on both runners; the direct reader FIFO case passed on Linux. |
| 7. Evidence quality | Retained SRT bytes including zero audio; observed live cancellation worker with retained handle and awaited exit; untouched direct FFmpeg-origin fixture provenance. | All **14 actual-media cases** passed on both runners. FFmpeg and fixtures are excluded from production archives. |

Final local Windows verification and Windows CI passed: Core **8**, Application **69 + 4 Linux-only skips**, Infrastructure **113 + 1 Linux-only skip**, Desktop **32**. Linux CI passed Core **8**, Application **73**, Infrastructure **114**, Desktop **32**, with no skips. All five FIFO cases ran there: source inspection, Segment Info and JSON record/marker/settings. Controlled filesystem/process, actual media, headless UI and native desktop evidence remain distinct.

## Status vocabulary

- **Implemented**: code/script exists and is reviewable; execution is not implied.
- **Verified**: the exact relevant check ran successfully; evidence records its limits.
- **Pending**: implemented or planned work still needs execution/evidence.
- **Blocked**: a concrete unavailable requirement prevents the stated acceptance.

## Phase 0 — foundation

| Acceptance | State | Evidence / remaining work |
| --- | --- | --- |
| Four-project dependency graph; .NET 10; stable aligned Avalonia; explicit composition | Verified by build/tests | Final locked restore, format verification, and Release build completed with 0 warnings and 0 errors. |
| Pinned SDK, runtime, package versions, lockfiles | Verified on Windows and Linux runners | SDK 10.0.402 and runtime 10.0.13 are separate pins. Locked restore and self-contained publishing completed successfully on both platforms. |
| English-first and live English/Russian switching/persistence | Headless verified; desktop pending | Current Desktop suite **32/32**; real-backend English/Russian headless renders at normal/narrow widths. Native desktop acceptance remains pending. |
| Pinned MKVToolNix acquisition/version/identification | Verified on Windows and Linux runners | 102.0 executables ran; both pinned archive hashes matched; real fixture identification reported format 20. |
| Portable Windows proof | Verified on Windows CI; target desktop pending | Self-contained ZIP/actual workflow and all **443** relocated manifest payload checks passed. The local deliverable is rebuilt after the evidence/doc commit. Windows 11 25H2 clean desktop remains unverified. |
| Linux closure without FUSE at runtime | Verified on Ubuntu runner and no-SDK container | Build-time AppImage extraction, native closure, archive/relocation and actual workflow passed; the no-SDK container also passed with ASCII parent locale, non-root user and no network. |
| CI required media integration and platform archives | Verified | Both jobs passed in run `39090376738` for review head `3b53414647a82eba2490e5298f37fa3bec990643`, using the synthetic merge identified below. Archives remain local to runners; only evidence is uploaded while redistribution obligations remain unresolved. |

## Phase 1 — one MKV workflow

| Checkpoint | State | Required acceptance evidence |
| --- | --- | --- |
| 1. Actual inspection, bilingual inspector, Keep semantics | Verified in backend/headless tests | Pinned real MKV identification and **32/32 Desktop tests**, including resulting default-audio status and warning-close review; native dialogs remain pending. |
| 2. Immutable plan/review, source/destination validation, actual progress/cancellation | Verified on Windows/Linux runners | Core **8/8**; Application Windows **69 + 4 OS skips**, Linux **73/73**; controlled process and actual MKV cases pass. All five Linux FIFO regressions ran successfully. |
| 3. Verification, warning review, no-overwrite publication, cleanup/journal | Verified for the documented subset | Infrastructure Windows **113 + 1 OS skip**, Linux **114/114**, including **14 actual-media cases**; controlled warning/race/filesystem tests. Simulated warnings remain separate from a real mkvmerge warning remux. |
| 4. Relocated archives and factual platform evidence | Runner/container verified; target desktops pending | Both actual packaged workflows/relocations and the no-SDK Linux container passed. Clean target desktop acceptance remains separate. |

## Executed evidence

The final local checks cover review head `3b53414647a82eba2490e5298f37fa3bec990643`. [CI run 39090376738](https://github.com/Nexusov/TrackTrim/actions/runs/39090376738) passed both complete jobs. GitHub checked out synthetic PR merge `5c6fe092db8d523fd62957e5c2293cfaf5c55d31`, with comparison base `69c9babe668c3ba6d0032de3304f793337964c4f` and that review head as parents. This distinguishes the tested review source from the CI checkout SHA; later evidence-only documentation commits do not change production behavior.

| Command/check | Result |
| --- | --- |
| `dotnet restore --locked-mode` | Passed on the final production source. |
| `dotnet format --verify-no-changes --no-restore` | Passed on the final production source. |
| `dotnet build --configuration Release --no-restore` | Passed: 0 warnings, 0 errors. |
| `dotnet test --configuration Release --no-build --no-restore` | Windows local/CI: **222 passed, 5 Linux-only skips**, 227 total. Linux CI: **227/227 passed**, no skips. Project counts are listed above. |
| Actual pinned `mkvmerge`/`mkvextract` integration | **14 actual-media cases passed on both runners**: retained VP8/PCM/SRT and direct FFmpeg-origin H264/AAC/SRT extraction/source hashes; semantic XML and real chapter/tag aliases; attachment payloads; multiple video; observed live-worker cancellation/reap; SegmentFamily rejection/ordinary publish control; shifted/known-end AAC negative verification cases. Controlled warning/failure cases are separate. |
| Windows packaged/relocated actual workflow | Passed: pinned pair, actual inspection/remux/semantic and timing verification/no-overwrite publication, **443** relocated manifest payload checks, unrelated working directory and spaces/Cyrillic/metacharacters. Source SHA256 remained `055c7f3ebdc3c579dd6d7740c78a1e8ee0e2525f23542b5c6af9a0869ce4a0f8`. The final local ZIP is regenerated after the evidence/doc commit. |
| Linux packaged/relocated actual workflow | Passed on Ubuntu 24.04.5: self-contained tar.gz, all **418** relocated manifest payload checks and actual workflow. Source SHA256 remained `cf883642cf19082d99f0682afd3f9b54fcee14c7daef90ceb2edc6b6cd955378`. |
| Linux CLI package in no-SDK/native-libraries-only container | Passed complete actual workflow under `LANG=C`, `LC_ALL=C`, `LC_CTYPE=C`, user `1001:1001`, disabled network and read-only OS. No SDK/runtime/media commands were installed separately. Cyrillic paths and source SHA256 `cf883642cf19082d99f0682afd3f9b54fcee14c7daef90ceb2edc6b6cd955378` were preserved. |
| Real desktop file picker, focus/scaling, close during work, both languages | Pending platform-specific execution. |

The successful CI result includes mandatory acquisition/fixture provisioning, locked restore, formatting/build, all platform tests, both self-contained archives/relocations and the complete no-SDK Linux container. No failing mandatory check was converted to a skip.

## Platform record and remaining acceptance

The available host is **Windows 10 Home Single Language 22H2 x64, build 19045.6456**. Windows acquisition, self-contained publishing, native CLI startup, actual packaged workflow, and relocated archive workflow ran there. The archive includes .NET runtime **10.0.13**, published by SDK **10.0.402**, with the actual version checked in `TrackTrim.runtimeconfig.json`. Developer overrides were removed for packaged tests. No system SDK was found in the current inventory, but a workspace SDK and multiple system runtimes exist; this is not a clean target-machine acceptance.

Executed CI environments were **Windows Server 2025 Datacenter 10.0.26100**, runner image `windows-2025-vs2026` version `20261007.264.1`, and **Ubuntu 24.04.5 LTS**, runner image `ubuntu-24.04` version `20261012.334.1`. The pinned native-libraries-only container also reports Ubuntu 24.04.5 LTS. Its exact base digest, `dpkg-query` versions, user, parent locale and unchanged source hash are retained in [the captured container environment](verification/linux-container-environment.txt), which is byte-for-byte identical to the successful run's evidence artifact (SHA256 `5c59af9d803d1c4c804f581fb941437b9c4183722f5bb90f456a6f50fba81cec`). These are CLI/headless checks, not native desktop sessions.

Local packaging uses the pinned SDK, fixture and verified cache in the workspace's `work` directory (shown with reproducible path resolution). The commands were exercised on the developer host and are repeated after the final evidence/doc commit to deliver a ZIP containing those documents:

```powershell
$workspaceRoot = (Resolve-Path ../..).Path
./build/package.ps1 -RuntimeIdentifier win-x64 -Dotnet (Join-Path $workspaceRoot 'work/dotnet/dotnet.exe') -FixturePath (Join-Path $workspaceRoot 'work/fixtures/ordinary.mkv') -ToolCacheDirectory (Join-Path $workspaceRoot 'work/tools') -Overwrite
./build/verify-package.ps1 -ArchivePath ./artifacts/TrackTrim-win-x64.zip -FixturePath (Join-Path $workspaceRoot 'work/fixtures/ordinary.mkv')
```

Packaging performs the declared-RID locked restore without a global runtime override, matching the committed multiple-RID lock shape. The ZIP was confirmed to have exactly one application root and no files in its `data` directory. Archive relocation verifies every file against `SHA256SUMS` before launching the application.

The intended first targets remain **Windows 11 x64 25H2** and **Ubuntu 24.04 x64 Desktop using X11/XWayland**. Neither clean target desktop has been exercised locally. The [current native acceptance attempt and manual handoff](verification/native-desktop-acceptance.md) use source/artifact `e16050e7de92ab8ee4ac8f86094b41b2e2583cec`: fresh Windows 443-payload/CLI checks passed with unchanged source hash, but supported GUI initialization failed before JavaScript import (`failed to write kernel assets`, OS error 3). No desktop GUI was launched, no native screenshots or application defects were demonstrated, and no fallback UI automation was used. Current Win10 dialog/focus/scaling/cancellation/close checks are blocked; verification-close and native warning cases remain not run. No usable Ubuntu Desktop/builder or Linux handoff archive/hash is available; owner build/hash prerequisites are recorded in the handoff. Earlier CI/container receipts above remain valid separate evidence.

Unverified areas remain: a clean Windows 11 target without installed SDK/media tools; native dialogs/scaling/shutdown on both intended desktops; removable/network filesystems and power loss; native Wayland; uncommon/private metadata outside the supported matrix; a real mkvmerge warning-remux fixture (warning approval/discard logic is verified with deterministic simulation); complete corresponding-source distribution and original application license selection. The bounded Segment Info reader rejects detected SegmentFamily/previous/next linkage; it cannot enumerate all unknown/private Matroska semantics.

Restart cleanup is deliberately conservative: if a recovered candidate still exists, worker termination cannot be proved and cleanup is unavailable. The source can be processed again as a fresh operation; old candidates are never automatically published or deleted.

Malformed or insufficient ownership JSON remains visible for manual review and never authorizes cleanup. Its original record, marker and candidate are retained. Headless warning-close and invalid-record regressions do not replace a real desktop shutdown test.

Public release readiness is blocked by the unresolved licensing/source-distribution review and any unmet platform/safety acceptance above. The next bounded task after this implementation is to close the recorded Phase 0/1 acceptance gaps, not to begin batch processing or MP4.
