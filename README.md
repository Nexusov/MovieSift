# MovieSift

Remove unwanted audio and subtitle tracks from videos without re-encoding.

Choose tracks for a single movie or a whole series in one workspace. Available for Windows and Linux.

**[Download latest release](https://github.com/Nexusov/MovieSift/releases/latest)**

<img src="docs/images/moviesift-dark.png" alt="MovieSift in dark mode, with files and shared track selection" width="800">

## Get started

These controls describe **1.1.0**. Version **1.0.0** uses Keep checkboxes; its archives are unchanged.

1. Download and extract the archive for your platform.
2. Run `MovieSift.exe` on Windows or `./MovieSift` on Linux.
3. Add files or a folder, then use **Exclude** (×) for unwanted tracks. Muted cards stay in place and offer **Restore**.
4. Review the changes and start processing.

The runtime and media tools are included. Linux requires the [listed system libraries](docs/SUPPORTED_FORMATS.md#platform-and-native-requirements).

## Features

- Select tracks across multiple files, with individual adjustments.
- Exclude all subtitle tracks in one action.
- Save language rules as reusable presets.
- Choose from 12 interface languages, with explicit on-demand translation downloads and offline reuse.
- Use light or dark themes and custom colors independently of language.

## Formats

Supports MKV and a defined MP4 subset: H.264 video, AAC audio and mov_text subtitles. See [format compatibility](docs/SUPPORTED_FORMATS.md) for details.

## Documentation

- [User guide](docs/USAGE.md)
- [Build, run and contribute](CONTRIBUTING.md)
- [Architecture](docs/ARCHITECTURE.md)

## License

[MIT](LICENSE). Bundled components retain their own [licenses and notices](THIRD_PARTY_NOTICES.md).
