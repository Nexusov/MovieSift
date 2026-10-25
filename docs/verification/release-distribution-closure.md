# MovieSift distribution requirements

Original MovieSift code uses [MIT](../../LICENSE), copyright (c) 2026 Nexusov. Third-party notices and source/build obligations remain independent. This document describes the implemented collection route and the remaining delivery requirements; it does not certify a public release or assign new identities to historical binaries.

## Current collection route

| Component | Required package materials and binding |
| --- | --- |
| FFmpeg / ffprobe | Build `9.0.2-MovieSift-source-2a571b606854` from complete pinned FFmpeg commit `2a571b606854520cf89804d8030c8b328e621689`, with explicit static zlib 1.3.2. [The lock](../../build/ffmpeg.lock.json), [recipe](../../build/build-ffmpeg.sh) and actual build receipt bind sources, recipe, enabled configuration and worker bytes. No GPL, version3, nonfree or optional external codec/hardware configuration is admitted. FFmpeg uses LGPL-2.1-or-later for this build; zlib and compiler-runtime terms remain independent. |
| Windows MKVToolNix | Build only the 102.0 CLI pair from complete pinned release source and UCRT64 inputs. Preserve actual workers, recursive ordinary PE-import closure, configuration, compiler observations and matching complete source/patch/build packages. Binary BUILDINFO must match the original PKGBUILD in the collected source package. [Source lock](../../build/mkv-source.lock.json), [builder](../../build/build-mkv-windows.sh) and [collector](../../build/collect-mkv-build.py) define the route; acquisition has no official-binary fallback. |
| Linux MKVToolNix | Retain the pinned 102.0 CLI executables and only the declared native dependency closure extracted from the upstream AppImage at build time. [Payload lock](../../build/tool-payload.lock.json) accounts for retained and omitted members. Actual runtime dependency checks and packaged execution remain required. No FUSE or runtime download is needed. |
| Linux covered libraries | Preserve the complete matched AlmaLinux source RPMs, including upstream archives, downstream patches and RPM build specifications, together with the actual binary-package notices. Shipped-file hashes establish binary-package membership; SourceRPM metadata identifies the collected source package. [Material lock](../../build/tool-materials.lock.json) pins these relationships. |
| QtCore | Preserve complete QtBase sources, downstream patches where applicable, actual configuration and module-specific notices. The implemented MKV distribution route selects the Core GPL2 alternative for Windows QtCore 6.12.0 and Linux QtCore 6.6.1 alongside the GPL2 worker. Package-wide Qt declarations must not be assigned indiscriminately to the retained Core module. |
| Other native libraries and compiler support | Keep actual applicable notices, full matched sources/build inputs where required, GCC Runtime Library Exception 3.1 and MinGW/runtime notices. GMP's GPL2 alternative is selected for this route. GNU libintl/libiconv retain LGPL-2.1-or-later terms separately from unshipped GPL companion programs. Permissive ICU, Boost, cmark, FLAC/Ogg/Vorbis and other components retain their actual terms. |
| .NET, NuGet and adapted appearance styles | Preserve the complete self-contained publish output, exact package-member inventory, copied package-specific notices and [third-party acknowledgments](../../THIRD_PARTY_NOTICES.md). Generated apphost/runtime files are not classified as original C# code. Adapted Filee styles retain their separate MIT attribution. |

The existing collector requires explicit primary-source notice extraction where a binary package contains no license directory, including GMP, ICU and Vorbis. Missing, changed or extra workers/materials, mismatched source packages and undeclared runtime dependencies fail acquisition or inventory. A generic homepage or similarly named source archive is not a substitute for these bindings.

## Rename boundary

The custom FFmpeg version label and controlling build-script bytes change with the MovieSift rename. Rebuild both workers and record the resulting actual hashes/configuration; the upstream FFmpeg/zlib source pins and feature configuration remain unchanged. Historical worker receipts, package hashes, source/material archives and test results remain available through the [immutable evidence index](README.md). Their bytes and labels must not be rewritten to claim a renamed build.

A renamed candidate requires fresh full inventory, matching source/material preparation, actual MKV/MP4 package execution, relocation and Linux container verification. Historical file counts and archive hashes are observations, not constants for future packages.

## Actual release delivery

1. Publish the exact binary archive together with its matching covered-source/patch/build material archive and notices through a durable release arrangement. Preserve the material manifest, source revision, inventory and binary/material SHA256 values so recipients can identify the matching set.
2. Verify the actual applicable license alternatives and complete materials for the distributed combination. Original-code MIT selection does not replace the separate GPL/LGPL obligations of bundled components. See [GPLv2 section 3](https://www.gnu.org/licenses/old-licenses/gpl-2.0.html), [Qt guidance](https://doc.qt.io/qt-6/lgpl.html), [GCC Runtime Library Exception](https://www.gnu.org/licenses/gcc-exception-3.1.html), [Boost terms](https://www.boost.org/LICENSE_1_0.txt) and [zlib terms](https://zlib.net/zlib_license.html).
3. Keep preparation and delivery claims separate. `prepare-review-materials.py` creates a revision-bound bundle; optional seven-day Actions retention hands off candidates. Neither alone establishes the final permanent release/source distribution method or public-release authorization.

The earlier closure review identified no additional missing Linux QtCore source, patch or relink-object artifact under the selected route. The library is separate and has no runtime replacement lock. If a different license alternative or binary combination is selected later, reassess its applicable conditions. A suitable shared-library mechanism under LGPL is an alternative to relink objects where the license allows it, not an automatic extra demand for both.

Complete historical compiler reconstruction, bit-identical rebuilds and complete source archives for every permissively licensed component are not universal release conditions. Preserve known provenance limits without inventing extra requirements. No upstream question was sent during the historical collection work; the old unsent draft remains historical, not an open action required by this rename.

See the [release checklist](../release-checklist.md) for publication and deferred desktop acceptance. Successful inventory proves the recorded file/material checks, not universal legal compliance or native desktop acceptance.
