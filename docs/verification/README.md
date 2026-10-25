# Verification history and current evidence

MovieSift keeps current requirements in [PLAN](../PLAN.md), the [release checklist](../release-checklist.md), [distribution requirements](release-distribution-closure.md) and [main protection](main-protection.md).

The records below predate the rename. Their original files, screenshots, version labels, archive names, hashes, observed outcomes and limitations are preserved **unchanged** at commit [`2ba8ba1d4a4507ff678ff0f31045c81e497451d0`](https://github.com/Nexusov/MovieSift/commit/2ba8ba1d4a4507ff678ff0f31045c81e497451d0). They have been removed from the current tree so obsolete names/interfaces are not presented as current documentation. Following a historical link intentionally opens the original record; it does not claim that its binary was rebuilt or renamed.

A successful historical automated job is not proof of a later renamed binary. Headless screenshots are actual offscreen renders of their identified revision, not native desktop or OS-scaling acceptance. New checks must identify their actual MovieSift commit and artifacts.

## Integrated baseline and handoff

- [PR #11](https://github.com/Nexusov/MovieSift/pull/11) merged the unified workspace and appearance changes; [PR #10](https://github.com/Nexusov/MovieSift/pull/10) integrated the release-candidate branch into main.
- [Main run 40148923120](https://github.com/Nexusov/MovieSift/actions/runs/40148923120) and [manual run 40150657001, attempt 1](https://github.com/Nexusov/MovieSift/actions/runs/40150657001) succeeded at the immutable baseline above. The latter delivered matching Windows/Linux binary/material sets. Their original names and hashes remain in the run/PR evidence; Actions artifact expiry does not change their identity.
- [Original development plan](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/PLAN.md) retains the full earlier chronology, commands, test counts and scope qualifications.

## Workspace and appearance

| Original record | Scope |
| --- | --- |
| [Unified workspace](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/unified-workspace-evidence.json) | Aggregate union, conservative grouping, file-local selections, explicit Review and real-media proof for the identified implementation. |
| [Soft workspace and corrections](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/soft-workspace-evidence.json) | Appearance/storage/contrast, structured Review and bounded UX corrections, with 45 original and 27 correction-pass headless renders. |
| [Original selected renders](https://github.com/Nexusov/MovieSift/tree/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/screenshots) | Exact published images for the earlier unified interface, including EN/RU, minimum dimensions and density variants. |
| [Earlier inspector images](https://github.com/Nexusov/MovieSift/tree/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/images) | Historical Phase 1 headless inspector images; not the current workspace. |

## Media, batch and persistence features

| Original record | Scope |
| --- | --- |
| [Initial batches](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/phase-2a-batch-evidence.json) | Independent sequential plans, controlled/headless cases and real batch verification. |
| [Native batch](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/native-batch-evidence.json) | Owner-assisted Windows 10 dialogs, independent plans, payload/source comparisons and scoped functional observations. |
| [New queue](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/new-batch-evidence.json) | Safe explicit reset and its limited native scenario. |
| [Language rules](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/phase-2b-language-rules-evidence.json) | Exact aliases/tags, explicit Preview/Apply and original native EN/RU check. |
| [Literal title filters](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/phase-2b-title-filter-evidence.json) | Language/title conjunction, real-media case and bounded bilingual state preservation. |
| [Forced subtitles](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/phase-2b-forced-subtitles-evidence.json) | Whole-track forced semantics; the native protocol deviation remains recorded rather than converted to a clean scenario pass. |
| [Named presets](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/phase-2c-rule-presets-evidence.json) | Explicit configuration persistence/load, corruption and write-gate corrections. |
| [Recursive folders](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/phase-2d-recursive-folders-evidence.json) | Bounded folder discovery, relative destinations and owner-assisted original GUI processing. |
| [Phase 2 completion](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/phase-2-completion-evidence.json) | Directory policy, unfinished-list recovery, real-media restoration and explicit Linux lock release. |
| [MP4](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/phase-3-mp4-evidence.json) | Admitted MP4 structures, encoded packet/timing preservation and the chapter-track correction. |

## Native observations and remaining limits

The historical native host was Windows 10 22H2 x64 build `19045.6456`. These records do not establish the renamed workspace, Windows 11, Ubuntu Desktop, native visual quality or OS scaling.

| Original record | Scope and limitation |
| --- | --- |
| [Native acceptance matrix](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/native-desktop-acceptance.md) | Exact original artifacts, owner assistance, manual procedures, automation limits and per-case outcomes. |
| [H.264/AAC native workflow](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/native-w07-h-evidence.json) | Original media workflow and output-folder acceptance; no visual/scaling claim. |
| [Earlier W13 cancellation](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/native-w13-evidence.json) | Cancelled/cleanup outcomes after remux completed; exact input timing and active-remux Cancel were not established. |
| [Earlier W14 single close](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/native-w14-evidence.json) | One correlated live-remux Alt+F4 passed for its original binary. Repeated close, verification close and visible stopping status were not established. |
| [Consolidated release candidate](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/release-candidate-evidence.json) | Original candidate identities, Windows worker/material replacement, MIT adoption and scoped native functional observations. |
| [Delivered-candidate smoke](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/release-candidate-native-smoke.json) | Frozen `b288be1` MKV/MP4 and language roundtrip. Later Cancelled/worker-exit/cleanup passed, but input-time uncertainty overlaps worker exit, so strict W13 remains unconfirmed. |
| [Recorded controls](https://github.com/Nexusov/MovieSift/tree/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/images) | Cropped owner-recorded UTC/status frames; no raw private desktop recording or new visual acceptance. |

On the delivered `b288be1` candidate, active/repeated/verification close remained unconfirmed, and real-warning scenarios were unrun. Real warning publication/discard/close, native focus/scrolling/scaling and the declared Windows 11/Ubuntu Desktop targets remain deferred. A missed input interval or an automation limitation is not an established application defect. No deferred campaign is restarted by retaining this index.

## Distribution, container and protection history

- [Original distribution audit](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/distribution-audit.md): initial inventory and material gaps. Later closure work superseded several findings; use the current [distribution requirements](release-distribution-closure.md) for the implemented route.
- [Original distribution closure](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/release-distribution-closure.md): exact earlier source/library relationships, worker build receipts and remaining actual delivery obligations.
- [Recorded Linux container environment](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/linux-container-environment.txt): original pinned base, installed packages, non-root/no-network CLI execution and source observations; not Ubuntu Desktop evidence.
- [Original main-protection receipt](https://github.com/Nexusov/MovieSift/blob/2ba8ba1d4a4507ff678ff0f31045c81e497451d0/docs/verification/main-protection.md): the 2026-10-19 application/readback. The [current protection record](main-protection.md) includes the post-rename readback.
