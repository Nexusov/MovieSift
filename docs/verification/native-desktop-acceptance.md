# Native desktop acceptance — Phase 0/1

This record covers the native acceptance attempt for the existing review artifact, not new product functionality.
**No desktop GUI was launched, no native screenshots were produced, and no application defect was demonstrated.**
Supported computer-use initialization failed before the JavaScript import could execute. No fallback UI automation was used.

## Artifact and supporting receipts

| Item | Recorded value |
| --- | --- |
| Attempt dates | 2026-10-18 and 2026-10-19 UTC |
| Repository source baseline | `e16050e7de92ab8ee4ac8f86094b41b2e2583cec` |
| Windows archive | `TrackTrim-win-x64.zip`, **112,923,570 bytes** |
| Windows archive SHA256 | `d621ddc50773b20ba5d0f5109e904046765632796d8c256852b878221e091f18` |
| Assembly ProductVersion | `1.0.0+e16050e7de92ab8ee4ac8f86094b41b2e2583cec` |
| Bundle | SDK 10.0.402 publication; self-contained runtime 10.0.13; MKVToolNix 102.0 |
| Fresh Windows supporting checks | **passed**, repeated 2026-10-19: archive identity, 443 manifest payloads, bundled-tool checks, actual CLI inspection/remux/verification/publication; source SHA256 unchanged |
| Native GUI acceptance | **blocked**: initialization error below; CLI/headless receipts do not establish native behavior |
| Confirmed application defects from this attempt | None; no GUI action reached the application |

The [CI and preservation receipts](../PLAN.md) identify their own revisions and remain separate evidence. A later documentation-only CI build cannot turn these blocked native cases into passes.

## Available environment and exact blocker

- Windows 10 Home Single Language (`CoreSingleLanguage`) 22H2 x64, build **19045.6456**.
- Interactive session **1**, Explorer and DWM present; display **1920×1080 at 60 Hz**.
- Registry `AppliedDPI=96` is configured; effective application scaling was **not observed**. No 150%/200% check ran.
- System .NET runtimes include 3.1/5/6/8/9 and 10.0.10; no system SDK was found, but workspace SDK 10.0.402 exists. This is not a clean target machine.
- No MKVToolNix/FFmpeg/ffprobe commands were found in PATH. Bundled executables were used for supporting CLI checks.
- Windows 11 target unavailable. WSL contains only stopped `docker-desktop`; the Docker Linux engine pipe, Ubuntu Desktop session and usable local Linux builder are unavailable.

Initialization stopped with the exact error:

```text
failed to write kernel assets: Системе не удается найти указанный путь. (os error 3)
```

### 2026-10-19 diagnosis and bounded recovery

The installed Computer Use skill `26.1013.40934` and its documented `node_repl` tools were available. A fresh minimal `@oai/sky` initialization failed before JavaScript executed. The documented `js_reset({})` returned `js kernel reset`; one initialization after that concrete recovery action produced the identical error above. No further unchanged retry was made.

Read-only diagnostics of the current Codex app log found `node_repl status=ready` at **11:57:39.033Z** and **11:57:45.177Z**, followed by OS error 3 at **11:57:45.484Z**. A bounded search of relevant technical WARN/ERROR records provided no failing asset path or additional cause. Private conversation/feedback bodies and raw logs are excluded from this repository. MCP discovery/readiness therefore worked, while kernel asset creation remained blocked. The import, app enumeration, application approval and desktop interaction were never reached. Unicode paths, permissions and desktop locking are not established causes.

The smallest owner recovery step is to finish active chats, restart the Codex host, then try the documented minimal initialization once. This is general [official troubleshooting guidance](https://learn.chatgpt.com/docs/reference/troubleshooting), not a proven fix for this OS error. If it persists, submit Feedback with the exact error, app/OS versions and reviewed/redacted relevant logs. No Codex-managed assets, plugin caches or security settings were modified. [Computer Use setup and desktop requirements](https://learn.chatgpt.com/docs/computer-use) remain separate from this pre-JavaScript failure.

No TrackTrim native window was opened and no native picker, screenshot, language, scaling or worker-cancellation action occurred on either attempt. W01–W14 remain **blocked**; W15–W17 remain **not run**. There is no observed application defect to correct.

Use report outcomes **passed**, **failed**, **blocked**, or **not run**. A native pass requires an observed GUI action and its evidence.
Use **failed** only for observed incorrect application behavior; environment failure is **blocked**. An unreached transient stage is **not run**.

## Windows handoff inputs and initial states

All Windows matrix rows use the exact archive/hash above and this workspace-relative kit:

```text
outputs/TrackTrim native acceptance Кириллица e16050e/
  fresh app 20261019/TrackTrim-win-x64/TrackTrim.exe
  test data Кириллица/{source ordinary.mkv,source cancellation.mkv,source h264 aac.mkv}
  results Кириллица/collision existing.mkv
```

The 2026-10-19 extraction is isolated from the older `app windows` copy. Its **443** payload hashes match the manifest, ProductVersion identifies `e16050e7de92ab8ee4ac8f86094b41b2e2583cec`, and `data` is empty. All three fixture hashes and the existing collision sentinel were rechecked unchanged. The separate supporting CLI relocation used a different extraction; it did not populate this fresh GUI copy. Later branch changes are documentation/repository policy only and do not change this artifact's source revision.

| Alias / input relative to the kit | SHA256 |
| --- | --- |
| O — `test data Кириллица/source ordinary.mkv` | `055c7f3ebdc3c579dd6d7740c78a1e8ee0e2525f23542b5c6af9a0869ce4a0f8` |
| C — `test data Кириллица/source cancellation.mkv` | `6cad069a1c51efd3d0cc9ee063bc293110938b3e3236b9868ffcfc94f3d4fde3` |
| H — `test data Кириллица/source h264 aac.mkv` | `b0b5fc3c0e290fb435be3cc09a1952ccfc343d93c2d6d13e9e8332e55b791000` |
| Collision sentinel — `results Кириллица/collision existing.mkv` | `ab52d4d4b9322d20ae34b7ce9873d3bf078d3df38b8b2bd1c611b840a7faa4e4` |

A longer cancellation input was prepared but **excluded**: 3,000,000 original VP8 keyframes, 272,813,320 bytes,
SHA256 `617c880dd67880b9ba342dc9e258ecdcdb6c204fca05d8ef3631ed2c236fc8bb`.
The packaged CLI rejected inspection with `UnsupportedFeature/SegmentStructure`: **The bounded Segment/Info element-header budget was exceeded.**
No remux/publication occurred; its source hash remained unchanged. This is a fail-closed supported-subset limit, not a demonstrated native GUI defect.
The rejected generated file is outside the manual kit. Use the existing 60,000-frame C input; it does not guarantee an observable active interval.

Preserve inputs/sentinel; hash before and after each media case. Give every successful case a new destination under `results Кириллица`.
Resolve kit-relative paths to absolute paths before entering the output textbox or recording evidence.
**S0:** fresh writable extraction, `data` empty, no GUI launch. If CLI checking populated that copy, extract another copy; do not clear existing user data.
**S1:** O inspected; IDs 0/1/2/3 all Keep; video 0 locked; no approved plan. **S2:** O with Keep 0/2, Remove 1/3, a new writable destination and approved plan.
Native file-dialog system controls may follow OS language. App-provided titles/resources must follow app language; original media text and diagnostics stay original.

## Manual action matrix

Every row requires archive identity, fixture/initial state, concrete action, expected observation and the evidence described below. Expectations are not executed results.

| ID / current outcome | Initial state and native actions | Expected visible state / file or process evidence |
| --- | --- | --- |
| W01 — blocked | S0; launch EXE normally, without CLI arguments. | English first window; `OpenFile` **Open MKV…**, `ReviewPlan` **Review output plan**. Capture complete native window and effective scale. |
| W02 — blocked | S0; **Open MKV… / Открыть MKV…** → real picker → O. | Four rows: VP8 video 0, PCM audio 1/2, SubRip/SRT 3; both audio titles `Matching audio`; audio 1 default, audio 2 commentary, subtitle 3 forced. All Keep; video **Always kept / Всегда сохраняется** cannot be removed. |
| W03 — blocked | S2; open and cancel source picker; open and cancel **Choose output folder… / Выбрать папку результата…**. | Each Cancel preserves source, Keep, destination and approved plan; focus returns to usable application controls. Record app-provided dialog title and OS control language separately. |
| W04 — blocked | S1; remove audio 1/subtitle 3; choose output folder; edit output to `results Кириллица/результат с пробелом ; &.mkv`; **Review output plan / Проверить план**. | Approved plan retains 2/removes 2, shows exact destination and `PlanNoDefaultAudio` (no replacement default). Editing selection/destination invalidates review and disables Run until reviewed again. |
| W05 — blocked | S2; switch **Русский**, then **English**. | App labels/plan translate, Keep 0/2/path/approved plan/Run availability remain. Original track titles/metadata stay unchanged. Capture both language states. |
| W06 — blocked | S2; choose Русский, wait for preference save, close idle window, restart same copy. | Russian restored; portable `settings.json` has `ru`. Record file and restarted window. Another S0 extraction still starts English. |
| W07 — blocked | S2; **Create new MKV / Создать новый MKV**. Repeat with H retaining one AAC and SRT in a separate destination. | Actual progress during `Remuxing`, then `Verifying`, then **New file created: … / Создан новый файл: …**. New output/probe match approved tracks; source hash unchanged; final path and size comparison truthful. |
| W08 — blocked | S1; remove a track, enter the existing collision sentinel's full path in the output textbox, Review, Run. | `ErrorDestinationExists`: **The destination already exists. Choose another output filename. / Файл результата уже существует. Выберите другое имя.** Sentinel/source hashes unchanged; no remux or output reservation. |
| W09 — blocked | S1; all Keep, new destination, Review then Run. | `NoChange`: **No tracks removed… / Дорожки не удаляются…**; no remux worker, candidate or new output. Inspection workers completed before this observation period. |
| W10 — blocked | S1; new destination, remove both audio, Review without confirmation; then check **I confirm removal of every audio track… / Подтверждаю удаление всех аудиодорожек…**, Review, Run. | First `ErrorZeroAudioConfirmationRequired`, no runnable plan. Then `PlanNoAudio`; output has video/SRT and no audio, preserves supported extras; source hash unchanged. |
| W11 — blocked | S1/S2; Tab/Shift+Tab, Space/Enter; picker open/cancel; resize toward minimum 640×480 logical units; scroll top/bottom. | Visible/recoverable keyboard focus; controls reachable, disabled video stays kept; long paths/Russian labels wrap; no clipped/overlapping actions or required horizontal scrolling. |
| W12 — blocked | S1/S2; real normal, 150% and 200% system scaling, each in English and Russian. | Record actual display setting/effective scale, monitor resolution and window size; native inspector and scrolled plan/result screenshots at all six combinations. Registry DPI/headless images are insufficient. |
| W13 — blocked | C; changed plan; while the matching remux worker is proven alive, press **Cancel / Отменить**. | `Cancelled`: original preserved; same retained worker exits before cleanup completes; no final output; exact owned candidate/journal removed or separate cleanup issue shown. Record timing/identity and source hash. |
| W14 — blocked | Separate C run; close window while that remux worker is proven alive; repeat close request during shutdown. | `CloseDuringWork` **Stopping the active operation safely before closing… / Безопасная остановка…**; await worker exit/cleanup, no racing second discard, no final output, source unchanged. |
| W15 — not run | Separate run; catch **Verifying the candidate… / Проверка временного результата…** and its active verification worker, then close. | Same exit/cleanup requirements as W14. If no active verification interval is reached, record **not run: stage not exercised**, never substitute a remux-close result. |
| W16 — not run | Obtain a representative real warning input, or an already available native development-only simulated harness; neither is currently available. | Verified warning candidate remains unpublished. **Publish with warnings / Сохранить с предупреждениями** publishes that candidate without a second remux; **Discard output / Удалить временный результат** awaits cleanup. Record whether evidence is real or simulated. |
| W17 — not run | Reach W16 warning review; press X; choose **Stay in review / Продолжить проверку**, then separately **Discard output and close / Удалить результат и закрыть**. | Focus initially Stay; remain preserves candidate; discard waits once despite repeated X; cleanup failure keeps window open. Current headless simulated tests do not establish this native case. |

## Evidence and cancellation identity

For each attempted row record UTC start/end, outcome/reason, OS/app/OS-dialog languages, effective scale, window size, exact paths and fixture hashes.
Save native screenshots or screen recording with IDs/language/scale in filenames; no native screenshots exist for the blocked attempt recorded here.
For successful GUI output, retain the visible final path, output hash, bundled `mkvmerge -J` read-only post-probe, source before/after hashes and app diagnostics/logs.

For W13–W15, use a read-only OS observer; it must not drive GUI or kill processes:

1. Record the application PID/start time/path. Use `Get-CimInstance Win32_Process` to identify its actual child, executable path, creation time and command line.
2. For remux require app-local `mkvmerge.exe` with the expected source and `.tracktrim-…/candidate.mkv`; `--version`/`-J` are not a remux. For verification identify its actual `mkvextract`/probe operation.
3. Before the operator clicks Cancel/X, retain the process object and OS handle; record `HasExited=false` at the action boundary. Keep that object, not a later PID lookup.

```powershell
# Set this to the observed matching child PID, not the application PID.
$observedWorkerId = <observed PID>
$observedWorker = [Diagnostics.Process]::GetProcessById($observedWorkerId)
$null = $observedWorker.SafeHandle
$observedWorker.StartTime.ToUniversalTime()
$observedWorker.HasExited # Must be false immediately before the native action.
# Operator performs the real native Cancel/X; the observer does not Kill().
$exitWait = [Diagnostics.Stopwatch]::StartNew()
$observedWorker.WaitForExit(30000) # Record false as failure/uncertainty, not success.
$exitWait.Stop()
$observedWorker.HasExited
if ($observedWorker.HasExited) {
    $observedWorker.ExitTime.ToUniversalTime()
}
$exitWait.Elapsed
$observedWorker.Dispose()
```

Record exit time/elapsed and inspect only the correlated job's candidate, marker, journal and final path. Never delete a published output.
Correlate the observer and native action in one recording; if the worker already exited or liveness at the request is uncertain, record **not run: active stage not exercised**.
Any replacement input must first pass packaged CLI inspection; the rejected 3,000,000-frame input cannot serve this purpose.
If termination is unconfirmed, retain temporary files and diagnostics; never force-delete or kill a stale PID to obtain a pass.
Cleanup failure must remain separately visible; no false clean/success outcome. An external Kill, CLI run or headless test cannot count as native Cancel/X.
Existing `WarningReviewCloseTests` use deterministic headless ports. There is no native warning harness or production warning switch in this baseline.

## Linux owner handoff and unavailable targets

Windows 11 and Ubuntu 24.04 Desktop/X11 or XWayland remain **blocked: target environment unavailable**.
No Linux archive is available for this native handoff. Its SHA256 is **unknown**, not inferred from a successful CI job; CI binary archives were not uploaded.
The available Docker-only WSL registration is not an Ubuntu Desktop or usable builder. Do not report native Linux acceptance from WSL/headless/container checks.

A separate Ubuntu **build host** needs Git, pinned SDK 10.0.402, PowerShell 7, Python 3.9+ and the native/archive prerequisites in [CONTRIBUTING](../../CONTRIBUTING.md) and [SUPPORTED_FORMATS](../SUPPORTED_FORMATS.md).
These are owner-executed reproduction commands, not commands executed in this native attempt:

```bash
git clone https://github.com/Nexusov/TrackTrim.git TrackTrim-build
cd TrackTrim-build
git checkout --detach e16050e7de92ab8ee4ac8f86094b41b2e2583cec
git rev-parse HEAD
pwsh ./build/acquire-tools.ps1 -RuntimeIdentifier linux-x64 -Destination ./tools
python3 ./build/generate-fixtures.py --tool-directory ./tools --destination ./artifacts/fixtures
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
pwsh ./build/package.ps1 -RuntimeIdentifier linux-x64 -FixturePath ./artifacts/fixtures/ordinary.mkv
pwsh ./build/verify-package.ps1 -ArchivePath ./artifacts/TrackTrim-linux-x64.tar.gz -FixturePath ./artifacts/fixtures/ordinary.mkv
sha256sum ./artifacts/TrackTrim-linux-x64.tar.gz
```

Record exact checkout, archive bytes/hash and package receipt **before** Linux native checks; preserve the archive and verify its hash after transfer.
Extract on a separate clean Ubuntu Desktop using X11/XWayland with documented OS libraries, without installing .NET/SDK/media tools; launch `TrackTrim-linux-x64/TrackTrim` directly.
Reuse this matrix with Linux paths/dialog observations. Copy the handoff fixtures unchanged or record actual regenerated hashes; do not apply the Windows archive hash to Linux.
Capture language, native dialogs, scaling, cancellation/close and worker identity there. Unreached stages remain not run; no Phase 2 work or release readiness is implied.
