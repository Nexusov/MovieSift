# Architecture

TrackTrim is one desktop process with short-lived MKVToolNix workers. It is a modular monolith with four production projects:

```text
Desktop -> Application -> Core
Bootstrap -> Infrastructure -> Application / Core
```

Core contains immutable source/track descriptions, approved retention plans, and pure selection validation. Application coordinates inspection, single jobs and sequential batch state through narrow media/filesystem/persistence ports. Infrastructure owns MKV-specific JSON/XML, argument arrays, worker processes, source revisions, bounded discovery, safe output publication, and portable JSON. Desktop owns typed compiled XAML bindings, observable ViewModels, native file dialogs, localization, and the composition root. Only `Bootstrap` selects Infrastructure implementations; ViewModels receive use cases/ports.

## Inspection and planning

Inspection records an actual detected container, source revision, pinned backend/version, and file-local track IDs separately from Matroska UIDs. The inspector uses revision ownership to prevent a late result for an older selection replacing the current file.

The pinned JSON protocol is supplemented by a narrow read-only EBML Segment/Info traversal because it omits SegmentFamily. The reader checks linkage only at defined Info-child boundaries, seeks past finite payloads, bounds header count/lengths, and rejects ambiguous layouts. An unknown-sized Segment is bounded by EOF; unknown-sized clusters and repeated/nested document structures are unsupported. A shared regular-file opener checks the opened handle, including Linux nonblocking/no-follow open and `fstat`, before creating a stream. No third media executable is introduced.

Keep starts enabled for every audio/subtitle track. Video is always retained. Planning snapshots the inspected tracks and selections into an immutable plan; it rejects video removal and requires explicit confirmation when the last existing audio track is removed. Empty subtitle selection is normal. No-change plans produce a result without launching remuxing. Source/selection/destination changes invalidate the preview; UI-language changes only update presentation.

## Job ownership and publication

The Application job coordinator owns one approved plan, candidate, tool outcome, verification evidence, review decision, publication fact, and cleanup result. A gate prevents conflicting jobs. UI state disables conflicting edits while the job/review owns its plan.

The lifecycle is validate, reserve an adjacent private output directory, persist ownership, remux into its candidate, wait/drain, verify, recheck the source, then publish or await warning review. Exit 0 only permits verification. Exit 1 requires explicit approval/discard after all required verification succeeds. Other unsuccessful exits fail. Warning approval revalidates the existing candidate and source; it never remuxes again. A close request during warning review offers remain-in-review or discard-and-close. Repeated close/discard requests are gated; the window awaits the one cleanup operation and remains open on cleanup failure.

The initial destination validation requests an advisory additional allocation of the source length. Publication rechecks path safety, aliases and collisions with zero additional media allocation: the adjacent verified candidate already occupies its space, and rename does not require a second source-sized payload. Capacity advice is not a reservation; actual filesystem/tool allocation failures remain failures.

Publication checks pending cancellation before a short non-cancellable boundary. The filesystem move enforces no overwrite at the operation itself; an earlier existence check is only validation. Source identity/size/modification are rechecked before work and publication; those checks do not cryptographically prevent a concurrent edit. A successful final output is never removed by temporary cleanup. Report the actual published path even if a later cleanup fails or cancellation arrives.

Each expected failure carries a stable error code and structured diagnostic details. Tool exit, verification, publication/path, cancellation, and secondary cleanup issues remain distinguishable. A cleanup error cannot replace a primary failure. The UI translates its own messages, preserving original filenames/media metadata and raw tool diagnostics.

## Phase 2A batch boundary

`FileBatchInputDiscovery` implements one filesystem port for explicit files or one top-level folder. It applies the existing local regular-file/reparse policy, canonical path and file-identity deduplication, deterministic ordinal order, cancellation and independent file/entry bounds. The MKV suffix is only a discovery filter; `InspectionService` still probes every candidate through the pinned backend. Invalid entries stay visible; a limit rejects the whole addition. No recursive scan or runtime media-tool acquisition is added.

`BatchSession` publishes immutable snapshots keyed by generated item IDs and full source paths. Every item owns its inspection/revision, current retained IDs, approval, inclusion and complete job result. Edits invalidate the aggregate review and only the affected file's plan. `BatchReviewValidator` checks all included remaining approvals against current source revisions and destination policy, including destinations shared by multiple rows or matching any source. A frozen review is consumed once. The single-job coordinator repeats its existing checks at work/publication time.

`BatchCoordinator` serializes probes and dispatches each approved plan to the existing `JobCoordinator`; it does not implement another remux/verification/cleanup lifecycle. One shared job owner serves both tabs, with separate single/batch inspection revision domains. Warnings, failures, cancellation and cleanup/termination uncertainty pause dispatch. Warning actions target the frozen item/job owner, even after changing the visible row/tab. They leave the queue paused. Explicit re-review/start is required for remaining work; a safety block cannot be erased by excluding a row. No-change plans reach the existing no-remux result.

`BatchViewModel` owns one awaitable UI operation and cancellation source, with stable lightweight `BatchItemViewModel` editors. Switching rows or language does not reconstruct another file's selections/plans. Progress is checked against operation generation, item and plan identities. Native dialogs and presentation stay in Desktop. Cancel/status controls remain outside the scrolling batch review. The window aggregates ownership across both tabs, awaits shutdown/discard once, and preserves cleanup failures. Unexpected escaped lifecycle exceptions conservatively block further dispatch rather than assuming worker ownership was released.

The queue is in memory only. Ownership journals describe individual temporary outputs, not queue persistence or permission to retry. Rules require explicit preview/application; there is no automatic application to later inputs, recursive discovery, parallel remuxing, persisted queue, automatic retry or byte-offset resume.

`BatchCoordinator.StartNewBatch` acquires the existing operation gate and atomically replaces a safe session with an empty snapshot at the next review revision. It rejects busy or unresolved warning/cleanup/ownership state without changing the old snapshot and performs no I/O. Desktop requires confirmation for a nonempty list and binds consent to that revision; declining preserves presentation. Successful reset clears editors, review diagnostics and progress while retaining language and the output folder. Old editor/progress callbacks are rejected by the existing item, plan and operation identities. The same source may be added again, but receives a new inspection and explicit approvals; existing files and collision checks remain intact.

## Phase 2B1 language-selection boundary

Core's pure `LanguageRuleMatcher` produces an immutable proposal against one inspection and its current retained IDs. Independent audio/subtitle modes default to unchanged, or retain all exact canonical language matches. The shared frozen ISO table supplies comparison aliases; Infrastructure's preservation policy retains its existing stricter suffix/unknown rules. Matching does not invoke tools or change metadata. Targeted ambiguous metadata or no audio matches leaves the whole file unchanged with explicit issues; no subtitle matches may remove subtitles. The supported tag grammar is documented in `SUPPORTED_FORMATS.md`.

`BatchCoordinator.PreviewLanguageRules` captures the session revision and every item/inspection/current selection without editing the session. All rows remain visible, including excluded, completed, failed or uninspected rows. Application keeps the issued immutable preview identity and rejects stale or substituted application under the existing operation gate and session lock. Applying reliable changed rows is one atomic selection edit: only affected approvals are cleared, aggregate Review is invalidated, and unrelated rows/results remain exact. A no-op preserves approval/review identities. Manual editing uses the same `SetSelection` path; zero-audio consent remains a per-file Desktop decision and is cleared on actual choice changes.

`BatchLanguageRulesViewModel` owns transient rule editing and per-file preview presentation; it does not own jobs. Compiled bindings and aligned EN/RU resources expose explicit Preview/Apply commands. Relevant edits or New batch clear the proposal, delayed notifications re-read the live session, and language/row switches preserve configuration and a valid proposal. New inputs are never automatically matched or approved. Busy/warning/unsafe ownership and conflicting single-file operations use the existing availability gates.

## Process boundary

Production executables are absolute app-relative paths. `ProcessStartInfo.ArgumentList` and `UseShellExecute=false` keep paths out of shell parsing. Both streams drain asynchronously even when retained diagnostics reach their bound; excessive/truncated structured data is a failure. Probe/startup operations have bounds; a valid large remux is not subject to a tiny fixed deadline.

Cancellation terminates and awaits the owned process, including verification operations. A failed termination is reported and prohibits deletion of a candidate that may still be written. Target-specific descendant termination and window-shutdown behavior require runtime acceptance; helper tests alone do not establish every OS behavior.

## Persistence and recovery

Application-owned settings, bounded local diagnostics, and versioned ownership records live in `data` beside the executable. English is the first-launch/default language; persisted languages are `en`/`ru`. JSON protocol values and plan semantics are culture invariant. An unwritable portable data location must be visible and prevent media writes that cannot be journaled safely.

Ownership records describe only temporary output and publication stages. Bounded JSON reads first use the shared regular-file handle check, so a FIFO named as a record, marker or settings file cannot block startup before size validation. Deserialized nullable records are untrusted: version, stage, process ID/start ticks, nonempty operation ID, complete hexadecimal token, canonical local paths, exact adjacent layout/record filename and matching marker are validated before dereferencing or using their evidence. Malformed JSON and missing/null/invalid fields produce visible `InvalidRecord` entries with the raw record path and manual-review guidance. They never authorize cleanup, deletion or process termination; original record/marker/candidate files are retained. Settings failure does not prevent an independent ownership scan.

On restart the application offers cleanup after validating ownership and only when the original owner is gone and no candidate exists. A recovered media candidate remains unresolved because parent death does not prove the former worker stopped writing; it is never automatically deleted. A new operation may start safely from the source. Old verification is never trusted for publication; stale PIDs are never killed; a matching final filename does not prove publication. The in-memory batch is not recovered. There is no automatic retry, byte resume, or deletion by glob.

## Verification level

Per-job structural and semantic metadata checks are separate from integration payload checks. The adapter compares the output to the approved retained mapping, deliberately accounts for permitted identifier/container-layout changes, checks XML chapters/tags where supported, and checks attachment metadata. One explicit ISO alias table serves track/chapter/tag comparison without culture-dependent rewriting. A narrow AAC-LC policy can recognize a newly inferred 1024-sample DefaultDuration from unchanged codec configuration; known durations, codec properties and independent timestamp/count checks remain strict.

Integration fixtures compare retained VP8/PCM/SRT and direct FFmpeg-origin H264/AAC/SRT payloads, attachment contents and original source hashes. The real batch case permutes one source's track order/IDs and independently retains different content from each input, comparing every retained payload and both original hashes. Controlled synchronization tests cover queue overlap, late changes, warning/failure/cleanup pauses and cancellation boundaries. Real cancellation evidence retains the observed worker process handle, proves it is active at cancellation and then awaits its exit; buffered progress from an exited worker is insufficient. Controlled ports/process helpers, headless UI and native desktop acceptance remain separate forms of evidence. See `SUPPORTED_FORMATS.md` for the concrete limits and `PLAN.md` for executed results.
