# Architecture

This document describes the current source tree. MovieSift is a desktop application that remuxes local MKV and supported MP4 files through bundled workers. It keeps encoded streams rather than re-encoding them. The [format reference](SUPPORTED_FORMATS.md) defines the preservation contract; [CONTRIBUTING](../CONTRIBUTING.md) covers builds and tests.

## Project boundaries

| Project | Responsibility | Dependencies |
| --- | --- | --- |
| `MovieSift.Core` | Media descriptions, immutable plans, selection, grouping and path policies | .NET base class library only |
| `MovieSift.Application` | Inspection, batch/job coordination, results and I/O ports | Core |
| `MovieSift.Infrastructure` | Media adapters, worker processes, filesystem operations and portable storage | Application and Core |
| `MovieSift.Desktop` | Avalonia views, ViewModels, localization and application composition | Application/Core; Infrastructure only in `Bootstrap` |
| `MovieSift.Updater` | External entry point for portable application replacement | Infrastructure |

`AppBootstrap` constructs the concrete adapters and injects them into the application. ViewModels do not build media commands, launch workers or publish files. Container-specific parsing stays in Infrastructure; pure selection policy stays in Core. Interfaces represent I/O or lifecycle boundaries rather than being added to every class.

## Workspace and review

One `BatchSession` serves one file or many. Its immutable snapshots hold each item's source revision, inspection, Keep choices, destination intent, approval and result. `BatchCoordinator` serializes inspection and execution; every media operation uses the same `JobCoordinator`.

Track IDs belong to a particular file and backend. They are distinct from Matroska UIDs, output indexes and UI rows. Audio and subtitle tracks initially have Keep selected; every video track is retained. Removing all subtitles is valid. Removing the last existing audio track requires consent for that file and selection revision.

A sole queue entry uses the shared file-local track editor directly. Multiple entries use the aggregate editor, even when only one is included; both presentations retain the same item editors and session authority.

The common list is the **union** of included inspections. `TrackGroupingPolicy` groups exact canonical language, title, codec/channels, forced status and semantic roles; unknown/conflicting metadata stays separate. Duplicates and per-file exceptions remain visible. Default/enabled flags are displayed separately.

Exclude/Restore buttons are a presentation of the existing Keep choices, not a second selection model. Excluded cards remain in their source/group order with semantic muted colors; their restore controls remain readable. Video is locked. Mixed-group exclusion targets editable members only.

The projection references authoritative session choices. Group, member and remove-all-subtitles edits validate the whole request before applying it atomically. Actual edits invalidate affected plans and Review; no-op edits preserve them. Completed results stay locked.

**Review** creates independent immutable plans and validates every included unfinished item. Unresolved included files block Run. A plan captures source/backend identity, retained tracks, destination, output mode and relevant consent. Source, selection or output changes require another Review; language and appearance changes do not. Review neither runs workers nor creates output directories. Its compact projection lists actual removed identities and retained category counts with expandable details. The separate Run action remains outside the scrolling rows and consumes a current review once. Visible Copy/Replace cards delegate to the existing output-mode edit path, including approval invalidation and replacement confirmation.

NoChange still validates the source and selection, but skips remuxing and destination-only checks. It neither creates a copy nor replaces the original. Other jobs must still avoid its source path, just as they must avoid excluded sources.

Warnings, errors, cancellation or unresolved cleanup pause sequential dispatch. Warning decisions stay attached to the actual item/job even if another row is selected. Continuing requires explicit review/start. A new queue cannot discard unresolved ownership; a nonempty reset requires confirmation and creates a fresh session revision.

Inspection publishes typed backend-stage observations through `InspectionService` and `BatchCoordinator`. Revision, cancellation and item checks reject stale observations. A Desktop-only monotonic timer displays elapsed time; it does not infer progress or remaining time from worker timeouts and does not change process ownership.

## Discovery and destinations

`FileBatchInputDiscovery` accepts explicit local files or a folder, with recursion off by default. It deduplicates canonical paths and file identities and returns a stable order. Suffixes filter candidates; inspection establishes the actual container.

A scan allows 100 entries (including visible failures), 10,000 examined filesystem entries and 32 nested directories. Each visited directory is checked for network mounts and redirection. Traversal errors or limits reject the whole addition.

Folder items retain their selected root and relative path. Recursive Copy can preserve that structure beneath a separate output root and excludes its subtree. Adjacent Copy/ReplaceSource need no separate root. Working-directory exclusions require actual portable paths or validated ownership, not filename prefixes.

Discovery and Review are read-only. Only a changed, approved Copy plan with an explicit output root may create missing output parents. Infrastructure prepares and revalidates those paths immediately before reservation. Empty parents may remain after failure; cleanup never recursively deletes a user's output tree.

## Selection rules and presets

`LanguageRuleMatcher` independently retains all matches per file/category, or leaves a category unchanged. It normalizes language aliases without rewriting metadata and preserves regional/script/private distinctions. Unknown/conflicting targeted metadata or no audio matches requires manual review.

Optional title matching is a trimmed case-insensitive literal substring, ANDed with language. Forced-only selection uses inspected subtitle flags. Neither constraint bypasses language validation.

Preview captures an immutable proposal without editing the session. Apply rejects a stale/substituted proposal and atomically changes only reliable, changed rows. Rules never approve plans or start media work, and new inputs do not inherit old selections automatically.

### Rule presets

`RulePresetService` supports Save as new, explicit Load, Rename and Delete. Presets store configuration, including inactive values, but no media/workflow state. Selection does not load; a changed Load clears Preview and an identical Load preserves it.

The catalog validates names, values and JSON depth within 100 presets and 65,536 UTF-8 bytes. Invalid records remain untouched while manual selection stays available. Mutations lock and reread the catalog before updating it.

## Media adapters and verification

`ContainerMediaBackend` selects an adapter during inspection. Execution uses the container and pinned backend captured in the plan; there is no fallback, transcoding or container conversion.

| Adapter | Processing | Per-job verification |
| --- | --- | --- |
| MKVToolNix | `mkvmerge` with explicit retained IDs/order and effective flags | Track mapping/properties, title, semantic chapter/tag XML, attachment metadata and frame timestamps/counts |
| FFmpeg/ffprobe | Explicit source-index maps and stream copy for every retained stream | Approved mapping, codec initialization, admitted metadata/dispositions/chapters, streamed packet payload hashes and rational timing |

MKV's bounded Segment/Info reader supplements identification with linkage checks. Chapter/tag XML disables DTDs and external resolution; language comparison uses an explicit alias registry. Retained MKV encoded bytes and attachment payloads are compared in fixtures, not hashed by every job.

MP4 capability inspection uses a narrow box reader and bounded ffprobe output. Unsupported representations fail before processing. At most one distinct referenced QuickTime chapter track is admitted. Artwork is retained and checked by encoded-byte identity rather than a fabricated timeline. A candidate-only fixup restores the supported `tkhd` enabled/in-movie/in-preview bits after checking the new file's identity and mapping; it never writes to the source.

Whole-container byte equality is not expected after remuxing. Timing exceptions, supported metadata and rejected features are detailed in [SUPPORTED_FORMATS](SUPPORTED_FORMATS.md). Controlled failure tests, real-media payload tests, headless UI checks and native desktop checks establish different things.

### Worker processes

Executables are absolute, application-relative paths. Commands use `ProcessStartInfo.ArgumentList` and `UseShellExecute=false`; user paths never pass through a shell. Both output streams drain continuously while retained diagnostics and structured records remain bounded. Truncated/malformed machine output fails verification.

Cancellation terminates and awaits the owned worker, including inspection/verification processes. If termination cannot be confirmed, its candidate remains protected from deletion. Probe/startup operations have time bounds; valid large remuxes are not cut off by a small fixed timeout. Closing the UI awaits safe shutdown or exposes the unresolved decision.

On Linux, cancellation captures descendants from the owned worker's procfs task/children records before the existing tree-kill request. PID, parent and birth identities are checked; captured descendants must disappear, change birth identity, or enter a non-writing Z/X state before cleanup. Observation is bounded to 256 descendants, 4,096 tasks, 16 KiB per record and the existing 15-second termination budget. Unreadable or incomplete ownership evidence preserves the candidate instead of treating pipe EOF or parent exit as proof that descendants stopped. This observer sends no additional signals.

## Job ownership and publication

`JobCoordinator` is the single owner of the plan, process, candidate, verification, warning decision, publication fact and cleanup result:

1. Recheck the source revision and validate the destination.
2. Reserve a private adjacent directory and record its ownership before media writing.
3. Remux into the owned candidate, wait for worker exit and drain diagnostics.
4. Verify the candidate, then recheck source, candidate and ownership.
5. Handle warnings, check cancellation and enter the short publication boundary.
6. Report the actual result and clean only validated owned temporary files.

Reservation retains identities for its partially created directory, marker and journal. Failure removes only resources still owned by that attempt; changed or foreign paths remain intact. A cleanup error accompanies the primary failure rather than replacing it.

MKVToolNix exit 1 requires explicit Publish with warnings or Discard after successful verification. Approval revalidates the existing candidate; it does not remux again. Failed verification cannot be overridden. FFmpeg nonzero exits and unrecognized/truncated warnings fail; only its documented pinned warning cases can proceed. Raw tool results remain available separately from normalized outcomes.

Free-space checks are advisory: remuxing needs a complete candidate alongside the source, while publication reuses that allocated file. An existence check alone never authorizes overwriting. Once publication is known, late cancellation or cleanup failure cannot erase the published path or delete the result.

### Output choice and source replacement

Copy is the default and creates an adjacent `.trimmed.mkv` or `.trimmed.mp4`, unless a separate destination is selected. Its same-filesystem move enforces no-overwrite at the operation itself. Every other source and output in the session is protected against collisions.

`OutputMode.ReplaceSource` is explicit immutable plan data and targets only that plan's exact source path. Remuxing and verification keep the source read-only. After fresh checks, the job records the intended commit and calls `File.Replace` with an owned backup. Publication is confirmed only when the inspected original is recognizable at the backup path and the verified candidate at the final path.

A durable published record permits backup cleanup after another identity check. An interrupted or uncertain commit retains its backup and journal for manual review; a missing candidate alone does not authorize deletion. A known published result is reported even if final journal writing or cleanup fails.

Read-only or multiply linked sources cannot be replaced. Linux preserves ordinary permission bits and requires matching owner/group; special bits and extended POSIX access ACLs are rejected. Other filesystem metadata has no blanket preservation guarantee. Revision checks do not exclude arbitrary external writers, and replacement is not a filesystem-wide or universal power-loss transaction.

## Portable storage and recovery

Application-controlled state and bounded logs live in `data` beside the executable:

| Record | Content |
| --- | --- |
| `settings.json` | UI language |
| `translations/<locale>.json` | Validated downloaded UI text; no media or workflow authority |
| `appearance.json` | Independent appearance preferences |
| `rule-presets.json` | Named rule configurations |
| `queue-list.json` | Unfinished listing intent |
| `updates.json` | Startup update-check preference |
| `ownership/*.json` | Media candidate ownership and publication stages |

Missing settings use defaults. Malformed records remain visible rather than being silently repaired. JSON readers validate regular-file handles before reading or checking length, then enforce size/schema/depth limits. Catalog and queue writes use permanent interprocess locks and flushed, uniquely owned sibling temporary files for replacement. Cleanup never adopts a later occupant of a temporary path.

Unavailable ownership storage disables media writes. An isolated language or appearance save failure instead reports a session-only preference; it does not invent a media-ownership failure.

### Interface translations and window presentation

The language chooser and appearance panel are separate, mutually exclusive cosmetic views. `LanguageSettingsViewModel` owns explicit download/cancel/retry intent; `LocalizationService` publishes a complete immutable dictionary only after validation and preserves full locale identifiers. Existing shell preference writes and shutdown remain authoritative. A translation request counts as an active shell operation so close cancels and drains it before disposing HTTP resources. It owns no media worker, approval or queue revision.

`ITranslationPackStore` is a narrow Application port. `GitHubTranslationPackStore` resolves the small official `git/ref/heads/main` response, then reads catalog and pack from that one immutable commit. Fixed locale filenames, exact SHA256/length, complete English key coverage and composite-format placeholders are checked before caching. Responses are bounded (8 KiB reference, 64 KiB catalog, 256 KiB pack), depth-limited and subject to a 30-second deadline. Redirects and unsupported locales are rejected. Cache access uses regular-file/reparse checks, interprocess exclusion and owned temporary replacement. Invalid files remain untouched until an explicitly requested, fully validated replacement succeeds. Startup reads only local cache and falls back to English with a visible notice.

English is built in; `package.ps1` removes only the original application's Russian satellite from portable output. All 11 downloadable source packs and their exact-byte catalog live under [translations](../translations/README.md); translated binaries or executable resources are never downloaded. A complete key set is a compatibility check, not a native-speaker quality certification or independently signed authenticity proof. A new incompatible English resource contract requires corresponding repository packs; old cached incompatible data is rejected visibly.

The Desktop project embeds the original film/scissors SVG, PNG and multi-resolution Windows ICO. Windows uses an integrated client caption over a resizable border; Linux keeps native decorations. Caption Close calls the existing `Window.Close()` lifecycle, and localization/appearance affect presentation only. Headless caption/layout tests are not native Windows resizing, snap or scaling acceptance.

Ownership schema 3 binds output mode and replacement source/candidate revisions; legacy schemas 1/2 allow only Copy and their original layouts. Restart validates the record, paths, marker, stage and process identity. Corrupt records authorize no action; a saved PID cannot authorize termination, nor a matching filename prove publication.

A remaining candidate is unresolved after restart because parent death does not prove worker exit. Cleanup is offered only when validated ownership/stage and process/file evidence allow it; replacement cleanup additionally requires its confirmed published record. Restart never automatically publishes, retries or resumes bytes.

### Unfinished queue-list recovery

The queue stores paths, inclusion, folder/output-root and output-mode intent. It excludes completed/NoChange or known published results and contains no inspections, selections, consent, approvals or process authority.

Restore requires an empty safe session and creates uninspected identities for fresh selection and Review. Discard empties only the listing. A stale Copy entry cannot overwrite an existing output; restored replacement needs fresh approval of the current source.

Schema 2 reads legacy schema 1 as Copy. Records are limited to 100 entries, 65,536 UTF-8 bytes and bounded paths/depth. Writes compare the expected revision under the interprocess lock, distinguishing Busy, Conflict and malformed records. Desktop coalesces checkpoints and awaits pending writes before Run/shutdown. Listing failures remain separate from media cleanup failures.

## Desktop presentation

Avalonia uses typed compiled bindings and CommunityToolkit.Mvvm commands. Responsive views and local-file drop share the existing workspace/discovery path. Structured Review displays actual plans/failures with separate Review/Run controls; warning, recovery and cleanup decisions remain accessible.

English is the first-launch language. All application strings and formatting arguments have aligned English/Russian resources; filenames, media metadata and raw diagnostics remain original. Switching language changes presentation without rebuilding plans or interrupting work.

Appearance uses semantic light/dark resources, its own keyboard scope and render-only motion. Palettes require 4.5:1 text and 3:1 essential-indicator contrast; invalid edits keep the last usable palette. System motion follows the supported Windows preference, otherwise uses reduced motion. Cosmetic changes preserve the workspace and do not delay cleanup.

Headless rendering and input tests do not establish native file-dialog behavior, OS scaling or target desktop compatibility.

## Portable application updates

Startup can asynchronously check the public stable GitHub release. Disabled checks, network failure or invalid responses do not block media use. A newer compatible release opens a dismissible offer; downloading and installing require an explicit action.

Staging validates archive size/hash, bounded extraction paths, the complete package manifest, runtime and assembly version. Linux internal file aliases become verified regular files; external links and special entries are rejected. The current bundled helper/runtime is copied to adjacent storage and checked before launch.

The shell waits for saves and refuses installation during media work or unresolved warning/recovery/cleanup decisions. A shared application lease prevents installation while a cooperating instance is alive. The external helper waits for the exact parent process to exit, takes the exclusive lease, preserves portable data and unknown user files, then moves the old tree to backup and the prepared tree into place. Conflicts fail before switching.

Migration preserves ordinary Linux permissions on the application root and retained files/directories, requires unchanged UID/GID, and rejects extended access/default ACLs, special bits or uninspectable permissions. It does not change ownership or request elevation.

On Windows, current allow/deny DACL entries become a protected snapshot, including inherited entries converted to explicit rules. Owner/group must match. EFS-encrypted data, noncanonical ACLs and unpreservable rules are rejected. Later parent permission changes no longer propagate automatically; SACL audit metadata is outside this contract.

A failed switch rolls back where possible. The restarted desktop acknowledges initialization; an exited, unacknowledged new process permits rollback, while a still-running one is not killed or overwritten. The old tree and recovery report remain in the update session. Two directory moves are not one atomic operation; power loss between them may require manual restoration. Version 1.0.0 predates the updater, so the first updater-enabled package must be installed manually.

See [distribution and licensing](DISTRIBUTION.md) for package contents and matching source/build materials.
