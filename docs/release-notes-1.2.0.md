# MovieSift 1.2.0

## Changes since 1.1.0

- Open Updates directly from the always-visible header button. A newer available version appears alongside it. Manual checks, the installed version and the startup-check preference no longer live inside Appearance.
- Startup checks run in the background and are enabled by default. Disabling them does not disable Check now. Network errors leave media work available; downloads and installation still require an explicit action.
- Use the supplied circular-arrow icon for update controls, tinted for the active theme. The MovieSift application icon is unchanged.
- Find one application launcher at the portable root. Internal DLLs, the self-contained runtime, helper, media tools and documentation now live in `app/`. Settings and saved lists stay in root `data/`.
- The root launcher loads the nested assembly directly in the same process using the pinned .NET apphost; it does not extract runtime files or launch a second GUI process.
- Recognize an already-successful worker at the start of Linux cancellation observation. Partial or uncertain descendant observations still preserve owned working files.

## Install and migrate

**One manual migration is required from 1.0/1.1.** Download the archive for your platform and extract into a separate writable directory. Version 1.0 has no updater; 1.1's immutable updater cannot validate the new nested layout. Do not overlay new files onto an old installation or treat the old updater's compatibility refusal as an installed update.

Run root `MovieSift.exe` on Windows or `./MovieSift` on Linux. Keep `app/` and the small root control files together. Follow the [manual upgrade guide](USAGE.md#upgrade-manually) to transfer ordinary settings; retain the old directory for unresolved ownership or recovery. The new updater supports future nested packages while retaining safe legacy-flat validation. It still stages/verifies separately, preserves user data and previous installations, and waits for owned shutdown before switching.

## Compatibility and verification scope

The MKV/MP4 preservation contracts, pinned dependencies, tools and original-code MIT license are unchanged. Every portable file remains covered by inventory and checksums. Matching source/build materials and notices accompany both binary archives; see [distribution requirements](DISTRIBUTION.md).

Automated and packaged-workflow checks do not establish native Windows 11/Ubuntu Desktop, visual/scaling or previously unverified cancellation/close/warning acceptance. Those limits remain in the [release checklist](release-checklist.md). No Computer Use or screen recording is part of this delivery.
