# Automatic prereleases

The Windows tests workflow keeps restore, Release build and all deterministic
tests on PRs, manual validation and every `main` push, including documentation.
Process/chat fixture teardown waits at most two seconds for Windows sharing or
lock violations after owned process exit; persistent locks and other errors
still fail. Regression tests reproduce both transient and persistent locks.
Only a live **push to `refs/heads/main`** can publish. PRs, manual runs, other
branches and tag events cannot reach the write-capable job. No additional
credentials, Copilot authentication, stable version bump or manual release run
is needed.

## Complete push classification

After successful tests, `Get-PrereleaseDecision.ps1` compares the event's full
`before` and `after` commits in a full-history checkout pinned to `github.sha`.
Git emits NUL-delimited names with rename detection disabled, so deletions and
both sides of renames count. This uses the entire push, not a final-commit,
300-file API sample or Actions path filter. A newly created main branch compares
its complete tree; deleted branches do not publish. Missing commits, malformed
SHAs, mismatched checkout/event revisions and invalid comparison results fail
explicitly.

A push is documentation-only when **every** changed path is under the
case-sensitive `docs/` directory (including assets) or has a case-insensitive
`.md` suffix anywhere, including root README and `.github` Markdown. Any other
path is eligible, including workflows, scripts, tests and mixed pushes.
An empty net diff does not release. Multiple commits in a push produce one
release opportunity.

## Validation, packages and publication

The package job needs successful same-run tests and classification; the publish
job needs both successful validation and packaging at the exact pushed SHA.
The script independently rechecks event eligibility, revision and both results
before any authenticated GitHub call.

`Directory.Build.props` remains the source of the base application version.
Release identity is `<base>-preview.<github.run_number>`, tag `v<identifier>`;
reruns do not use `run_attempt`. The app still displays the base version.
`Publish-App.ps1` creates the actual Windows x64 framework-dependent and
self-contained packages and verifies every archive member using
`Test-Package.ps1`. Four assets are uploaded:

| Variant | Assets | Prerequisite |
|---|---|---|
| Framework-dependent | `WinChatCopilot-<identifier>-win-x64-framework-dependent.zip` and `.sha256.json` | .NET 10 Desktop Runtime x64 |
| Self-contained | `WinChatCopilot-<identifier>-win-x64-self-contained.zip` and `.sha256.json` | Runtime bundled |

The release client validates packages/provenance before writing, pins a tag
directly to the exact pushed commit, creates a **draft prerelease**, then uploads
all four files. Its release-note ledger records the revision, both versions,
asset names, sizes and hashes. It downloads every uploaded file, recomputes
SHA-256, revalidates both full packages and rechecks the tag before publishing.
Publication sets `prerelease: true` and `make_latest: false`; notes identify the
source commit, runtime choices and checksum instructions. Failures retain a
draft and actionable job diagnostics, never a newly published incomplete release.

## Permissions, concurrency and retries

Default/build permissions are `contents: read`. Packaging additionally has
`actions: read` to retrieve this run's retained packages. Only the main-push
publication job has `contents: write` plus `actions: read`. Checkouts do not
persist credentials. The script uses the job's temporary GitHub Actions token;
no repository secret or external publication service is introduced.

Publication concurrency is keyed by **run ID**, not branch, with cancellation
disabled. Distinct eligible pushes retain distinct opportunities even when
later pushes arrive early. Repeated attempts for one run use the same tag.

Before upload, the four exact package files are retained in an Actions artifact
named `prerelease-packages-<run_id>` for 90 days. A rerun restores those original
bytes instead of rebuilding timestamp-dependent ZIPs/manifests. A partial draft
can then upload only missing assets and validate every existing asset. A matching
published release is verified read-only; no assets are replaced or deleted.
Conflicting tag/revision/ledger, unexpected/duplicate/missing published assets,
hash mismatches, expired original packages and API/permission failures fail
explicitly. There is no automatic overwrite, retry loop or admin bypass.

Resolve the reported cause before rerunning the original failed workflow.
Do not delete/replace tags or published assets to silence conflicts. If original
artifacts are unavailable, recovery requires an explicit human decision.
Inspect the failing job's comparison, package and HTTP operation diagnostics;
the artifact and any unpublished draft remain available for investigation.

## Evidence boundaries

`PrereleaseTests` run table-driven real-Git comparison fixtures, workflow
contract assertions and a synthetic in-process GitHub transport. They cover
draft/upload/publication ordering, retry failures and conflicts without dummy
releases or network publication. Actual local packages and owned-window checks
are separate evidence, not fabricated from these fixtures.

Live acceptance is observed after the implementation's eligible merge push:
verify one prerelease at that exact SHA, download both ZIPs/manifests and
recompute their hashes. A subsequent genuine documentation-only main push must
retain routine tests while skipping publication. Do not create unrelated
commits or synthetic live releases merely to manufacture this evidence; record
pending live observations distinctly from passing deterministic checks.
