# Main protection verification

Readback date: **2026-10-19 UTC**.

Repository: [Nexusov/TrackTrim](https://github.com/Nexusov/TrackTrim). The existing [verification workflow](../../.github/workflows/verify.yml) is unchanged.
Protection was applied through [ruleset 25401528 — Protect main](https://github.com/Nexusov/TrackTrim/rules/25401528), then checked through the ruleset and effective-branch APIs.
The [canonical ruleset configuration](../../.github/rulesets/protect-main.json) contains configuration fields returned by the final GET, including explicit false/empty values; it contains no credentials.

## Applied policy and readback

| Setting | Observed value |
| --- | --- |
| Target and enforcement | Branch; exact `refs/heads/main` only; active |
| Exclusions / bypass actors | Empty / empty |
| Authenticated administrator bypass | `current_user_can_bypass: never` |
| Pull request | Required; zero approving reviews |
| Review conversations | Resolution required |
| Code-owner / last-push / unattributed-change extra approval | All explicitly false |
| Dismiss stale approvals / required reviewers | False / empty |
| Required checks | Both contexts below; strict/latest-base policy enabled; enforced on branch creation |
| Expected producer | GitHub Actions, observed App ID `15368`, slug `github-actions` |
| Force pushes / deletion | Blocked by `non_fast_forward` / `deletion` rules |
| Merge options | Merge, squash and rebase remain enabled; automatic merging remains disabled |

Required contexts, using their observed check-run names:

- `Required media checks and win-x64 package`
- `Required media checks and linux-x64 package`

The final ruleset GET and `GET /repos/Nexusov/TrackTrim/rules/branches/main` agreed on the applied settings. The ruleset list showed this as the only active ruleset.
The authenticated administrator readback reported no bypass. An initial classic branch-protection GET returned HTTP 404; that endpoint does not establish absence of ruleset protection.

The protection operation left `main` at `69c9babe668c3ba6d0032de3304f793337964c4f`.
At review head `daea2bb000e463df4b6dfa6206940082a447e012`, [run 39093848695](https://github.com/Nexusov/TrackTrim/actions/runs/39093848695) had both checks successful, produced by App `15368` / `github-actions`.
After protection was applied, `gh pr checks 1 --required` listed both contexts as successful. This records the existing run; it does not claim CI for a later documentation commit.

## Read-only reproduction

These commands inspect the current server state; a future readback may differ from this receipt. Use existing authenticated repository access; never include tokens in saved output.

```powershell
gh api repos/Nexusov/TrackTrim/rulesets
gh api repos/Nexusov/TrackTrim/rulesets/25401528
gh api repos/Nexusov/TrackTrim/rules/branches/main
gh api repos/Nexusov/TrackTrim/branches/main/protection
gh api repos/Nexusov/TrackTrim/git/ref/heads/main --jq '.object.sha'
gh api repos/Nexusov/TrackTrim --jq '{default_branch, allow_merge_commit, allow_squash_merge, allow_rebase_merge, allow_auto_merge}'
gh api repos/Nexusov/TrackTrim/commits/daea2bb000e463df4b6dfa6206940082a447e012/check-runs --jq '.check_runs[] | {name, status, conclusion, head_sha, app: {id: .app.id, slug: .app.slug}, html_url}'
gh pr checks 1 --repo Nexusov/TrackTrim --required
```

## Required workflow coverage

`pull_request` has no path, draft or fork filter and no job-level skip condition. The two OS matrix jobs retain mandatory pinned-tool acquisition and real-fixture generation, locked restore, formatting, Release build, unit/headless/controlled/real-media tests, self-contained packages and relocation. Linux additionally runs the no-SDK/native-libraries-only container workflow.

Permissions remain `contents: read`; action references remain SHA-pinned. The jobs retain a 35-minute timeout, independent matrix failure reporting and concurrency cancellation for superseded runs. Evidence upload cannot convert a failed mandatory step into success.

GitHub may pause fork runs for contributor approval, and merge conflicts or commit skip instructions may prevent a run. Those conditions do not remove the required checks. The required contexts are bound to their observed GitHub Actions producer. [Fork approval](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/approve-runs-from-forks), [required-check behavior](https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/troubleshooting-required-status-checks).

Protection and successful CI do not establish native GUI acceptance. The [native desktop record](native-desktop-acceptance.md) retains its own artifact identity, blocked/not-run cases and prerequisites. No merge, release, license selection or Phase 2 functionality is part of this protection change.
