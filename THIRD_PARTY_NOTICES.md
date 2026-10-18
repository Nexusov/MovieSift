# Third-party notices and provenance

No license for original TrackTrim code has been selected. Do not infer an author identity or apply MIT to every shipped file. The archives produced locally by this branch are acceptance artifacts; public release readiness is not claimed. A review/testing label does not exempt binary redistribution from any license duty. CI builds and tests its archives locally on each runner but uploads only test/provenance evidence until redistribution/source obligations are resolved.

## Application and build dependencies

Versions are pinned in `global.json`, `Directory.Packages.props`, the committed package lockfiles, and `build/tools.lock.json`. Package SHA512 integrity values are in the lockfiles; binary downloads are SHA256 checked. Package archives include `build-provenance.json`, `tool-provenance.json`, and `licenses/nuget/inventory.json`, recording the actual SDK/runtime and restored package metadata/license files.

| Component | Version / role | License / primary source |
| --- | --- | --- |
| .NET SDK | 10.0.402; development/build only | MIT and component notices; [dotnet/sdk](https://github.com/dotnet/sdk) |
| .NET runtime | 10.0.13; self-contained production runtime | MIT plus runtime third-party notices; [dotnet/runtime](https://github.com/dotnet/runtime) |
| Avalonia, Avalonia.Desktop, Fluent theme | 12.1.4; production UI/native assets | MIT plus bundled component notices; [Avalonia](https://github.com/AvaloniaUI/Avalonia) |
| CommunityToolkit.Mvvm | 8.4.2; production ViewModel helpers | MIT; [dotnet CommunityToolkit](https://github.com/CommunityToolkit/dotnet) |
| Serilog | 4.3.1; local application diagnostics | Apache-2.0; [Serilog](https://github.com/serilog/serilog) |
| Serilog.Sinks.File | 7.0.0; bounded file diagnostics | Apache-2.0; [file sink](https://github.com/serilog/serilog-sinks-file) |
| xUnit, test SDK/runner, Avalonia.Headless | Locked test versions; development only | Package-specific metadata/notices in restored packages; excluded from runtime project references |
| MKVToolNix | 102.0; separate production worker executables | GPL v2 as stated by upstream; [versioned binaries/source](https://mkvtoolnix.download/), [requirements/included components](https://mkvtoolnix.download/doc/README.md) |

The package inventory is a record of restored package metadata, not an assertion that every restored package ships or that all release obligations are complete. Native Skia/HarfBuzz and other transitive packages retain their actual notices. The packaging script copies license/notice files and `.nuspec` metadata from the restored packages without changing their terms.

## MKVToolNix artifacts

`build/tools.lock.json` is authoritative for exact versioned URLs and hashes. Windows uses the official portable x64 ZIP. Linux uses a pinned official x86_64 AppImage extracted during the build; its dependency tree is retained. Acquisition also verifies the versioned MKVToolNix source tarball against upstream's pinned SHA256 and includes that complete archive under `tools/licenses/corresponding-source`, with COPYING, README, and `doc/licenses` under `tools/licenses/source-notices`. This supplies MKVToolNix's own source/notices; it does not by itself supply every external dependency's corresponding source or the upstream binary build configuration. The application invokes only `mkvmerge` and `mkvextract`; no FFmpeg binary is bundled for future features.

MKVToolNix includes several separately licensed components, including libEBML/libMatroska/librmff (LGPL), avilib (GPL), JSON and pugixml (MIT), utf8-cpp (Boost), fmt (BSD), Qt, and upstream artwork. The exact notices/license texts bundled with the acquired artifact remain the primary record; this list is not a substitute for those texts. Running a separate executable does not remove its redistribution obligations or automatically assign that executable's license to the original C# code.

## Outstanding public-release obligations

- Select a license for original application code explicitly.
- Complete the exact native/transitive component audit for the actual Windows and Linux archives, including Qt and any AppImage-provided libraries/artwork.
- Provide the applicable corresponding-source/build material for the exact redistributed MKVToolNix binaries and their covered dependencies using a distribution method that satisfies their licenses. A homepage link alone is insufficient. Include source versions, build configuration/patches, and any required offers/material with a future public release.
- Confirm that all notices, attribution, license texts, and applicable relinking/source obligations are satisfied for the final shipped closure.
- Recheck bundled runtime/tool security updates before releasing and rebuild when the self-contained runtime changes.

The current scripts and recorded upstream links improve provenance but do not declare these outstanding steps complete. Do not publish release assets until they are resolved.
