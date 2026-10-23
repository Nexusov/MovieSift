# Original application license decision

**Decision: MIT for original TrackTrim code, copyright (c) 2026 Nexusov.** The owner explicitly selected MIT and the attribution `Nexusov` on 2026-10-23. The operative grant is [LICENSE](../LICENSE), included unchanged in portable packages and their matching source/material bundles. This attribution was supplied by the owner, not inferred from GitHub or machine metadata.

The decision covers original project material to which the grant applies; third-party files and contributions retain their applicable notices and terms. It does not certify third-party redistribution readiness, resolve aggregate-versus-combined-work questions, authorize public publication or establish ownership over material the licensor does not control. Contribution terms are recorded in [CONTRIBUTING](../CONTRIBUTING.md#changes-and-licensing).

The comparison below is retained as background research checked on 2026-10-20 against application source at `41b036e34eb4663505f0232dfdb96ee626493ef1` and the pinned dependencies. The alternatives are no longer pending choices for this candidate.

## Confirmed implementation and component terms

The [production project](../src/TrackTrim.Desktop/TrackTrim.Desktop.csproj) references the managed dependencies below. Versions are pinned in [central package versions](../Directory.Packages.props) and package locks; the self-contained runtime is shipped, while the SDK is a build dependency.

| Component | Pinned version | Primary license evidence |
| --- | --- | --- |
| .NET runtime | 10.0.13; SDK 10.0.402 is build-only | [Runtime MIT license](https://github.com/dotnet/runtime/blob/v10.0.13/LICENSE.TXT), plus its component notices |
| Avalonia / Desktop / Fluent | 12.1.4 | [MIT](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/licence.md) |
| CommunityToolkit.Mvvm | 8.4.2 | [MIT](https://github.com/CommunityToolkit/dotnet/blob/v8.4.2/License.md) |
| Serilog / Serilog.Sinks.File | 4.3.1 / 7.0.0 | [Serilog Apache-2.0](https://github.com/serilog/serilog/blob/v4.3.1/LICENSE), [sink Apache-2.0](https://github.com/serilog/serilog-sinks-file/blob/v7.0.0/LICENSE) |
| MKVToolNix workers | 102.0 | The [versioned source archive](https://mkvtoolnix.download/sources/mkvtoolnix-102.0.tar.xz) contains GPL v2 `COPYING`; `README.md:55` and both worker entry-point headers specify GPL v2. No blanket later-version permission is assumed. |
| FFmpeg / ffprobe candidate workers | `9.0.2-TrackTrim-source-2a571b606854` | The same pinned source is built without GPL/version3/nonfree or optional external codec libraries. [Source terms](https://github.com/FFmpeg/FFmpeg/blob/2a571b606854520cf89804d8030c8b328e621689/LICENSE.md) establish LGPL-2.1-or-later for this configuration; static zlib and compiler support retain their own terms. Exact sources/configuration/receipts accompany the candidate; see [current closure](verification/release-distribution-closure.md). |

The inspected MKVToolNix source archive matches [tools.lock.json](../build/tools.lock.json): SHA256 `9f0a810f17c7df8adb9064a3a41d5784399be412d19704cf080745ad7d45da30`. The rolling upstream README is not versioned evidence for this artifact. Native Skia/HarfBuzz/ANGLE, runtime components and the MKVToolNix dependency closure retain their own terms. A restored-package inventory does not establish which files ship or complete their obligations. Test packages are build-only; the separately bundled FFmpeg/ffprobe workers are now runtime distribution components.

[MkvBackend](../src/TrackTrim.Infrastructure/MediaTools/MkvBackend.cs) invokes app-local `mkvmerge` and `mkvextract` through [OwnedProcessRunner](../src/TrackTrim.Infrastructure/Processes/OwnedProcessRunner.cs): separate processes, argument arrays, redirected streams, JSON/XML and media files. Production project references do not link MKVToolNix code into the C# application.

The MP4 adapter uses the same process boundary for app-local FFmpeg/ffprobe. Candidate acquisition deliberately replaces the broad prior optional-library binary with the same FFmpeg source and a captured minimal build. The original-code MIT decision is independent of this replacement. Separate executables do not by themselves settle the combination question discussed below.

## Alternatives for original code

| Choice | Practical redistribution, modification and contribution effects |
| --- | --- |
| [MIT](https://opensource.org/license/mit) | Allows commercial use, redistribution and proprietary modified versions. Copies or substantial portions retain the copyright and permission notice; publishing modification source is not required. Its short text has no express patent grant. Contributors retain ownership; a clear contribution policy should obtain their permission under the chosen terms. |
| [Apache-2.0](https://www.apache.org/licenses/LICENSE-2.0) | Also permits proprietary modifications without source publication. Redistribution retains the license and required notices, carries applicable existing NOTICE attribution, and marks modified files. Sections 3 and 5 provide a scoped contributor patent grant with termination conditions and default terms for intentionally submitted contributions, subject to separate agreements. This is more explicit than MIT, with more notice administration. |
| [GPL-3.0-or-later](https://www.gnu.org/licenses/gpl-3.0.txt) | Allows commercial use and modification, but conveying a covered modified work requires GPL terms; conveying binaries requires a compliant corresponding-source route. Private modifications alone need not be published. Contributors retain ownership while recipients receive GPL permissions. The later-version option must be granted explicitly; choosing it for original code does not upgrade third-party licenses. See sections 2, 5, 6 and 14 and the [SPDX identifier](https://spdx.org/licenses/GPL-3.0-or-later.html). |

None of these choices transfers contributors' copyright to the maintainer. Existing contributions require verified licensing authority; future submissions need a documented compatible contribution policy.

## Recommendation and unresolved legal questions

**MIT was recommended and selected for original TrackTrim code:** it follows the brief's proposed simple license and permits broad reuse with little administration. The tradeoff is allowing closed downstream modifications and having no express patent grant. Apache-2.0 offers explicit contributor patent terms; GPL-3.0-or-later offers reciprocal source availability for conveyed covered modifications. Neither alternative is adopted here.

**Inference, not a legal conclusion:** the observed CLI boundary supports treating the application and workers as independent programs. It does not settle derivative-work or aggregation analysis. The [FSF's aggregation FAQ](https://www.gnu.org/licenses/gpl-faq.en.html#MereAggregation) considers communication semantics as well as process boundaries; applicable copyright law and the actual combination still require assessment.

One concrete compatibility question must remain visible: [the ASF's compatibility explanation](https://www.apache.org/licenses/GPL-compatibility.html) permits Apache-2.0 code in GPLv3 works but identifies incompatibility with GPLv2. Serilog is already Apache-2.0, and the inspected worker notices specify GPL v2. If these components were legally one GPLv2-covered work, an original-code MIT label would not resolve that conflict. Do not assume a worker upgrade to GPLv3 or that selecting GPL-3.0-or-later for TrackTrim fixes it.

No copyright assignment is inferred from a GitHub account or commit metadata. The exact shipped native closure, notices, source/build materials and applicable public distribution method remain independently governed by [THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES.md#outstanding-public-release-obligations). Original-code license selection does not complete those obligations, and a review-build label does not waive redistribution duties.
