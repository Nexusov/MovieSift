# MovieSift — current delivery status

## Integrated baseline

The unified selection workspace and Soft-inspired appearance redesign are integrated. [PR #11](https://github.com/Nexusov/MovieSift/pull/11) merged through `0cac3586f808d61c8096345f6d5a1d3fc2f1ea49`; [PR #10](https://github.com/Nexusov/MovieSift/pull/10) then merged to main at `2ba8ba1d4a4507ff678ff0f31045c81e497451d0`. Its tree is `e6c597b5aeaf2b0a38613b8f1ead55502e72d661`, identical to the reviewed implementation at `54225849b331b4886277209d3432a99a95f8d209`. The proposed version remains **0.1.0-rc.1**; no public release or tag has been created.

[Integrated-main CI](https://github.com/Nexusov/MovieSift/actions/runs/40148923120) and [candidate handoff run 40150657001, attempt 1](https://github.com/Nexusov/MovieSift/actions/runs/40150657001) succeeded for that exact main revision. The handoff retained separate verified binary/material pairs for Windows and Linux. These are **historical, pre-rename results**: their original archive names, bytes, checksums and tool-version labels remain unchanged. They do not prove a later MovieSift build.

## Current bounded change: MovieSift identity

MovieSift is the sole current project, repository, application, assembly and package name. This change aligns source paths/namespaces, XAML/resource references, test workers, environment variables, executable/archive names, build scripts, CI and current documentation. Product behavior, dependency source revisions, the media preservation contract and the MIT grant remain unchanged. The custom FFmpeg build label changes with the application name; matching workers must be rebuilt from the same pinned source.

Local rename checks on 2026-10-25 used the pinned .NET SDK **10.0.402** and PowerShell **7.6.8**. Locked restore and formatting verification passed; the Release build completed with **0 warnings and 0 errors**. The existing C# suites passed **1,088 tests**, with **32 real-media cases skipped because this local environment did not acquire the bundled media workers**. Test discovery has fewer parameterized media cases without those workers, so this is not the full required media result. The **101 Python checks passed** after acquiring and SHA256-verifying the pinned FFmpeg/zlib source archives. After the final monogram/resource-key change, all **200 Desktop tests** passed again and generated **27 current headless renders**; the README includes the actual EN/RU workspace images.

Executed repository commands were `dotnet restore --locked-mode`, `dotnet build --configuration Release --no-restore --disable-build-servers -m:1`, `dotnet format --verify-no-changes --no-restore`, `dotnet test --configuration Release --no-build --no-restore --logger trx --results-directory ./TestResults`, and `python3 -m unittest discover -s build/tests`. The final UI check targeted `tests/MovieSift.Desktop.Tests/MovieSift.Desktop.Tests.csproj` with `MOVIESIFT_SOFT_RENDER_OUTPUT` set to an owned `artifacts/rename-renders` directory. The formatter corrected only import ordering required by the renamed namespace. Initial local attempts were blocked by missing PowerShell/source prerequisites and sandbox IPC/cache restrictions; installing the documented prerequisites and using permitted IPC/writable temporary cache paths resolved those environment failures without changing application or test behavior.

Merge requires the existing mandatory Windows/Linux jobs, including real-media tests, packaging/inventory, matching source materials, relocation and the Linux container check. Their exact checkout identities and results are attached to the implementing pull request and [Verify and package runs](https://github.com/Nexusov/MovieSift/actions/workflows/verify.yml). Local optional-media results, earlier successful runs and delivered archives cannot substitute for those required checks. New packages and material bundles record their own actual identities.

Historical receipts and screenshots are retained in immutable Git history and indexed in [verification history](verification/README.md). Removing their old copies from the current tree prevents outdated product names and interfaces from being presented as current. Their original content, hashes and qualifications remain available at the linked revision.

## Delivered behavior

- One workspace accepts one file, several files or bounded local folder discovery. Aggregate tracks show the union of included inspections, preserving duplicate occurrences and exact per-file identities; individual exceptions remain available.
- One explicit Review validates separate immutable per-file plans. Run is a separate action. Zero-audio consent is file-specific, NoChange creates no output, and sequential jobs preserve the existing warning/cancellation/cleanup and no-overwrite contracts.
- MKV and the documented MP4 subset retain their input container and encoded media. Rules, named portable presets, recursive relative outputs and explicit unfinished-list Restore/Discard remain available.
- English/Russian, light/dark/system appearance, custom colors with contrast validation and reduced motion use independent portable preferences. Cosmetic edits preserve selections and job authority.

The [README](../README.md), [architecture](ARCHITECTURE.md) and [supported formats](SUPPORTED_FORMATS.md) define the precise behavior and limits. No additional product phase is required to complete the rename.

## Deferred acceptance and publication

Manual native interaction, recording, visual/focus/scrolling checks and OS scaling remain deferred. Windows 11 x64 25H2 and Ubuntu 24.04 x64 Desktop with X11/XWayland remain declared acceptance targets; historical Windows 10 functional results and Linux CLI/container results do not establish those desktop checks.

The archived `b288be1d185c62157e7d1bd1249f2f925ffe3093` candidate has MKV/MP4 and EN → RU → EN native functional evidence on Windows 10 `19045.6456`. One owner-operated cancellation confirmed Cancelled, worker exit, unchanged source, no publication and cleanup. Its input-time uncertainty overlaps worker exit, leaving strict W13 active-remux correlation unconfirmed. That candidate did not establish W14/W15 active/repeated/verification close. An earlier binary's single-close result remains scoped to that binary. Real warning workflows, native visual/scaling and declared-target desktop acceptance remain open. These gaps are not established application defects.

Public release is a separate decision. Preserve the exact matching covered-source/build materials and notices, choose a durable distribution arrangement, and state the actual acceptance scope before publication. The [release checklist](release-checklist.md) and [distribution requirements](verification/release-distribution-closure.md) contain the finite remaining requirements. Main protection and both required check names remain in force; see [protection verification](verification/main-protection.md).
