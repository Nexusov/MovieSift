# MovieSift 1.1.0

## Changes since 1.0.0

- Exclude unwanted tracks with × and restore them in place. Excluded cards remain visible and muted; video is always retained.
- Edit a single film's actual tracks directly. Multiple files retain grouped choices and per-file exceptions.
- Review removed tracks and a compact retained-track summary before a separate processing action. Disclosure icons and scrollbar spacing are corrected.
- Choose between trimmed copies beside originals, a separate output folder, or verified replacement of originals. Replacement still needs temporary disk space and explicit confirmation.
- See actual inspection stages and elapsed time. Remaining time is explicitly unavailable when the backend cannot estimate it.
- Choose interface language independently from Appearance. English is bundled; eleven additional translations download explicitly from the official repository and are cached for offline use.
- Use the film/scissors application icon and integrated Windows caption controls. Linux retains native window decorations.
- Check for future stable releases and install verified portable updates using the bundled helper.

## Install or upgrade

Extract the complete archive into a writable directory and keep its files together. Version 1.0.0 has no updater: install 1.1.0 manually in a separate directory. Follow the [user guide](USAGE.md#upgrading-a-portable-copy) when transferring portable data; preserve the old directory for unresolved work.

For source replacement, use Copy if files are read-only, have multiple hard links, or carry unsupported permissions. Replacement is not an in-place edit and requires space for a complete verified temporary result. Existing Copy destinations are never overwritten. Unchanged plans create no output and do not replace originals.

## Compatibility and acceptance

The MKV and supported H.264/AAC/mov_text MP4 preservation contracts are unchanged. Native Windows 11/Ubuntu Desktop, visual/scaling, and some cancellation/close/warning scenarios remain unverified as recorded in the [release checklist](release-checklist.md). Automated, headless and container checks do not establish these desktop results. Translation coverage is not native-speaker certification.

Original MovieSift code is MIT-licensed. Keep matching source/build materials, inventories, summaries and notices with the binary downloads; see [distribution requirements](DISTRIBUTION.md).
