# Citrus roadmap

Citrus is a Windows x64 preview. The current release includes the compact file wheel, workspace and focused editors; it does not establish that every format, native workflow or Windows configuration is qualified. Read the [preview notes](docs/PREVIEW-NOTES.md) for current boundaries.

This roadmap describes priorities, without release dates or a promise that every item will ship. An open issue is a proposed task. Work is in progress only when someone has posted that status, and verification is complete only when the relevant evidence is recorded.

## Current focus

### Confidence in the preview

- Make development and test prerequisites understandable without acquiring the complete engine pack for a small documentation or core change.
- Extend focused regression coverage for settings validation and collision-safe output publication.
- Qualify installation, resident-app exit, update guards and uninstall in a disposable Windows environment. Preserve settings and unrelated output files, and record failures as well as passes.
- Keep format, codec, reconstructed-document and installer limitations visible. Source-build CI, engine fidelity and native acceptance remain separate checks.

### Interaction and accessibility

- Record Settings keyboard and screen-reader behavior as text, with the exact version, Windows build and assistive technology.
- Continue qualifying the complete Explorer/OLE interaction, cancellation and input recovery, and physical mixed-DPI transitions.
- Preserve readable controls, high-contrast fallbacks and reduced-motion behavior. A completed check for one window or device does not certify the whole application.

### Documentation that supports contribution

- Explain file actions with short examples, including the difference between copies, paths, shelf references and moving originals.
- Map common changes to the relevant test suites and their prerequisites.
- Add an offline check for broken repository-relative documentation and image targets.

## Next investigations

After the relevant preview checks have evidence, assess publisher signing, download size and the cost of maintaining additional distribution channels. These are investigations, not claims that a signed installer, smaller package or package-manager release is available today.

New formats and tools need a concrete file task, suitable fixtures, preserved originals and documented failure behavior. Requests should state which current workflow is blocked before proposing a broader editor or interface change.

## Later decisions

Broader compatibility depends on tested engines, platform behavior, maintainership and distribution terms. The supplied application remains Windows x64. This roadmap does not announce macOS, Linux or native ARM64 support.

## Find a task

Start with [Contributing](CONTRIBUTING.md), then browse [good first issues](https://github.com/Skytuhua/Citrus/issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22) or [help wanted](https://github.com/Skytuhua/Citrus/issues?q=is%3Aissue+is%3Aopen+label%3A%22help+wanted%22). The initial contribution areas are:

- [Core settings-validation matrix #2](https://github.com/Skytuhua/Citrus/issues/2) (good first issue): cover valid key permutations, invalid enum values and duplicate assignments without changing the settings contract.
- [File/directory output collisions #3](https://github.com/Skytuhua/Citrus/issues/3) (good first issue): add focused tests that protect existing entries when the requested output name is occupied by a different filesystem kind.
- [Offline documentation target check #4](https://github.com/Skytuhua/Citrus/issues/4) (help wanted): report missing relative files and images without fetching websites or assets.
- [File-actions quick start #5](https://github.com/Skytuhua/Citrus/issues/5) (good first issue): help users choose the correct action while preserving move and cancellation qualifications.
- [Test-suite prerequisite map #6](https://github.com/Skytuhua/Citrus/issues/6) (good first issue): show which commands and prerequisites a contributor needs for a particular change.
- [Settings accessibility qualification #7](https://github.com/Skytuhua/Citrus/issues/7) (help wanted): record reproducible keyboard and screen-reader findings as text, with conclusions limited to the tested configurations.

Comment on the relevant issue with the scope you intend to take. Use its acceptance checks and exclusions to keep the change reviewable. Larger proposals belong in a [feature request](https://github.com/Skytuhua/Citrus/issues/new/choose) before implementation.

## Media work

New product screenshots, recordings and simulated application interfaces remain on hold. Current contribution tasks use source, tests and text-based results. Existing public media retains its [documented provenance](docs/launch/2026-10-21/media/README.md).

## Keeping this roadmap current

Link a finished item to its merged change and relevant verification, or update its scope when new evidence changes the priority. [Maintaining Citrus](docs/MAINTAINING.md) describes triage, release checks and measurement. The roadmap is not a support contract or response-time commitment.
