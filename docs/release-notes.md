# MovieSift 1.0.0

Remove unwanted audio and subtitle tracks from MKV and supported MP4 files without re-encoding.

## Downloads

- **[Windows x64](https://github.com/Nexusov/MovieSift/releases/download/v1.0.0/MovieSift-win-x64.zip)** — extract the ZIP and run `MovieSift.exe`.
- **[Linux x64](https://github.com/Nexusov/MovieSift/releases/download/v1.0.0/MovieSift-linux-x64.tar.gz)** — extract the archive and run `./MovieSift`. See the [required system libraries](SUPPORTED_FORMATS.md#platform-and-native-requirements).

The .NET runtime and media tools are included. Keep the extracted application folder together. For an existing installation, follow the [portable upgrade instructions](USAGE.md#upgrading-a-portable-copy).

## Highlights

- Process individual files or a whole folder in one workspace.
- Choose tracks across multiple files, with per-file adjustments.
- Remove all subtitles in one action.
- Review changes before creating `.trimmed` copies.
- Use language rules, presets and English/Russian UI.
- Choose a light or dark theme and customize colors.

Original files remain unchanged. MP4 support covers ordinary H.264/AAC/mov_text files; see [format compatibility](SUPPORTED_FORMATS.md). Native desktop/scaling and some cancellation/close scenarios remain unverified; the [release checklist](release-checklist.md) records the details.

## Checksums and source materials

- [SHA256SUMS](https://github.com/Nexusov/MovieSift/releases/download/v1.0.0/SHA256SUMS)
- [Windows matching source/build materials](https://github.com/Nexusov/MovieSift/releases/download/v1.0.0/MovieSift-win-x64-materials-6fb2a2711745.tar.gz)
- [Linux matching source/build materials](https://github.com/Nexusov/MovieSift/releases/download/v1.0.0/MovieSift-linux-x64-materials-6fb2a2711745.tar.gz)

MovieSift uses the [MIT License](../LICENSE); bundled components retain their own terms. The matching bundles include the sources and build materials for the distributed packages. They accompany the binaries on the [release page](https://github.com/Nexusov/MovieSift/releases/tag/v1.0.0), along with inventories and summaries.

<details>
<summary>Release verification</summary>

Tag `v1.0.0` identifies source `6fb2a2711745880d1bb39f1177fadbd6a64827f3`, tree `82d40f3f3b121f828a41713240651d1e22b665dc`. [Main CI](https://github.com/Nexusov/MovieSift/actions/runs/40253431802) and [retained candidate run](https://github.com/Nexusov/MovieSift/actions/runs/40254293203), attempt 1, passed the required Windows/Linux checks, including real media, packaging, inventories, matching materials, relocation and the Linux clean container.

Both packages have 1.0.0 application metadata without a prerelease suffix. All nine release assets were checked against their actual sizes and SHA256 values. Later repository changes do not alter these published binaries or their source identity. GitHub's automatic source downloads do not replace the matching material bundles.

</details>

## Earlier release notes

The former 0.1.0-rc.1 prerelease is superseded by 1.0.0. Its [archived release notes](https://github.com/Nexusov/MovieSift/blob/fd334506e5809673dd375e4031ad6fc1df43b3d4/docs/release-notes.md#moviesift-010-rc1--pre-release) and [verification history](verification/README.md) retain the original source, build and acceptance records.
