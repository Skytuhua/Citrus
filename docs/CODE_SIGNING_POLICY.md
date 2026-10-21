# Code signing policy

Status: free SignPath Foundation application submitted on October 21, 2026; awaiting Foundation review and approval. Existing Citrus preview releases are unsigned. This page does not claim sponsorship, acceptance or an issued certificate.

## Intended signing scope

The first integration requests an Authenticode signature for `Citrus-Setup.exe`, built from a reviewed, public Citrus release tag by GitHub Actions on GitHub-hosted runners. A successful request must return a Windows-trusted signature from an explicitly approved signing certificate before the installer can enter a release draft. Checksums and release metadata are generated from the returned installer bytes.

This initial scope covers the installer only. It does not sign the installed Citrus executable, managed libraries, generated NSIS uninstaller or bundled engine executables. Signing those components requires a separate reviewed change. Third-party bytes and any upstream signatures are preserved.

If the Foundation accepts Citrus and enables its certificate, the credit will be: Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/). That credit describes the intended arrangement; it is not a claim that the current previews are signed.

## Responsibilities

Proposed author, reviewer and release-signing approver: [Skytuhua](https://github.com/Skytuhua), the Citrus repository owner. External contributions require maintainer review. Each signing request requires manual release approval in SignPath. Additional contributors or approvers must be named here before receiving access.

Every participant must enable MFA for GitHub and SignPath before participating in Foundation signing. Account MFA status and the final SignPath roles remain onboarding checks; this repository does not attest that those private account settings have already been verified.

## Privacy and network behavior

Citrus converts and edits files locally. Its updater does not upload source-file contents, local file paths or a device identifier. Installed releases check GitHub for updates at startup and every six hours when automatic updates are enabled, which is the default. GitHub receives ordinary connection information, including IP address and HTTP request metadata. Users can turn automatic updates off in Settings. A manual update check can also download and stage a new installer.

The [privacy page](https://citrous.app/privacy/) explains app behavior, GitHub downloads and the website's separate hosting and analytics. See also [GitHub's privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement). Application-signing artifacts are transmitted to GitHub Actions and SignPath for signing; user conversion files are not part of those artifacts.

## Components and release status

Citrus application source uses MIT. The complete installer also contains separately licensed engines and Microsoft runtime / Windows SDK redistributables. The [third-party notices](../THIRD-PARTY-NOTICES.md) identify those components. Inclusion under the Foundation's System Libraries exception and the current dependency bootstrap must be confirmed during onboarding.

An unsigned release is never relabeled as signed because a workflow or account exists. Check the specific installer signature and release metadata. A valid Authenticode signature identifies the certificate holder and protects signed bytes; it does not establish universal compatibility or immediate SmartScreen reputation.

Reviewed: October 21, 2026. [Setup and eligibility record](SIGNPATH.md).
