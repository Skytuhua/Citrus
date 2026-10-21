# Maintaining Citrus

This guide describes how to keep the preview, its documentation and contributor tasks useful. It sets no release calendar, response-time guarantee or staffing commitment.

## Triage a report

Read the reported version and reproduction before assigning a label. Ask for a small, non-sensitive example when the file is essential, and direct private vulnerability details to [Security](../SECURITY.md). Do not ask people to post credentials, private originals or unredacted logs.

Distinguish a reproduced defect from a feature request, a coverage gap and an unqualified native scenario. Use `bug` only when evidence supports a defect. A useful test task can cover an existing contract without alleging data loss. A qualification task should say what environment and observations are missing.

Keep an issue's scope small enough to review. Give it a problem statement, starting files, expected behavior, acceptance checks and exclusions. Use `good first issue` for work that a newcomer can attempt with documented prerequisites; use `help wanted` when it requires deeper context or a prepared environment. Check comments and linked pull requests before suggesting that someone take a task.

Acknowledge useful context, explain requested changes and record why an issue is closed or deferred. Avoid automatic assignment or a deadline unless someone has agreed to it. Keep unrelated requests in separate issues.

## Review a change

- Check the concrete problem and resulting behavior against the linked issue.
- Review preservation of source files, validated inputs, cancellation, output publication and accessibility fallbacks.
- Confirm that reported checks actually ran with their necessary prerequisites. Passing source CI does not qualify engines, the native desktop, output fidelity or an installer.
- Inspect changed documentation links and user-facing terminology. Keep historical evidence dated or clearly labeled, and preserve current limitations beside capability claims.
- Check dependency rights, pinned inputs and third-party notices before accepting a new binary or engine.

New product screenshots, recordings and simulated UI remain on hold. Use text findings for current verification tasks and retain the existing media's provenance.

## Before publishing a release

Use the release's actual tag, assets, bytes, hashes and qualification records. Keep the README, release notes, [quick start](QUICKSTART.md) and [preview notes](PREVIEW-NOTES.md) consistent with those records. A prerelease should remain clearly described as a preview, and download links must resolve to that release rather than assume a stable/latest destination.

Verify the checks relevant to the change and record their boundaries separately:

| Evidence | What to record |
|---|---|
| Source and core | Revision, pinned SDK, build and focused contract-test result. |
| Engines and output | Exact engine inputs, fixtures, reopened output and preservation checks for the operations exercised. |
| Windows interaction | Preview/revision, Windows build, display scale, keyboard or assistive technology, steps and observed result. |
| Installation | Release hash and a disposable, owned Windows environment; install, resident-app quit, update guards, uninstall and preservation results. |
| Performance | Scenario, machine, warm/cold conditions, sample count and actual measurements. Do not turn a single observation into a percentile. |

Missing prerequisites, skipped checks and failures remain explicit. Do not run installation experiments against a user's real installation or delete directories containing their files. Do not infer publisher identity from a checksum. Preserve an unsigned disclosure until the published installer is actually signed and verified.

Signing, download-footprint reductions and additional distribution channels are [roadmap investigations](../ROADMAP.md#next-investigations). They require their own evidence before being advertised as available.

## Keep priorities and contributor tasks current

Revisit the [roadmap](../ROADMAP.md) when a merged change or new evidence alters a priority. Link completed items to the relevant change and verification. Close duplicate or superseded starter tasks and remove an approachability label when the work grows beyond its original scope.

Use the existing issue forms and support documents before opening another channel. Discussions can remain disabled when Issues serves the current workflow; enabling another forum requires capacity to maintain it. Do not promise translations, platform support or moderation staffing that the project does not have.

## Measure useful outcomes

Record a dated baseline before a meaningful presentation or distribution change. At an appropriate review interval, compare:

- Net stars and repository visitors, with the revision and any launch context.
- Available referring sources and popular documentation, recognizing that referral coverage is incomplete.
- Per-asset download-count changes, labeled as downloads rather than unique users or successful installs.
- Reproducible reports, installation problems, resolved issues, first-time and repeat contributors, and the time and work needed to respond.

GitHub's visitor and clone traffic window is limited, so retain snapshots privately if a longer comparison is needed. Do not commit private traffic exports or identifying report details. A spike in stars or downloads does not establish repeat use or prove that a README change caused growth.

Use findings to choose one next improvement and record the reason. This process adds no application telemetry, scheduled automation or automatic outreach. Any future collection method needs its own purpose and disclosure. Promotion in another community requires an appropriate audience and explicit authorization to post on the maintainer's behalf.
