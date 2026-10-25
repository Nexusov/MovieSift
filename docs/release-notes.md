# MovieSift 0.1.0-rc.1 — Pre-release

[MovieSift 0.1.0-rc.1](https://github.com/Nexusov/MovieSift/releases/tag/v0.1.0-rc.1) is the first public release candidate, published for evaluation rather than as a fully accepted stable release. It includes the integrated unified workspace, appearance redesign and reviewed F1–F9 corrections. Original MovieSift code uses [MIT](../LICENSE), copyright (c) 2026 Nexusov. Bundled components retain their own terms.

## Downloads and portable startup

- [Windows x64 ZIP](https://github.com/Nexusov/MovieSift/releases/download/v0.1.0-rc.1/MovieSift-win-x64.zip)
- [Linux x64 tar.gz](https://github.com/Nexusov/MovieSift/releases/download/v0.1.0-rc.1/MovieSift-linux-x64.tar.gz)
- [Windows matching source/build materials](https://github.com/Nexusov/MovieSift/releases/download/v0.1.0-rc.1/MovieSift-win-x64-materials-a0ea505a6275.tar.gz) and [Linux matching source/build materials](https://github.com/Nexusov/MovieSift/releases/download/v0.1.0-rc.1/MovieSift-linux-x64-materials-a0ea505a6275.tar.gz)
- [SHA256SUMS](https://github.com/Nexusov/MovieSift/releases/download/v0.1.0-rc.1/SHA256SUMS), with separate platform inventories and material summaries on the release page.

Extract the complete archive into a new writable folder. Launch `MovieSift.exe` on Windows or `./MovieSift` from the extracted `MovieSift-linux-x64` folder on Linux. No application installer, separately installed .NET or media tools are required. Linux still needs the documented [OS native libraries](SUPPORTED_FORMATS.md#platform-and-native-requirements).

## Included functionality

- Remove selected embedded audio and subtitle tracks from MKV and the admitted H.264/AAC/mov_text MP4 subset without re-encoding or replacing originals. Retain every admitted video stream and supported attachment/artwork.
- Add files or bounded local folders to one workspace. The common track list shows the union across all included files, including tracks present in one episode only, with per-file exceptions and duplicate occurrences preserved.
- Review independent plans and destinations, then start a separate sequential run. Zero-audio consent remains file-specific; NoChange creates no copy and ignores an unused destination; changed outputs never overwrite files or alias any source. Audio/subtitle sections include an explicit reversible Remove all subtitles from copies action.
- Preview and explicitly apply exact language rules with optional literal-title and whole-track forced-subtitle constraints. Named portable presets store configuration only and require explicit Load.
- Preserve relative output directories for optional recursive folders. Restore an unfinished path listing only by explicit choice, followed by fresh inspection, selections and Review.
- Use English or Russian, responsive file/track panels, light/dark/system appearance, custom colors with contrast checks, and reduced motion. Appearance preferences are independent of media plans and language storage. Settings use a keyboard-contained cosmetic overlay with Escape/return focus; preference-only faults do not block healthy media storage.
- Report the measured size difference for published outputs when both lengths are known, including larger or equal results. Originals continue occupying disk space.

## Rename and portable upgrade

Executable and archive names are `MovieSift`, `MovieSift.exe`, `MovieSift-win-x64.zip` and `MovieSift-linux-x64.tar.gz`. Development environment variables use the `MOVIESIFT_` prefix. Project names, resource references and matching source/material bundles use the same identity.

Extract the renamed application into a fresh directory. Resolve or discard pending operations in the previous candidate before carrying over portable preferences; never rename private job directories or edit ownership-journal paths. See the [upgrade instructions](../README.md#upgrading-an-earlier-portable-candidate).

The FFmpeg/ffprobe custom version label is now `9.0.2-MovieSift-source-2a571b606854`; the pinned upstream source revision and enabled feature configuration are unchanged. New workers must be rebuilt, and their actual identities must be recorded. Earlier binary/material bundles retain their original names, bytes and evidence.

## Verification and distribution status

Tag `v0.1.0-rc.1` identifies source `a0ea505a6275f3b89e7b08ac4e6a7ae058c6ca5a`, tree `6024784b24242e776a9478b0218be49c239ac93c`. [Main CI](https://github.com/Nexusov/MovieSift/actions/runs/40239000664) and [retained-run attempt 1](https://github.com/Nexusov/MovieSift/actions/runs/40239884148) both passed the required Windows/Linux jobs on that direct commit, including real media, packaging/inventory/materials, relocation and the Linux clean container. The published archives are the unchanged retained candidates. Later documentation commits are not their binary source. [Historical verification](verification/README.md) retains original receipts without assigning them to this binary.

Manual native/visual/scaling acceptance remains deferred, including the declared Windows 11 and Ubuntu Desktop targets and the recorded active-cancellation/close and real-warning gaps. Automated/headless or container checks do not establish those cases.

The [supported-format contract](SUPPORTED_FORMATS.md), [distribution requirements](verification/release-distribution-closure.md) and [release checklist](release-checklist.md) define the supported subset and remaining acceptance limits. The public release hosts nine assets: two unchanged binary archives, two matching source/build-material archives, two inventories, two material summaries and a common `SHA256SUMS`. GitHub's automatic source archives do not replace the matching material bundles. Preparation-time fields in historical summaries remain unchanged; the [published release](https://github.com/Nexusov/MovieSift/releases/tag/v0.1.0-rc.1) records actual delivery.

No conversion, transcoding, playback, parallel remuxing, source replacement, automatic execution after restart, preset overwrite/import/export or additional matching mode is introduced by the quality pass.
