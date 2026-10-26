# MovieSift

Remove unwanted audio and subtitle tracks from MKV and supported MP4 files without re-encoding.

Process a single movie or a whole series in one workspace. MovieSift 1.0 creates `.trimmed` copies and keeps the originals unchanged.

**[Download 1.0.0 for Windows x64](https://github.com/Nexusov/MovieSift/releases/download/v1.0.0/MovieSift-win-x64.zip)** · **[Download 1.0.0 for Linux x64](https://github.com/Nexusov/MovieSift/releases/download/v1.0.0/MovieSift-linux-x64.tar.gz)** · [Release notes](https://github.com/Nexusov/MovieSift/releases/tag/v1.0.0)

![MovieSift in dark mode](docs/images/moviesift-dark.png)

## Get started

Extract the complete archive into a writable folder. Run `MovieSift.exe` on Windows or `./MovieSift` on Linux.

The .NET runtime and media tools are included. Linux also needs the [listed system libraries](docs/SUPPORTED_FORMATS.md#platform-and-native-requirements).

1. Add files or a folder.
2. Uncheck the audio and subtitle tracks you want to remove. The shared list includes tracks found in only some files; expand a group to adjust individual files.
3. Choose an output folder and select **Review changes**.
4. Check the summary, then select **Create copies**.

Video tracks are always kept. Files with no changes are skipped, and existing output files are never overwritten. Removing every audio track requires confirmation.

## Features

- One workspace for individual files, folders and series.
- Shared track selection with per-file exceptions.
- Remove all subtitles in one action.
- Optional language rules and reusable presets.
- English and Russian interface.
- Light and dark themes, custom colors and reduced motion.

## Supported formats

- **MKV:** audio, subtitles, attachments, ordinary chapters and supported metadata.
- **MP4:** ordinary H.264 video, AAC audio and mov_text subtitles, with supported chapters and artwork.

MovieSift validates files before processing. See [supported formats and limits](docs/SUPPORTED_FORMATS.md) for the compatibility details. Outstanding desktop and scaling checks are recorded in the [release checklist](docs/release-checklist.md).

## Documentation

- [User guide: queues, rules and saved lists](docs/USAGE.md)
- [Build, run and contribute](CONTRIBUTING.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Release downloads, checksums and matching source materials](https://github.com/Nexusov/MovieSift/releases/tag/v1.0.0)

<a id="upgrading-an-earlier-portable-candidate"></a>

Upgrading from an earlier portable version? Follow the [upgrade instructions](docs/USAGE.md#upgrading-a-portable-copy) before moving settings or unfinished work.

## License

MovieSift is available under the [MIT License](LICENSE).

Bundled components retain their own [licenses and notices](THIRD_PARTY_NOTICES.md). Matching source and build materials are provided with the release.
