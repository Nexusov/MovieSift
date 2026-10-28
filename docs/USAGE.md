# MovieSift user guide

See the [README](../README.md) for downloads and startup.

**Version note:** this guide describes the current source. Published **1.0.0** creates copies in a selected output folder; it has neither source replacement nor an updater. Download and extract the first release containing those features manually.

## Choose what to keep

Add local files or a folder. **Audio** and **Subtitles** show tracks from every included file, including tracks found in only one episode. Checked tracks are kept; unchecked tracks are removed from the result. All tracks start selected, and video is always kept.

Use a group checkbox to change its tracks together, or expand it for per-file exceptions. Grouping uses metadata, not spoken content. Excluding a file removes it from these actions without deleting it.

**Remove all subtitles** clears subtitle selections for included, editable files. You can select them again before Review. Audio, video, attachments and completed results are unaffected.

## Choose the result

| Output mode | Result |
| --- | --- |
| **Create trimmed copies** (default) | Creates `name.trimmed.mkv` or `name.trimmed.mp4` beside each original. Choose an output folder to save elsewhere. Originals remain unchanged. |
| **Replace original files** | Creates and verifies a temporary result, then replaces the file at its original path. Review identifies every replacement. |

Existing copy destinations are never overwritten or automatically renamed. An **Unchanged** file creates no output and is never replaced.

Replacement still needs space for a complete temporary result. Read-only files, files with multiple hard links and unsupported Linux permissions require copy mode. If replacement is interrupted or uncertain, keep the reported backup and recovery files until the outcome is resolved. See [replacement details](ARCHITECTURE.md#output-choice-and-source-replacement).

## Review and run

Select **Review changes** and check each file's tracks and destination. Removing the last audio track requires separate confirmation for that file. Unresolved inspection errors in included files prevent Review.

Review does not process files. Use the separate action to create copies or replace the stated number of originals. Selection, destination or output-mode changes require another Review; language and theme changes do not.

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

## Appearance and language

Use the translation-icon button for **Interface language** and **Appearance** for themes, custom colors and reduced motion. Escape closes the current cosmetic panel and returns focus to its button. Neither action changes track selections, plans or media metadata. A preference that cannot be saved may still apply for the current session; MovieSift reports the problem without interrupting media work.

Portable packages include English only. The language chooser offers English, Russian, German, Spanish, French, Brazilian Portuguese, Ukrainian, Polish, Turkish, Japanese, Korean and Simplified Chinese. Selecting another language explicitly downloads its complete translation from the MovieSift GitHub repository, with progress and cancellation. The current language stays active until validation completes. Network, compatibility and cache errors remain visible; **Retry** downloads a replacement without deleting the previous cache first.

Downloaded packs are stored in `data/translations` and work offline after restart. Startup never downloads a translation automatically. If a saved language is unavailable or invalid, the application stays usable in English and explains how to download it. Preset names, source paths, track titles and backend diagnostics remain original. Translation packs are data, not executable extensions. New source-tree functionality requires a corresponding newly built application; the already published 1.0.0 archives are unchanged.

On Windows, the application uses an integrated caption with minimize, maximize/restore and close buttons. Drag the header or double-click it to maximize/restore. Resizing remains available. Close retains the existing warning decisions and safe asynchronous shutdown. Linux retains the desktop's native decorations. Both platforms use the film/scissors application icon; no minimize-to-tray behavior is introduced.

## Upgrading a portable copy

### Install updates

MovieSift checks GitHub for newer stable releases after startup. **Close** dismisses the notification; **Update available** remains in the header and **Appearance → Updates**. That panel also offers manual checks and an option to disable startup checks. Network errors do not block media use.

Only **Install and restart** downloads and installs. Finish processing, resolve recovery notices and allow settings saves to complete first. Settings and the unfinished list are retained; track choices and Review must be recreated after restart.

Installation requires a local writable portable folder and its bundled updater. Unsupported locations or permissions require manual migration. Windows updates preserve current access rules as a fixed snapshot; later parent-folder permission changes do not propagate automatically.

The old application remains in the adjacent `.moviesift-update-…/previous` folder. Remove it after checking the new version. If a switch is interrupted, consult `UPDATE-RESULT.txt` in the same update folder. Never replace files beneath a running application.

### Upgrade manually

1. Finish or explicitly discard pending work, then close the old copy.
2. Extract the new release into a separate writable directory. Keep all bundled files together.
3. To retain preferences, copy `data/settings.json`, `data/appearance.json`, `data/updates.json` and `data/rule-presets.json` if present. Check access permissions when migrating data.
4. Optionally copy `data/queue-list.json`; Restore still requires fresh inspection and Review.

Keep the old directory for unresolved work. Do not transfer unresolved `data/ownership` records or mix media tools from different releases.

## Compatibility and help

See [supported formats](SUPPORTED_FORMATS.md) for media and system requirements. Problem reports should include the version, operating system, steps and displayed error. Remove personal filenames and paths from shared logs.
