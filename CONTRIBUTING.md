# Contributing to Citrus

Useful contributions include reproducible file issues, clear documentation, focused regression tests and small fixes. Start with the [roadmap](ROADMAP.md) and [open issues](https://github.com/Skytuhua/Citrus/issues). The application is a preview; read its [current limits](docs/PREVIEW-NOTES.md) before describing a behavior as supported or fully tested.

Please follow the [Code of Conduct](CODE_OF_CONDUCT.md). It includes a private contact for conduct concerns; security vulnerabilities use the separate route in [Security](SECURITY.md).

## Choose a first change

1. Check existing issues and pull requests. [Good first issues](https://github.com/Skytuhua/Citrus/issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22) have bounded scope; [help wanted](https://github.com/Skytuhua/Citrus/issues?q=is%3Aissue+is%3Aopen+label%3A%22help+wanted%22) may require deeper experience or a prepared Windows environment.
2. Comment on the issue with the part you intend to take and your approach. Read any existing discussion to avoid overlapping work. A comment does not promise assignment, acceptance or a response within a particular time.
3. Make a focused branch and keep the pull request about one problem. Follow the issue's acceptance checks and exclusions.
4. Explain the resulting behavior, checks actually run and remaining limits. Link the issue and identify any prerequisites that prevented verification.

For a larger feature, interface change, dependency or architecture proposal, open a [feature request](https://github.com/Skytuhua/Citrus/issues/new/choose) and discuss the file task and intended result before implementing it. Small documentation corrections can be proposed directly.

## Pick the checks your change needs

Read [development setup](docs/DEVELOPMENT.md) for the pinned toolchain and full build instructions.

**Documentation:** verify relative links, headings, commands and factual claims against the referenced source. Inspect the GitHub-rendered result where layout matters. A prose change does not require acquiring engines or launching the application.

**Core behavior:** the core test project can be built and run without the desktop app or offline conversion engines. With the pinned .NET SDK available, use:

```powershell
dotnet restore tests/Citrus.Core.Tests/Citrus.Core.Tests.csproj --locked-mode
dotnet test tests/Citrus.Core.Tests/Citrus.Core.Tests.csproj -c Release --no-restore
```

Add a regression that checks an observable contract, such as preserving existing bytes or rejecting invalid settings. Tests should demonstrate the failure they prevent, not merely repeat the implementation.

**Conversion, packaging or Windows integration:** follow the relevant prerequisites in the development guide. These checks may need pinned engines, Windows x64, an interactive desktop or explicit opt-ins. Do not interpret a skipped check or a missing prerequisite as a pass. Keep native Explorer behavior, visual matching, performance, engine acquisition, output fidelity and clean-machine installation evidence distinct from source-build/core CI.

## Preserve the file and interaction boundaries

Read the [operation contracts](docs/operations.md). Preserve originals, source verification, collision-safe publication, cancellation and accessibility fallbacks. Keep the accepted compact wheel and white-menu design unless a discussed change has a specific reason to alter it. Do not weaken path or format validation to make a fixture pass.

Use the current public mapping: **Shift for Convert, Alt for Tools and Ctrl for Files**. Do not add an activation-delay description from historical documentation. Internal implementation names can differ from public labels; identify that difference explicitly when it matters.

Use small samples you own and can share. Never attach private documents, credentials or unredacted desktop captures; review logs before posting them. Vulnerabilities use the private route in [Security](SECURITY.md), while ordinary defects and feature ideas use Issues.

## Reports and non-code contributions

For a bug, include the version, Windows build, display scale, exact steps and expected versus actual behavior. Name the format and codec when known. State which result you inspected and whether the original remained unchanged. Text-based accessibility findings should name the window, assistive technology and keyboard steps, and distinguish observations from untested cases.

New product screenshots, recordings and simulated application interfaces remain on hold. Keep current contribution work to source, tests and text-based evidence. Existing public artwork and demonstrations retain their provenance; do not relabel an interface export as a live Explorer recording.

Documentation improvements and reproduction details are welcome without a code change. Discuss translations before starting so the project can agree on scope and upkeep.

## Rights and review

Application contributions use the [MIT License](LICENSE). Do not submit binaries, photography or dependencies without the rights, sources, licenses and provenance needed to distribute them. Third-party components retain their own licenses; update [notices](THIRD-PARTY-NOTICES.md) when relevant.

Keep generated outputs, acquired engine trees, private logs and the independent website checkout out of application pull requests. Review is based on the task, its evidence and the maintenance cost; a published roadmap or label does not guarantee that a proposal will merge. [Maintaining Citrus](docs/MAINTAINING.md) explains the maintainer process.
