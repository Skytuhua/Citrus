<p align="center"><img src="docs/launch/2026-10-21/media/citrus-mark.png" width="64" height="64" alt="Citrus"></p>

<h1 align="center">Citrus for Windows</h1>
<p align="center"><strong>Convert files. Keep your flow.</strong></p>
<p align="center">Local file conversion and focused editors for Windows.<br>Choose a task from a compact wheel or the workspace. Save a separate result.</p>

<p align="center"><a href="https://github.com/Skytuhua/Citrus/releases"><strong>Download the Windows preview →</strong></a> · <a href="docs/QUICKSTART.md">Quick start</a> · <a href="https://citrous.app">Website</a> · <a href="mailto:leo@citrous.app">Email support</a></p>

<p align="center">Windows x64 · v0.3.0-preview.2 · Unsigned preview · 1.30 GB download</p>

<p align="center"><a href="https://github.com/Skytuhua/Citrus/actions/workflows/ci.yml"><img src="https://github.com/Skytuhua/Citrus/actions/workflows/ci.yml/badge.svg?branch=main" alt="Source build and core contracts"></a> · <a href="LICENSE">MIT application license</a></p>

<p align="center"><a href="#what-you-can-do">Features</a> · <a href="#how-it-works">How it works</a> · <a href="#install-the-preview">Installation</a> · <a href="#preview-boundaries">Preview limits</a> · <a href="ROADMAP.md">Roadmap</a> · <a href="#help-and-contributions">Contributing</a></p>

[![Citrus wheel and focused editors, composed from the actual interface with sample files](docs/launch/2026-10-21/media/product-hero.webp)](https://citrous.app/#product-demo)

*Actual Citrus interface arranged with sample files. [Demonstration and media provenance](docs/launch/2026-10-21/media/README.md).*

## What you can do

- **Images:** Convert PNG, JPEG, WebP, HEIC, TIFF, AVIF, BMP and more. Adjust light and color, crop, annotate, redact, add a background, compress or edit metadata.
- **Video:** Convert, trim, crop, change speed, mute, redact, take a snapshot or split a clip. Play and scrub the video while editing.
- **Audio:** Convert, trim, normalize, adjust channels, add bleep ranges, visualize, compress and edit metadata. Audition the edited result.
- **PDFs:** Reorder, rotate, duplicate, split or merge pages. Export text, images and reconstructed editable documents, with OCR for supported scans.
- **Documents and books:** Convert documents, presentations, spreadsheets and EPUB locally. Pages destinations produce clearly labeled, Pages-compatible DOCX files.
- **Archives and subtitles:** Create or repack archives, extract supported archives, and convert SRT/VTT or readable text.

Available operations depend on the actual file and codec. PDF reconstruction and document layout are best effort. Check the [capability table](docs/operations.md) for individual operations and the [preview notes](docs/PREVIEW-NOTES.md) for boundaries.

## How it works

**From the workspace:** Open Citrus, choose **Add files** or **Open workspace**, then pick a format or tool and review the output.

**From Explorer:** Start dragging a local file, then tap a mode key:

- <kbd>Shift</kbd> **Convert**
- <kbd>Alt</kbd> **Tools**
- <kbd>Ctrl</kbd> **Files**

Move over a choice and release. A key held before dragging also works after the normal Windows drag threshold. Scroll for more wheel pages; gaps retain the last choice, and dropping outside accepts it. The neutral center, Escape or right-click cancels. [Gesture details and settings](docs/QUICKSTART.md#choose-a-task).

<details>
<summary>View PNG → WEBP in four seconds (animated)</summary>

[![Drag sample.png, hold Shift, choose WEBP, and keep the original beside the converted copy](docs/launch/2026-10-21/reference-demo-v3/citrus-reference-4s.gif)](https://citrous.app/#product-demo)

The current Citrus wheel and a real PNG → WEBP conversion, composed as an edited demonstration. The original remains untouched. [Watch the four-second video](https://citrous.app/assets/launch-v1/citrus-reference-4s.mp4) · [Media provenance](docs/launch/2026-10-21/reference-demo-v3/README.md).

</details>

<details>
<summary>See the image and video editors</summary>

![Citrus Add BG editor and its saved sample image](docs/launch/2026-10-21/media/image-editor.webp)

Compare the photograph with its original and save a separate result. Add a background, padding and rounded corners when needed.

![Citrus video editor with direct trim controls](docs/launch/2026-10-21/media/video-editor.webp)

Drag trim or crop handles, scrub to a moment, and step through source frames.

</details>

## Install the preview

1. Download **Citrus-Setup.exe** from the [v0.3.0-preview.2 release](https://github.com/Skytuhua/Citrus/releases). Read the release notes and check the published **SHA256SUMS**.
2. Verify that the release comes from the official Citrus repository before running the installer. It is **unsigned**, so Windows may report an unknown publisher. A matching checksum establishes consistency with the published file, not publisher identity or a guarantee of safety.
3. Install for your Windows user, open Citrus and add a local file. Review the resulting copy, especially after lossy conversion or document reconstruction.

Windows 11 x64 is recommended; the technical minimum is Windows 10 build 19041. No macOS, Linux or native ARM64 installer is supplied. The complete installer includes the runtime and offline engines; the expanded pack is approximately **3.1 GB**. See the [download page](https://citrous.app/download/) for release details.

Closing the launcher leaves Citrus in the tray. Choose **Quit** from the tray to exit before updating or uninstalling. [Installation and uninstall help](docs/QUICKSTART.md).

Installed releases check for updates in the background and install verified new versions when Citrus is idle. Earlier previews need one manual upgrade to acquire the updater. [Automatic updates and controls](docs/AUTOMATIC-UPDATES.md).

## Local processing, separate outputs

Conversion and editing run on your computer, without an upload workflow or account. Jobs preserve the original file's bytes and location; moving originals is a separate, explicit action. Review the returned file and destination before relying on the result.

## Preview boundaries

Citrus is actively developed. Format and codec compatibility, output fidelity and processing time vary by file. The workspace's static video thumbnail is limited; the video editors show playable video.

Full Explorer/OLE, physical mixed-DPI and clean-machine installation acceptance remain unqualified. The [source CI](https://github.com/Skytuhua/Citrus/actions/workflows/ci.yml) covers source builds and core contracts. Detailed verification scope, performance observations and known limitations live in the [preview notes](docs/PREVIEW-NOTES.md); release changes are in the [changelog](CHANGELOG.md).

## Help and contributions

Citrus is maintained by [Skytuhua](https://github.com/Skytuhua). Reproducible file issues, documentation corrections and focused contributions are welcome.

See the [roadmap](ROADMAP.md) for current priorities and [good first issues](https://github.com/Skytuhua/Citrus/issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22) for scoped starting points. Follow the [Code of Conduct](CODE_OF_CONDUCT.md); conduct concerns have a private reporting route.

- [Report a bug or suggest a feature](https://github.com/Skytuhua/Citrus/issues/new/choose). Include the version, Windows build and exact steps, using a small, non-sensitive sample.
- For help with Citrus, email [leo@citrous.app](mailto:leo@citrous.app) or visit [online support](https://citrous.app/support/).
- Read [Support](SUPPORT.md) for troubleshooting and [Contributing](CONTRIBUTING.md) before proposing a change.
- Follow the [development guide](docs/DEVELOPMENT.md) to build and test from source.
- Report vulnerabilities through the private route in [Security](SECURITY.md).

Never attach private documents, credentials or unredacted desktop captures to public issues.

## License and credits

### Code signing policy

Free SignPath Foundation signing is being prepared and requires provider approval. Current previews remain unsigned. Read the [Code signing policy](docs/CODE_SIGNING_POLICY.md) for the proposed installer-only scope, release responsibilities and privacy disclosures.

Citrus application code uses the [MIT License](LICENSE). Bundled engines, fonts and Windows components retain their own licenses and terms; see [third-party notices](THIRD-PARTY-NOTICES.md), the installer's component notice and the sources included with the engine pack.

The native wheel material adapts Liquid Glass Studio's optical work with attribution. Launch photography is original AI-generated sample content; product UI is rendered from the app. See [media provenance](docs/launch/2026-10-21/media/README.md).
