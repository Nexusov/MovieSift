# MovieSift 0.1.0-rc.1 — unreleased

MovieSift is the project and application name. The unified workspace and appearance redesign are integrated. The proposed version remains subject to release review; no public release or tag is published. Original MovieSift code uses [MIT](../LICENSE), copyright (c) 2026 Nexusov. Bundled components retain their own terms.

## Included functionality

- Remove selected embedded audio and subtitle tracks from MKV and the admitted H.264/AAC/mov_text MP4 subset without re-encoding or replacing originals. Retain every admitted video stream and supported attachment/artwork.
- Add files or bounded local folders to one workspace. The common track list shows the union across all included files, including tracks present in one episode only, with per-file exceptions and duplicate occurrences preserved.
- Review independent plans and destinations, then start a separate sequential run. Zero-audio consent remains file-specific; NoChange creates no copy; existing files are never overwritten.
- Preview and explicitly apply exact language rules with optional literal-title and whole-track forced-subtitle constraints. Named portable presets store configuration only and require explicit Load.
- Preserve relative output directories for optional recursive folders. Restore an unfinished path listing only by explicit choice, followed by fresh inspection, selections and Review.
- Use English or Russian, responsive file/track panels, light/dark/system appearance, custom colors with contrast checks, and reduced motion. Appearance preferences are independent of media plans and language storage.
- Report the measured size difference for published outputs when both lengths are known, including larger or equal results. Originals continue occupying disk space.

## Rename and portable upgrade

Executable and archive names are `MovieSift`, `MovieSift.exe`, `MovieSift-win-x64.zip` and `MovieSift-linux-x64.tar.gz`. Development environment variables use the `MOVIESIFT_` prefix. Project names, resource references and matching source/material bundles use the same identity.

Extract the renamed application into a fresh directory. Resolve or discard pending operations in the previous candidate before carrying over portable preferences; never rename private job directories or edit ownership-journal paths. See the [upgrade instructions](../README.md#upgrading-an-earlier-portable-candidate).

The FFmpeg/ffprobe custom version label is now `9.0.2-MovieSift-source-2a571b606854`; the pinned upstream source revision and enabled feature configuration are unchanged. New workers must be rebuilt, and their actual identities must be recorded. Earlier binary/material bundles retain their original names, bytes and evidence.

## Verification and distribution status

[Current status](PLAN.md) records the local rename checks and distinguishes the integrated pre-rename baseline from the new revision. Both required Windows/Linux checks must pass on the renamed source before merge; their linked workflow results identify the exact checkout and generated packages. [Historical verification](verification/README.md) preserves exact original receipts without treating them as new MovieSift results.

Manual native/visual/scaling acceptance remains deferred, including the declared Windows 11 and Ubuntu Desktop targets and the recorded active-cancellation/close and real-warning gaps. Automated/headless or container checks do not establish those cases.

The [supported-format contract](SUPPORTED_FORMATS.md), [distribution requirements](verification/release-distribution-closure.md) and [release checklist](release-checklist.md) define the supported subset and the remaining publication decisions. Binary release distribution must include the applicable exact matching source/build materials and notices; an expiring Actions artifact alone is not the durable release arrangement.

No conversion, transcoding, playback, parallel remuxing, source replacement, automatic execution after restart, preset overwrite/import/export or additional matching mode is introduced by the rename.
