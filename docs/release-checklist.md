# MovieSift release checklist

[MovieSift 1.0.0](https://github.com/Nexusov/MovieSift/releases/tag/v1.0.0) is published as an ordinary Release and Latest. Tag `v1.0.0` remains on `6fb2a2711745880d1bb39f1177fadbd6a64827f3`, tree `82d40f3f3b121f828a41713240651d1e22b665dc`; later documentation commits are not the source of the frozen release archives. The earlier [0.1.0-rc.1 Pre-release](https://github.com/Nexusov/MovieSift/releases/tag/v0.1.0-rc.1), its tag and assets remain unchanged.

## Current candidate verification

Current locked restore, formatting, Release build, focused/control/headless/media and Python verification are distinguished in [PLAN](PLAN.md) and its linked evidence. Current paths, UI/resource names, commands, links and package definitions consistently use MovieSift. Historical originals remain accessible through the [immutable evidence index](verification/README.md).

[Version 1.0.0 main CI](https://github.com/Nexusov/MovieSift/actions/runs/40253431802) and the single [retained-run attempt 1](https://github.com/Nexusov/MovieSift/actions/runs/40254293203) passed both required platform jobs on `6fb2a2711745880d1bb39f1177fadbd6a64827f3`. Mandatory real-media, inventory, matching-material, relocation and Linux container checks passed. The downloaded archives, packaged 1.0.0 application metadata and all nine published asset identities were verified. Subsequent corrections require their own applicable checks.

Historical RC verification: [PR #13](https://github.com/Nexusov/MovieSift/pull/13) integrated the quality corrections at `a0ea505a6275f3b89e7b08ac4e6a7ae058c6ca5a`, tree `6024784b24242e776a9478b0218be49c239ac93c`. [Main CI](https://github.com/Nexusov/MovieSift/actions/runs/40239000664) and the single [retained-run attempt 1](https://github.com/Nexusov/MovieSift/actions/runs/40239884148) passed both required platform jobs on that exact direct commit. Mandatory real-media, inventory, matching-material, relocation and Linux container checks passed. Downloaded candidate sets and all published asset identities were checked; subsequent corrections still require their own applicable checks.

Every newly prepared binary/material pair must identify its actual source commit, tree, archive bytes and tool receipts. Rebuild workers carrying the custom FFmpeg version label; never reuse or relabel pre-rename hashes as MovieSift evidence. Candidate handoff and public release remain separate from this verification.

## Public release decisions

1. **Original-code license — complete.** [MIT](../LICENSE), `Copyright (c) 2026 Nexusov`, applies to original MovieSift code. Preserve this exact grant and independent third-party terms in binary and source/material packages.
2. **Matching covered-source delivery - published for 1.0.0.** The release hosts the [Windows matching materials](https://github.com/Nexusov/MovieSift/releases/download/v1.0.0/MovieSift-win-x64-materials-6fb2a2711745.tar.gz) and [Linux matching materials](https://github.com/Nexusov/MovieSift/releases/download/v1.0.0/MovieSift-linux-x64-materials-6fb2a2711745.tar.gz) beside its new binaries, together with inventories, summaries and [SHA256SUMS](https://github.com/Nexusov/MovieSift/releases/download/v1.0.0/SHA256SUMS). They contain the collected corresponding source, patches, compilation/install scripts and notices identified by the [distribution requirements](verification/release-distribution-closure.md). Preserve this matching arrangement for future distributions; an expiring Actions artifact alone is insufficient. Mechanical validation is not a blanket legal certification.
3. **Desktop acceptance — deferred.** Native visual/focus/scrolling, OS scaling at the supported 100%/150%/200% combinations, Windows 11 x64 25H2 and Ubuntu 24.04 x64 Desktop/X11-XWayland are not established by the historical Windows 10 functional or Linux container results. Preserve the strict W13 correlation, active/repeated/verification-close and real-warning limitations. Do not relabel them as passes or application defects. Publication did not resume these campaigns.
4. **Version 1.0.0 publication - complete.** The owner-authorized [Release](https://github.com/Nexusov/MovieSift/releases/tag/v1.0.0) is public, not draft or prerelease, and designated Latest. It publishes the new frozen binaries and matching materials with documented acceptance limits. These flags do not prove previously unperformed native/scaling checks. A future changed binary distribution requires its own review and authorization.

Bit-for-bit reproduction, complete historical compiler reconstruction and source archives for every permissively licensed component are not universal release conditions. Apply the notice, corresponding-source and relinking requirements to the actual shipped components and selected license alternatives.

## Optional candidate handoff

The **Verify and package** workflow defaults `workflow_dispatch.inputs.retain_candidates` to boolean `false`. Pull requests, pushes and default manual runs upload compact evidence. Explicit `retain_candidates=true` retains one separate binary/material set per runtime only after that platform passes all mandatory checks, including Linux's container check.

For an authorized handoff from a reviewed branch, use its exact ref; for example:

```sh
gh workflow run verify.yml --repo Nexusov/MovieSift --ref main -f retain_candidates=true
```

Staging verifies the clean checked-out revision/tree, runtime, binary/material archive sizes and SHA256 values, and inventory/summary agreement. Exactly five files are retained: the binary archive, matching source/material archive, distribution inventory, material summary and `SHA256SUMS`. Original archive bytes remain unchanged inside the Actions transport ZIP. A synthetic merge, when checked out, is recorded as that actual source identity.

Artifacts are named `candidate-<runtime>-<full-checked-source-SHA>-run-<run-id>-attempt-<attempt>` and expire after **seven days**. MovieSift is public: users with repository read access who are signed in can [download them](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts). Candidate retention is not a GitHub Release, permanent source hosting or redistribution assurance. The preparation summary's `publicDeliveryPerformed: false` describes preparation before any optional upload.

The completed [pre-rename handoff](https://github.com/Nexusov/MovieSift/actions/runs/40150657001) belongs to `2ba8ba1d4a4507ff678ff0f31045c81e497451d0`, attempt 1. It remains historical and does not supply renamed application archives. The later [MovieSift handoff](https://github.com/Nexusov/MovieSift/actions/runs/40239884148), attempt 1, produced the unchanged `a0ea505a6275f3b89e7b08ac4e6a7ae058c6ca5a` candidates published in the [historical RC](https://github.com/Nexusov/MovieSift/releases/tag/v0.1.0-rc.1); its original assets remain unchanged.

The [1.0.0 handoff](https://github.com/Nexusov/MovieSift/actions/runs/40254293203), attempt 1, produced the new `6fb2a2711745880d1bb39f1177fadbd6a64827f3` binary/material pairs published in 1.0.0. Seven-day retention is not their public-release hosting arrangement; the public release links above identify the delivered set.
