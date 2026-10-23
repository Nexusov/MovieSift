# TrackTrim — Product and Engineering Brief

Status: reviewed implementation baseline; no implementation or runtime acceptance is claimed.
Reviewed: 2026-10-18.
Working name: TrackTrim. Reuse an existing repository's established name.

## 1. Product and delivery scope

Build a maintainable, open-source Windows/Linux desktop application that removes unwanted embedded audio and subtitle tracks from local video files. The complete product accepts one file, several files, or a folder containing a television series. Its central feature is understandable, safe selection across files.

Use remuxing: copy the retained encoded streams into a new file without re-encoding. Preserve the input container type in supported workflows. Implement application logic around existing command-line tools; do not implement codecs, a muxer, or a media-player engine.

The first public release is intended to support a documented subset of MKV and MP4, multiple-file/folder input, manual selections, reusable rules, preview, a sequential queue, cancellation, and new-file output. The initial implementation task covered **Phase 0 and Phase 1 only**. Later explicitly authorized deliveries completed the Phase 2/3 implementation; the current finite release-candidate scope is recorded in `docs/PLAN.md`. Roadmap requirements alone do not authorize new features or public publication.

The application must ship as portable archives, without an installer, administrator rights, separately installed .NET, or separately installed media tools on the declared supported systems. English and Russian are mandatory UI languages. The first launch uses English regardless of operating-system language. Users can switch languages in the running application and persist their choice.

Never modify or delete original media in these phases. Producing a smaller second file does not itself free disk space while the original remains. Source replacement is a separate future feature requiring a recoverable design.

Out of scope: burned-in subtitle removal, separating sounds already mixed into one track, DRM handling, downloading, playback, transcoding, cropping, subtitle editing, media-library cataloguing, accounts, telemetry, auto-updaters, plugins, and concatenating episodes.

## 2. Technology baseline

- **C# / .NET 10 LTS.** Pin an actual supported SDK in `global.json`, disallow previews, and record the runtime actually shipped. SDK and runtime version numbers are different. Self-contained runtime security updates require rebuilding and redistributing the application.
- **Stable Avalonia 12 with XAML and the Fluent theme.** Avalonia 12.1.4 was present on the official NuGet page at review time. Verify availability and compatible versions when implementing; align related Avalonia packages. Do not use prereleases or change the major framework without a concrete blocker.
- **CommunityToolkit.Mvvm.** Use observable state, asynchronous commands, and compiled bindings with explicit data types. Version 8.4.2 was present at review time. Do not add another MVVM framework.
- **MKVToolNix for MKV.** Use `mkvmerge` for identification/remuxing and `mkvextract` from the same pinned distribution for chapter/tag inspection and appropriate verification. `mkvpropedit` cannot replace physical track removal. Do not substitute an FFmpeg-based MKV implementation.
- **FFmpeg / ffprobe for MP4 in Phase 3.** They may be explicit development-only fixture/verification tools earlier. A Phase 1 runtime must not secretly require them, and production archives must not include unused tools merely for future work.
- **System.Text.Json** for portable settings and small versioned records. Phase 1 needs settings and temporary-output ownership records. Presets and persisted batch queues belong to Phase 2. No database initially.
- **Explicit constructor injection and one composition root.** Microsoft.Extensions.DependencyInjection is optional if it simplifies real wiring. No service locator, runtime assembly scanning, or custom DI framework.
- **Serilog with a maintained file sink** for bounded local diagnostics at the application boundary. Keep logging out of the pure Core project. Do not create another logging framework or remote sinks.
- **xUnit**, a compatible **Avalonia.Headless** integration, .NET SDK analyzers, `.editorconfig`, `dotnet format`, central package versions, locked dependency restore, and GitHub Actions. Use ordinary assertions initially.

Every dependency must have a current use, compatible licensing, and an identifiable maintained source. Record stable versions, authoritative URLs, and tool provenance; pin the implementation instead of leaving floating package versions. A high-level FFmpeg wrapper is unnecessary for this process boundary.

## 3. Architecture and code quality

Use one desktop application with four small production projects. External media processes are short-lived workers. This is a modular monolith, not a service architecture.

| Project | Responsibility | Dependency rule |
| --- | --- | --- |
| `TrackTrim.Core` | Media descriptions, selections, immutable plans, pure policy and validation | Base class library only; no UI, processes, filesystem operations, persistence, or logging |
| `TrackTrim.Application` | Inspection and job use cases, lifecycle coordination, outcomes, narrow I/O ports | Depends on Core; no Avalonia or media-tool commands |
| `TrackTrim.Infrastructure` | Backend adapters, tool DTOs, process ownership, filesystem publication, JSON persistence | Implements Application ports; depends inward on Application/Core |
| `TrackTrim.Desktop` | Views, ViewModels, localization, UI interaction, composition | Uses Application/Core; an Infrastructure reference is permitted for `Bootstrap` composition only |

Create corresponding test projects when they contain meaningful tests. Keep build scripts under `build` and documentation under `docs`. Organize code inside projects by current features such as inspection, planning, jobs, and media tools. Do not create empty folders, speculative modules, or test projects solely for symmetry.

ViewModels must not construct CLI arguments, launch processes, verify media, or publish files. UI-only code-behind is acceptable. Only the composition root chooses and constructs Infrastructure implementations; this exception does not authorize Infrastructure usage throughout Desktop. Keep the project graph acyclic.

Introduce interfaces where they express a real boundary or make important failure modes testable: backend access, output publication, and persistence are examples. Ordinary internal classes do not each need an interface. Share the small cross-container concepts that actually exist; keep container-specific DTOs and preservation rules in their adapters. Do not prebuild an MP4 implementation or a universal media framework in Phase 1.

Use descriptive names, cohesive types, nullable reference types, immutable values at workflow boundaries, explicit results for expected failures, and normal readable control flow. Prefer internal visibility unless a type must cross a project boundary. Keep error codes and structured parameters separate from localized messages. Preserve an original failure when reporting a secondary cleanup error.

Avoid global mutable state, unobserved tasks, duplicated orchestration, generic utility dumping grounds, arbitrary file-size quotas, and interfaces that merely mirror every class. Do not introduce MediatR, CQRS frameworks, event sourcing, generic repositories, plugin discovery, an embedded HTTP server, or speculative inheritance hierarchies.

Quality is assessed through understandable responsibilities, explicit invariants, readable diffs, and relevant tests. A layer name, dependency count, or coverage percentage is not evidence that the architecture is correct.

## 4. Media identity, selections, and plans

Inspect the actual detected container, source identity, tracks, relevant known features, backend/version, and capability limitations. Accept local regular files in supported containers. An extension alone is insufficient to identify a format.

Display each track's type, backend-local identifier, codec, language, title, available channel information, and relevant effective flags. Distinguish unknown information from a format-defined default. An omitted Matroska flag with a documented default is not the same as a field the probe could not inspect. Keep original metadata available separately from normalized matching values. Language metadata does not prove which language is actually spoken.

Treat mkvmerge track IDs, Matroska TrackUIDs, ffprobe indexes, and UI row numbers as different concepts. Track identity is local to an inspected file and backend. Output indexes can change after removals. Use explicit mappings; never infer correctness from matching codec/language alone when two tracks have the same attributes.

The main checkbox means **Keep**. Start with every audio and subtitle track selected. Preserve every video track and all required supported attachments. Removing subtitles does not authorize removing fonts or artwork. Video-track removal is not part of this product.

Removing all subtitles is normal. Removing all audio from a source that contains audio requires an explicit confirmation in the plan review. An already silent input is not an error. A no-change selection produces a visible no-change result and does not launch a redundant remux.

Preserve the relative order and effective disposition flags of retained tracks. When a default audio track is removed, show the resulting default-track situation; do not silently mark a different track as default or promise a particular player's selection behavior.

An approved immutable plan contains source identity/revision, the inspected track mapping, approved keep selections, preservation expectations, destination, backend/version, and review decisions. Snapshot collections; execution must not consult mutable checkbox state. Source, selection, destination, or backend changes invalidate the plan. A UI-language change does not.

Do not allow a late result from inspecting file A to overwrite the current inspector after file B was selected. Serialize or revision-check inspection responses. While a job owns an approved plan, disable conflicting source/selection edits or make an explicit separate future plan; do not mutate the running job.

### Later batch selection rules

Evaluate rules independently for every file. Never reuse episode one's numeric track positions in another episode. Support explicit language matching, track-title matching, and forced-subtitle criteria only with documented semantics. Specify whether a rule retains all matches or requires exactly one. Two matching tracks may both be intentional.

Normalize common language-code aliases for matching without rewriting saved metadata. Preserve regional distinctions unless the rule explicitly matches a base language. Unknown languages, conflicting tags, ambiguous single-match rules, and unexpected zero-audio results require review. Phase 1 does not implement these batch rules.

## 5. Backend and preservation contract

Use MKVToolNix for MKV-to-MKV and, later, FFmpeg for the supported MP4-to-MP4 subset. This capability split is a project decision, not an unmeasured speed claim. There is no silent cross-backend fallback or transcoding fallback.

For each backend, maintain a concrete preservation matrix that states:

1. What is supported and retained.
2. What can be detected and inspected.
3. What the application actually verifies for every job.
4. What is verified only by integration fixtures.
5. What remains unverified or is rejected.

The initial ordinary MKV workflow must retain encoded video and selected audio/subtitles, relevant track names/languages/effective flags, container title, ordinary chapters, supported user tags, and attachments. Pin and test the implementation's actual supported cases. Do not call a field verified merely because the command normally copies it.

Known unsupported features must produce explicit, actionable errors before processing when detected. Linked segments, ordered or externally linked chapters, advanced HDR/Dolby Vision metadata, uncommon track types, MP4 edit lists/timed data, and unusual metadata need concrete evaluation. Do not silently drop a detected feature, but do not invent unsupported restrictions from a filename or vague codec guess. A probe cannot enumerate every possible unknown container element; document this limit.

Use mkvmerge identification JSON and the schema corresponding to the pinned binary, including its identification-format version. Do not pin a schema number merely because an old manual links it. Bound JSON size/depth and fail on malformed, truncated, or excessive structured output. Unknown optional fields can be ignored only where they do not weaken a required capability check.

### MKV command policy

Build explicit retention arguments and track ordering. An empty audio/subtitle selection needs an explicit remove-all option. Preserve effective flags deliberately. Account for mkvmerge's language normalization, generated statistics tags, and possible UID remapping rather than accepting every metadata difference. Use `--track-order`; test `--normalize-language-ietf off` and `--disable-track-statistics-tags` for the intended preservation policy. Document any remaining equivalent-field additions or unavoidable identifier changes. Do not use UID equality as the sole verification oracle.

Use the documented GUI/progress output contract for progress, with machine parsing independent of the UI language. Do not parse translated human-readable listings as the primary API.

Identification JSON does not provide complete chapter/tag content. When those features are present and part of the checked contract, extract chapter/tag XML with the same-version `mkvextract` and compare semantic content. Compare against the expected post-removal metadata: preserve global and retained-track tags, account for tags targeting removed tracks and permitted UID mappings, and reject unsupported chapter-reference changes instead of silently rewriting them. Do not substitute chapter/tag counts or simplified chapter text for this check. Disable DTD processing/external entity resolution and bound XML input. Preserve references consistently when permitted UID changes occur.

Check attachment metadata per job and compare attachment payloads in integration fixtures; report that distinction accurately. If a stronger per-job attachment-content guarantee is claimed, implement that comparison. Extraction must use application-owned names and paths, never unrestricted filenames taken from attachments.

### Tool outcomes and warning review

For mkvmerge, exit 0 permits verification; it does not by itself prove a successful job. Exit 1 means warnings. It must never automatically publish a final output or become a clean success. Other unsuccessful exits fail the job.

If a warning-bearing output passes all required verification, keep the owned candidate in a review state. Offer explicit **Publish with warnings** and **Discard output** actions. Approval publishes that candidate rather than performing another remux. Recheck source revision, candidate ownership/integrity, and destination immediately before approval is committed; repeat the relevant verification if the candidate may have changed. A failed or incomplete required verification cannot be overridden by this action.

If a warning candidate is discarded or a job fails/cancels, clean only owned temporary files. Report cleanup failure separately. After an application restart, a prior verified flag is insufficient to publish a candidate; Phase 1 offers safe cleanup or a fresh operation from the source, not automatic publication or resume.

### Verification limits

Remuxing may legitimately change container layout, indexes, writing-application metadata, and explicitly documented identifiers. Whole-container byte equality is not the contract. Enumerate tolerated changes narrowly, including the policy for generated statistics and equivalent language fields; do not ignore arbitrary metadata mismatches.

Compare output against the approved retained-stream mapping, known codec properties, required metadata, timing expectations, and supported chapters/tags/attachments. Relate timing checks to retained content: deleting the longest track can change overall container duration legitimately. Document units and justified tolerances.

Routine probing and metadata checks are structural verification, not proof that every encoded packet is intact. Integration tests must compare retained encoded payloads or codec-appropriate equivalents on representative fixtures. Do not present matching stream counts as proof that the intended soundtrack was retained.

The later MP4 adapter must use explicit maps, stream copy for every retained stream, and deliberate metadata/chapter/disposition handling. A generic copy-metadata option is not proof of preserving all MP4 structures. Do not add filtering, encoder fallback, bitrate changes, or unintended timestamp conversions.

## 6. Safe output protocol and ownership

Each job has one owner responsible for its plan, active tool process, temporary output, verification, warning review, publication, and cleanup.

1. Recheck the inspected source using available filesystem identity plus size and modification information. These checks have concurrent-change limits and are not cryptographic proof. Never acquire write access to source media for this workflow.
2. Validate local-file eligibility, capabilities, destination permissions, path policy, input/output aliasing, and likely free-space needs. Existing output is a conflict. Detect relevant symlink/hard-link identity conflicts; path-string comparison alone is insufficient.
3. Reserve uniquely owned temporary storage adjacent to the final destination on the same filesystem. A private per-job directory containing a valid `.mkv` file is acceptable. Record ownership before writing media; do not reuse a predictable shared temp filename.
4. Remux only into the owned candidate. Never modify the source, delete it to free space, or overwrite any existing destination.
5. Wait for tool exit and drain both output streams. Verify the candidate against the plan. Recheck source identity after processing and before publication; a changed source invalidates the operation.
6. Apply warning review where required. Revalidate relevant assumptions after any user-review delay.
7. Check pending cancellation and enter a short, explicitly non-cancellable publication step. Publish through a same-filesystem no-overwrite operation that enforces the collision rule at the operation itself. Do not implement publication as a mere `File.Exists` check followed by an overwriting move/copy.
8. Record the actual publication result and path. After a successful publication, a late cancel request or cleanup error cannot erase the fact that an output exists. Never delete the final output during cancellation cleanup.
9. Report the result, verification level, warnings, and measured file-size difference. Estimated savings must be labelled or shown as unavailable. Explain that the original still occupies disk space.

Use a separate user-selected output directory by default. Later folder mode preserves relative paths and excludes output/temp folders from discovery. Recheck collisions at publication even if the destination was free before remuxing. Failed publication is not a completed job; if a crash or OS error makes publication uncertain, report uncertainty rather than inferring success from a matching filename.

Free-space checks are advisory and race-prone. Handle write failures correctly and account for a complete new output coexisting with the original. Do not advertise atomic rename as a universal power-loss transaction, particularly on removable/network filesystems.

Keep execution phase separate from outcome evidence. Preserve tool exit status, verification result, publication result/path, cancellation request, primary error, and cleanup issues without forcing every combination into one enormous enum. Use a small valid-transition model for live progress; a published result with a cleanup issue must stay distinguishable from an unpublished failed job.

### Phase 1 interruption handling

Persist only the small versioned records needed to associate a job with its owned temporary paths and publication stage. Use bounded records and safe settings/journal writes. On restart, show interrupted or unresolved work and offer cleanup only after validating ownership. Corrupt or insufficient records must not authorize deletion. Never delete by filename extension, broad glob, or loose name prefix.

Never kill a process solely from a PID stored in an old journal. Never treat the existence of the planned final path as proof that the interrupted job published it. Do not auto-publish, auto-retry, or resume bytes after restart. Persisting and restoring a full batch queue belongs to Phase 2.

## 7. Process execution and input safety

Use `System.Diagnostics.Process`, absolute executable paths, `UseShellExecute = false`, and `ProcessStartInfo.ArgumentList`. Resolve bundled tools relative to `AppContext.BaseDirectory`, not the current directory or arbitrary PATH entries. Do not concatenate user paths into commands or run media work through cmd.exe, PowerShell, or sh.

An explicit development override may point to a specific compatible executable. Show its resolved path/version and label such a build accurately. Missing or incompatible tools produce an actionable error; the application must not download tools at first launch or quietly use a different backend.

Drain stdout/stderr asynchronously and continuously. Limit retained diagnostics without stopping drainage; separately reject truncated structured data. Avoid UI-thread blocking, `.Wait()`, `.Result`, and unobserved fire-and-forget work. Bound probe/startup operations and specify a hung-job policy without imposing a tiny fixed timeout on large valid remuxes.

Cancellation must terminate and await/reap the directly owned process and handle any owned descendants that can occur. Cancelling a .NET wait does not stop the tool. The parent process exiting is not proof that all descendants have exited. Verify actual behavior on each target OS. Keep process ownership narrow; add OS-specific containment only when needed to meet the tested contract.

If termination cannot be established, report it and do not delete a file that a surviving worker may still be writing. Closing the window during a job must await safe shutdown or keep the application running with an explicit user choice. Cancellation also covers probing and verification subprocesses.

Progress uses real backend data. Use an indeterminate state if reliable progress is unavailable. Throttle UI notifications without dropping final transitions, and marshal UI-owned updates to the UI thread.

Test spaces, Cyrillic, Unicode, metacharacters, leading-dash names, and symbols legal on each OS. Double quotes cannot be part of a Windows filename; test them where legal and test argument handling independently. Absolute paths and argument arrays do not remove the need to respect the tool's own argument grammar.

Reject URLs, playlists, non-regular inputs, and arbitrary tool arguments. Do not follow directory symlinks/junctions during future recursion by default. Restrict network protocols where applicable to the later backend. Treat media metadata as untrusted input. External processes are an integration boundary, not a security sandbox; do not run elevated.

## 8. UI and localization

### Phase 1 screen

Provide a real file picker, an inspector with actual tracks, Keep checkboxes, a readable output-plan review, output location selection, processing progress, cancellation, warning review, and a result with its output path. Use ordinary Avalonia controls and Fluent styling. Support keyboard navigation, clear focus, resizable layouts, long filenames, and readable errors. Do not build a custom design system or media player.

### Mandatory English/Russian behavior

- First launch uses **English**, including on a Russian operating system. Do not automatically select the OS language.
- Provide an obvious **English / Русский** selector. Switching updates current application-owned labels, statuses, validation messages, and accessible names without restarting, reloading the file, losing selections, or restarting/cancelling a job.
- Persist `en` or `ru` in portable settings. Restore it on subsequent launches; invalid/missing values fall back to English. Report a persistence failure honestly.
- Keep English neutral resources and Russian resources together, preferably `.resx` with a small observable localization service. Do not scatter localized string literals through Views or ViewModels. Changing a culture variable alone is insufficient unless visible bindings are notified.
- Keep application results/errors as stable codes and structured parameters below Desktop. Localize their presentation in Desktop. Raw backend diagnostics, original media titles/tags, and filenames remain unchanged; an OS-native file dialog may follow OS localization.
- Keep JSON, CLI arguments, numeric protocol parsing, and persisted values culture-invariant. A language switch must not change the generated media plan or command.
- Test English-first startup under a Russian culture, live switching with preserved selections/progress, persistence, fallback, resource-key/format-argument parity, and layouts in both languages. Avoid implementing a general translation framework.

### Later batch UI

Add a file list, per-file inspector, rule controls, per-file preview, output selector, and sequential queue. Use bounded concurrent probing and one remux worker by default. Extra remux concurrency needs measurements and an explicit setting.

Review-required files must not start automatically. Allow approved jobs to run while reporting skipped/review-required files clearly. Folder discovery needs optional recursion, stable ordering, de-duplication, a symlink/junction policy, and output/temp exclusions. Do not modify external subtitle files.

## 9. Portable distribution

Ship separate self-contained archives:

- `TrackTrim-win-x64.zip`
- `TrackTrim-linux-x64.tar.gz`

Portable means no application installer, elevation, separately installed .NET, or user-installed media tools on the declared baseline. It does not mean one universal executable, zero OS dependencies, or forensic absence of traces.

Include the complete normal publish output, required Avalonia/native assets, `mkvmerge`, `mkvextract`, their redistributable dependency closure, notices, and provenance. Add FFmpeg/ffprobe when production features require them. Preserve Linux executable permissions.

The Windows entry point is `TrackTrim.exe`; Linux uses `TrackTrim`. Use app-local `tools`, `data`, and `licenses` directories. A `portable.flag` may identify the data policy. Do not strip runtime files to make the archive appear simpler.

Settings, temporary-output records, and application-controlled logs use `data` relative to the application directory. If it is unwritable, explain the problem and offer a user-selected writable data directory or an honest non-persistent mode. Do not silently redirect to AppData or a home config directory. Non-persistent mode must not claim restart ownership recovery; disable media writes when required ownership records cannot be stored safely.

Use **Windows 11 x64 on a supported release, initially 25H2**, and **Ubuntu 24.04 x64 Desktop** as proposed first acceptance environments. Record exact tested OS builds/editions and revise the matrix with evidence. A framework's minimum Windows build is not the same as a currently serviced OS release. Compatibility with other Windows builds or Linux distributions is not implied. Ubuntu 24.04 is currently an Avalonia Tier 2 platform; support of this application still requires its own testing.

Use X11/XWayland for the initial Linux route. Avalonia 12.1 has an opt-in native Wayland backend, but upstream describes it as early-stage; native Wayland is a separate future acceptance target. Test actual desktop file dialogs, window behavior, and scaling. Headless tests do not establish those capabilities.

Self-contained .NET does not include every native OS library. Determine the combined requirements of .NET, Avalonia, and bundled tools. Do not claim arbitrary glibc distributions, musl/Alpine, or NixOS support. A CLI-only MKVToolNix package can still require Qt-linked libraries; inspect the actual binaries rather than guessing dependencies from the absence of a GUI.

Prove Linux tool bundling during Phase 0. Evaluate a repeatable source build or a deliberate build-time extraction/repackaging of a pinned upstream artifact. An AppImage requiring unavailable FUSE is not a solved no-install workflow. Do not make users install mkvmerge, FFmpeg, or extra tooling to compensate for an incomplete archive.

Build scripts must pin downloads/source revisions, verify hashes, record versions/configuration and native-library requirements, and avoid unpinned latest downloads. Do not place downloaded binary collections in normal Git history. Explicit build-time acquisition is allowed; hidden application startup downloads are not.

Test relocated archives from paths with spaces/Cyrillic, with a different current working directory and no development SDK or separately installed media tools. Re-run a real MKV workflow from the packaged paths. If a target OS is unavailable, add its CI checks and label its actual runtime/desktop acceptance unverified. Creating an archive or cross-publishing it does not prove it runs.

Native AOT, trimming, single-file publishing, and an application AppImage are later measured optimizations. Do not enable them speculatively, remove localization resources, or disable globalization to make the first package smaller.

## 10. Licensing and provenance

Preserve the repository's operative license. Following the explicit owner decision on 2026-10-23, original TrackTrim code uses MIT with `Copyright (c) 2026 Nexusov`; see `LICENSE` and `docs/license-decision.md`. Do not extend that grant to third-party material or invent further author details. Public release authorization and covered-source delivery remain separate requirements.

MKVToolNix states GPL v2 licensing and lists included components separately. FFmpeg's effective license depends on its build configuration. Executing a separate tool does not remove obligations for redistributing that tool. Do not infer either that every shipped file is MIT or that launching a GPL executable automatically determines the original C# application's license.

Record exact binary versions, hashes, authoritative URLs, licenses, build options, source versions, and required notices. Provide corresponding source/build material through an applicable distribution method for the actual shipped artifacts. Exclude non-redistributable configurations. A generic homepage link is not evidence that redistribution obligations are fulfilled.

Copy third-party code/assets only after verifying license compatibility and attribution. Document concrete unresolved redistribution issues before claiming public-release readiness. Do not copy an entire existing GUI to avoid implementing the product's small workflow.

## 11. Tests and acceptance evidence

Test the behavior being delivered, with priority on data safety and correct track selection. Do not demand an arbitrary coverage percentage, mirror every getter, or implement Phase 2 tests by prematurely building Phase 2 code.

**Core:** immutable plans, explicit removal sets, no-change handling, confirmed zero-audio selection, retained order/flags, unsupported capabilities, and plan invalidation. Batch matching cases are added with Phase 2.

**Application:** stale inspection results/plans, valid transitions, actual published-vs-unpublished outcomes, failed verification, warnings and review, cancellation before/while/after work, a collision appearing at publication, source changes during work, termination failure, secondary cleanup failure, and minimal interrupted-record handling.

**Infrastructure:** real JSON/XML fixtures from the pinned tools, correct argument arrays, bounded/invalid/truncated output handling, concurrent stream drainage, real process cancellation, safe filesystem publication, and realistic permission/write failures. Use a controlled helper process or narrow fake port to test deterministic failures/races; identify these tests clearly and retain real backend integration tests.

**Real media integration:** small synthetic or appropriately licensed MKV fixtures containing video, distinguishable audio tracks, subtitle tracks, titles/languages/flags, ordinary chapters, user tags, and an attachment. Include two audio tracks with identical language/codec metadata but different content, at least one multiple-video case, and a retained-order case. Include removal of all subtitles and removal of the default audio track. Verify precisely which content survives, not just stream counts.

Keep fixture-generation scripts and provenance in Git. Generation tools can be development dependencies without becoming production requirements. Compare extracted retained payloads or codec-appropriate normalized equivalents where suitable. Check attachment content and semantic chapters/tags. Hash the source before/after success, failure, and cancellation to demonstrate that the application did not alter it. Do not compare entire input/output container hashes for equality.

Use deterministic synchronization for race tests rather than relying solely on arbitrary sleeps. A helper that simulates exit 1 verifies warning-handling logic; it does not establish real mkvmerge remux correctness. If a real warning fixture is unavailable, state that narrower coverage.

**UI:** use compatible Avalonia.Headless tests for bindings, selections, preview invalidation, errors, language switching/persistence, and job-state presentation. Run separate real desktop checks for file pickers, scaling, executable startup, window closure during work, and display-server integration. Add folder/drag-and-drop checks when those features exist.

**CI:** locked restore, formatting verification, build, relevant unit/UI tests, actual backend integration, and platform-specific archives. A designated media-integration/package job must fail if its required tool cannot be provisioned; it must not become green by skipping the entire backend suite. Optional local tests can report a clear skip, which leaves the corresponding acceptance unverified.

Record exact commands, versions, meaningful test results, and remaining gaps. Do not invent counts or report unavailable/skipped/defined-but-unrun CI checks as passed. An ordinary-media suite does not prove universal HDR/container fidelity.

## 12. Repository and contribution workflow

Maintain concise professional English documentation:

- `README.md`: actual purpose, current supported behavior, limitations, real screenshots when available, and run/build/test/package commands.
- `AGENTS.md`: durable repository rules, dependency boundaries, safety invariants, actual verification commands, and bounded task workflow.
- `CONTRIBUTING.md`: developer prerequisites, explicit tool acquisition, code conventions, tests, and expectations for small contributions.
- `docs/ARCHITECTURE.md`: responsibilities, allowed dependencies, process ownership, publication semantics, and error outcomes.
- `docs/SUPPORTED_FORMATS.md`: tested versions and the supported/inspected/verified/unverified/rejected matrix.
- `docs/PLAN.md`: milestone acceptance with planned, implemented, verified, and blocked items clearly distinguished.
- `THIRD_PARTY_NOTICES.md`: actual component licenses/provenance and outstanding release obligations.

Keep significant decision records short and useful. Do not create empty ADR collections, duplicate plans, or a conversation diary. Explain behavior, constraints, and reasons directly. Do not write about what an owner requested or an AI agreed to.

Inspect Git state and repository instructions before changes. Preserve unrelated work. Use small cohesive commits consistent with repository policy; do not mix features with unrelated formatting/dependency upgrades. Do not reset local changes, push, publish, sign releases, or change repository settings without explicit authorization.

An implementation report is in Russian and separates behavior implemented, exact executed checks/results, known limitations, and the next bounded task. Code, documentation, and commit messages are in English. Do not claim architecture or production readiness merely because files were generated.

## 13. Milestones and stopping point

### Phase 0 — Feasibility and foundation

Inspect the repository/toolchain, verify stable compatibility, pin dependencies, establish an actually building Avalonia shell, and prove English/Russian switching. Invoke bundled MKV tools for a real version/identification check. Establish focused tests and CI. Produce the portable packaging proof early, especially Linux dependency resolution. Record redistribution/runtime blockers honestly and continue independent work when one target is unavailable.

### Phase 1 — One safe MKV workflow

Complete these small implementation checkpoints in order, building and testing as appropriate after each:

1. Real single-file inspection and a bilingual inspector with correct Keep semantics.
2. Pure immutable planning, review, source/destination checks, and UI-independent remux/process ownership with real progress and cancellation.
3. Required verification, warning-candidate review, no-overwrite publication, distinct cleanup outcomes, minimal interruption records/cleanup, and focused real-media tests.
4. Relocated portable archives, executed checks on available platforms, and factual documentation of remaining acceptance gaps.

The completed result must open one actual MKV, show its real tracks, remove approved audio/subtitle tracks without re-encoding, preserve the documented retained content, publish a new file safely, and behave correctly in both UI languages. Do not stop at a mock screen or planning documents when implementation is feasible.

Phase 1 includes portable settings and minimal temporary-output ownership/restart cleanup. It does not include batch scanning, a persisted queue, selection presets, MP4 production processing, or automatic source replacement. Stop before Phase 2. A missing platform test remains unverified; do not weaken safety requirements to declare the phase complete.

### Phase 2 — Files, folders, and batch rules

Add bounded discovery, independent per-file rule matching, ambiguity review, relative output paths, a sequential queue, presets, and restoration of queued/interrupted job listings. Retrying starts safely from the source; do not implement byte-offset resume. Test episodes with deliberately different track orders and metadata.

### Phase 3 — MP4 and format hardening

Implement FFmpeg/ffprobe behind the existing narrow boundary and prove the supported MP4 preservation/timing contract. Expand media fixtures without weakening MKV guarantees or adding silent conversion.

### Phase 4 — Public portable release

Complete the platform/desktop matrix using packaged tools on clean target systems; finish notices/corresponding-source distribution and license selection; review accessibility, both languages, documentation, and contribution setup. Source replacement is not required for this release.

### Later, only as separate tasks

Recoverable source replacement, extra containers, measured remux parallelism, native Wayland acceptance, AppImage/AOT/trimming, and playback preview. Do not build extension mechanisms for them in advance.

## 14. Primary references

Use documentation matching the versions actually pinned. Review-time availability is not an implementation lock or a runtime test.

- .NET support policy: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- .NET 10 downloads: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
- SDK selection: https://learn.microsoft.com/en-us/dotnet/core/tools/global-json
- .NET publishing: https://learn.microsoft.com/en-us/dotnet/core/deploying/
- Windows release lifecycle: https://learn.microsoft.com/en-us/lifecycle/products/windows-11-home-and-pro
- Avalonia package: https://www.nuget.org/packages/Avalonia
- Avalonia supported platforms: https://docs.avaloniaui.net/docs/supported-platforms
- Avalonia Linux: https://docs.avaloniaui.net/docs/platform-specific-guides/linux
- Avalonia localization: https://docs.avaloniaui.net/docs/app-development/localizing
- Avalonia headless testing: https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform
- MVVM Toolkit: https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/
- MVVM Toolkit package: https://www.nuget.org/packages/CommunityToolkit.Mvvm
- MKVToolNix downloads: https://mkvtoolnix.download/downloads.html
- mkvmerge manual: https://www.bunkus.org/videotools/mkvtoolnix/doc/mkvmerge.html
- mkvextract manual: https://mkvtoolnix.download/doc/mkvextract.html
- MKVToolNix requirements/licenses: https://mkvtoolnix.download/doc/README.md
- Matroska elements/defaults: https://www.matroska.org/technical/elements.html
- FFmpeg: https://ffmpeg.org/ffmpeg.html
- ffprobe: https://ffmpeg.org/ffprobe.html
- FFmpeg formats: https://ffmpeg.org/ffmpeg-formats.html
- FFmpeg licensing: https://ffmpeg.org/legal.html
- Process arguments: https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist?view=net-10.0
- Process termination: https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0
- AppImage/FUSE: https://docs.appimage.org/user-guide/troubleshooting/fuse.html
- Serilog: https://serilog.net/
