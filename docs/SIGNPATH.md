# Free Windows installer signing with SignPath Foundation

This setup uses only the Foundation's free certificate and service. Do not open a paid SignPath subscription, buy a certificate or enable Azure signing to follow it.

## Current assessment

Citrus can apply. Acceptance remains SignPath Foundation's decision, and no certificate or trusted signature has been issued for this setup.

| Check | Evidence and remaining work |
| --- | --- |
| Open-source application | [MIT application license](../LICENSE); public [Skytuhua/Citrus](https://github.com/Skytuhua/Citrus) repository. Bundled components retain separate licenses. |
| Maintained and released | Two published Windows previews on October 21, 2026; a public release workflow builds from version tags. |
| Product documentation | [Homepage](https://citrous.app/), [download page](https://citrous.app/download/), repository README and component notices. |
| Reputation | GitHub API snapshot on October 21: repository created October 19, one star, one fork, 14 total installer downloads. These are download requests, not verified users. Adoption is new; acceptance is uncertain. |
| Bundled system components | Disclose the Microsoft VC++ redistributables and Windows SDK projections. Ask SignPath to confirm its System Libraries exception covers the actual package. |
| Dependency origin | The workflow builds Citrus source and restores pinned upstream engine files from an earlier public Citrus installer. Disclose that these are reused binaries, not engines rebuilt inside the signing job. |
| Artifact size | Current installer: 1,303,285,793 bytes. Confirm that the free profile can process this size before enabling signing. |
| Signing policy and roles | [Proposed policy](CODE_SIGNING_POLICY.md). Publish the reviewed policy and link it from the homepage and release/download pages before submitting the application. Confirm MFA and final roles during onboarding. |

The [Foundation rules](https://signpath.org/terms) require verifiable origin and manual release approval. They provide no automatic entitlement to a certificate and publish no numeric minimum-star threshold on that page. The certificate holder will be SignPath Foundation.

## Apply without purchasing anything

1. Review the completed application in the [free Foundation form](https://signpath.org/apply). A private local copy of the application and contact fields is kept under `.local/signpath-2026-10-21/`; do not commit it.
2. Publish the proposed policy links and explicitly retain the pending/unsigned status. Ask the Foundation about reputation, Microsoft components, dependency origin and the installer size in the application.
3. The maintainer must approve the required Code of Conduct and personal-data processing consents. Optional marketing consent is not required. Submit only after those approvals and required public policy links are in place.
4. Wait for an acceptance email. Do not substitute a paid trial if the application is declined. If reputation is insufficient, gather real adoption and reapply with accurate evidence.

## Configure after Foundation acceptance

Use the free organization and project supplied during Foundation onboarding. Link the GitHub.com Trusted Build System to the Citrus project, restrict it to `Skytuhua/Citrus`, GitHub-hosted builds and reviewed release tags. Install/authorize the SignPath GitHub App only after reviewing its requested access and limiting repository access to Citrus. Repository permissions are an account-owner action.

Configure a release policy with a named human approver. Upload the reviewed XML artifact configuration in `packaging/signpath/`; it signs only the final Citrus installer. Have SignPath validate its XML and metadata restrictions before using it. Never sign upstream DLLs with the Citrus subscription.

In GitHub repository **Settings → Secrets and variables → Actions**, configure:

| Setting | Kind | Value |
| --- | --- | --- |
| `SIGNPATH_API_TOKEN` | Secret | Foundation-issued submitter token; never put it in source, issue text or chat. |
| `SIGNPATH_ORGANIZATION_ID` | Variable | Actual free organization ID. |
| `SIGNPATH_PROJECT_SLUG` | Variable | Actual Citrus project slug. |
| `SIGNPATH_SIGNING_POLICY_SLUG` | Variable | Approved policy with manual release approval. |
| `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | Variable | Approved installer-only artifact configuration. |
| `SIGNPATH_ALLOWED_SIGNER_THUMBPRINTS` | Variable | Exact approved certificate SHA-1 thumbprint(s), comma-separated; obtain through the authenticated Foundation account. This is an identity pin, not the file digest algorithm. |

Certificate rotation requires checking the replacement certificate through the Foundation and updating the allowed list before another signed release. A failed or expired signing attempt must not fall back to unsigned publication.

## First signed release

Use a new reviewed application version/tag. Existing published installers are immutable and must not be replaced. Run the release workflow manually with SignPath selected, inspect the linked signing request and approve it in SignPath. The workflow waits for that human approval, validates the returned installer signature and approved signer, and generates fresh checksums and release metadata from the final bytes. It prepares a draft; publication still requires release acceptance.

Before publication, check the downloaded installer signature on a separate Windows machine, install/uninstall behavior, and the final published asset digest. Update website release data only for the actual signed version. The installed app, DLLs and uninstaller remain outside this first milestone; do not describe the whole payload as signed.

## Verification boundaries

Local script tests validate rejection paths and metadata handling. They do not establish Foundation approval, trusted certificate issuance, artifact-size acceptance, successful signing, a passing hosted release run, SmartScreen behavior or clean-machine installation. Those checks remain pending until the provider account is approved and a real release is signed.

References: [Foundation application](https://signpath.org/apply), [Foundation terms](https://signpath.org/terms), [GitHub integration](https://docs.signpath.io/trusted-build-systems/github), [artifact configuration](https://docs.signpath.io/artifact-configuration/syntax).
