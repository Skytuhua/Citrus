# Build and develop Citrus

Native WPF on .NET, Windows integration, a shared core and offline engines. Application code is MIT; engine and Windows-component licenses differ. Read [third-party notices](../THIRD-PARTY-NOTICES.md).

## Toolchain

- .NET SDK **10.0.402**, pinned in `global.json`.
- Windows x64 for the desktop app and native tests.
- Python **3.12+** for packaging; **NSIS 3.11** for the installer.
- Pinned offline packs under `packaging/`, with sources/notices. Large acquired runtime trees are not ordinary Git source files.

On Windows, `./dev.ps1` restores, builds and runs the separate development profile. `./dev.ps1 -Test -BuildOnly` runs core/conversion checks; `-DesktopTests` adds desktop checks in an interactive session. Quit an installed resident app before testing global gestures.

```powershell
dotnet restore Citrus.sln --locked-mode
dotnet build Citrus.sln -c Release --no-restore -m:1
dotnet test tests/Citrus.Core.Tests -c Release --no-build --no-restore
```

Conversion/desktop checks need actual engines. Set `CITRUS_TEST_FFMPEG` and `CITRUS_TEST_FFPROBE` to pinned binaries for Windows media tests. Device, capture and some native tests are explicit opt-ins. Missing prerequisites are not a pass.

[Test prerequisites and check selection](../tests/README.md) lists each suite's requirements, the engine-free core path, the opt-ins and which checks suit a change.

## Engine packs and publishing

Read scripts and locks before acquisition. `packaging/build-media.sh` describes media source builds. Image/document scripts pin genuine HEIC, ExifTool, Office/Pandoc and OCR inputs. Runtime jobs do not download codecs.

On a prepared Windows machine, `./packaging/publish.ps1 -Phase Payload` builds and validates a self-contained payload. `python packaging/test-package.py dist/Citrus-win-x64` checks integrity and negative cases. `./packaging/publish.ps1 -Phase Installer` creates the installer from that unchanged payload. Retain notices, sources and terms. Use isolated owned installation-test targets, never a user's real installation.

Public CI builds source and runs its declared core contracts. It does not establish offline-pack acquisition, native Explorer behavior, fidelity or clean-machine deployment. Check specific runs and release verification.

## Documentation links

From the repository root, `python tools/check-doc-links.py` checks relative link and image targets in tracked Markdown offline with the Python standard library. It prints `file:line` for each missing target and exits nonzero. `python -m unittest discover -s tools/tests` runs its fixture tests; CI runs both in a separate `docs` job.

It checks inline Markdown links/images and quoted HTML `href`/`src`, resolving from the document or, with a leading `/`, the repository root. It ignores external URLs, same-page `#anchors`, fenced code and inline code, and checks only the file part of `file.md#section`. It skips vendored `packaging/notices/` and `packaging/Distribution-README.md`, whose links resolve in the installed layout. Heading anchors, reference-style links, `srcset`, HTML comments and link availability are not checked.

## Architecture and verification

[Conversion UI](conversion-interface.md), [media](media-engine.md), [PDF](pdf-engine.md), [archives/subtitles](subtitle-archive-engines.md) and [settings/actions](native-actions-settings.md) describe boundaries. Immutable recipes, verified private sources, the job queue and collision-safe publication are shared by preview/export.

Read [preview notes](PREVIEW-NOTES.md). Do not commit logs, desktop captures, credentials, private samples or the independent website checkout. Marketing source lives in a separate repository.
