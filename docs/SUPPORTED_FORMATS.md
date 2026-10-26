# Supported formats and preservation

MovieSift removes audio and subtitle tracks by remuxing, without decoding or re-encoding. It keeps every video track. Inspection determines support: an `.mkv` or `.mp4` extension alone is insufficient. There is no container conversion or backend fallback.

| Container | Supported content | Main exclusions |
| --- | --- | --- |
| MKV | Ordinary self-contained Matroska video, audio and subtitles; supported chapters, tags and attachments | Linked segments, ordered chapters, advanced references and detected HDR/Dolby Vision properties |
| MP4 | H.264/AVC video, AAC audio, embedded `mov_text` subtitles and JPEG/PNG artwork | Other codecs, fragmented/encrypted files, external references, complex edits and unsupported metadata |

The bundled backends are MKVToolNix **102.0** and FFmpeg/ffprobe **9.0.2-MovieSift-source-2a571b606854**. Exact sources/configuration are pinned in [tools.lock.json](../build/tools.lock.json) and [ffmpeg.lock.json](../build/ffmpeg.lock.json). Keep the packaged tools with their application version.

## MKV preservation

| Content | Preserved and checked |
| --- | --- |
| Video, selected audio and subtitles | Retained mapping and order, codec/known properties, titles, languages and supported flags |
| Container title | Inspected title |
| Chapters | Supported ordinary chapter content, compared as structured XML rather than counts alone |
| User tags | Supported global and retained-track tags, with track references checked against the retained mapping |
| Attachments | Supported fonts/artwork and other attachments, including after subtitle removal; order, UID, name, description, MIME type and size |
| Timing | Every retained track's frame count and timestamps, known duration properties and the extractor's terminal boundary; **1 ms** timestamp tolerance for Matroska rounding |

Routine MKV verification does **not** compare every encoded payload or attachment byte, or independently parse every packet's duration field. Matching metadata cannot prove attachment byte equality or support for every font/artwork representation or private Matroska element.

### Container changes

Remuxing changes layout, indexes, writing-application metadata and track numbers. Whole-file byte equality is not expected. Total duration may shorten when the longest track is removed; verification follows retained content instead.

Track UIDs may change for inputs identified as written by MakeMKV; other inputs must retain them. Actual retained tracks and tag references are mapped explicitly. New track-statistics tags are disabled. Existing statistics tags remain checked; if the backend drops a required property, verification fails.

Track, chapter and tag comparisons recognize registered language aliases such as `ger`/`deu`/`de` and `fre`/`fra`/`fr`. Regional/script/private suffixes and unknown values remain exact. Original metadata is not rewritten from system culture data; an absent IETF language field may appear only as an equivalent registered plain code.

### AAC timing exception

An absent AAC `DefaultDuration` may be added only when unchanged codec configuration proves ordinary AAC-LC object type 2, **1,024 samples per frame**, a supported sampling-rate index matching the track rate, supported channel configuration and zero general-audio extension flags. Only zero padding or the recognized SBR-disabled sync extension is allowed. Duration must equal `1024 × 1,000,000,000 / sample rate`, within **1 ns** of integer rounding.

The extractor appends a synthetic boundary after actual frame timestamps. If the source boundary duplicates its last frame timestamp, that boundary alone may gain the proven duration above, within the same 1 ms tolerance. Actual frames remain checked. This does not permit HE-AAC, 960-sample frames, changed configuration or changes to a known duration; some ordinary AAC files therefore fail verification.

### Unsupported MKV structures

Inspection rejects:

- Segment families and previous/next links; ordered chapters and chapter segment, edition, track or process references.
- Uncommon track types; tags with multiple TrackUID targets, mixed track/non-track targets or dangling nonzero TrackUIDs.
- Known advanced luminance, chromaticity, white-point, HDR/Dolby Vision properties and PQ/HLG transfer characteristics exposed by inspection.
- Malformed or ambiguous structure, multiple/nested segments, duplicate segment information, unknown-sized children/clusters, unrecognized trailing data, or more than **100,000** inspected element headers. An unknown-sized top-level Segment bounded by the file's end is allowed.

Identification warnings prevent processing. Inspection cannot detect every private element or codec-level HDR feature; ordinary SDR support is not a promise of HDR preservation.

## MP4 preservation

Supported files are self-contained, unencrypted and nonfragmented, with one `moov` and a major brand of `isom`, `iso2`–`iso6`, `mp41`, `mp42` or `avc1`. Header inspection must agree with ffprobe; its general MOV/MP4 label is insufficient.

| Content | Preserved and checked for each result |
| --- | --- |
| Streams | Explicit retained mapping/order, codec initialization, dimensions, channels, sample rate, profile and supported color properties |
| Encoded packets | Payload SHA256, packet sizes, order and count |
| Timing | Exact rational PTS, DTS and durations, packet flags, and AAC skip-sample/discard-padding data |
| Languages and titles | Original language, actual track title/name and separate handler name; handler labels are not track titles for rules |
| Flags | Supported default/forced/role flags and ordinary enabled/in-movie/in-preview flags; removing default audio does not promote another track |
| Chapters and metadata | Ordinary rate-one chapter boundaries/titles, container title, supported common iTunes-style text/numeric tags and track metadata |
| Artwork | JPEG/PNG cover streams are always kept; encoded bytes, codec properties and flags are checked. Artwork has no independent timed presentation. |

One distinct referenced QuickTime text chapter track is supported as chapter metadata, not a removable subtitle. Several streams may reference that same track; multiple distinct chapter tracks are rejected. Chapter IDs and QuickTime/Nero representation may change while preserving the checked content.

Ordinary B frames, AAC priming and simple rate-one edits, including a preceding empty delay, are supported. Cuts, splices, dwell and reverse edits are not. Arbitrary timestamp shifts and truncation to the shortest stream are forbidden.

Fragmentation, encryption/protection boxes, external data references, unsupported codecs/timed data, advanced HDR metadata, unknown structural extensions and unrepresentable metadata fail inspection. Inspection cannot validate every private box or codec extension; universal MP4/HDR compatibility is not promised.

Parsing is bounded to **100,000** box headers, depth **16**, **256** tracks, inspected fields of **64 KiB**, structured probe output of **2 MiB** and **1,024** chapters. Packet verification streams records, may take time proportional to file size and remains cancellable.

Container layout, indexes, brands, track IDs, bitrate/statistics and writer metadata may change. Total duration may shorten when a track is removed. These differences do not relax retained-packet or timing checks.

## Warnings and output safety

MKVToolNix warnings require a separate publish/discard decision after successful verification. FFmpeg failures cannot use that override: nonzero exit status, unrecognized warnings and truncated diagnostics fail the job. Only specific known warnings for supported track-name/artwork representations may proceed to verification; diagnostics remain visible.

Verification failure prevents publication. Copy mode never overwrites a destination. Replacement verifies a temporary result before replacing its own inspected source; it cannot replace another input. Unchanged files create no output. Source revision checks detect ordinary changes but cannot prevent arbitrary concurrent edits by another program. See [output choices](USAGE.md#choose-the-result) and the [replacement design](ARCHITECTURE.md#output-choice-and-source-replacement).

## Aggregate selection scope

The track list is the union of all included, successfully inspected files. Tracks found in only one episode remain visible. Included pending/failed inspections prevent Review.

Groups match kind, exact canonical language, title trimmed at its edges, codec, audio channels, forced status and semantic roles. Titles are case-sensitive here. Regional/script language distinctions remain; unknown/conflicting language tracks stay separate. Default/enabled differences remain individual metadata, unchanged by selection.

Duplicate tracks remain distinct occurrences. Group actions affect only their represented tracks; per-file exceptions produce a mixed checkbox state. New or reinspected tracks start kept. Excluded files and completed results are not edited. Grouping does not establish content equivalence or spoken language.

## Input discovery scope

Add several explicit local files or one folder at a time; subfolders are opt-in. Limits are **100 queue entries** including visible failures, **10,000 examined filesystem entries** per folder scan including directories/non-media files, and **32 directory levels**.

Network locations and redirected directories, including symlinks and junctions, are rejected. Failed, cancelled or over-limit scans add no partial selection; cancellation during later inspection retains accepted rows. Duplicate paths/file identities keep the first accepted entry. Missing, unreadable or nonregular inputs remain visible as failures.

Recursive copies to a separate output folder preserve relative subdirectories. That folder must exist and cannot equal or contain the source tree; a descendant output subtree is excluded from scanning. Adjacent copies and replacement use source directories without a separate output root. Known application working locations are also excluded; existing `.trimmed` files are not excluded by name.

Review creates no directories. Execution may create missing parents only under an approved output root; empty created directories can remain after cancellation/failure. Copy destinations cannot collide with existing files, another output or any source. Replacement may target only its own source.

URLs, playlists, external subtitle editing, transcoding, playback and byte-offset resume are unsupported. Restored lists require fresh inspection and Review; see the [user guide](USAGE.md#saved-lists-and-interrupted-work).

## Language-selection rules

Audio and subtitles are configured independently and default to unchanged. A targeted category keeps **every** match, without preferring the first, default or highest-channel track. Preview proposes choices; Apply changes them. Review is still required before processing.

Matching recognizes ISO 639-1 and ISO 639-2 bibliographic/terminologic aliases from the shared [registry table](https://www.loc.gov/standards/iso639-2/ISO-639-2_utf-8.txt). Supported tags follow `primary[-Script][-REGION]`: a known two/three-letter language, optional four-letter ASCII script, then optional two-letter ASCII or three-digit region. Script/region shapes are validated, not registry membership. Complete tags compare case-insensitively after alias normalization: `ger` equals `de`, but `ru` differs from `ru-RU`, and `sr-Latn` from `sr-Cyrl`. Other variants/extensions need manual selection.

A targeted track needs a known plain legacy language. Existing IETF metadata is authoritative only when its full tag is supported and its primary agrees with that legacy language. Missing, unregistered, private-use, `und`, `mul`, `mis`, `zxx`, malformed or conflicting values leave **all choices for that file unchanged**, even when the affected track would fail another filter. Untargeted categories remain unchanged.

Optional filters combine with the language match:

- **Title contains:** literal, case-insensitive matching against actual track titles. Search text is trimmed at its edges; internal whitespace/punctuation remain literal. Blank titles do not match; an enabled blank filter is invalid. Filenames, handler labels, codec names and fallback titles are not searched. There are no regex, wildcard, fuzzy or title-only rules.
- **Keep only forced subtitles:** keeps whole matching subtitle tracks whose actual forced flag is set. Titles/default flags do not establish forced status. It neither extracts selected subtitle events nor promises player behavior.

No audio matches in a file containing audio leaves the whole file unchanged for manual selection; an already silent file is valid. No subtitle matches can propose keeping none and is reported explicitly. Rules never rewrite metadata or relax verification. New/reinspected files require another Preview/Apply; presets store configuration only.

## Platform and native requirements

Portable archives target **Windows 11 x64 25H2** and **Ubuntu 24.04 x64 Desktop with X11/XWayland**. Native Wayland and other Linux distributions are outside that target. Desktop behavior and scaling still have [outstanding checks](release-checklist.md); automated package checks do not establish complete desktop compatibility.

The .NET runtime and media tools are included. Ubuntu still needs system libraries. The [package-check environment](../build/portable-smoke.Dockerfile) uses:

```text
ca-certificates libc6 libgcc-s1 libstdc++6 zlib1g libssl3t64 libicu74
libgssapi-krb5-2 libbrotli1 libgmp10 tzdata libx11-6 libice6 libsm6
libfontconfig1 libgl1 libegl1 libxrandr2 libxcursor1 libxi6 libxinerama1
```

Each Linux package includes `native-dependencies.txt` with actual library resolution. Required Qt libraries are bundled; GMP (`libgmp10`) is a system dependency. Extracted tools run directly without FUSE. Another .NET runtime or separate media-tool installation is unnecessary.

The optional .NET LTTng tracing provider may report missing `liblttng-ust.so.0`; that dependency alone is not required for normal execution. Missing application/media-tool libraries remain errors. For upstream background, see [.NET Ubuntu dependencies](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install?pivots=os-linux-ubuntu-2404&tabs=dotnet10) and [Avalonia platform requirements](https://docs.avaloniaui.net/docs/supported-platforms).
