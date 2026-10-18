# Phase 0 and Phase 1 acceptance

Scope stops before Phase 2. Implemented code, an executed test, and acceptance on a clean desktop are different evidence. A pending platform check must not be treated as passing.

## Status vocabulary

- **Implemented**: code/script exists and is reviewable; execution is not implied.
- **Verified**: the exact relevant check ran successfully; evidence records its limits.
- **Pending**: implemented or planned work still needs execution/evidence.
- **Blocked**: a concrete unavailable requirement prevents the stated acceptance.

## Phase 0 — foundation

| Acceptance | State | Evidence / remaining work |
| --- | --- | --- |
| Four-project dependency graph; .NET 10; stable aligned Avalonia; explicit composition | Verified by build/tests | Final locked restore, format verification, and Release build completed with 0 warnings and 0 errors. |
| Pinned SDK, runtime, package versions, lockfiles | Verified for Windows package | SDK 10.0.402 and runtime 10.0.13 are separate pins. Desktop locked restore and self-contained publish completed successfully. Full-solution checks are recorded below. |
| English-first and live English/Russian switching/persistence | Headless verified; desktop pending | Desktop suite 20/20; real-backend English/Russian headless renders at normal/narrow widths. Native desktop acceptance remains pending. |
| Pinned MKVToolNix acquisition/version/identification | Verified on Windows developer host | 102.0 executables ran; official Windows archive SHA256 matched; real fixture identification reported format 20. |
| Portable Windows proof | Verified on Windows developer host; target acceptance pending | Self-contained ZIP built, relocated, and completed the actual workflow; Windows 11 25H2 clean desktop remains unverified. |
| Linux closure without FUSE at runtime | Implemented; runtime pending | Build-time AppImage extraction, retained native tree, dependency report. Must execute on Ubuntu 24.04. |
| CI required media integration and platform archives | Implemented; run pending | Archives are built/tested locally on runners; only test/provenance evidence is uploaded while redistribution obligations remain unresolved. A definition is not a passing CI run. |

## Phase 1 — one MKV workflow

| Checkpoint | State | Required acceptance evidence |
| --- | --- | --- |
| 1. Actual inspection, bilingual inspector, Keep semantics | Verified in backend/headless tests | Pinned real MKV identification and 20/20 Desktop tests; native dialogs remain pending. |
| 2. Immutable plan/review, source/destination validation, actual progress/cancellation | Verified on Windows test host | Core 8/8, Application 25 passed / 1 Linux-only skip, process tests and real MKV integration. Linux FIFO check must run in Linux CI. |
| 3. Verification, warning review, no-overwrite publication, cleanup/journal | Verified for documented fixture/failure subset | Infrastructure 26/26 including 6 real-media cases; deterministic warning/race/failure tests. Simulated warnings remain separate from a real mkvmerge warning remux. |
| 4. Relocated archives and factual platform evidence | Windows developer-host verified; Linux pending | Actual packaged workflow and ZIP relocation completed on the available Windows host. Clean target desktop acceptance remains separate. |

## Executed evidence

The completed local checks are recorded below. CI links/results remain pending until its actual run completes; pending rows are not passing checks.

| Command/check | Result |
| --- | --- |
| `dotnet restore --locked-mode` | Passed on the final production source. |
| `dotnet format --verify-no-changes --no-restore` | Passed on the final production source. |
| `dotnet build --configuration Release --no-restore` | Passed: 0 warnings, 0 errors. |
| `dotnet test --configuration Release --no-build --no-restore` | Whole-solution run: 78 passed, 1 Linux-only skip. After the final tag-regression test/attachment description change, Infrastructure rerun passed 26/26 and Desktop rerun passed 20/20. Current tested aggregate: **79 passed, 1 Linux-only skip** across Core 8, Application 25 + 1 skip, Infrastructure 26, Desktop 20. |
| Actual pinned `mkvmerge`/`mkvextract` integration | 6 real-media Infrastructure cases passed, including exact retained VP8/PCM/SRT extraction, ordinary semantic XML, attachment payload hashing, multiple video and source preservation. This is separate from deterministic simulated warning/failure coverage. |
| Windows packaged/relocated actual workflow | Passed on final production source: pinned tool pair, actual inspection/remux/semantic and timestamp verification/no-overwrite publication, relocated spaces/Cyrillic paths and unrelated working directory. All 442 payload files passed the archive manifest check. Final generated source SHA256 remained `055c7f3ebdc3c579dd6d7740c78a1e8ee0e2525f23542b5c6af9a0869ce4a0f8`. |
| Linux packaged/relocated actual workflow | Pending Linux CI/runtime execution. |
| Linux CLI package in no-SDK/native-libraries-only container | Implemented; unrun locally because Docker is unavailable. CI must run it with the pinned Ubuntu 24.04 image, no network and a non-root user; record actual result separately from Desktop acceptance. |
| Real desktop file picker, focus/scaling, close during work, both languages | Pending platform-specific execution. |

## Platform record and remaining acceptance

The available host is **Windows 10 Home Single Language 22H2 x64, build 19045.6456**. Windows acquisition, self-contained publishing, native CLI startup, actual packaged workflow, and relocated archive workflow ran there. The archive includes .NET runtime **10.0.13**, published by SDK **10.0.402**, with the actual version checked in `TrackTrim.runtimeconfig.json`. Developer runtime/tool override variables were removed for packaged tests; the system still has a development SDK, so this is not a clean no-SDK machine acceptance.

Executed packaging checks used the pinned SDK, fixture, and verified cache in the workspace's `work` directory (shown with reproducible path resolution):

```powershell
$workspaceRoot = (Resolve-Path ../..).Path
./build/package.ps1 -RuntimeIdentifier win-x64 -Dotnet (Join-Path $workspaceRoot 'work/dotnet/dotnet.exe') -FixturePath (Join-Path $workspaceRoot 'work/fixtures/ordinary.mkv') -ToolCacheDirectory (Join-Path $workspaceRoot 'work/tools') -Overwrite
./build/verify-package.ps1 -ArchivePath ./artifacts/TrackTrim-win-x64.zip -FixturePath (Join-Path $workspaceRoot 'work/fixtures/ordinary.mkv')
```

Packaging performs the declared-RID locked restore without a global runtime override, matching the committed multiple-RID lock shape. The ZIP was confirmed to have exactly one application root and no files in its `data` directory. Archive relocation verifies every file against `SHA256SUMS` before launching the application.

The intended first targets remain **Windows 11 x64 25H2** and **Ubuntu 24.04 x64 Desktop using X11/XWayland**. Neither clean target desktop has been exercised locally. Native UI automation initialization failed before a desktop check could run (`failed to write kernel assets`, OS error 3); file picker/scaling/real desktop shutdown checks remain unavailable. Linux cross-publication and headless tests are not Linux desktop runtime acceptance.

Unverified areas remain: clean systems without SDK or installed media tools; native dialogs and scaling; behavior on removable/network filesystems and power loss; native Wayland; uncommon metadata outside the supported matrix; real mkvmerge warning-remux coverage if only a simulated exit-1 helper is available; complete corresponding-source distribution and original application license selection. Restart cleanup is deliberately conservative: if a recovered candidate still exists, worker termination cannot be proved and cleanup is unavailable. The source can be processed again as a fresh operation; old candidates are never automatically published or deleted.

Public release readiness is blocked by the unresolved licensing/source-distribution review and any unmet platform/safety acceptance above. The next bounded task after this implementation is to close the recorded Phase 0/1 acceptance gaps, not to begin batch processing or MP4.
