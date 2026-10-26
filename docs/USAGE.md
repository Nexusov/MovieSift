# MovieSift user guide

This guide describes the published [MovieSift 1.0.0](https://github.com/Nexusov/MovieSift/releases/tag/v1.0.0). See the [README](../README.md#get-started) for download and startup instructions.

## Choose what to keep

Add local files or a folder. The **Audio** and **Subtitles** sections show tracks across every included file, including tracks found in only one episode. A checked track is kept; an unchecked track is omitted from the copy. Expand a group to make per-file choices. Video tracks are always kept.

**Remove all subtitles from copies** clears subtitle selections for included, editable files. You can select them again before Review. This action does not change audio, attachments, excluded files or completed results.

Choose an output folder, select **Review changes**, and check each file's plan. Removing every audio track requires separate confirmation for that file. Files marked **NoChange** need no output folder and create no copy. Review does not start processing: use the separate **Create copies** action when ready.

Original files remain unchanged in 1.0.0. Keeping the original and the smaller copy uses space for both files. Existing outputs are not replaced or automatically renamed; choose a different destination if a name is already taken.

## Folders and rules

**Include subfolders** keeps relative subdirectories under the output folder you choose. A queue can contain up to 100 entries; a folder scan examines at most 10,000 filesystem entries and 32 directory levels. Network locations and redirected directories, including symlinks and junctions, are not followed. An incomplete folder scan does not silently add a partial selection.

Language rules are optional. Use **Preview** to inspect their proposed changes, then **Apply** to change the current selections. Title filters match literal text; forced-subtitle filters use the track's actual forced flag. A preset stores rule settings and is applied only after you explicitly **Load** it. Rules and presets never start processing. See [rule details](SUPPORTED_FORMATS.md#language-selection-rules).

## Pause, cancel and warnings

Files are processed one at a time. **Pause after current file** lets the current file finish. **Cancel batch** requests cancellation of the active job and prevents the next file from starting. Previously completed outputs stay intact.

A warning or failure pauses the queue. If an MKV result needs warning review, read the message and choose whether to publish or discard that result. A failed verification cannot be overridden. When closing during work, let MovieSift finish stopping its worker and report the result before moving files or deleting temporary directories.

**New queue** clears the current list after confirmation. It does not delete media, completed outputs, preferences or presets.

## Saved lists and interrupted work

After a restart, choose **Restore saved list** to load unfinished paths and destinations, or **Discard saved list** to clear the saved listing. Neither action processes media or deletes original files.

Restored files need fresh inspection, track selection and Review. Old track selections and approvals are not reused. If a saved list is unreadable, MovieSift leaves it untouched and reports the problem. Keep a backup before correcting it externally, then use **Reload saved list** with an empty queue.

Records of interrupted media operations are separate from the saved list. Do not rename private working directories or edit their recorded paths. Keep the application directory available while any interrupted operation remains unresolved. The [recovery design](ARCHITECTURE.md#unfinished-queue-list-recovery) explains the distinction.

## Appearance and language

Open **Appearance & language** to switch between English and Russian, choose a theme, customize colors or reduce motion. Changes preserve your track choices and reviewed plans. Escape closes the panel and returns keyboard focus.

If a preference cannot be saved, it may still apply for the current session. MovieSift reports the problem without treating a cosmetic save failure as a media-processing failure.

## Upgrading a portable copy

MovieSift 1.0.0 does not check for or install updates. Download a newer release from [GitHub Releases](https://github.com/Nexusov/MovieSift/releases).

1. Finish or explicitly discard pending work in the old copy and close it.
2. Extract the new archive into a new writable directory. Keep its complete contents together.
3. If needed, copy `data/settings.json`, `data/appearance.json` and `data/rule-presets.json` from the old directory.
4. A copied `data/queue-list.json` still requires explicit Restore, fresh inspection and Review.

Keep the old application directory for any unresolved work. Do not copy unresolved `data/ownership` records, rename private job directories or edit journal paths. Keep each release's bundled tools with that release; mixing application and worker versions can prevent processing.

## Compatibility and help

See [supported formats](SUPPORTED_FORMATS.md) for codec, metadata and platform requirements. The [release checklist](release-checklist.md) records remaining desktop, scaling and cancellation/close checks.

When reporting a problem, include the MovieSift version, operating system, the action you took and the displayed error. Remove personal filenames and paths from any logs you share.

## Changes in the development build

These features are being prepared after 1.0.0 and are not included in the download links above.

### Choose the result

**Create a copy** is the default. With no output folder selected, each file produces a `.trimmed` copy beside its original. You can still select a separate output folder.

**Replace originals** creates and verifies temporary results before replacing the selected files at their original paths. Review shows exactly which files will be replaced; changing the mode requires another Review. NoChange files remain untouched. Cancel or a verification error before replacement leaves the original in place.

If a replacement outcome is uncertain, MovieSift keeps its backup and recovery information and pauses the queue. Follow the displayed paths; do not delete those files until the result is understood. Read-only or multiply linked files are not replaceable. Linux files with unsupported permission/ownership or extended ACL requirements require copy mode.

### Install updates

After opening, MovieSift can check GitHub for a newer stable version. **Install and restart** downloads and checks the package, closes the application safely and starts the new version. **Close** dismisses the notification; the available update remains in the header and in **Appearance & language → Updates**. You can also check manually or disable startup checks there.

Installation waits until processing, recovery decisions and settings saves are finished. Your portable settings and unfinished file list are retained. Track choices and Review are recreated after restart, just as when restoring a saved list. Network errors do not block media use.

Automatic installation requires a local writable portable application folder and its bundled helper. A source/debug build or a read-only location may require manual downloading. The updater keeps the old application in its adjacent `.moviesift-update-…/previous` folder; remove that backup after verifying the new version. If an interrupted directory switch requires manual recovery, consult `UPDATE-RESULT.txt` in that same folder. Do not replace files beneath a still-running application.

Version 1.0.0 has no updater. The first version with this feature must be downloaded and extracted manually; later compatible releases can be installed from the application.
