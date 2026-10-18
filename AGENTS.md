# Repository instructions

Read `PROJECT_BRIEF.md` before changing behavior. Work only on the explicitly authorized phase. Phase 1 covers one MKV workflow; batch processing, folders, presets, MP4 production support, playback, source replacement, and speculative frameworks belong to later tasks.

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
- Reserve private adjacent temporary storage and persist ownership before media writing. Publish only a verified candidate with a same-filesystem no-overwrite operation.
- Exit 1 is warnings, never automatic publication. Approval revalidates the existing candidate; it never remuxes again. Failed verification cannot be overridden.
- Preserve publication facts and primary failures independently from cleanup issues. Never delete a published final output during cleanup.
- Restart records do not authorize auto-publication, stale-PID termination, resume, or deletion by glob/prefix.

## Code and localization

Use readable names, short cohesive types, early returns, and braces. Keep data type definitions separate from behavior where practical. Explain each function/component briefly in English XML documentation. Avoid nested conditions and unnecessary abstractions. Keep every public-facing application string in aligned English/Russian resources; raw media metadata and tool diagnostics remain original. The initial language is English and persisted values are `en`/`ru`. Plans, CLI values, and serialized data are culture invariant. Language changes must not invalidate a plan or cancel an active job.

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
