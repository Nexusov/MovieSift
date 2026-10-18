# Architecture

TrackTrim is one desktop process with short-lived MKVToolNix workers. It is a modular monolith with four production projects:

```text
Desktop -> Application -> Core
Bootstrap -> Infrastructure -> Application / Core
```

Core contains immutable source/track descriptions, approved retention plans, and pure selection validation. Application coordinates inspection and one job through narrow media/filesystem/persistence ports. Infrastructure owns MKV-specific JSON/XML, argument arrays, worker processes, source revisions, safe output publication, and portable JSON. Desktop owns typed compiled XAML bindings, observable ViewModels, native file dialogs, localization, and the composition root. Only `Bootstrap` selects Infrastructure implementations; ViewModels receive use cases/ports.

## Inspection and planning

Inspection records an actual detected container, source revision, pinned backend/version, and file-local track IDs separately from Matroska UIDs. The inspector uses revision ownership to prevent a late result for an older selection replacing the current file.

Keep starts enabled for every audio/subtitle track. Video is always retained. Planning snapshots the inspected tracks and selections into an immutable plan; it rejects video removal and requires explicit confirmation when the last existing audio track is removed. Empty subtitle selection is normal. No-change plans produce a result without launching remuxing. Source/selection/destination changes invalidate the preview; UI-language changes only update presentation.

## Job ownership and publication

The Application job coordinator owns one approved plan, candidate, tool outcome, verification evidence, review decision, publication fact, and cleanup result. A gate prevents conflicting jobs. UI state disables conflicting edits while the job/review owns its plan.

The lifecycle is validate, reserve an adjacent private output directory, persist ownership, remux into its candidate, wait/drain, verify, recheck the source, then publish or await warning review. Exit 0 only permits verification. Exit 1 requires explicit approval/discard after all required verification succeeds. Other unsuccessful exits fail. Warning approval revalidates the existing candidate and source; it never remuxes again.

Publication checks pending cancellation before a short non-cancellable boundary. The filesystem move enforces no overwrite at the operation itself; an earlier existence check is only validation. Source identity/size/modification are rechecked before work and publication; those checks do not cryptographically prevent a concurrent edit. A successful final output is never removed by temporary cleanup. Report the actual published path even if a later cleanup fails or cancellation arrives.

Each expected failure carries a stable error code and structured diagnostic details. Tool exit, verification, publication/path, cancellation, and secondary cleanup issues remain distinguishable. A cleanup error cannot replace a primary failure. The UI translates its own messages, preserving original filenames/media metadata and raw tool diagnostics.

## Process boundary

Production executables are absolute app-relative paths. `ProcessStartInfo.ArgumentList` and `UseShellExecute=false` keep paths out of shell parsing. Both streams drain asynchronously even when retained diagnostics reach their bound; excessive/truncated structured data is a failure. Probe/startup operations have bounds; a valid large remux is not subject to a tiny fixed deadline.

Cancellation terminates and awaits the owned process, including verification operations. A failed termination is reported and prohibits deletion of a candidate that may still be written. Target-specific descendant termination and window-shutdown behavior require runtime acceptance; helper tests alone do not establish every OS behavior.

## Persistence and recovery

Application-owned settings, bounded local diagnostics, and versioned ownership records live in `data` beside the executable. English is the first-launch/default language; persisted languages are `en`/`ru`. JSON protocol values and plan semantics are culture invariant. An unwritable portable data location must be visible and prevent media writes that cannot be journaled safely.

Ownership records describe only temporary output and publication stages. On restart the application offers cleanup after validating ownership and only when the original owner is gone and no candidate exists. A recovered media candidate remains unresolved because parent death does not prove the former worker stopped writing; it is never automatically deleted. A new operation may start safely from the source. Old verification is never trusted for publication; stale PIDs are never killed; a matching final filename does not prove publication. There is no batch queue, automatic retry, byte resume, or deletion by glob.

## Verification level

Per-job structural and semantic metadata checks are separate from integration payload checks. The adapter compares the output to the approved retained mapping, deliberately accounts for permitted identifier/container-layout changes, checks XML chapters/tags where supported, and checks attachment metadata. Integration fixtures compare retained encoded payloads and attachment contents. See `SUPPORTED_FORMATS.md` for the concrete limits and `PLAN.md` for executed evidence.
