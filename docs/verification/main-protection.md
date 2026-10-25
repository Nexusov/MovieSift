# MovieSift main protection

Readback date: **2026-10-25 UTC**, after the repository rename.

The [Protect main ruleset, ID 25401528](https://github.com/Nexusov/MovieSift/rules/25401528), remains active on `Nexusov/MovieSift`. The [API readback](https://api.github.com/repos/Nexusov/MovieSift/rulesets/25401528) reports the renamed repository as its source and retains the existing policy. Its last configuration update is `2026-10-19T15:02:49.229+03:00`; the rename did not change its rules.

| Policy | Observed value |
| --- | --- |
| Target | Exact `refs/heads/main`; active enforcement; no exclusions |
| Bypass | No bypass actors; authenticated administrator `current_user_can_bypass: never` |
| Changes | Pull request required; zero approving reviews; review-thread resolution required |
| Required checks | Both platform contexts below, bound to GitHub Actions integration ID `15368` |
| Check freshness | Strict/latest-base policy; enforced on branch creation |
| Force pushes and deletion | Blocked by `non_fast_forward` and `deletion` rules |
| Permitted merge methods | Merge, squash and rebase |

Required check names remain:

- `Required media checks and win-x64 package`
- `Required media checks and linux-x64 package`

The [canonical ruleset configuration](../../.github/rulesets/protect-main.json) preserves these fields. The [verification workflow](../../.github/workflows/verify.yml) keeps the mandatory platform jobs, pinned tools, real-media checks, packaging/inventory, relocation and Linux container check. Renaming assemblies or archives does not authorize weakening the required checks or bypassing the ruleset.

## Read-only reproduction

```sh
gh api repos/Nexusov/MovieSift/rulesets/25401528
gh api repos/Nexusov/MovieSift/rules/branches/main
gh api repos/Nexusov/MovieSift/git/ref/heads/main --jq '.object.sha'
```

A classic branch-protection endpoint may return 404 even while ruleset protection is active; inspect the actual ruleset/effective rules. Future server settings may differ from this dated readback.

The original 2026-10-19 receipt and its contemporaneous branch/CI identities remain in [verification history](README.md#distribution-container-and-protection-history). Protection is separate from application correctness, native desktop acceptance and public-release authorization.
