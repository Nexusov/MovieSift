# Packaging and distribution

MovieSift's original code uses [MIT](../LICENSE). Bundled components retain the terms and attribution in [THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES.md) and their packaged license files.

## Release materials

Distribute each Windows/Linux binary archive together with its matching source/build material archive, platform inventory and material summary. A common `SHA256SUMS` identifies the published files. The material bundle must contain the exact application source and the applicable complete third-party sources, patches, build scripts, configuration and notices for those binaries.

[prepare-review-materials.py](../build/prepare-review-materials.py) prepares this bundle from a package and its inventory. The manifest and summary bind it to the actual source revision and binary archive. Publish the matching set through [GitHub Releases](https://github.com/Nexusov/MovieSift/releases); GitHub's automatic repository source downloads do not include all bundled third-party materials. Temporary Actions retention is described in the [release checklist](release-checklist.md#optional-candidate-handoff).

## Sources and build records

The locks and packaged receipts identify the current versions and hashes; keep these records with the corresponding files.

| Component | Collection route and retained materials |
| --- | --- |
| FFmpeg / ffprobe | [Source lock](../build/ffmpeg.lock.json) and [build recipe](../build/build-ffmpeg.sh): complete pinned FFmpeg and static zlib sources, configuration, compiler/runtime observations, notices and worker hashes. The selected build excludes GPL, version3, nonfree and optional external codec/hardware libraries; its FFmpeg terms are LGPL-2.1-or-later. |
| Windows MKVToolNix | [Source lock](../build/mkv-source.lock.json), [builder](../build/build-mkv-windows.sh) and [collector](../build/collect-mkv-build.py): source-built CLI workers, actual DLL dependencies, original package metadata/notices and complete matching source/patch packages. Binary BUILDINFO recipe hashes must match the collected PKGBUILD. Acquisition has no official-binary fallback. |
| Linux MKVToolNix | [Tool lock](../build/tools.lock.json), [payload lock](../build/tool-payload.lock.json) and [material lock](../build/tool-materials.lock.json): selected CLI executables and required AppImage libraries, complete matching AlmaLinux source RPMs, upstream archives, patches, build specifications and original notices. QtCore/ICU package-member hashes bind the matching Qt SDK materials. |
| QtCore and other native dependencies | Retain complete applicable source/build materials and module-specific notices. The current MKV route selects QtCore's GPL2 alternative on both platforms and GMP's GPL2 alternative. GCC Runtime Library Exception, MinGW, LGPL and permissive component terms remain separate. Preserve primary source notices when a binary package has no license directory. |
| .NET and NuGet | Retain the self-contained publish output, package-specific license/notice files, package-member inventory and generated apphost/runtime provenance. [License materials](../build/license-materials.lock.json) pin additional required upstream notices. |
| Adapted appearance styles | Preserve the [Filee MIT notice](third-party/Filee-LICENSE.txt) and attribution in [THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES.md). |

Packages include build and tool provenance files plus `licenses/nuget/inventory.json`. [distribution-inventory.py](../build/distribution-inventory.py) checks staged/archive hashes, package membership and source/material bindings. Missing or changed required files and undeclared runtime dependencies fail validation. A similarly named source archive or an upstream homepage is not a replacement for matching materials.

## Distribution review

Before distributing a new binary set:

- Retain all applicable license texts, copyright notices and attribution, including the original application license.
- Provide the corresponding source, patches and build/relink materials required by the selected component terms. Keep the source and binary sets identifiable and available together.
- Review unresolved provenance in the generated inventory and [native component records](../build/native-components.json) against the files actually shipped. Successful hash/inventory checks establish the recorded relationships, not every licensing condition.
- Reassess component terms when changing dependencies or how programs are combined. A separate process boundary does not itself settle aggregation or derivative-work questions; original-code MIT does not resolve the [GPLv2/Apache-2.0 compatibility issue](https://www.apache.org/licenses/GPL-compatibility.html) if those components form one covered work.

Source and relinking requirements depend on the selected license alternative and the files actually shipped. Use each component's license text to determine which materials must accompany the distribution.
