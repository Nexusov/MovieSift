# Third-party notices and provenance

Original MovieSift code is licensed under [MIT](LICENSE), copyright (c) 2026 Nexusov. Bundled components retain their own licenses. Release downloads include matching source and build materials; see [distribution requirements](docs/DISTRIBUTION.md).

## Application and build dependencies

Versions are pinned in `global.json`, `Directory.Packages.props`, the committed package lockfiles, `build/tools.lock.json`, `build/mkv-source.lock.json`, and `build/ffmpeg.lock.json`. Package SHA512 integrity values are in the lockfiles; binary/source downloads are SHA256 checked. Package archives include `build-provenance.json`, `tool-provenance.json`, `ffmpeg-provenance.json`, Windows `mkv-source-provenance.json`, and `licenses/nuget/inventory.json`, recording actual builds and restored package metadata/license files.

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
| FFmpeg / ffprobe | `9.0.2-MovieSift-source-2a571b606854`; separate production MP4 workers | Source-built LGPL-2.1-or-later, explicit static zlib 1.3.2 and applicable compiler-runtime terms; [exact FFmpeg source](https://github.com/FFmpeg/FFmpeg/tree/2a571b606854520cf89804d8030c8b328e621689), [FFmpeg legal guidance](https://ffmpeg.org/legal.html) |

Per-platform distribution reports account for staged and archived files. SHA256 comparisons with signed NuGet archive members distinguish shipped payloads from build-only dependencies. Native PDBs are included in that accounting; restore content hashes and archive SHA512 values are reported separately.

The collector preserves package license/notice files and `.nuspec` metadata. Component-specific texts for Avalonia, MicroCom.Runtime, Tmds.DBus.Protocol, Serilog and Serilog.Sinks.File come from the repository commits declared by those packages. URLs and SHA256 values are pinned in `build/license-materials.lock.json`; missing or changed required materials fail packaging.

## MKVToolNix artifacts

`build/tools.lock.json` pins MKVToolNix 102.0 and its complete source. Windows uses a same-version source build; Linux uses the CLI executables and the verified library closure from the pinned official AppImage. `build/tool-payload.lock.json` records selected and omitted libraries; changed or unaccounted files fail acquisition. Both platforms include complete MKVToolNix source under `tools/licenses/corresponding-source` and upstream notices under `tools/licenses/source-notices`. MovieSift invokes `mkvmerge`, `mkvextract` and `mkvpropedit`; the header editor operates only on an owned input copy for supported legacy DTS timing preparation, never on the original.

MKVToolNix includes separately licensed libEBML/libMatroska/librmff (LGPL), avilib (GPL), JSON and pugixml (MIT), utf8-cpp (Boost), fmt, Qt and artwork. The actual `lib/fmt/LICENSE` is MIT-style with an optional compiled-object exception. These texts, vendor LGPL notices and upstream build recipes are retained with the source materials.

Windows uses `build/build-mkv-windows.sh` with pinned MSYS2 UCRT64 inputs. The GUI is disabled; QtCore **6.12.0** is required. Shipped DLLs must match their binary packages, whose BUILDINFO recipe hashes must match collected source-package PKGBUILDs. `tools/licenses/mkv-source-build` contains complete source packages, patches, binary metadata/notices, the MSYS2 recipe-snapshot BSD3 notice, scripts, configuration and build receipts. GMP, ICU and libvorbis notices are collected from their source archives, including ICU 78.3's Unicode V3 notice. QtCore's GPL2 alternative and GMP's GPL2-or-later alternative at GPL2 are selected. GCC runtime exceptions, winpthreads and LGPL terms remain component-specific.

Linux retains QtCore **6.6.1** and 21 other required libraries. Unused Qt modules and static Boost archives are excluded. Fifteen retained libraries match AlmaLinux RPM members; SourceRPM headers identify 13 complete source RPMs with archives, patches and build specifications. QtCore and the three ICU libraries match the Qt 6.6.1 SDK. Complete QtBase source, SDK configuration and module/runtime notices are collected. `build/tool-materials.lock.json` binds these relationships; sources are under `tools/licenses/corresponding-source` and notices/configuration under `tools/licenses/upstream-terms`. QtCore's GPL2 alternative is selected.

The original license texts remain authoritative. Using separate executables does not remove their redistribution obligations or automatically extend their licenses to MovieSift's C# code.

## FFmpeg artifacts

`build/ffmpeg.lock.json` pins the complete FFmpeg source snapshot `2a571b606854520cf89804d8030c8b328e621689` and zlib 1.3.2 source by SHA256. `build/build-ffmpeg.sh` builds only `ffmpeg` and `ffprobe` with explicit built-in capabilities and static zlib, disabling automatic dependency detection and networking. No optional external codec/hardware libraries or `ffplay` are shipped. Acquisition and inventory reject GPL, version3 and nonfree configuration; this build is LGPL-2.1-or-later. This does not assign that license to original MovieSift code.

Materials include both complete source archives, license texts, the build recipe, configure arguments/output and a build receipt. The receipt binds source/recipe hashes, compiler/target/runtime observations, make/NASM versions and worker hashes. Changed workers or missing materials fail validation. GCC Runtime Library Exception 3.1, GPL3 and applicable MinGW runtime notices are collected separately.

Both builds link pinned zlib statically. Other runtime dependencies are limited to the recorded Windows OS/UCRT/API-set DLLs or Linux loader/libc/libm/libmvec/libgcc. PE imports and `ldd` output are recorded; undeclared dependencies fail acquisition.

## Redistribution

Distribute the applicable source archives, patches, build scripts/configuration and notices for the exact binaries supplied. Preserve attribution and any applicable source/relinking requirements; upstream homepage links do not replace those materials. Mechanical inventory checks do not establish license compatibility. Follow [distribution requirements](docs/DISTRIBUTION.md) and the [release checklist](docs/release-checklist.md) when publishing or changing bundled components.

## Filee Soft appearance reference

MovieSift’s Soft styles and render-only interaction timing are adapted from the MIT-licensed Filee visual reference at commit `e830916e7f02526bff6b7576edc22e51ecf9c126` ([source](https://github.com/KnifeLemon/Filee/tree/e830916e7f02526bff6b7576edc22e51ecf9c126)). Copyright (c) 2026 Myeonggu Kim (KnifeLemon) and Filee contributors. The complete [MIT notice](docs/third-party/Filee-LICENSE.txt) is included with the source and portable documentation. The palette validation, appearance storage and MovieSift workflow remain MovieSift implementations; no Filee conversion architecture, fonts or unrelated assets are bundled.
