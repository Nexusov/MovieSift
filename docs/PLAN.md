# MovieSift — current delivery status

## Integrated baseline

The unified selection workspace and Soft-inspired appearance redesign are integrated. [PR #11](https://github.com/Nexusov/MovieSift/pull/11) merged through `0cac3586f808d61c8096345f6d5a1d3fc2f1ea49`; [PR #10](https://github.com/Nexusov/MovieSift/pull/10) then merged to main at `2ba8ba1d4a4507ff678ff0f31045c81e497451d0`. Its tree is `e6c597b5aeaf2b0a38613b8f1ead55502e72d661`, identical to the reviewed implementation at `54225849b331b4886277209d3432a99a95f8d209`. The proposed version remains **0.1.0-rc.1**; no public release or tag has been created.

[Integrated-main CI](https://github.com/Nexusov/MovieSift/actions/runs/40148923120) and [candidate handoff run 40150657001, attempt 1](https://github.com/Nexusov/MovieSift/actions/runs/40150657001) succeeded for that exact main revision. The handoff retained separate verified binary/material pairs for Windows and Linux. These are **historical, pre-rename results**: their original archive names, bytes, checksums and tool-version labels remain unchanged. They do not prove a later MovieSift build.

## Reviewed MovieSift baseline and current quality pass

[PR #12](https://github.com/Nexusov/MovieSift/pull/12) integrated the MovieSift rename at `3ee89d45f1be64e80db92c5bdfb24b2aa31a1016`. [Main CI 40160031978](https://github.com/Nexusov/MovieSift/actions/runs/40160031978) passed both required platform jobs at that direct commit. The current bounded quality pass starts from that exact fetched revision on `review/final-quality-pass`; no newer upstream changes were present. MovieSift is the sole current name. The original rename evidence remains at its [immutable baseline](https://github.com/Nexusov/MovieSift/blob/3ee89d45f1be64e80db92c5bdfb24b2aa31a1016/docs/PLAN.md).

F1–F9 address partial-reservation rollback, genuine output-independent NoChange, language-only persistence notices, effective helper contrast, the expanded minimum-size file shelf, cosmetic-overlay keyboard policy, explicit all-subtitle removal/accessibility, consolidation on the current Review boundary, and complete developer/user documentation. No media contract, tool/dependency pin, required CI gate, release authorization or native acceptance boundary is changed. F10 refresh optimization, F11 acquisition dead-branch cleanup and cosmetic redesign remain deferred.

Before-fix controlled/headless regressions reproduced the unreported private-directory leak, four NoChange failures, a preference-only Run block, custom helper contrast of 3.319:1, a zero-height expanded track viewport in EN/RU, and missing Appearance focus ownership. The focused tests retain those exact risks. The [quality evidence](verification/final-quality-pass.json) records actual checks, source scope and selected nonprivate renders. They are not native desktop, screen-reader or OS-scaling evidence.

The required Windows/Linux jobs must execute full mandatory media, packaging/inventory, matching-material, relocation and applicable clean-container verification on the submitted revision. Actual final checkout/parent/tree identities and CI links belong in the draft PR, rather than a CI-ID-only documentation commit. Local optional-media tests and historical successful builds cannot substitute for these gates. No worker/candidate-retention workflow or native campaign is dispatched by this quality pass.

## Delivered behavior

- One workspace accepts one file, several files or bounded local folder discovery. Aggregate tracks show the union of included inspections, preserving duplicate occurrences and exact per-file identities; individual exceptions remain available.
- One explicit Review validates separate immutable per-file plans. Run is a separate action. Zero-audio consent is file-specific, NoChange creates no output, and sequential jobs preserve the existing warning/cancellation/cleanup and no-overwrite contracts.
- MKV and the documented MP4 subset retain their input container and encoded media. Rules, named portable presets, recursive relative outputs and explicit unfinished-list Restore/Discard remain available.
- English/Russian, light/dark/system appearance, custom colors with contrast validation and reduced motion use independent portable preferences. Cosmetic edits preserve selections and job authority.

The [README](../README.md), [architecture](ARCHITECTURE.md) and [supported formats](SUPPORTED_FORMATS.md) define the precise behavior and limits. No additional product phase or optional polish is a prerequisite for this bounded correction delivery.

## Deferred acceptance and publication

Manual native interaction, recording, visual/focus/scrolling checks and OS scaling remain deferred. Windows 11 x64 25H2 and Ubuntu 24.04 x64 Desktop with X11/XWayland remain declared acceptance targets; historical Windows 10 functional results and Linux CLI/container results do not establish those desktop checks.

The archived `b288be1d185c62157e7d1bd1249f2f925ffe3093` candidate has MKV/MP4 and EN → RU → EN native functional evidence on Windows 10 `19045.6456`. One owner-operated cancellation confirmed Cancelled, worker exit, unchanged source, no publication and cleanup. Its input-time uncertainty overlaps worker exit, leaving strict W13 active-remux correlation unconfirmed. That candidate did not establish W14/W15 active/repeated/verification close. An earlier binary's single-close result remains scoped to that binary. Real warning workflows, native visual/scaling and declared-target desktop acceptance remain open. These gaps are not established application defects.

Public release is a separate decision. Preserve the exact matching covered-source/build materials and notices, choose a durable distribution arrangement, and state the actual acceptance scope before publication. The [release checklist](release-checklist.md) and [distribution requirements](verification/release-distribution-closure.md) contain the finite remaining requirements. Main protection and both required check names remain in force; see [protection verification](verification/main-protection.md).
