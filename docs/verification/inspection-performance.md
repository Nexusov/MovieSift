# Inspection latency and track-size evidence

Scope: initial source inspection, including an owner-provided slow source, not remux/verification. Its title and absolute path are omitted here; raw local observations are not committed.

## Findings

| Path | Work and complexity | Consequence |
| --- | --- | --- |
| `InspectionService` / `FileSourceAccess.ReadRevisionAsync` | Handle identity, length and modification time before/after inspection; no full-file SHA256 | Initial loading does not hash the entire movie here. These checks must remain. |
| MKV identification | Two pinned-worker version probes, bounded `mkvmerge -J`, then Segment/Info validation | Worker startup, storage latency and native identification can contribute independently. |
| `MkvSegmentInfoReader` | O(H) headers, at most 100,000; seeks past encoded payloads, reads bounded element prefixes | Many distant clusters can cause storage seeks. Removing this scan would weaken linked/ambiguous-container rejection; it has not been removed. |
| MKV metadata | One `mkvextract` invocation when chapters/tags exist; bounded XML validation | Native metadata discovery can seek through a file. No extraction is launched if both are absent. |
| MP4 inspection | Bounded box/probe evidence | Encoded packet hashes run during output verification, not initial track loading. |
| Batch addition | Inspects only accepted included `PendingInspection` entries | Adding another file does not automatically reinspect completed inspections. |

The static complexity scanner's loop warnings were checked against actual code; no generic nested-loop rewrite was justified. No unsafe cache of a source inspection or tool identity was introduced.

## Owner-provided large source

The production `InspectionService` and `MkvBackend` inspected the actual 14,235,596,230-byte source on Windows 10 x64 (10.0.19045), on local NTFS storage backed by a WDC WD5000AAKX SATA HDD. An isolated development console referenced copies of the built application assemblies, used the pinned runtime tools and task-owned scratch, and invoked inspection only. No remux, output publication or source write was requested.

Baseline results before the header-I/O correction:

| Boundary | Duration |
| --- | ---: |
| Tool checks | 285 ms |
| Native identification | 5,983 ms |
| Managed structural scan | 464,834 ms |
| Metadata extraction | 195 ms |
| Complete inspection | 471.311 s |

Inspection succeeded with 12 tracks. Handle-based source revision (identity, full length and modification time) was identical before/after; owned scratch was empty. No full source SHA256 was computed for this read-only latency experiment. The timed development Infrastructure assembly SHA256 was `cd55f60ce314d1ff1a8d25540d44956aa3532be85541491e0b33d786d1974cf1`; this is not an acceptance claim for an older released binary.

During the structural stage, the isolated process accumulated 8,571 read operations and 34,911,198 transferred bytes after approximately seven minutes, with less than two CPU seconds. Together with the boundary timings, this establishes storage/header I/O as the dominant observed cost, rather than a full-file hash, native identification or UI rendering.

The corrected scan still visits every top-level and Info child header, preserving all linkage, duplicate/unknown element, length, cancellation and 100,000-header limits. It uses a bounded positional prefix read of at most 12 bytes per element and a random-access file hint instead of discarding a buffered stream on every cluster seek. Controlled tests verify far-end Info/linkage detection, maximum-width prefixes and the per-header requested-byte bound. The original file/handle, regular-file, symlink and source-revision protections remain in force.

The next corrected inspection succeeded in 0.830 s, including 171 ms structural scanning, with the same 12 tracks, unchanged source revision and empty scratch. **This is a warmed-cache observation, not a 568-fold correction claim:** an immediate old-reader control also completed in 0.877 s (229 ms structure).

A further warmed comparison used Windows `GetProcessIoCounters` only around the structural boundary:

| Reader | Structure | Read operations | Requested bytes |
| --- | ---: | ---: | ---: |
| Old buffered cursor | 202 ms | 9,341 | 38,260,736 |
| Corrected positional prefixes | 159 ms | 9,356 | 112,272 |

These counters describe process read requests, not physical platter transfers. They establish approximately 341 times less requested header I/O, while the number of header reads remains similar. No OS cache flush, privileged disk operation or claim of controlled cold-storage benchmarking was made. Cold-HDD seek latency can therefore remain; eliminating the complete walk would weaken the supported-format checks. Both repeated runs succeeded, preserved the source revision and left no scratch entries. The corrected development Infrastructure assembly SHA256 was `ac46fa526e6d30566aaae5d56d5eba482af8abe2d0c697732cd9548371a8b6e6`.

## Read-only native-worker sample

On the available Windows 10 x64 host, using the existing pinned MKVToolNix 102.0 workers and original generated fixtures:

| Fixture | Bytes | `mkvmerge --version` | `mkvextract --version` | `mkvmerge -J` | Chapter/tag extraction |
| --- | ---: | ---: | ---: | ---: | ---: |
| `ordinary.mkv` | 392,464 | 7.7531 s | 2.8585 s | 0.1620 s | 0.1089 s |
| `cancellation.mkv` | 5,668,802 | 0.1058 s | 0.0962 s | 0.3083 s | Not needed |

These are single sequential wall-clock observations, not a benchmark or a large-movie speed claim. The cold-start difference is observed; antivirus, disk-cache or other attribution was not established. This isolated tool sample does not measure the production structural scan or GUI appearance.

SHA256 before/after was unchanged for both:

- `ordinary.mkv`: `3870bf6f064a5e0824d775aeb334275dfb806472339fd8a3352feea4fdf61d29`
- `cancellation.mkv`: `52c7ef6402ad348ec7a2cf47d5f47dc6bc6b9160741d8df9dfa6c6f40f0c9219`

## Diagnostic and display changes

MKV inspection now distinguishes **Checking container structure** from native identification and metadata extraction. Bounded monotonic timing messages identify tools, identification, structure and metadata boundaries without recording media paths or probe contents. They make a future slow source diagnosable without pretending that a timeout or file size is a completion estimate. Existing cancellation, source checks, structure budgets and output verification remain intact.

Track-size presentation uses facts already available during inspection:

- MKV: consistent positive `NUMBER_OF_BYTES` tags targeting one actual inspected track UID, bounded by source length. These are labelled **reported**; stale statistics are not proof of encoded content.
- MP4: optional existing stream bitrate multiplied by duration and divided by eight, with numeric bounds. These are labelled **estimated**.
- Missing, conflicting or unusable statistics remain **size unavailable**. No extra packet extraction or media scan is added to compute display sizes.

Size descriptions carry no approval, matching, preservation or publication authority. Focused controlled tests cover malformed/conflicting statistics and MP4 estimates; a required real-media test exercises production stage reporting and owned scratch cleanup. Their actual results are reported with the delivery's executed suite, separately from this read-only sample.
