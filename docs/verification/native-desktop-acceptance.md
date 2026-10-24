# Native desktop acceptance — Phase 0/1

This record covers the native acceptance attempt for the existing review artifact, not new product functionality.
**The retry operated the packaged native GUI through Computer Use UIA and keyboard input, including actual remux/publication. Native visual/scaling and target-desktop acceptance remain incomplete. No application defect has been demonstrated.**
The earlier pre-JavaScript initialization failure is historical; its cause remains unknown. Current capture and file-picker automation limitations prevent full acceptance. No fallback UI automation was used.

## Delivered candidate owner-operated continuation

The [delivered-candidate smoke receipt](release-candidate-native-smoke.json) separately records source `b288be1d185c62157e7d1bd1249f2f925ffe3093`, tree `6bbd076bd2853b6b6183e406f481c8174b67a6e6`, Windows ZIP **535154654 bytes**, SHA256 `f406162e52c9de3a44c6c18d0eae620fce2a1d710694e614eda6847fe51562a3`, from [run 40027855578, attempt 1](https://github.com/Nexusov/TrackTrim/actions/runs/40027855578). On Windows 10 22H2 x64 `19045.6456`, native MKV/MP4 smoke and EN → RU → EN passed with recorded owner assistance. Independent checks cover complete retained MKV payloads/timestamps/chapters/tags/attachment and 241 MP4 packets, rational media timing, chapters and untimed artwork. All three sources remained unchanged; six normally completed outputs were preserved. The tested GUI closed **idle**, exit 0; the older application instance remained open.

During that smoke, W13 Cancel and both W14 single-close attempts **did not send the required input** before their workers exited. W15 retained a live verifier, which exited before the close guard; no Close was sent. W14 repeated close was blocked/not run. These are **not established**, not application failures or successful active cancellation/shutdown. Visual/scaling, declared Windows 11/Ubuntu Desktop targets and real warnings remain open. The Phase 0/1 records below retain their original binaries and results, including their separately established W14 single close.

**One owner-operated W13 was completed on 2026-10-24; no retry was performed.** The same extraction and all 628 inventoried files were checked before preparation. Computer Use prepared Keep **0/2/3**, Remove **1** with the existing C1GiB (**1079470784 bytes**, SHA256 `0565b34f5fc0965225bf45b8d37e5cb007fe20611b77f0f66c175c970e4307f9`) and a fresh `manual-W13-01.trimmed.mkv`. The [unchanged read-only observer](../../build/observe-native-workers.ps1) retained the new GUI's exact path/start identity before the owner physically used Run/Cancel. An idle ScreenReplay sample first established readable host UTC and distinguishable button transitions; the recorder has no click marker. It does not provide an exact input timestamp.

The recording shows **Copying retained streams…**, Cancel's pressed/released appearance, then **Operation cancelled. The original is preserved.** The retained remux ran **01:26:52.2757779–01:26:55.0947517Z**, exit **−1**; eight same-handle liveness reads end at **01:26:55.0102679–55.0103170Z**. The displayed Cancel transition is conservatively bracketed by **01:26:54.937–01:26:55.185Z**, already including worker exit. Observed UTC refresh was about 60–130 ms, with additional unmeasured display/composition lag; neither frame rate nor mouse-down appearance is the command's exact activation time. **Strict active-remux input ordering remains not established**, rather than passed or an application defect. `OperatorInputTimestampsRecorded=false` remains explicit.

**Functional cancellation and cleanup passed:** subsequent native UIA read Cancelled with Review enabled and Run/Cancel disabled; the same GUI remained responsive. The complete source hash is unchanged, the fresh final output is absent, and the exact job `f56d7cb69a7546afb2bed2a43a595902` candidate/marker directory and journal are absent. Ownership/inspection storage is empty and no matching packaged media worker remains. An independent read-only audit also rehashed all three sources, the original delivered archive and all six previously published outputs unchanged. The observer stopped through its own marker after retained exits; it recorded one unretained short-child discovery race, so a complete process trace is not claimed. Both GUI instances were preserved. No external termination, larger fixture, production change, package rebuild or second attempt was used.

The receipt's `ownerOperatedW13` section binds the original private recording's SHA256, selected retained-handle observations, source/artifact identities and local receipt hashes. Its [six cropped recording frames](images/delivered-candidate-w13-owner-controls.png) show only UTC and application status/buttons; private desktop pixels and raw recordings remain local. These are owner-recorded functional evidence, not a successful Computer Use capture or layout/scaling acceptance. Physical Escape interruptions stopped automation; the final explicit continuation only observed the completed attempt. W14/W15 and warning cases were not repeated. A future repeated-close input must still target the same observed, still-existing test window during shutdown, never blind repeated Alt+F4.

## Artifact and supporting receipts

| Item | Recorded value |
| --- | --- |
| Attempt dates | 2026-10-18 and 2026-10-19 UTC |
| Repository source baseline | `e16050e7de92ab8ee4ac8f86094b41b2e2583cec` |
| Documentation/configuration checkouts | Initial launch: `fc7e4df`; ordinary-file cases: `535f9eb89e040496a4f8b7780f1fc5e636f9ce14`; earlier native 1 GiB Cancel/close attempts: `e846212c2d6e9aa59280c5f594698adb30426b88`; first owner-assisted Cancel and keyboard preparation: `a44baafff3bc493477f37493700973a7efd3d6f2`; keyboard execution: `cd55890b1a97a6b8a1fd34a0fc71c4ab9afb99b8`; H264/AAC and accepted folder: `df1ce2545b0987bc97f6395a1849d950cef5e02d`. All retain the archive's production source. |
| Windows archive | `TrackTrim-win-x64.zip`, **112,923,570 bytes** |
| Windows archive SHA256 | `d621ddc50773b20ba5d0f5109e904046765632796d8c256852b878221e091f18` |
| Assembly ProductVersion | `1.0.0+e16050e7de92ab8ee4ac8f86094b41b2e2583cec` |
| Bundle | SDK 10.0.402 publication; self-contained runtime 10.0.13; MKVToolNix 102.0 |
| Fresh Windows supporting checks | **passed**, repeated 2026-10-19: archive identity, 443 manifest payloads, bundled-tool checks, actual CLI inspection/remux/verification/publication; source SHA256 unchanged |
| Native GUI acceptance | **blocked** overall; functional cases below passed on actual Windows 10 using UIA/keyboard and owner-assisted file opening; no native screenshot was obtained |
| Confirmed application defects from this attempt | None; capture/automation errors below do not establish application defects |

The [CI and preservation receipts](../PLAN.md) identify their own revisions and remain separate evidence. A later documentation-only CI build cannot turn these blocked native cases into passes.

## Available environment and runtime limitations

- Windows 10 Home Single Language (`CoreSingleLanguage`) 22H2 x64, build **19045.6456**.
- Interactive session **1**, Explorer and DWM present; display **1920×1080 at 60 Hz**.
- Registry `AppliedDPI=96` is configured; effective application scaling was **not observed**. No 150%/200% check ran.
- System .NET runtimes include 3.1/5/6/8/9 and 10.0.10; no system SDK was found, but workspace SDK 10.0.402 exists. This is not a clean target machine.
- No MKVToolNix/FFmpeg/ffprobe commands were found in PATH. Bundled executables were used for supporting CLI checks.
- Windows 11 target unavailable. WSL contains only stopped `docker-desktop`; the Docker Linux engine pipe, Ubuntu Desktop session and usable local Linux builder are unavailable.

The earlier initialization attempt stopped with the exact error:

```text
failed to write kernel assets: Системе не удается найти указанный путь. (os error 3)
```

### Earlier 2026-10-19 diagnosis and bounded recovery

The installed Computer Use skill `26.1013.40934` and its documented `node_repl` tools were available. A fresh minimal `@oai/sky` initialization failed before JavaScript executed. The documented `js_reset({})` returned `js kernel reset`; one initialization after that concrete recovery action produced the identical error above. No further unchanged retry was made in that attempt.

Read-only diagnostics of the Codex app log found `node_repl status=ready` at **11:57:39.033Z** and **11:57:45.177Z**, followed by OS error 3 at **11:57:45.484Z**. A bounded search of relevant technical WARN/ERROR records provided no failing asset path or additional cause. Private conversation/feedback bodies and raw logs are excluded from this repository. MCP discovery/readiness worked, while kernel asset creation was blocked. Import, app enumeration, application approval and desktop interaction were not reached in that earlier attempt. Unicode paths, permissions and desktop locking are not established causes.

Finishing active chats and restarting the Codex host was the proposed general [official recovery step](https://learn.chatgpt.com/docs/reference/troubleshooting), not a proven fix for this OS error. The user-requested retry below initialized successfully; the cause of the earlier failure and its resolution is unknown. No Codex-managed assets, plugin caches or security settings were modified. [Computer Use setup and desktop requirements](https://learn.chatgpt.com/docs/computer-use) remain separate from the historical pre-JavaScript failure.

### Current 2026-10-19 native retry

On the user's explicit retry request, minimal `@oai/sky` initialization and app/window enumeration succeeded. The unchanged archive was rehashed at **12:43:55Z**; the fresh extraction's `data` directory was empty before launch. Supported `sky.launch_app` started the exact prepared application as **PID 13440**, session **1**, at **12:44:03.590Z**, returning native window **2690126**. The user explicitly confirmed that the window was visible. Read-only process observation reported `Responding=true` and no owned media workers.

Native UIA-only observation (`include_screenshot:false, include_text:true`) showed the initial English window, **Open MKV…** and **Ready**. The application created portable `settings.json` with `Language=en`. App-only first-launch UIA evidence is retained locally at `work/native-acceptance-20261019/native-uia-first-launch.json`; unrelated app listings and private file-picker contents are not published.

Image capture first failed with `FrameArrived timed out: timed out waiting on channel`. Refreshing window selection and retrying once produced `window capture timed out: timed out waiting on channel`; later captures after confirmed GUI interaction and after the complete GUI restart produced the same timeout. No native screenshot was obtained. Bounded technical-log inspection around **12:44–12:46Z** established no cause or failing path. Pixel appearance, clipping and effective scaling remain unverified.

The initial element-index click on **Open MKV…** (then index 20) failed with `coordinate input geometry is unavailable`. Supported `sky.set_value` on **OutputTextBox** (then index 22) did work, setting a unique synthetic destination containing spaces and Cyrillic; this created no media output. **Shift+Tab**, then **Return**, opened the native **Select an MKV file** picker. **Escape** closed it and preserved the entered output. At this point there was no source, selection or approved plan. A later user-requested **Tab** observation confirmed focus moving from Open to Output. These are observed functional parts, not complete W03/W04/W11 passes.

A second keyboard-opened picker was observed. Its UIA fields advertised settable values, but `sky.set_value` reported unavailable cached elements or non-settable controls; keyboard navigation did not confirm filename focus and `list_windows` returned no separate picker. These are plugin limitations. At **13:06Z**, the source was still empty; this is a historical observation, not the final result. A later parent-combo value action filled O's full path, and the owner pressed **Open**. At **13:27:28Z**, native UIA confirmed O's real tracks. Subsequent source selection was also owner-assisted; in-application actions below were performed with supported Computer Use input. No fallback UI automation was used.

### Observed functional cases after source selection

All rows use the archive/source above on Windows 10 session 1, Russian OS dialogs and configured DPI 96; effective scale and pixel appearance remain unobserved. UTC intervals identify locally retained, app-only UIA events (`work/native-acceptance-20261019/loaded-source-native-events.jsonl`). These are native GUI interactions, separate from the independent read-only media probes below and from headless/CI tests.

| Case / UTC on 2026-10-19 | Actions and observed native result | File/process evidence and limitation |
| --- | --- | --- |
| W02 / 13:27:28 | O selected through the real picker with owner assistance; four actual track rows, locked video, audio default/commentary and forced SRT, one attachment and chapters/tags appeared. | Default all-Keep was corroborated by W09's 4-retained plan. No pixel-layout evidence. |
| W09 / 13:27–13:34 | All Keep; fresh destination; Review then Run. English **No tracks removed. A new file is unnecessary; no tool was run.** | No destination, candidate or journal; source hash unchanged. No remux was observed. The first observer's action-marker date parsing failed, so it is not a precise liveness/timing receipt; no active-cancellation claim is made. |
| W04/W05 / 13:35–13:40 | Space toggled audio 1 and SRT off; Review retained IDs 0/2. RU then EN preserved exact path, selections, approved plan, enabled Run and the no-replacement-default explanation. | English and Russian UIA states retained. Output edit invalidated the previous plan. Folder acceptance was not reached in this earlier attempt; the owner-assisted completion is recorded below. |
| W03 / 13:40–13:42 | From the valid approved plan, open/Escape source picker, then open/Escape output-folder picker. | Both preserved source, Keep 0/2, destination and runnable plan. App dialog titles were English; system controls were Russian. Setting the folder field failed with unavailable cached-element error; Cancel worked. |
| W07 O / 13:42–13:43 | Keyboard **Create new MKV**; actual **Copying retained streams…**/progress; then **New file created: …** and 192,470-byte reduction. | Output 199,994 bytes; original 392,464 bytes unchanged. Independent actual payload/probe receipts below. H subsequently completed as recorded below; visual progress remains open. |
| W08 / 13:43–13:44 | Enter existing sentinel, Review, Run. English destination-exists error; switching RU translated the same actionable error. | Sentinel/source hashes unchanged; no owned result directory/journal. No overwrite occurred. |
| W10 / 13:44–13:46 | Both audio off, SRT on; unconfirmed Review blocked with RU confirmation error. Explicit Space on confirmation, Review IDs 0/3, Run; **Проверка временного результата…**, then Russian completion. | Output 7,775 bytes; zero audio, retained video/SRT/extras and source hash corroborated below. Cleanup evidence is a post-success filesystem snapshot, not a captured transient W10 job UUID. |
| W06 / 13:47 | Close completed idle RU window with Alt+F4; old PID 13440 exited. Restart same portable copy as PID 25792 at 13:47:51.257410Z; RU **Готово** observed at 13:47:58.785Z. | `settings.json` has version 1/language `ru`; old window absence confirmed at 13:47:35.465Z. The immediate post-close enumeration was stale and was not used as exit proof. |
| W14 attempts / 13:53 and 13:55 | CLI-accepted C256 inspected natively, changed plan IDs 0/2/3 reviewed. Both runs completed before the next action could find a live remux; **no close input was sent**. | Retained workers 30676 (13:53:50.8711556–13:53:53.7320792Z) and 27160 (13:55:35.3728066–13:55:38.4046541Z) exited 0 and published separate outputs. **Not run: active close stage not exercised.** Published outputs are preserved. |
| W13 attempt / 14:09–14:12 | Owner opened accepted C1GiB; Russian native inspector and reviewed Keep 0/2/3 observed. Run, Shift+Tab to Source, Shift+Tab to Cancel reached the actual button. At the prospective Space action, no remux remained alive, so **no Cancel input was sent**. | Retained worker 2844 lived 14:11:45.0788446–14:11:53.3001614Z (8.2213168 s), exit 0. The action gate ran at 14:12:04.211Z. Ordinary completion published `W13 отмена 1GiB.mkv`; preserve that output. **Not run: active Cancel stage missed.** |
| W14 single close / 14:13–14:14 | Same C1GiB and Russian GUI, fresh destination, reviewed Keep 0/2/3; native Run then one Computer Use Alt+F4 after retained-handle liveness confirmation. | **Passed: single active-remux close.** Actual worker exit preceded GUI exit, source hash matched, no final output or candidate/journal remained. Exact receipt below. Repeated close and visible stopping text were not exercised. |

Native keyboard traversal reached the language, Review/Run, source/output, folder and audio/subtitle/confirmation controls; automatic scrolling moved the UIA scrollbar. Visible focus, wrapping, resizing and clipping are still unverified. Immediate UIA snapshots sometimes preceded asynchronous focus/localization updates; settled observations were used before further input. This did not demonstrate an application defect.

The successful W14 action used the unchanged e160 archive on the same Windows 10 session/display context above. A read-only observer identified the app-local `mkvmerge --gui-mode --output <owned candidate>` child of GUI PID 25792, retained its OS handle and start time, and confirmed `HasExited=false` before native input. It never drove GUI or killed processes. The native Sky input performed the close; process/filesystem checks only observed the result.

| W14 single-close receipt, all UTC 2026-10-19 | Value |
| --- | --- |
| Worker identity | PID 4780; start `14:13:32.7640428Z`; retained key `4780:639280160127640420` |
| Same handle confirmed alive | `14:13:37.6046439Z` |
| Actual native Alt+F4 start/end | `14:13:37.611Z` / `14:13:37.729Z` |
| Same worker exit | `14:13:37.8167765Z`, exit **-1** |
| GUI exit | `14:13:38.0386043Z`, exit **0**; subsequent native enumeration returned no window |
| Publication/cleanup | `W14 закрытие 1GiB.mkv` absent; adjacent `.tracktrim-817f3821564d48c29128e5d955d72bba` absent; `settings.json` is the only file under app-local `data` (empty work roots remain) |
| C1GiB original SHA256 after close | `0565b34f5fc0965225bf45b8d37e5cb007fe20611b77f0f66c175c970e4307f9`, unchanged |
| Limits | No second close during the short shutdown interval, no captured stopping text/pixels, no verification-close; those are **not run**, not inferred passes. |

The observer was stopped and handles disposed after GUI exit. The [sanitized W14 evidence extract](native-w14-evidence.json) publishes the actual native-action record, seven correlated observer events and post-action receipt, with raw-source byte counts/hashes, artifact/fixture identities and disclosed path redactions/omissions. It was derived from task-local `W14-close-1GiB-01-native-action.json`, `workers-native-04.jsonl` and `W14-close-1GiB-receipt.json`; the successful close was not repeated for this extract. Its separately timestamped current OS inventory is supporting context, not a reconstructed historical observation. W13's missed action has its own receipt and is not conflated with W14.

### Bounded W13 native Cancel attempts

At **18:25:20Z on 2026-10-19**, the same archive and accepted C1GiB fixture were rehashed; both identities above matched. Computer Use launched the same portable copy as **GUI PID 15992**, start **18:25:47.1402183Z**, in the same Windows 10/session/display context. Russian settings persisted. A fresh destination under `results Кириллица/w13-a711db73/` was reserved; previous published outputs were preserved.

The installed `26.1013.40934` API exposes one key/chord per `press_key`, indexed `click`, and only advertised secondary actions; no batch-key API or generic Invoke is documented. Its guidance requires one state-derived action followed by refresh. An idle Open-button click at **18:26:03.309Z** again failed with `coordinate input geometry is unavailable`. Keyboard input opened the real MKV picker, but setting its advertised filename combo failed with `Cannot set a value for an element that is not settable`; native enumeration exposed no separately targetable picker. No screenshot timeout was retried and no alternative automation was introduced.

The owner subsequently completed C1GiB opening. Computer Use prepared the fresh output, removed audio 1, observed the approved Keep **0/2/3** plan at **18:46:30.088Z** and focused Run at **18:46:43.962Z**. The read-only observer was armed at **18:47:06.7684662Z** before requesting the owner's short native Run/Cancel actions; the slow 20-second tool route was not repeated. Syntax, recorded-remux/probe classification and a three-second idle identity check remain supporting checks, separate from the actual attempt below.

| First W13 owner-assisted attempt / UTC 2026-10-19 | Actual observation |
| --- | --- |
| Native input evidence | Owner reported **Run и Cancel нажаты во время копирования**. Exact input timestamp and hardware method were not independently recorded. |
| Correlated remux | App-local `mkvmerge --gui-mode`, parent GUI 15992, retained PID **30236**, start **18:48:10.5303728Z**, exit **0** at **18:48:18.3115781Z**. Thirty-three retained-handle liveness reads; last read **18:48:18.1455150–18.1455818Z**. |
| Subsequent verification | Candidate-identification `mkvmerge -J`, retained PID **34684**, start **18:48:18.6539447Z**, exit **-1** at **18:48:19.6405468Z**. One other short-lived child could not be retained; a complete verification-child trace is not claimed. |
| Native result | UIA at **18:48:38.897Z**: **Операция отменена. Оригинал сохранён.** Later native output editing/review remained responsive. |
| Source/publication/cleanup | Full source hash matched at **18:49:35.9302566Z**; final output, candidate and owned `.tracktrim-ef53ead314514d82be04dc1b0f3af0ba` directory absent. App-local data files: only `settings.json`; no remaining media children at the receipt read. |
| Acceptance boundary | Native Cancelled and cleanup after a completed remux are observed. **Active-remux Cancel remains not established**, because the remux exited 0 before the verifier exited -1 and the input time is unknown. This is not an observed cancellation failure. |

The [sanitized W13 extract](native-w13-evidence.json) now includes both attempts, the completed observer log's identity, selected raw retained-worker events, filesystem receipts and native approved-plan/Cancelled projections. Native UIA remains an explicitly bounded prefix snapshot. Original timestamp strings, omitted events and path redactions are identified; no operator-input timestamp or complete child-process trace is invented.

A separate approved output under `results Кириллица/w13-keyboard-99bbc614/` was prepared for one timing change: owner-operated keyboard Run/Cancel instead of the longer tool route. The supplied instruction assumed two reverse-Tab steps to Cancel. The owner's actual focus report differed; that count is not a dependable instruction.

| Keyboard W13 attempt / UTC 2026-10-19 | Actual observation |
| --- | --- |
| Native input evidence | Owner reported successful cancellation, with the second Shift+Tab reaching **Исходный файл** and the third reaching **Отменить**; the owner expressed uncertainty about the sequence. Exact key times and intermediate focus were not independently captured. This is not a universal three-step route. |
| Correlated remux | Same app-local backend/GUI parent, retained PID **27852**, start **19:14:08.1754572Z**, exit **0** at **19:14:14.9074127Z**. Thirty-three retained-handle liveness reads; last read **19:14:14.8139545–14.8139926Z**. |
| Subsequent verification | Retained candidate-identification PID **36056**, start **19:14:15.2465606Z**, exit **0** at **19:14:17.3540523Z**. Short-lived children 31708 and 2576 could not be retained; neither their operation nor cancellation is inferred. |
| Native result/responsiveness | UIA at **19:16:08.832Z**: **Операция отменена. Оригинал сохранён.** A later native Tab at **19:18:06.751Z** focused Open while preserving that status. Visible focus/pixels remain unverified. |
| Source/publication/cleanup | Full source hash matched at **19:17:39.7648911Z**. No final output, candidate or owned `.tracktrim-ab2c9bb8d49a40a7b615a080b40b2e4f` directory; result directory empty. Only `settings.json` remained under app-local data; no media children at receipt read. |
| Acceptance boundary | Native Cancelled/cleanup is observed, but retained remux and candidate-identification workers both exited 0. **Active-remux Cancel remains not established**, rather than failed. No input/worker timing guarantee follows from the reported Tab count. |

The observer stopped through its own marker at **19:16:51.5806007Z** (`OwnerStopMarker`, GUI still alive), and its hosting session completed with exit 0, disposing retained handles. It is no longer armed. The bounded attempts ended without fixture enlargement, worker suspension/throttling, external kill or production delay. No further remux was started and no application defect was demonstrated.

### Independent output evidence

Outputs are under the fresh kit directory `results Кириллица/gui-1019-dbebe4/`; paths contain spaces/Cyrillic and W07 also contains `; &`. Read-only bundled `mkvmerge -J` and `mkvextract` ran after the native GUI operation; they did not create the application result.

| Output | Verified evidence |
| --- | --- |
| `W07 результат ; &.mkv` | SHA256 `c2fa88e9debea01c3bc8416d848c52108fae3f7baaed9684155d089cbfb7039c`; source IDs 0/2 → output 0/1, codecs/UIDs/known flags match; retained audio default=false/commentary=true. All 20 VP8 frame payloads match (SHA256 `692eefd4e16a948435100b6fbe277807393cfbcb14f6b123305f7f71cfcb52cf`); all 96,000 PCM sample frames match (`bb351ddfe75c071063115e43110eac0b35ee0bf6a93c901fc56d6e711fe92657`). One attachment and two chapter entries retained. |
| `W10 no audio.mkv` | SHA256 `5ab5340c36a9c801b0028d9619cf5562b08815b49644cc1fb0aaefbb08c76cc2`; source IDs 0/3 → output 0/1, no audio; subtitle UID/flags, rus/ru and Captions preserved. Actual SRT extraction is 69 identical bytes, SHA256 `1dde3885b740ce1c19ff31e13cba5e64c9a2c26a90ba7ebd1170277854876eac`, including BOM/line endings. |

Original O and the collision sentinel still match their full hashes below. After success, `settings.json` was the only file under app-local `data`; no journals/private result directories remained, while empty work roots may remain. W07's observed job `ce60f8b1d96c4521bea20b0c2662ea38` candidate/record were absent. Independent W07/W10 receipts are retained locally under `work/native-acceptance-20261019/{w07,w10}-independent-verification/`; generated media, raw logs, binaries and private picker listings are excluded from Git.

Use report outcomes **passed**, **failed**, **blocked**, or **not run**. A native pass requires an observed GUI action and its evidence.
Use **failed** only for observed incorrect application behavior; environment failure is **blocked**. An unreached transient stage is **not run**.

### H264/AAC workflow and accepted output folder

On **2026-10-19**, checkout `df1ce25` reused the unchanged e160 ZIP/hash above. A new extraction under `outputs/TrackTrim native ordinary Кириллица ordinary-20261019-224835-9e7bf8/` verified all **443** payloads and empty app-local data before native launch. GUI PID **30888** started at **19:48:56.0958881Z**, on the same Windows 10 build/session/display described above. Source H matched its original FFmpeg 7.1.1 provenance: **20,357 bytes**, SHA256 `b0b5fc3c0e290fb435be3cc09a1952ccfc343d93c2d6d13e9e8332e55b791000`. No production code, fixture or tooling pin changed.

| ID / actual native observation, all times UTC | Result |
| --- | --- |
| W04 — **passed, owner-assisted native functional** | Computer Use opened the actual English-titled folder dialog with Russian system controls. UIA became intermittently null; address `set_value` failed with `element 266 is not available in cached app state for TrackTrim.exe`. The owner reported accepting the prepared folder. At **20:15:35.831Z**, native UIA showed the expected derived `results Кириллица/выбранная папка с пробелами/source h264 aac.trimmed.mkv`. No application output-textbox edit was made. Exact manual action times/order were not recorded; the subsequent reviewed destination corroborates acceptance. This is separate from earlier picker-cancellation evidence. |
| W07 H — **passed, native functional** | Owner-assisted source opening produced real video 0, German AAC 1, French AAC 2 and SRT 3. Computer Use removed AAC 1; Review at **20:16:25.396Z** confirmed Keep **0/2/3**, no replacement default audio and the fresh folder-derived destination. Russian switching preserved that plan. Native Space on Run at **20:17:26.923–27.007Z** produced **Копирование выбранных потоков…**, progress value 0 at **27.466Z**, then **Создан новый файл…** at **20:17:33.385Z** with **4,243-byte** reduction. No intermediate progress trajectory or verification interval was captured. |

The published result is **16,114 bytes**, SHA256 `ac3b9fa1769c52c90f8bd7779e974ddfb2f16d3bf37b547c924e6feef5b3b8c9`. Read-only bundled 102.0 probes/extraction independently confirmed source IDs **0/2/3 → output 0/1/2**, UIDs **1/3/4**, unchanged codec/private data, names, legacy language tags, effective flags and container title. No retained audio acquired a default flag. XML retains the three original track tags and removes only deleted UID 2. The legitimate IETF additions (`und/fr/fr`), AAC `default_duration=21333333`, track renumbering and container bookkeeping are recorded explicitly.

Complete retained payloads match: **1,437-byte H264**, **8,803-byte AAC** and **85-byte SRT**. All **50 video / 95 AAC / 1 subtitle** actual timestamps were compared; maximum differences were **0 / 0.666669 / 0 ms**. Known video/SRT end boundaries match. The AAC source's duplicate-last-frame synthetic boundary becomes last-frame + 1024/48000 seconds, within the existing narrow AAC-LC policy; the container duration header changes **2.021 → 2.026 s** and is not used as frame-integrity proof.

The full original hash remained unchanged. At **20:18:11.3388085Z**, only the intended output existed in the result tree, no `.tracktrim-…` directory or journal remained, app-local files contained only Russian `settings.json`, and no media child remained; empty `inspection`/`ownership` roots remained. This is a post-success snapshot, not a captured job UUID, deletion timeline or worker-handle trace. The [sanitized W04/W07 evidence](native-w07-h-evidence.json) binds selected actual UIA/input events, raw-source hashes and independent receipts; private picker listings, media, binaries and raw logs are excluded.

No screenshot was retried: the installed capture method/version and prior timeout blocker were unchanged. EN/RU text and keyboard reachability are functional UIA evidence; appearance, visible focus, wrapping, resizing, scrolling and effective scale remain unverified. Configured DPI **96** does not establish 100% effective scaling; no 150%/200% or display-setting change occurred. W13 was not retried, W14 was not repeated, and no application defect was demonstrated.

## Windows handoff inputs and initial states

All Windows matrix rows use the exact archive/hash above and this workspace-relative kit:

```text
outputs/TrackTrim native acceptance Кириллица e16050e/
  fresh app 20261019/TrackTrim-win-x64/TrackTrim.exe
  test data Кириллица/{source ordinary.mkv,source cancellation.mkv,source h264 aac.mkv}
  results Кириллица/collision existing.mkv
```

The 2026-10-19 extraction is isolated from the older `app windows` copy. Its **443** payload hashes match the manifest, ProductVersion identifies `e16050e7de92ab8ee4ac8f86094b41b2e2583cec`, and `data` was empty before the current GUI launch; that launch subsequently created English settings. All three fixture hashes and the existing collision sentinel were rechecked unchanged. The separate supporting CLI relocation used a different extraction; it did not populate this GUI copy. Later branch changes are documentation/repository policy only and do not change this artifact's source revision.

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
The rejected generated file is outside the manual kit. The existing 60,000-frame C input does not guarantee an observable active interval.

A separate 1,000,000-frame input was also **excluded**: 90,813,320 bytes, SHA256 `af7602a38dbc81118da8de84553844737102faefdb101469d4e161f8f862a3cf`. Packaged CLI inspection hit the same 100,000-header bound; no remux/publication occurred and its source hash remained unchanged. The rejected source was renamed as rejected in its own scratch directory. It is not approved for native GUI cancellation checks.

The accepted **C256** alternative adds one original deterministic 256 MiB `text/plain` attachment to C without adding frames. Its source is `work/native-cancellation-attachment-20261019/test data Кириллица/source cancellation attachment 256MiB.mkv` (workspace-relative), 274,164,416 bytes, SHA256 `4dfca07a7ed0980c6199c3a139df37161a7d8c3b5f2871b1345baee5f48cf1aa`. Pinned 102.0 created it and the unchanged packaged production CLI completed inspection/remux/verification/publication with the source hash unchanged. Preparation stayed below a 900 MB scratch limit; raw attachment generation used ordinary writes, not sparse allocation. This establishes a supported input, not native cancellation. Its observed native remux lifetimes above were still too short for the next Computer Use action. Fixture provenance/CLI receipts remain local beside that source; media is excluded from Git.

The subsequent bounded preparation retained two larger accepted variants under `work/native-cancellation-large-20261019/`: **512MiB** (`542,599,872` bytes, SHA256 `a9c8d794e4b20c48e671de2d51f4babda5c4d03b4cbf141824fdc6c51b50cb8e`) and **C1GiB** (`1,079,470,784` bytes, SHA256 `0565b34f5fc0965225bf45b8d37e5cb007fe20611b77f0f66c175c970e4307f9`). Each source is in `<variant>/test data Кириллица/source cancellation attachment <variant>.mkv`, with variant `512MiB` or `1GiB`. Both passed the same packaged production CLI workflow with unchanged source hashes; retained real remux handles measured 4.3221259 s and 7.1841039 s respectively. Combined owned task usage after preparation was 3,243,808,118 bytes within an 8 GiB limit; free space exceeded 12 GiB before each write. The 1 GiB attachment cap was reached; no duration guarantee or native-cancellation pass follows from these CLI measurements. These are original supported attachments, not production test switches or weakened inspection limits. C1GiB subsequently supplied the separately observed native W13/W14 attempts above.

Preserve inputs/sentinel; hash before and after each media case. Give every successful case a new destination under `results Кириллица`.
Resolve kit-relative paths to absolute paths before entering the output textbox or recording evidence.
**S0:** fresh writable extraction, `data` empty, no GUI launch. If CLI checking populated that copy, extract another copy; do not clear existing user data.
**S1:** O inspected; IDs 0/1/2/3 all Keep; video 0 locked; no approved plan. **S2:** O with Keep 0/2, Remove 1/3, a new writable destination and approved plan.
Native file-dialog system controls may follow OS language. App-provided titles/resources must follow app language; original media text and diagnostics stay original.

## Manual action matrix

Every row requires archive identity, fixture/initial state, concrete action, expected observation and the evidence described below. Expectations are not executed results.

| ID / current outcome | Initial state and native actions | Expected visible state / file or process evidence |
| --- | --- | --- |
| W01 — blocked | S0; launch EXE normally, without CLI arguments. | English first window; `OpenFile` **Open MKV…**, `ReviewPlan` **Review output plan**. Native launch/English/Ready observed through UIA; complete image and effective-scale evidence remain blocked. |
| W02 — passed, native UIA; owner-assisted Open | S0; **Open MKV… / Открыть MKV…** → real picker → O. | Observed four expected rows, titles/flags, supported extras and locked video. Default all-Keep corroborated by the no-change plan. Pixel-layout acceptance remains separate. |
| W03 — passed, native functional | S2; open and cancel source picker; open and cancel **Choose output folder… / Выбрать папку результата…**. | Both Escape cancellations preserved source, Keep, destination and runnable approved plan. Native keyboard input remained usable. App titles EN, system controls RU. |
| W04 — passed, native functional; owner-assisted folder acceptance | S1; remove audio 1/subtitle 3; choose output folder; review a separate destination. H additionally exercises the accepted folder with Keep 0/2/3. | Earlier O checks proved typed Cyrillic/metacharacter destination, 2-retained plan, no-default explanation and invalidation. Later the owner accepted the real folder dialog; the expected derived H destination appeared without textbox edits and produced a runnable plan/output. Plugin-only folder automation and pixels remain blocked. |
| W05 — passed, native functional | S2; switch **Русский**, then **English**. | Both settled UIA states show translated app labels/plan with Keep 0/2/path/approved plan/Run preserved. Original media titles/tags unchanged. Screenshots remain blocked. |
| W06 — passed, native functional | Choose Русский, complete the ordinary/zero-audio checks, close the idle completed window, restart same copy. | Russian restored in the restarted native window; portable `settings.json` has `ru`. English-first S0 was observed before this preference existed. |
| W07 — passed, native functional O and H; visual presentation blocked | S2; **Create new MKV / Создать новый MKV**. Repeat with H retaining one AAC and SRT in a separate destination. | O and H native remux/publication passed with independent retained payload/metadata/source-hash/cleanup evidence. H has EN/RU approved-plan text, Russian Copying and completion observations. Verification status was separately observed during W10. Intermediate H progress/verification stages and native pixel presentation are not inferred passes. |
| W08 — passed, native functional | Remove a track, enter the existing collision sentinel's full path in the output textbox, Review, Run. | Exact actionable destination-exists error observed in EN and RU; source/sentinel unchanged and no private output reservation remained. |
| W09 — passed, native functional EN | S1; all Keep, new destination, Review then Run. | English NoChange observed; no new output, candidate or journal; source unchanged. No remux was observed; sampling/marker limitation is recorded above. RU NoChange was not exercised natively. |
| W10 — passed, native functional RU | New destination, remove both audio, Review without confirmation; then check **Подтверждаю удаление всех аудиодорожек…**, Review, Run. | Unconfirmed plan blocked with exact RU error; explicit confirmation enabled a video/SRT plan and successful native remux. Actual retained SRT bytes and source hash match; zero audio confirmed by probe. EN confirmation was not exercised natively. |
| W11 — blocked | S1/S2; Tab/Shift+Tab, Space/Enter; picker open/cancel; resize toward minimum 640×480 logical units; scroll top/bottom. | Visible/recoverable keyboard focus; controls reachable, disabled video stays kept; long paths/Russian labels wrap; no clipped/overlapping actions or required horizontal scrolling. |
| W12 — blocked | S1/S2; real normal, 150% and 200% system scaling, each in English and Russian. | Record actual display setting/effective scale, monitor resolution and window size; native inspector and scrolled plan/result screenshots at all six combinations. Registry DPI/headless images are insufficient. |
| W13 — not run: active-remux Cancel not established | C/C1GiB; changed plan; while the matching remux worker is proven alive, press **Cancel / Отменить**. | Both owner-operated attempts reached native `Cancelled` and clean files after remux exited 0; exact input times are unknown. The fixed reverse-Tab count was unreliable; observer is now stopped. Remaining clause: correlate actual Cancel with retained live remux, same worker exit before cleanup, no publication and unchanged source. |
| W14 — passed: single close; not run: repeat/stopping display | Separate C1GiB run; close window while the remux worker is proven alive; separately repeat close during shutdown. | One native Alt+F4 passed with the retained-worker/exit/source/cleanup proof above. Rapid second close and visible stopping status were not captured; these remaining clauses are not accepted from the single-close result. |
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
Any replacement input must first pass packaged CLI inspection; the rejected 3,000,000-frame and 1,000,000-frame inputs cannot serve this purpose.
If termination is unconfirmed, retain temporary files and diagnostics; never force-delete or kill a stale PID to obtain a pass.
Cleanup failure must remain separately visible; no false clean/success outcome. An external Kill, CLI run or headless test cannot count as native Cancel/X.
Existing `WarningReviewCloseTests` use deterministic headless ports. There is no native warning harness or production warning switch in this baseline.

The development-only [Windows worker observer](../../build/observe-native-workers.ps1) automates this read-only process evidence. Run it in PowerShell 7 before native Run/Cancel, supplying the already observed GUI PID, exact portable executable/ProductVersion and its `tools` directory, plus fresh evidence/stop-marker paths:

```powershell
./build/observe-native-workers.ps1 -GuiProcessId <observed PID> -ExpectedApplicationPath '<portable executable>' -ExpectedProductVersion '1.0.0+e16050e7de92ab8ee4ac8f86094b41b2e2583cec' -ToolDirectory '<portable tools directory>' -EvidencePath '<fresh task-local JSONL>' -StopMarkerPath '<fresh task-local marker>' -MaximumSeconds 300
```

It retains the GUI/worker path/start identities, distinguishes private-candidate remux from probes, records remux `HasExited=false` read intervals, and records exit from the same handles. The configured liveness interval is 100 ms between discovery polls; actual timestamps reflect discovery/read latency. Evidence is bounded to 2 MiB/128 workers and handles are disposed on stop; creating the specified stop marker stops observation only. The script sends no GUI input, changes no media and records no operator-action timestamp. Idle validation and command-line classification are supporting checks, not a W13 pass. It is not copied into portable production packages.

For the remaining W13 check on the available Windows host, restart the exact portable copy above, select C1GiB, remove audio 1, choose a fresh result path and Review. With the read-only worker observer ready, Run and physically press **Отменить** while the same retained remux worker is confirmed alive. The demonstrated Computer Use keyboard route took about 20 seconds across observed actions, longer than this remux's 8.22 seconds; reaching the button after worker exit cannot pass W13. Capture the native action/time, **Cancelled** state, retained-worker exit, unchanged source hash and absence of final/candidate/journal. No larger fixture or production delay is required by this handoff. W14's rapid repeated-close and W15's active verification interval still need separate observed actions; do not infer either from the successful single close.

Navigate to the actual Cancel control and confirm focus before activation; do not prescribe two or three reverse-Tab steps as a fixed shortcut. Any future W13 attempt needs a dependable native input-time observation correlated with the retained worker. Repeating the same unmeasured sequence does not resolve this acceptance gap. The observer must be started again before such an attempt.

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

## Phase 2A limited native startup (separate binary)

The later [bounded native batch receipt](native-batch-evidence.json) covers the fixed `83f6116` portable GUI on the same Windows 10 host, including owner-assisted dialogs/start and independently verified A/B outputs. It does not change the historical scope below or the open visual, timing-sensitive cancellation, warning and target-desktop requirements.

On 2026-10-20 (local time), a new isolated extraction of code revision `78a5b170184d01f3e3a348e8bbc4a81ab933c533` ran on **Windows 10 Home Single Language 22H2 x64, build 19045.6456**. Its clean-build Windows ZIP is **113,185,284 bytes**, SHA256 `4031cf21f291585ffcf0ed2d3caa3f04f0f615a2cc447fa6833a565f15e1ad48`. The extraction path contains spaces/Cyrillic and started with empty app-local data. This is a new binary; all W01–W17 evidence above retains the historical e160 artifact and limits.

**Passed, limited native functional scope:** Computer Use `@oai/sky` launched the exact extracted executable, observed English first launch through UIA, navigated by native Tab/Right to **Several files / folder**, then Shift+Tab/Space on the observed Russian language control. Settled UIA exposed the Russian Batch instructions, limits, file list and correctly disabled empty-queue execution controls. Alt+F4 closed only this empty test window; subsequent native enumeration and a read-only process check found it gone. App-local settings contain `ru`; no ownership entries exist. The [sanitized receipt](phase-2a-batch-evidence.json) retains actual EN/RU observations and the asynchronous close observations without private workspace paths.

**Not run:** opening files/folders, approving/processing a native batch, active-job cancel/close and warning review on this binary. **Still blocked/unverified:** native capture, appearance, visible focus, resize/scroll usability, effective scale (including 150%/200%), Windows 11 and Ubuntu Desktop. No screenshot attempt or display-setting change was made. Existing picker limitations were not retried, no W13 timing attempt was made, and no old native result was relabeled. The new real two-file workflow was checked separately through Application integration in both platform jobs and through controlled/headless UI tests, as recorded in [PLAN](../PLAN.md).
