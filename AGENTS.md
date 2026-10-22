# Repository instructions

Read `PROJECT_BRIEF.md` before changing behavior. Work only on the explicitly authorized phase. Phase 2A adds bounded explicit-file/top-level-folder discovery, independent manual plans and a sequential in-memory queue over the existing single-job lifecycle. Phase 2B adds explicit language/optional-title/forced-subtitle preview and application before those approvals. Phase 2C1 adds named portable rule presets with Save as new, explicit Load, Rename and confirmed Delete. Phase 2D1 adds default-off recursive folder discovery with relative output directories beneath an explicit existing output root. Phase 2 completion adds minimal saved unfinished listings and explicit Restore/Discard with fresh inspections and approvals. Preset overwrite/update, import/export, automatic loading, title-only/regex rules, other forced-filter modes, live/persistent job authority, byte-offset resume, MP4, playback, source replacement and speculative frameworks remain outside this task.

## Architecture

- Core is pure BCL-only media/planning policy. No files, processes, UI, persistence, or logging.
- Application owns inspection/job use cases and narrow I/O ports. It has no Avalonia or CLI argument construction.
- Infrastructure implements the ports: MKVToolNix, process ownership, files, JSON settings, and ownership records.
- Desktop owns Views, ViewModels, resources, and UI interaction. Its Infrastructure reference is used in `Bootstrap` only. ViewModels must not launch processes or publish media.
- Use explicit constructor injection, immutable workflow snapshots, nullable reference types, and stable error codes. Add interfaces only for meaningful boundaries.

## Safety invariants

- Never open source media for writing, overwrite/delete it, or replace it in these phases.
- Keep every video track. Audio/subtitle selections mean Keep and begin selected. Confirm zero audio; no-change plans never remux.
- Tool paths are absolute and app-relative in production. Use `ArgumentList` and `UseShellExecute=false`; never interpolate media paths into a shell.
- Drain both streams, bound retained diagnostics and structured data, and reject truncated/malformed JSON/XML. XML resolution/DTDs stay disabled.
- One owner controls process, candidate, verification, publication, and cleanup. Cancellation must reap the process; uncertain termination prohibits candidate deletion.
- Folder discovery must enforce global 100-entry/10,000-examined-entry/depth-32 bounds, reject incomplete scans transactionally, and never follow redirected directories. Apply the complete existing local-directory policy, including network-filesystem classification, before enumerating every non-excluded directory. Preserve immutable selected-root/relative-path provenance; exclude the output subtree and actual known working/owned locations, never guessed media-name suffixes. Discovery/review never creates directories. Only an approved explicit output root permits missing output parents; Infrastructure prepares them immediately before reservation and retains empty parents after failure/cancellation.
- Batch orchestration must reuse that owner, run at most one job, retain file-local identities and immutable approvals, and pause on warning, failure, cancellation or cleanup uncertainty. Never infer a selection from another file's IDs or resume automatically after warning review.
- Reserve private adjacent temporary storage and persist ownership before media writing. Publish only a verified candidate with a same-filesystem no-overwrite operation.
- Exit 1 is warnings, never automatic publication. Approval revalidates the existing candidate; it never remuxes again. Failed verification cannot be overridden.
- Closing warning review requires an explicit remain/discard choice and awaits one owned cleanup; repeated close requests must not race it.
- Preserve publication facts and primary failures independently from cleanup issues. Never delete a published final output during cleanup.
- Restart records do not authorize auto-publication, stale-PID termination, resume, or deletion by glob/prefix. Queue-list storage contains only unfinished path/inclusion/provenance intent, never inspections, backend IDs, selections, approvals or process authority. Restore is explicit and creates fresh uninspected identities; Discard replaces only listing data. Ownership journals remain independent. CAS under interprocess exclusion prevents stale queue writers, and corrupt records must remain visible and untouched. Queue checkpoint errors are not media-ownership errors.
- Check JSON records/markers/settings through a regular-file handle before length/read operations. Treat every deserialized ownership field as untrusted; validate complete version/identity/stage/path/marker evidence before use. Invalid records remain visible and never authorize deletion.
- Keep Segment Info linkage checks bounded and schema-position-aware, using a regular read-only handle. Unknown-sized children or ambiguous document boundaries fail closed; do not substitute raw byte searching or a third runtime tool.
- Preserve every actual frame timestamp/count and known terminal boundary. Any AAC missing-duration exception requires the explicit bounded AAC-LC proof; do not ignore the extractor's last line or relax other codecs/known durations.

## Code and localization

Use readable names, short cohesive types, early returns, and braces. Keep data type definitions separate from behavior where practical. Explain each function/component briefly in English XML documentation. Avoid nested conditions and unnecessary abstractions. Keep every public-facing application string in aligned English/Russian resources; raw media metadata and tool diagnostics remain original. The initial language is English and persisted values are `en`/`ru`. Plans, CLI values, and serialized data are culture invariant. Language changes must not invalidate a plan or cancel an active job.

Track/chapter/tag language equivalence uses the shared explicit ISO registry table. Preserve regional/script/private/unknown distinctions; do not infer media-language identity through OS cultures.

Language-selection rules normalize aliases only for matching and compare complete supported tags exactly. Preview is read-only and revision-bound; Apply uses each item's actual inspection and current selection. Unknown/conflicting targeted metadata or no audio matches leaves that whole file unchanged for manual review. Only actual choice changes invalidate approvals; no operation implicitly approves, excludes or runs a file. Keep matching policy separate from preservation verification.

Title constraints default off and are ANDed with language matches. Trim search whitespace and use literal `OrdinalIgnoreCase` substring matching against the actual inspected title only. Enabled blank searches are invalid; missing/blank titles do not match. Evaluate every targeted language even when its title does not match. Configuration edits discard only the transient preview, never selections or approvals.

The subtitle-only forced constraint defaults off, is ignored in unchanged mode, and is ANDed with language/optional title using inspected `MediaTrack.IsForced`. Validate every targeted language even on non-forced tracks. Never infer forced status from titles, default flags or subtitle text; this selects whole tracks and does not promise player behavior. No combined subtitle matches explicitly proposes none. Reuse the existing proposal identity, ownership gates and changed-only Apply lifecycle.

Presets contain rule configuration only, including inactive values; never media paths, track IDs, selections, approvals, queue/job state or UI language. Selection alone must not load. Compare configuration content, including ordered language values and literal title text, rather than immutable-array identity. Changed explicit Load clears only transient Preview; identical Load preserves it. Keep catalog errors visible without blocking manual rules or overwriting malformed data. Catalog mutations reread and validate the complete bounded file under fail-fast interprocess exclusion, then flush a uniquely owned sibling temporary file before replacement. Delete only an operation's still-owned temporary path; preserve primary, committed and cleanup outcomes separately.

## Verification and delivery

```powershell
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --no-restore
pwsh ./build/acquire-tools.ps1 -RuntimeIdentifier win-x64 -Destination ./tools
pwsh ./build/package.ps1 -RuntimeIdentifier win-x64
```

Run the corresponding Linux commands on Linux. Tool acquisition is explicit, pinned, and hash checked. CI's media/package job is required to fail if provisioning fails; do not skip its backend suite to make it green. Consult `CONTRIBUTING.md` for fixture and environment setup. Record exact executed commands, versions/results, and pending acceptance in `docs/PLAN.md`. Defined CI and cross-publication are not runtime proof. Do not claim packet integrity from stream counts or source preservation without an actual check.

Inspect Git status and applicable instructions before editing. Preserve unrelated work. Make small coherent commits and review diffs. Push/PR actions require session authorization; never merge, force-push, rewrite pushed history, publish releases, or push task work to an existing default branch without explicit authorization.
