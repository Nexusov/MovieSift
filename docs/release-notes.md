# TrackTrim 0.1.0-rc.1 — proposed first release

This is an unreleased review candidate, not a public release or tag. The proposed version is subject to final review. Original TrackTrim code is licensed under [MIT](../LICENSE), copyright (c) 2026 Nexusov. Bundled components retain their own terms; release source/material delivery and the remaining desktop acceptance gates are independent requirements.

## Included functionality

- Remove selected audio and subtitle tracks from local MKV and the admitted H.264/AAC/mov_text MP4 subset without re-encoding or replacing originals. Keep every admitted video stream and supported attachment/artwork.
- Inspect and approve each file independently, review destinations, and run mixed MKV/MP4 batches sequentially. Existing files are never overwritten.
- Add explicit files or a bounded folder tree. Optional recursion preserves relative output directories; input and output paths retain the documented local-file and containment checks.
- Preview and explicitly apply exact language rules with optional literal-title and whole-track forced-subtitle constraints. Named portable presets store configuration only and require explicit Load.
- Restore an unfinished path listing after restart only by explicit choice, with fresh inspection, selections, approval and Review. No live job or old track-index authority is restored.
- Use English or Russian, including portable language, rule-preset and listing data beside the application.

## Integration corrections

MP4 capability inspection now refuses more than one distinct referenced QuickTime chapter-track ID. Several media tracks may reference the same chapter track. This prevents aggregate chapter reporting from concealing the loss of a second chapter list. Existing Nero/QuickTime consistency checks remain in force.

The integration also places the already reviewed explicit Linux lock-release correction before the recursive-folder PR, so inherited descriptors cannot keep a completed preset write locked while the stack is integrated.

## Candidate distribution

The release-candidate review uses the same FFmpeg source revision with a deliberately minimal source-built worker pair, explicit static zlib, captured configuration and source/build materials. It does not use the previous broad optional-library binary build. MKVToolNix remains the pinned 102.0 backend. Only proven CLI-needed Linux bundle libraries and Windows CLI payloads are retained; full packaged runtime checks remain required.

The [supported-format contract](SUPPORTED_FORMATS.md), [distribution closure](verification/release-distribution-closure.md) and [release checklist](release-checklist.md) define the actual subset and remaining gates. Historical native observations belong to their recorded binary and OS; a successful hosted job is not target-desktop acceptance.

No MP4/MKV conversion, transcoding, playback, parallel remuxing, source replacement, automatic execution after restart, preset overwrite/import/export or new matching mode is included.
