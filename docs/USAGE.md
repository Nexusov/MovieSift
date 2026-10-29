# MovieSift user guide

See the [README](../README.md) for downloads and startup.

**Version note:** this guide describes the **1.2.1 review build**. Version **1.0.0** has no updater. Version **1.1.0** requires the old flat package layout. Install the 1.2 layout manually in a separate directory once, using the migration instructions below; do not mix files from these versions.

## Choose what to keep

Add local files or a folder. **Audio** and **Subtitles** show tracks from every included file, including tracks found in only one episode. All tracks initially stay in the result. Use **Remove** (×) for an unwanted track. Its card stays in the same position, becomes muted, and offers **Restore**. Video is always kept and has no exclusion action.

With one file, its actual tracks are shown directly. Audio and Subtitles have separate tabs; switching tabs does not change choices or Review. Video stays required and can be inspected separately. **Details** contains file information and destination editing without a second track list. With multiple files, remove a group to change its editable tracks together, or expand it for per-file exceptions. Restore reverses an editable track choice. Completed results are unaffected. Grouping uses metadata, not spoken content. The queue-row cross removes that unfinished file from the list without deleting its media.

Track sizes are labelled reported (container statistics) or estimated (bitrate and duration). Missing or unreliable statistics show size unavailable. They are not measured savings or preservation evidence; no extra full payload scan is performed solely to calculate these labels.

**Remove all subtitles** clears subtitle selections for included, editable files. Their muted cards remain available for Restore before Review. Audio, video, attachments and completed results are unaffected.

During inspection, the workspace shows the current backend stage and elapsed time for the active file. Remaining time is explicitly unavailable because the tools do not report a reliable fraction of inspection completed. A slow-check notice appears after 30 seconds; it is not a timeout prediction. Cancel remains available.

## Choose the result

Choose either visible output card above **Review changes**. The folder chooser appears for Copy; Replace explains the temporary space needed. Changing mode invalidates affected plans and requires fresh Review.

**Remember this choice** saves only the selected Copy/Replace default in this portable copy. New and restored files still need fresh inspection and explicit Review; remembered replacement is not reusable approval. With remembering off, a new queue defaults to Copy. A save failure is shown as a session-only preference.

| Output mode | Result |
| --- | --- |
| **Create trimmed copies** (default) | Creates `name.trimmed.mkv` or `name.trimmed.mp4` beside each original. Choose an output folder to save elsewhere. Originals remain unchanged. |
| **Replace original files** | Creates and verifies a temporary result, then replaces the file at its original path. Review identifies every replacement. |

Existing copy destinations are never overwritten or automatically renamed. An **Unchanged** file creates no output and is never replaced.

Replacement still needs space for a complete temporary result. Read-only files, files with multiple hard links and unsupported Linux permissions require copy mode. If replacement is interrupted or uncertain, keep the reported backup and recovery files until the outcome is resolved. See [replacement details](ARCHITECTURE.md#output-choice-and-source-replacement).

## Review and run

Select **Review changes** and check each file's destination and removed tracks. Retained video/audio/subtitle counts stay visible; expand retained details for the file-local IDs and metadata. Removing the last audio track requires separate confirmation for that file. Unresolved inspection errors in included files prevent Review.

Review does not process files. Use the separate action to create copies or replace the stated number of originals. Selection, destination or output-mode changes require another Review; language and theme changes do not.

Question-mark help sits beside its subject and opens on hover or keyboard focus. Escape dismisses the open help without changing choices or plans.

## Folders and rules

**Include subfolders** searches below the chosen folder. A separate output folder preserves relative directories: `Series/Season 2/episode.mkv` becomes `Output/Season 2/episode.trimmed.mkv`. Saving beside originals uses their existing directories.

The queue holds at most 100 entries. Scans have [additional limits](SUPPORTED_FORMATS.md#input-discovery-scope), reject inaccessible or redirected directories, and never silently accept an incomplete selection. Existing `.trimmed` files are ordinary inputs, not automatically excluded by name.

Language rules are optional: **Preview** the proposed choices, then **Apply**. Ambiguous language metadata can leave a file unchanged for manual selection. Title filters match literal text; forced-subtitle filters select whole tracks marked as forced. See [matching rules](SUPPORTED_FORMATS.md#language-selection-rules).

Presets store rule settings. Select a preset, explicitly **Load** it, then Preview and Apply. Neither rules nor presets start processing.

## Pause, cancel and warnings

Files run one at a time. **Pause after current file** lets the active file finish. **Cancel batch** requests cancellation and prevents the next file from starting; completed results remain intact. Cancellation before the final replacement step preserves the original; a replacement already in progress may complete.

A warning or failure pauses the queue. For an MKV warning, inspect the message and choose whether to publish or discard the verified result. Failed verification cannot be overridden. When closing during work, wait for MovieSift to stop its worker and report the outcome before moving or deleting working files.

**New queue** clears the list after confirmation without deleting media, completed outputs, settings or presets.

## Saved lists and interrupted work

After restarting, **Restore saved list** recovers unfinished paths and destinations; **Discard saved list** clears the listing. Neither processes or deletes media. Restored files need fresh inspection, selections and Review.

An unreadable list is left untouched. Back it up before correcting it externally, then use **Reload saved list** with an empty queue.

Interrupted-operation records are separate. Keep the old application directory and reported working files until recovery is resolved. Do not rename working directories or edit journal paths. See [recovery details](ARCHITECTURE.md#unfinished-queue-list-recovery).

## Theme and language

Use the translation-icon button for **Interface language** and **Theme** for colors and reduced motion. The panel has one sticky heading and an icon Close button; outside click and Escape also close it. Colors show their effective HEX values and swatches. Reset stays at the panel bottom while long settings scroll separately. Neither panel changes track selections, plans or media metadata. A preference that cannot be saved may still apply for the current session; MovieSift reports the problem without interrupting media work.

Portable packages include English only. The language chooser offers English plus Russian, German, Spanish, French, Brazilian Portuguese, Ukrainian, Polish, Turkish, Japanese, Korean, Simplified Chinese, Swedish, Danish and Czech. Selecting an uninstalled language explicitly downloads its complete translation from the MovieSift GitHub repository, with progress and cancellation. Switching a compatible installed language needs no download bar. The current language stays active until validation completes. Network, compatibility and cache errors remain visible; **Retry** downloads a replacement without deleting the previous cache first.

Downloaded packs are stored in `data/translations` and work offline after restart. On the first launch of a new build, installed packs are checked/refreshed in the background against one immutable official catalog snapshot, with a shared 30-second limit. It does not install additional languages. Valid offline translations remain usable if refresh fails; incompatible old files remain on disk while the UI falls back to English. A successful refresh updates the active dictionary without changing language or media choices. Preset names, source paths, track titles and backend diagnostics remain original. Translation packs are data, not executable extensions. Complete key coverage does not constitute native-speaker review.

Repository translation sources now live in `lang`. The released 1.2.0 downloader uses the previous repository path and cannot install new languages after that move; update MovieSift first. Existing cached packs remain on disk and the updater is independent of the translation endpoint. New builds can use their own immutable source snapshot when the current catalog has an incompatible text contract.

On Windows, the application uses an integrated caption with minimize, maximize/restore and close buttons. Drag the header or double-click it to maximize/restore. Resizing remains available. Close retains the existing warning decisions and safe asynchronous shutdown. Linux retains the desktop's native decorations. Both platforms use the film/scissors application icon; no minimize-to-tray behavior is introduced.

## Upgrading a portable copy

### Install updates

MovieSift checks GitHub for newer stable releases after startup. The permanent **Updates** header button opens its own panel, separate from Theme, with the installed version, check result, **Check now** and the startup-check preference. A newer available version is shown beside the button. Closing an offer keeps that indicator available. Manual checks work even when startup checks are disabled; network errors do not block media use.

Only **Download and update** downloads and installs, then restarts MovieSift. Download progress and cancellation remain visible. Finish processing, resolve recovery notices and allow settings saves to complete first. Portable settings, custom colors, rule presets, downloaded translations and the unfinished list are retained; track choices and Review must be recreated after restart. Installed translations are checked against the new build's text contract on startup as described above. There is no automatic download or installation on startup.

Installation requires a local writable portable folder and its bundled updater. Unsupported locations or permissions require manual migration. Windows updates preserve current access rules as a fixed snapshot; later parent-folder permission changes do not propagate automatically.

The old application remains in the adjacent `.moviesift-update-…/previous` folder. Remove it after checking the new version. If a switch is interrupted, consult `UPDATE-RESULT.txt` in the same update folder. Never replace files beneath a running application.

### Upgrade manually

The 1.2 portable root has one launcher (`MovieSift.exe` on Windows, `MovieSift` on Linux), `app/`, `data/`, and small license/provenance/checksum records. Internal assemblies, runtime, updater, tools, notices and documentation belong under `app/`. Keep that entire directory alongside the launcher; do not move individual DLLs or start the internal updater yourself. The layout does not depend on Windows Hidden attributes or runtime extraction into temporary directories.

The new updater understands this layout and legacy flat packages, but the immutable 1.1 updater does not understand nested packages. Consequently, 1.0/1.1 → 1.2 requires the manual steps below. The old updater rejects an incompatible archive before switching the current installation; this is not a successful upgrade. Existing release archives are not replaced.

1. Finish or explicitly discard pending work, then close the old copy.
2. Extract the new release into a separate writable directory. Keep all bundled files together.
3. To retain preferences, copy `data/settings.json`, `data/appearance.json`, `data/updates.json`, `data/output-preferences.json`, `data/rule-presets.json` and the `data/translations` directory if present. Check access permissions when migrating data.
4. Optionally copy `data/queue-list.json`; Restore still requires fresh inspection and Review.

Keep the old directory for unresolved work. Do not transfer unresolved `data/ownership` records or mix media tools from different releases.

## Compatibility and help

See [supported formats](SUPPORTED_FORMATS.md) for media and system requirements. Problem reports should include the version, operating system, steps and displayed error. Expand the file diagnostics for the precise failure. Returned inspection, review and processing failures are also recorded under portable `data/logs`; successful remux alone does not imply successful verification or publication. Remove personal filenames and paths from shared logs.
