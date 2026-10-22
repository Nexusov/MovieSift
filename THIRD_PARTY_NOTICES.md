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

The NuGet inventory records restored package metadata; the separate per-platform [distribution reports](docs/verification/distribution-audit.md) account for every actual staged and archived file. Exact SHA256 matches to signed NuGet archive members distinguish shipped payloads from metadata-only/build dependencies. Native PDBs are distributed assets, not build-only files. Restore content hashes and signed-archive SHA512 have different semantics and are reported separately.

The collector preserves package license/notice files, declared license-file paths and `.nuspec` metadata. It also supplies missing component-specific texts for Avalonia 12.1.4, MicroCom.Runtime 0.11.6, Tmds.DBus.Protocol 0.94.1, Serilog 4.3.1 and Serilog.Sinks.File 7.0.0 from the exact repository commits declared by their packages. URLs and SHA256 are pinned in `build/license-materials.lock.json`; a missing or changed required material fails packaging. This correction is not a finding that no generic Apache text previously shipped, or that all attribution requirements are now complete.

## MKVToolNix artifacts

`build/tools.lock.json` is authoritative for exact versioned URLs and hashes. Windows uses the official portable x64 ZIP. Linux uses a pinned official x86_64 AppImage extracted during the build; its dependency tree is retained. Acquisition also verifies the versioned MKVToolNix source tarball against upstream's pinned SHA256 and includes that complete archive under `tools/licenses/corresponding-source`, with COPYING, README, and `doc/licenses` under `tools/licenses/source-notices`. This supplies MKVToolNix's own source/notices; it does not by itself supply every external dependency's corresponding source or the upstream binary build configuration. The application invokes only `mkvmerge` and `mkvextract`; no FFmpeg binary is bundled for future features.

MKVToolNix includes separately licensed libEBML/libMatroska/librmff (LGPL), avilib (GPL), JSON and pugixml (MIT), utf8-cpp (Boost), fmt, Qt and artwork. The verified source's actual `lib/fmt/LICENSE` is MIT-style with an optional compiled-object exception; upstream's BSD description/notice filename is not substituted for that text. Acquisition now also extracts that license, three vendor LGPL texts, and the source archive's Windows/AppImage/configure recipes. They supplement the complete source tarball, without proving the published binary build inputs.

Observed Windows workers embed static Qt **6.10.2**; Linux retains Qt **6.6.1**, including **VirtualKeyboard**, plus other shared libraries and three Boost static archives even when the CLI never loads them. VirtualKeyboard's exact 6.6.1 source offers GPL-3.0-only/commercial terms, not LGPL. QtCore has module/file-specific licensing alternatives; neither static linkage nor a generic Qt license label determines the chosen path. Exact upstream Qt/OpenSSL/FLAC terms and selected source/header evidence are hash-pinned in `build/tool-materials.lock.json` and copied under `tools/licenses/upstream-terms`; they are not complete corresponding source for downstream builds.

The exact texts remain the primary record. Running a separate executable does not remove its redistribution obligations, automatically assign its license to the C# code, or settle whether a particular combination is an aggregate. [The original-code license proposal](docs/license-decision.md) keeps that question separate from the author's pending choice.

## Outstanding public-release obligations

- Select a license for original application code explicitly.
- Resolve the remaining binary/source/build identities and static-component closure recorded in the [distribution audit](docs/verification/distribution-audit.md), including unused AppImage libraries, Qt modules and opaque resources. Complete file coverage is not complete obligation resolution.
- Provide the applicable corresponding-source/build material for the exact redistributed MKVToolNix binaries and their covered dependencies using a distribution method that satisfies their licenses. A homepage link alone is insufficient. Include source versions, build configuration/patches, and any required offers/material with a future public release.
- Confirm that all notices, attribution, license texts, and applicable relinking/source obligations are satisfied for the final shipped closure.
- Recheck bundled runtime/tool security updates before releasing and rebuild when the self-contained runtime changes.

The current scripts and recorded upstream links improve provenance but do not declare these outstanding steps complete. Do not publish release assets until they are resolved.
