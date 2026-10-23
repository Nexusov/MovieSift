# TrackTrim

TrackTrim is a portable Windows/Linux desktop application for removing selected embedded audio and subtitle tracks from local MKV and supported MP4 files without re-encoding. It writes a separate file in the input container; originals remain on disk. Creating a trimmed copy does not free the original's space.

The integrated feature stack is frozen for the proposed **0.1.0-rc.1** private review candidate; no public release or tag is published. MP4 admits ordinary self-contained nonfragmented H.264/AAC/mov_text, ordinary chapters (at most one referenced QuickTime chapter track; admitted Nero data must agree), and JPEG/PNG artwork within the [preservation matrix](docs/SUPPORTED_FORMATS.md#mp4-preservation-contract). Several media tracks may reference that same chapter track; distinct chapter lists are rejected before work. [Release notes](docs/release-notes.md), [acceptance/evidence](docs/PLAN.md) and the [finite release checklist](docs/release-checklist.md) distinguish verified behavior, unavailable target desktops and specific distribution requirements. CI uploads compact test/provenance evidence, not binary release assets.

The application uses C#/.NET 10, Avalonia 12 with Fluent controls, CommunityToolkit.Mvvm, pinned MKVToolNix for MKV and pinned FFmpeg/ffprobe for MP4. English is the initial language, including on a Russian OS. The English / Русский selector changes application text without discarding the current selection; the choice is saved beside the application.

## Single-file workflow

1. Open one MKV or MP4 with the native file picker and inspect its real tracks.
2. Leave **Keep** checked for each audio/subtitle track to retain. Every video track and supported attachment is retained.
3. Select a separate destination and review the immutable plan. Confirm removal of all audio explicitly when applicable.
4. Run the remux, observe measured backend progress, or cancel. A no-change plan does not remux.
5. The application verifies the candidate before publishing through a no-overwrite move. MKVToolNix warnings require a separate publish/discard decision after successful verification. FFmpeg errors or unrecognized warnings fail the operation. The narrowly documented admitted demux warnings retain diagnostics and still require full verification. Closing during MKV warning review offers **remain in review** or **discard and close**; the latter waits for cleanup.

## Manual batch workflow

1. In **Batch**, add several MKV/MP4 files or one folder. **Include subfolders** defaults off; enable it explicitly for subsequent folder additions after choosing an existing output folder. The setting never reinterprets existing rows. A session holds at most **100** entries, including failed entries; a complete folder scan examines at most **10,000** entries with at most **32** nested directories. Directory symlinks/junctions are never followed. Every visited directory must pass the local-filesystem policy, including ordinary mounted children; network filesystems are rejected before enumeration. A limit or traversal failure rejects the whole addition, without a truncated subset. Cancellation before acceptance adds nothing; cancellation during subsequent automatic inspection keeps already accepted rows.
2. Choose an output folder. Folder-derived files preserve their relative parents: `Series/Season 1/episode.mkv` becomes `Output/Season 1/episode.trimmed.mkv`, without adding `Series`. Explicit file-picker inputs keep flat outputs. Recursive input excludes the selected output subtree and known application data/owned working directories; the output root cannot equal or contain the source root. Each file is inspected independently. Select a row, adjust its own Keep choices, confirm zero audio for that file when needed, and approve its plan. Switch rows freely; selections and approvals stay with their source. Remove unwanted unstarted rows or explicitly exclude them. Failed inspections have no invented tracks.
3. Review the remaining included plans, sources, outputs and retained/removed tracks. Every included item needs approval; unresolved items must be corrected or visibly excluded. Names use `<source>.trimmed.mkv` or `<source>.trimmed.mp4`, retaining each input container; existing files, source aliases and duplicate batch destinations are errors. Correct the destination explicitly; there is no automatic renaming or overwrite.
4. Start the reviewed batch. One job runs at a time through the same verification/publication lifecycle as a single file. No-change items remain visible and do not remux. Editing a selection or destination invalidates its approval; changing the shared output folder invalidates every affected approval. Language changes preserve plan semantics.
5. **Pause after current file** lets that job finish. **Cancel batch** cancels the active job and prevents the next one starting. Warnings, failures, cancellation or cleanup problems stop the queue. Resolve the warning or correct/exclude an unsuccessful item, then explicitly review and start the remaining work. Previously published outputs stay intact; uncertain worker termination or unresolved cleanup blocks further work.
6. **New batch** starts an empty in-memory list without restarting. Confirm clearing a nonempty list, or keep the current batch unchanged. Created files, settings, interface language and the chosen output folder are retained. Sources can be added again, with all 100 entry slots available; every new plan needs fresh approval and Review. Active work, warning review and unresolved ownership/cleanup block this action. [Verification evidence](docs/verification/new-batch-evidence.json) separates real repeat-session tests, headless coverage and the bounded native functional check.

[Recursive-folder verification](docs/verification/phase-2d-recursive-folders-evidence.json) records the required real-media case and an owner-assisted Windows 10 portable GUI run: two same-named episodes published into separate season directories, with independently compared retained payloads and unchanged originals. UIA functional observations do not establish appearance, checkbox ToggleState, scaling or clean target-desktop acceptance.

Missing season output directories are validated without creation during discovery, planning and Review. Only execution prepares them under the explicit existing output root, before candidate reservation; paths and collisions are rechecked before work/publication. Cancellation/failure still cleans owned candidates and journals, but may leave newly created empty output directories. Manual destinations outside that root retain the ordinary existing-parent requirement. Selecting another shared output folder re-derives eligible unfinished destinations and requires fresh approval; completed outputs stay intact. The recursion option is not part of rule presets.

The live queue and job authority remain in memory. Its unfinished listing is checkpointed in `data/queue-list.json`: source paths, intended destinations, inclusion and folder provenance only. After restart, explicitly **Restore saved list** or **Discard saved list**. Restore never starts inspection or jobs; freshly inspect every included source, choose tracks again, approve individual plans and perform Review. Old track IDs, selections, zero-audio consent and approvals are never trusted. Completed/no-change and actually published results are omitted; if a crash precedes the next checkpoint, existing-output collision checks still prevent overwrite.

Discard changes only saved listing data. **New batch** clears the current list and checkpoints that empty intent while retaining preferences/presets and all files. Closing awaits pending checkpoints and the existing owned-job shutdown/warning decision. Ownership journals alone govern interrupted candidates: a restored queue cannot publish, delete or terminate an old candidate/worker.

The queue record has schema version 1, a 64 KiB actual UTF-8 budget, depth 16, at most 100 entries and 4096 UTF-16 code units per path field. Cross-instance revision checks return conflict instead of last-writer-wins replacement. Invalid/incompatible data stays unchanged with a visible error; queue-write failures remain distinct from ownership failures and leave manual work available, with restart recovery explicitly unconfirmed. Correct the record externally and use **Reload saved list** with an empty current batch. No automatic retry, queue execution restoration or byte-offset resume is provided. [Phase 2 completion evidence](docs/verification/phase-2-completion-evidence.json) separates controlled persistence tests, real restored-media processing and the owner-assisted Windows 10 native restart/Restore observation.

### Explicit language and title rules

In Batch, audio and subtitles each default to **leave current choices unchanged**. Enable a category and enter comma-separated language codes, then **Preview choices**. Every file shows its own retained/removed tracks, original metadata and manual-review reasons. Every matching track is retained, including multiple tracks in one language. **Apply preview** changes Keep choices only; inspect/edit each file, approve its plan and perform aggregate Review before Run.

Supported ISO aliases match canonically, while complete script/region tags match exactly: `eng` matches `en`, but `ru` does not match `ru-RU`. Unknown, unsupported or conflicting targeted metadata, or no audio matches, leaves that whole file unchanged for manual review. No subtitle matches may keep no subtitles and is explicitly shown. See the [precise matching contract](docs/SUPPORTED_FORMATS.md#language-selection-rules) for supported tag syntax. Metadata is not proof of the language spoken.

Each enabled category can additionally require **title contains**. This constraint defaults off and keeps every track matching **both** language and literal title text. Surrounding search whitespace is trimmed; comparison is culture-independent and case-insensitive (`OrdinalIgnoreCase`). Only the actual inspected track title is searched: missing/blank titles do not match, and neither filenames nor display fallbacks are used. An enabled blank search is invalid. Punctuation is literal, including `[]`, `.*` and `?`; there are no regexes or wildcards. Language uncertainty and zero-audio safeguards still apply to the whole file.

Preview never edits selections or approvals. Relevant batch edits make it stale; only actual applied changes invalidate affected approvals and aggregate Review. Excluded/completed items remain unchanged. Rules do not approve, exclude or run unresolved files, and never apply automatically to later additions/reinspections. Language switching preserves the configuration and valid proposal; New batch clears transient proposals. Active work and unresolved ownership block preview/application through the existing gates.

For matching subtitles, **Keep only forced subtitles** optionally also requires the inspected track's effective forced flag. It defaults off; unchanged subtitle mode ignores the stored setting. Language, optional title and forced predicates are combined, retaining every matching whole track in its original order. Titles such as “Forced” and individual subtitle events are not interpreted. Uncertain language on any targeted subtitle still requires manual review; no combined matches explicitly proposes no subtitles. Audio, video and retained metadata are unchanged. Changing the setting clears only the proposal; explicit Preview/Apply and fresh approvals are still required after actual selection changes. Player selection/display is not guaranteed.

[Forced-subtitle verification](docs/verification/phase-2b-forced-subtitles-evidence.json) separates pure/controlled/headless checks, the real two-file payload/flag regression and Windows 10 native Preview/EN-RU/Apply observations. The native sequence has an explicit automation protocol deviation (unintended aggregate Review), so it is not a clean pass of the prescribed no-Review scenario. Earlier evidence retains its original binary/platform; visual and target-desktop acceptance remain open.

[Language-rule verification](docs/verification/phase-2b-language-rules-evidence.json) separates the real two-file backend case, controlled/headless tests, portable checks and the bounded Windows 10 native Preview/Apply/EN-RU functional check. Native source selection was owner-assisted; visual and target-desktop acceptance remain open.

[Literal-title verification](docs/verification/phase-2b-title-filter-evidence.json) records the real two-file conjunction/payload case, 56 focused tests, full platform CI and two bounded checks on the identified Phase 2B2 Windows 10 GUI: English title Preview/Apply, then both-category EN → RU → EN state preservation. After one fresh baseline Preview, the latter retained settings, proposal, paths and existing approval without another Preview, Apply, Review or Run. Source selection for the original check was owner-assisted; subsequent input used Computer Use. Visual and target-desktop acceptance remain open.

### Named portable rule presets

**Save as new** stores the current audio/subtitle rule settings, including inactive language/title values and forced-only. Select a saved name, then explicitly **Load** it; selection alone changes no rules. Loading different values clears only the transient Preview, preserving actual Keep choices, plans, destinations and Review. Identical values preserve a valid Preview. Rename and confirmed Delete affect only the catalog; deleting loaded values leaves them in the editor. Startup restores the list without selecting/loading anything, and New batch keeps the catalog and rule configuration.

Presets live in `data/rule-presets.json` beside the portable application. They contain no media paths, output folder, track IDs, selections, approvals, jobs, queue or interface language. Names are trimmed, unique without case, and limited to 80 characters without control characters. The versioned catalog is bounded by 64 KiB and 100 entries; corrupt/incompatible files remain untouched with a visible error, while manual rules remain usable. Writes use fail-fast interprocess exclusion and complete-file validation. See [storage bounds and failure behavior](docs/ARCHITECTURE.md#phase-2c1-portable-rule-presets) and the [feature evidence](docs/verification/phase-2c-rule-presets-evidence.json). No overwrite/update, import/export or automatic preset application is provided.

Do not use the application as a universal preservation tool. The [supported preservation matrix](docs/SUPPORTED_FORMATS.md) defines the inspected, verified, and rejected cases. Title-only/regex rules, other forced-filter modes, preset update/import/export, automatic retries/live job restoration, playback and source replacement remain outside this implementation.

Preflight supplements MKVToolNix identification with bounded, read-only Segment Info inspection. Detected segment families and previous/next links are rejected before processing; layouts that cannot be inspected within those bounds fail explicitly. This check uses no additional media executable.

The images below are historical headless renders of the Phase 1 inspector. The [Phase 1 native matrix](docs/verification/native-desktop-acceptance.md) retains its recorded `e16050e` binary and Windows 10 results. A separate [native batch receipt](docs/verification/native-batch-evidence.json) records the fixed `83f6116` portable GUI: owner-assisted native dialogs/start, Computer Use independent A/B plans and EN→RU preservation, two published outputs, and five independently compared payloads. This functional Windows 10 check does not establish visual layout, visible focus, scaling or clean Windows 11/Ubuntu Desktop acceptance. The W13 active-remux input-correlation gap and W14 single-close limits are unchanged; [PLAN](docs/PLAN.md) distinguishes all evidence scopes.

![English TrackTrim inspector with real video, two audio tracks, and subtitles](docs/images/tracktrim-en.png)

![Russian TrackTrim inspector showing the same real tracks and preserved Keep selections](docs/images/tracktrim-ru.png)

## Build and run

Install the exact stable SDK in `global.json`, PowerShell 7, and Python 3 for the build scripts (Python 3.9+ with standard-library `tarfile`/XZ support; the local verification used Python 3.13.0). Python extracts pinned source notices and generates original fixtures; it is not an application runtime dependency. No media tools are downloaded by the running application. Acquisition is an explicit developer/build operation:

```powershell
pwsh ./build/acquire-tools.ps1 -RuntimeIdentifier win-x64 -Destination ./tools
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
```

Use `linux-x64` on Linux. Place the acquired `tools` directory beside the built entry point before a development run. `TRACKTRIM_TOOL_DIRECTORY` is a test setting; the desktop application always uses its app-local bundle. A portable package supplies this layout automatically.

```powershell
Copy-Item -LiteralPath ./tools -Destination ./src/TrackTrim.Desktop/bin/Release/net10.0 -Recurse
dotnet run --project src/TrackTrim.Desktop --configuration Release --no-build
dotnet format --verify-no-changes --no-restore
dotnet test --configuration Release --no-build --no-restore
```

See [CONTRIBUTING](CONTRIBUTING.md) for mandatory real-media checks and [fixture provenance](tests/TrackTrim.Infrastructure.Tests/Fixtures/README.md) for the generated VP8/PCM/SRT and checked-in direct FFmpeg-origin H264/AAC/SRT samples. The production archive contains the pinned FFmpeg/ffprobe pair for MP4. Fixture encoding is development-only; production uses explicit stream copy. Unit tests and headless UI tests do not replace a real desktop or a packaged workflow.

## Portable archives

```powershell
pwsh ./build/package.ps1 -RuntimeIdentifier win-x64
pwsh ./build/package.ps1 -RuntimeIdentifier linux-x64
```

Run each command on its target OS. The outputs are `artifacts/TrackTrim-win-x64.zip` and `artifacts/TrackTrim-linux-x64.tar.gz`. Each contains the complete self-contained runtime, tool dependency closure, license inventory, provenance, and an empty app-local `data` directory. Publishing is untrimmed, with no AOT or single-file optimization. Runtime security updates require rebuilding the archives.

Linux packaging extracts a pinned upstream MKVToolNix AppImage **at build time**. The application launches its extracted native CLI executables directly, with an app-relative library path in the child environment; it does not run media through a shell. Build-time acquisition/fixture helpers may use fixed wrappers. No FUSE, extraction, or download is required at first launch. Native dependency checks must pass during packaging; this is not a guarantee for arbitrary Linux distributions.

Windows packaging uses a verified source build of the same MKVToolNix 102.0 CLI pair, with its actual DLL closure and complete matched source/build materials. The build runs on an ephemeral Windows runner; an identified build can be reused locally without installing compilers. Provide `-WindowsMkvBuildDirectory` (or `TRACKTRIM_WINDOWS_MKV_BUILD_DIRECTORY`) as described in [CONTRIBUTING](CONTRIBUTING.md#explicit-media-tool-acquisition). Missing or changed workers/materials fail acquisition; official Windows binaries are not a fallback. This changes the worker distribution arrangement, not the MKV preservation contract.

Declared acceptance targets remain Windows 11 x64 25H2 and Ubuntu 24.04 x64 Desktop using X11/XWayland. Exact tested hosts and unverified desktop checks are recorded in [PLAN](docs/PLAN.md). See [native requirements](docs/SUPPORTED_FORMATS.md#platform-and-native-requirements) before making a compatibility claim.

The `62713d8` Windows candidate has [consolidated native functional evidence](docs/verification/release-candidate-evidence.json) on Windows 10: owner-assisted pickers, single MKV/MP4, mixed recursive plans/rules/presets and explicit queue Restore/Discard. Independent checks matched fourteen retained stream payloads across four GUI outputs and confirmed unchanged sources. UIA functional evidence does not establish visual layout, effective scaling or acceptance on the declared desktop targets.

To test a relocated archive, provide a generated multi-track fixture:

```powershell
pwsh ./build/verify-package.ps1 -ArchivePath ./artifacts/TrackTrim-win-x64.zip -FixturePath ./artifacts/fixtures/ordinary.mkv -Mp4FixturePath ./artifacts/fixtures/mp4/ordinary.mp4
```

This runs the actual packaged entry point from an unrelated working directory, tests a path containing spaces/Cyrillic/metacharacters, performs the real application workflow, and hashes the source before/after. It does not establish native dialogs, scaling, or clean-machine desktop acceptance.

Linux CI also verifies the actual workflow in a pinned Ubuntu 24.04 container with native OS libraries, no installed SDK/runtime/media commands, no network, and a non-root runtime user. Run it with `bash ./build/verify-clean-linux.sh ./artifacts/TrackTrim-linux-x64.tar.gz ./artifacts/fixtures/ordinary.mkv ./artifacts/fixtures/mp4/ordinary.mp4`. The successful check records the exact base image, installed package versions, user, and ASCII parent locale (`C`); the native worker boundary handles Cyrillic paths independently. See [PLAN](docs/PLAN.md) for executed evidence; a container CLI check is separate from Ubuntu Desktop acceptance.

Settings, bounded logs, and temporary-output ownership records are stored in `data` relative to the executable. An unwritable data directory is an error for media writes; the application must not silently redirect to a user profile. Startup recovery offers validated owned-temporary cleanup, never automatic publication or byte resume. Malformed, incomplete or nonregular records remain visible for manual review; they cannot authorize cleanup, and their original files remain untouched.

## Licensing

Original TrackTrim code is licensed under [MIT](LICENSE), copyright (c) 2026 Nexusov. [The decision record](docs/license-decision.md) records the explicit selection and scope. Bundled components retain their own licenses; [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES.md) and the [distribution closure](docs/verification/release-distribution-closure.md) record independent notice/source/build obligations. Portable packages include the operative `LICENSE`; private review source/material bundles bind those materials to each exact binary archive and are not public source delivery. CI uploads compact evidence, not binary archives; no installer or release is published.
