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
| Pinned SDK, runtime, package versions, lockfiles | Verified on Windows and Linux runners | SDK 10.0.402 and runtime 10.0.13 are separate pins. Locked restore and self-contained publishing completed successfully on both platforms. |
| English-first and live English/Russian switching/persistence | Headless verified; desktop pending | Desktop suite 25/25 on production source `c063a25f63d3910cda95421588f69a7662a87684`; real-backend English/Russian headless renders at normal/narrow widths. Native desktop acceptance remains pending. |
| Pinned MKVToolNix acquisition/version/identification | Verified on Windows and Linux runners | 102.0 executables ran; both pinned archive hashes matched; real fixture identification reported format 20. |
| Portable Windows proof | Verified on developer host and Windows Server CI; target desktop pending | Self-contained ZIP built, relocated, and completed the actual workflow; Windows 11 25H2 clean desktop remains unverified. |
| Linux closure without FUSE at runtime | Verified on Ubuntu 24.04.5 runner and no-SDK container | AppImage build-time extraction, retained native closure, self-contained archive, manifest/relocation and real workflow passed. The clean container also passed under an ASCII parent locale with Cyrillic paths, no network and no root. |
| CI required media integration and platform archives | Verified | Both jobs passed in CI run `39084885603` on production source `c063a25f63d3910cda95421588f69a7662a87684`. Archives remain local to runners; only evidence is uploaded while redistribution obligations remain unresolved. |

## Phase 1 — one MKV workflow

| Checkpoint | State | Required acceptance evidence |
| --- | --- | --- |
| 1. Actual inspection, bilingual inspector, Keep semantics | Verified in backend/headless tests | Pinned real MKV identification and 25/25 Desktop tests, including explicit resulting default-audio status; native dialogs remain pending. |
| 2. Immutable plan/review, source/destination validation, actual progress/cancellation | Verified on Windows and Linux runners | Core 8/8; Application Windows 26 passed / 1 Linux-only skip, Linux 27/27; process tests and real MKV integration. Linux FIFO rejection ran successfully. |
| 3. Verification, warning review, no-overwrite publication, cleanup/journal | Verified for documented fixture/failure subset | Infrastructure 26/26 including 6 real-media cases; deterministic warning/race/failure tests. Simulated warnings remain separate from a real mkvmerge warning remux. |
| 4. Relocated archives and factual platform evidence | Windows/Linux runner and no-SDK Linux container verified; desktops pending | Actual packaged workflow and archive relocation completed on both runners; the no-SDK container passed its Unicode regression. Clean target desktop acceptance remains separate. |

## Executed evidence

The completed local checks and full CI run used production source `c063a25f63d3910cda95421588f69a7662a87684`. The checked-in documentation may receive later evidence-only changes; this SHA identifies the tested production implementation.

| Command/check | Result |
| --- | --- |
| `dotnet restore --locked-mode` | Passed on the final production source. |
| `dotnet format --verify-no-changes --no-restore` | Passed on the final production source. |
| `dotnet build --configuration Release --no-restore` | Passed: 0 warnings, 0 errors. |
| `dotnet test --configuration Release --no-build --no-restore` | Windows local/CI: **85 passed, 1 Linux-only skip** out of 86: Core 8, Application 26 + 1 skip, Infrastructure 26, Desktop 25. Linux CI: **86/86 passed**, including the actual FIFO rejection (Application 27/27). |
| Actual pinned `mkvmerge`/`mkvextract` integration | 6 real-media Infrastructure cases passed on both runners, including exact retained VP8/PCM/SRT extraction, ordinary semantic XML, attachment payload hashing, multiple video and source preservation. This is separate from deterministic simulated warning/failure coverage. |
| Windows packaged/relocated actual workflow | Passed in Windows Server CI: pinned pair, actual inspection/remux/semantic and timestamp verification/no-overwrite publication, relocated spaces/Cyrillic paths and unrelated working directory. Developer-host archive also passed its manifest checks and preserved source SHA256 `055c7f3ebdc3c579dd6d7740c78a1e8ee0e2525f23542b5c6af9a0869ce4a0f8`; the deliverable is regenerated after the final evidence/doc commit. |
| Linux packaged/relocated actual workflow | Passed on Ubuntu 24.04.5 runner. Self-contained tar.gz created, all **417** manifest payload checks passed after relocation, actual workflow completed. Source SHA256 stayed `cf883642cf19082d99f0682afd3f9b54fcee14c7daef90ceb2edc6b6cd955378`. |
| Linux CLI package in no-SDK/native-libraries-only container | Passed the complete actual workflow under `LANG=C`, `LC_ALL=C`, `LC_CTYPE=C`, user `1001:1001`, disabled network and a read-only OS image. No SDK/runtime/media commands were installed separately. Cyrillic paths and source SHA256 `cf883642cf19082d99f0682afd3f9b54fcee14c7daef90ceb2edc6b6cd955378` were preserved. |
| Real desktop file picker, focus/scaling, close during work, both languages | Pending platform-specific execution. |

[CI run 39084885603](https://github.com/Nexusov/TrackTrim/actions/runs/39084885603) **passed both platform jobs**, including mandatory real-media tests, self-contained archives, relocation checks, and the complete no-SDK Linux container workflow. It tested production source `c063a25f63d3910cda95421588f69a7662a87684`.

## Platform record and remaining acceptance

The available host is **Windows 10 Home Single Language 22H2 x64, build 19045.6456**. Windows acquisition, self-contained publishing, native CLI startup, actual packaged workflow, and relocated archive workflow ran there. The archive includes .NET runtime **10.0.13**, published by SDK **10.0.402**, with the actual version checked in `TrackTrim.runtimeconfig.json`. Developer runtime/tool override variables were removed for packaged tests; the system still has a development SDK, so this is not a clean no-SDK machine acceptance.

Executed CI environments were **Windows Server 2025 Datacenter 10.0.26100**, runner image `windows-2025-vs2026` version `20261007.264.1`, and **Ubuntu 24.04.5 LTS**, runner image `ubuntu-24.04` version `20261012.334.1`. The pinned native-libraries-only container also reports Ubuntu 24.04.5 LTS; its exact base digest, `dpkg-query` versions, user, parent locale and unchanged source hash are retained in [the captured container environment](verification/linux-container-environment.txt). These are CLI/headless checks, not native desktop sessions.

Executed packaging checks used the pinned SDK, fixture, and verified cache in the workspace's `work` directory (shown with reproducible path resolution):

```powershell
$workspaceRoot = (Resolve-Path ../..).Path
./build/package.ps1 -RuntimeIdentifier win-x64 -Dotnet (Join-Path $workspaceRoot 'work/dotnet/dotnet.exe') -FixturePath (Join-Path $workspaceRoot 'work/fixtures/ordinary.mkv') -ToolCacheDirectory (Join-Path $workspaceRoot 'work/tools') -Overwrite
./build/verify-package.ps1 -ArchivePath ./artifacts/TrackTrim-win-x64.zip -FixturePath (Join-Path $workspaceRoot 'work/fixtures/ordinary.mkv')
```

Packaging performs the declared-RID locked restore without a global runtime override, matching the committed multiple-RID lock shape. The ZIP was confirmed to have exactly one application root and no files in its `data` directory. Archive relocation verifies every file against `SHA256SUMS` before launching the application.

The intended first targets remain **Windows 11 x64 25H2** and **Ubuntu 24.04 x64 Desktop using X11/XWayland**. Neither clean target desktop has been exercised locally. Native UI automation initialization failed before a desktop check could run (`failed to write kernel assets`, OS error 3); file picker/scaling/real desktop shutdown checks remain unavailable. Linux cross-publication and headless tests are not Linux desktop runtime acceptance.

Unverified areas remain: a clean Windows 11 target without installed SDK/media tools; native dialogs and scaling on both intended desktops; behavior on removable/network filesystems and power loss; native Wayland; uncommon metadata outside the supported matrix; a real mkvmerge warning-remux fixture (warning approval/discard logic is verified with deterministic simulation); complete corresponding-source distribution and original application license selection. **Known preservation gap:** actual pinned 102.0 JSON does not expose `SegmentFamily`, and a generated retention remux dropped it. Family-linked files are outside the supported subset; current preflight cannot guarantee their rejection. Closing that detection gap is part of the next bounded acceptance task.

Restart cleanup is deliberately conservative: if a recovered candidate still exists, worker termination cannot be proved and cleanup is unavailable. The source can be processed again as a fresh operation; old candidates are never automatically published or deleted.

Public release readiness is blocked by the unresolved licensing/source-distribution review and any unmet platform/safety acceptance above. The next bounded task after this implementation is to close the recorded Phase 0/1 acceptance gaps, not to begin batch processing or MP4.
