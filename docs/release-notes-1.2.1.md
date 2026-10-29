# MovieSift 1.2.1 review build

This is a review build, not a newly published stable release. Existing release archives remain unchanged.

- One sticky Theme heading, rounded vector Close/Update controls, stable scrollbar gutters and padded shared choice cards.
- Effective custom HEX colors with swatches, bottom-aligned Reset, smooth interruptible panels/disclosures and reduced-motion support.
- Cosmetic panels dismiss on outside click/Escape without disabled workspace colors or stale focus callbacks. Windows uses the custom undecorated caption; native resizing/snap and visual/scaling acceptance remain outstanding.
- Separate Audio/Subtitles tabs preserve independent selections and Review. Removed tracks remain restorable. Remove an unfinished queue row directly; New queue explicitly confirms clearing the list without deleting media.
- Remember Copy/Replace as a portable preference, never as reusable approval. Source paths, inspections and jobs are not part of that record.
- Portable updates retain colors, presets, downloaded languages and settings. Existing translation caches refresh once per new build, with offline preservation and strict immutable-source validation. Installed-language switches no longer display a false download bar. Swedish, Danish and Czech bring the chooser to 15 languages including English; portable archives still ship English only.
- Track sizes use labelled metadata/bitrate estimates where available, with unknown values explicit and no new full payload scan.
- The slow owner-provided MKV was inspected through the production path. Structural header I/O dominated the observed 471-second cold-HDD run. Bounded positional prefixes reduce requested bytes while preserving all existing structure checks. Warmed old/new controls do not establish a cold-storage speed guarantee; see [measured evidence](verification/inspection-performance.md).

The media preservation contract, dependency/tool pins, source checks, owned cancellation and no-overwrite publication remain unchanged. Automated results and source/package identities belong in the review PR and local handoff receipt. No new native desktop, Windows 11/Ubuntu Desktop, W13–W15 or scaling acceptance is claimed. Translation coverage and placeholder checks are mechanical checks, not native-speaker certification.
