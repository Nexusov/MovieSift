# MovieSift release checklist

The implementation, earlier candidate handoff and MovieSift rename are integrated. The F1–F9 quality corrections have their own draft review; public release/tag and fresh candidate retention remain separately authorized actions. No further feature milestone is required.

## Current candidate verification

Current locked restore, formatting, Release build, focused/control/headless/media and Python verification are distinguished in [PLAN](PLAN.md) and its linked evidence. Current paths, UI/resource names, commands, links and package definitions consistently use MovieSift. Historical originals remain accessible through the [immutable evidence index](verification/README.md).

The rename is merged at `3ee89d45f1be64e80db92c5bdfb24b2aa31a1016`; its main run `40160031978` passed. Any subsequent correction must pass both existing required platform jobs for its actual revision before integration. Keep mandatory real-media, inventory, matching-material, relocation and Linux container checks. The implementing pull request and its linked workflow runs record those results rather than copying historical pass counts.

Every newly prepared binary/material pair must identify its actual source commit, tree, archive bytes and tool receipts. Rebuild workers carrying the custom FFmpeg version label; never reuse or relabel pre-rename hashes as MovieSift evidence. Candidate handoff and public release remain separate from this verification.

## Public release decisions

1. **Original-code license — complete.** [MIT](../LICENSE), `Copyright (c) 2026 Nexusov`, applies to original MovieSift code. Preserve this exact grant and independent third-party terms in binary and source/material packages.
2. **Matching covered-source delivery — required.** Deliver the collected corresponding source, patches, compilation/install scripts and notices for the actual Windows MKV pair, retained Linux closure and FFmpeg builds. The [distribution requirements](verification/release-distribution-closure.md) identify the implemented collection routes. Locally prepared bundles and expiring candidate artifacts do not replace the release's durable delivery arrangement.
3. **Desktop acceptance — deferred.** Native visual/focus/scrolling, OS scaling at the supported 100%/150%/200% combinations, Windows 11 x64 25H2 and Ubuntu 24.04 x64 Desktop/X11-XWayland are not established by the historical Windows 10 functional or Linux container results. Preserve the strict W13 correlation, active/repeated/verification-close and real-warning limitations. Do not relabel them as passes or application defects. These campaigns are not resumed by the quality pass.
4. **Publication — separate owner decision.** Review the exact candidate identities, required CI, material delivery and documented acceptance limitations before creating a public tag/release. Earlier integration authorization and candidate upload do not themselves authorize a public release.

Bit-for-bit reproduction, complete historical compiler reconstruction and source archives for every permissively licensed component are not universal release conditions. Apply the notice, corresponding-source and relinking requirements to the actual shipped components and selected license alternatives.

## Optional candidate handoff

The **Verify and package** workflow defaults `workflow_dispatch.inputs.retain_candidates` to boolean `false`. Pull requests, pushes and default manual runs upload compact evidence. Explicit `retain_candidates=true` retains one separate binary/material set per runtime only after that platform passes all mandatory checks, including Linux's container check.

For an authorized handoff from a reviewed branch, use its exact ref; for example:

```sh
gh workflow run verify.yml --repo Nexusov/MovieSift --ref main -f retain_candidates=true
```

Staging verifies the clean checked-out revision/tree, runtime, binary/material archive sizes and SHA256 values, and inventory/summary agreement. Exactly five files are retained: the binary archive, matching source/material archive, distribution inventory, material summary and `SHA256SUMS`. Original archive bytes remain unchanged inside the Actions transport ZIP. A synthetic merge, when checked out, is recorded as that actual source identity.

Artifacts are named `candidate-<runtime>-<full-checked-source-SHA>-run-<run-id>-attempt-<attempt>` and expire after **seven days**. MovieSift is public: users with repository read access who are signed in can [download them](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts). Candidate retention is not a GitHub Release, permanent source hosting or redistribution assurance. The preparation summary's `publicDeliveryPerformed: false` describes preparation before any optional upload.

The completed [pre-rename handoff](https://github.com/Nexusov/MovieSift/actions/runs/40150657001) belongs to `2ba8ba1d4a4507ff678ff0f31045c81e497451d0`, attempt 1. It remains historical and does not supply renamed application archives.
