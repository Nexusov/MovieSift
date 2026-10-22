# Original application license decision

**Status: pending rights-holder decision.** Research checked on 2026-10-20 against application source at `41b036e34eb4663505f0232dfdb96ee626493ef1` and the pinned dependencies. MIT is recommended for original TrackTrim code, consistent with [the brief](../PROJECT_BRIEF.md#10-licensing-and-provenance). This note grants no license, identifies no author or copyright owner, and does not change the pending status in [README](../README.md#licensing) or [THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES.md).

## Confirmed implementation and component terms

The [production project](../src/TrackTrim.Desktop/TrackTrim.Desktop.csproj) references the managed dependencies below. Versions are pinned in [central package versions](../Directory.Packages.props) and package locks; the self-contained runtime is shipped, while the SDK is a build dependency.

| Component | Pinned version | Primary license evidence |
| --- | --- | --- |
| .NET runtime | 10.0.13; SDK 10.0.402 is build-only | [Runtime MIT license](https://github.com/dotnet/runtime/blob/v10.0.13/LICENSE.TXT), plus its component notices |
| Avalonia / Desktop / Fluent | 12.1.4 | [MIT](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/licence.md) |
| CommunityToolkit.Mvvm | 8.4.2 | [MIT](https://github.com/CommunityToolkit/dotnet/blob/v8.4.2/License.md) |
| Serilog / Serilog.Sinks.File | 4.3.1 / 7.0.0 | [Serilog Apache-2.0](https://github.com/serilog/serilog/blob/v4.3.1/LICENSE), [sink Apache-2.0](https://github.com/serilog/serilog-sinks-file/blob/v7.0.0/LICENSE) |
| MKVToolNix workers | 102.0 | The [versioned source archive](https://mkvtoolnix.download/sources/mkvtoolnix-102.0.tar.xz) contains GPL v2 `COPYING`; `README.md:55` and both worker entry-point headers specify GPL v2. No blanket later-version permission is assumed. |

The inspected MKVToolNix source archive matches [tools.lock.json](../build/tools.lock.json): SHA256 `9f0a810f17c7df8adb9064a3a41d5784399be412d19704cf080745ad7d45da30`. The rolling upstream README is not versioned evidence for this artifact. Native Skia/HarfBuzz/ANGLE, runtime components and the MKVToolNix dependency closure retain their own terms. A restored-package inventory does not establish which files ship or complete their obligations. Test packages and the development FFmpeg fixture encoder are outside production project references.

[MkvBackend](../src/TrackTrim.Infrastructure/MediaTools/MkvBackend.cs) invokes app-local `mkvmerge` and `mkvextract` through [OwnedProcessRunner](../src/TrackTrim.Infrastructure/Processes/OwnedProcessRunner.cs): separate processes, argument arrays, redirected streams, JSON/XML and media files. Production project references do not link MKVToolNix code into the C# application.

## Alternatives for original code

| Choice | Practical redistribution, modification and contribution effects |
| --- | --- |
| [MIT](https://opensource.org/license/mit) | Allows commercial use, redistribution and proprietary modified versions. Copies or substantial portions retain the copyright and permission notice; publishing modification source is not required. Its short text has no express patent grant. Contributors retain ownership; a clear contribution policy should obtain their permission under the chosen terms. |
| [Apache-2.0](https://www.apache.org/licenses/LICENSE-2.0) | Also permits proprietary modifications without source publication. Redistribution retains the license and required notices, carries applicable existing NOTICE attribution, and marks modified files. Sections 3 and 5 provide a scoped contributor patent grant with termination conditions and default terms for intentionally submitted contributions, subject to separate agreements. This is more explicit than MIT, with more notice administration. |
| [GPL-3.0-or-later](https://www.gnu.org/licenses/gpl-3.0.txt) | Allows commercial use and modification, but conveying a covered modified work requires GPL terms; conveying binaries requires a compliant corresponding-source route. Private modifications alone need not be published. Contributors retain ownership while recipients receive GPL permissions. The later-version option must be granted explicitly; choosing it for original code does not upgrade third-party licenses. See sections 2, 5, 6 and 14 and the [SPDX identifier](https://spdx.org/licenses/GPL-3.0-or-later.html). |

None of these choices transfers contributors' copyright to the maintainer. Existing contributions require verified licensing authority; future submissions need a documented compatible contribution policy.

## Recommendation and unresolved legal questions

**Recommend MIT for original TrackTrim code:** it follows the brief's proposed simple license and permits broad reuse with little administration. The tradeoff is allowing closed downstream modifications and having no express patent grant. Prefer Apache-2.0 if explicit contributor patent terms are the priority; prefer GPL-3.0-or-later if reciprocal source availability for conveyed covered modifications is the goal. These are policy alternatives, not an adopted license.

**Inference, not a legal conclusion:** the observed CLI boundary supports treating the application and workers as independent programs. It does not settle derivative-work or aggregation analysis. The [FSF's aggregation FAQ](https://www.gnu.org/licenses/gpl-faq.en.html#MereAggregation) considers communication semantics as well as process boundaries; applicable copyright law and the actual combination still require assessment.

One concrete compatibility question must remain visible: [the ASF's compatibility explanation](https://www.apache.org/licenses/GPL-compatibility.html) permits Apache-2.0 code in GPLv3 works but identifies incompatibility with GPLv2. Serilog is already Apache-2.0, and the inspected worker notices specify GPL v2. If these components were legally one GPLv2-covered work, an original-code MIT label would not resolve that conflict. Do not assume a worker upgrade to GPLv3 or that selecting GPL-3.0-or-later for TrackTrim fixes it.

Before adopting an operative license, confirm the rights holder(s), authority over existing original code/assets/contributions, and the intended scope. No identity or assignment is inferred from a GitHub account or commit metadata. Independently resolve the exact shipped native closure, notices, source/build material and applicable distribution method recorded in [THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES.md#outstanding-public-release-obligations). Original-code license selection cannot complete that review, and a review-build label does not waive redistribution duties.
