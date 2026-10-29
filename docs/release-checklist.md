# Release checklist

Use this checklist for a new MovieSift release. Published downloads and version-specific notes belong on [GitHub Releases](https://github.com/Nexusov/MovieSift/releases).

## Build and verify

- [ ] Set the application version and choose the exact source commit for the release tag. Confirm the version in both packaged applications; a stable release must have no prerelease suffix.
- [ ] Pass both required jobs in [Verify and package](../.github/workflows/verify.yml) for that source: Windows and Linux builds, tests, real MKV/MP4 workflows, package inventory, updater archive validation, relocation and the clean Linux container check.
- [ ] Review the outstanding desktop checks below. Report their actual status in the release notes.
- [ ] Prepare a matching binary/material pair for each runtime using the [distribution requirements](DISTRIBUTION.md). Check the source revision, archive sizes, hashes, inventory and material summary against the actual files to be published.
- [ ] Review bundled runtime and tool updates, notices and source materials. Changed dependencies require a new build and matching materials.

Build and local verification commands are in [CONTRIBUTING](../CONTRIBUTING.md). Keep the [main protection rules](../.github/rulesets/protect-main.json) and required platform checks enabled.

## Publish

- [ ] Create a version tag on the verified source commit and describe user-visible changes, compatibility limits and upgrade instructions.
- [ ] Publish the binary archives, matching material archives, platform inventories and material summaries, plus a common `SHA256SUMS`, together on the release page.
- [ ] Verify the uploaded assets and public download links against the prepared set. Retain the matching source materials with the binaries; expiring CI artifacts are insufficient.
- [ ] Use the intended stable/prerelease and Latest settings. Publish changed binaries under a new version rather than replacing an existing release's files.

Version 1.0.0 has no built-in updater; 1.1.0's updater accepts only flat packages. Both require one manual migration to the 1.2 nested portable layout, as described in the [user guide](USAGE.md#upgrade-manually). Subsequent updates use the layout-aware updater.

## Optional candidate handoff

To retain packages from a verified workflow run, start **Verify and package** manually with `retain_candidates=true` on the intended ref:

```sh
gh workflow run verify.yml --repo Nexusov/MovieSift --ref main -f retain_candidates=true
```

The default is `false`. Each successful platform retains five files: its binary archive, matching material archive, distribution inventory, material summary and `SHA256SUMS`. Linux retention also requires the container check. Artifact names include the runtime, actual checked-out commit, run and attempt.

Artifacts expire after **seven days**. They are downloadable by signed-in users with repository read access. Download and verify the complete set before expiry; use a GitHub Release for lasting distribution.

## Outstanding desktop checks

Native acceptance remains incomplete on **Windows 11 25H2 x64** and **Ubuntu 24.04 x64 Desktop with X11/XWayland**. The remaining checks are:

- Visual layout, keyboard focus, scrolling and dialogs in English/Russian at 100%, 150% and 200% OS scaling.
- Cancel during a live remux worker; single/repeated close during processing; close during verification. Confirm input timing against the worker lifetime and inspect cleanup and published results.
- Real warning scenarios, including publish, discard and close.

Automated tests and offscreen renders do not establish these native results. Earlier Windows 10 functional checks and Linux CLI/container checks do not cover the declared desktop targets. These unverified cases are not confirmed application defects.
