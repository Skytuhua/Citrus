# Test prerequisites and check selection

Use this page to choose the checks a change needs and to report them accurately. Run commands from the repository root in PowerShell. [Development setup](../docs/DEVELOPMENT.md) covers the full toolchain and [Contributing](../CONTRIBUTING.md) covers what to report.

The SDK is pinned to **10.0.402** in [`global.json`](../global.json) with `rollForward` disabled, so another SDK version cannot run these commands. Packaging scripts need Python 3.12+.

## Test groups

| Group | Establishes | Does not establish | Requires | Suits |
| --- | --- | --- | --- | --- |
| **Core**: [`Citrus.Core.Tests`](Citrus.Core.Tests/Citrus.Core.Tests.csproj) | `Citrus.Core` contracts: settings validation and migration, gestures, wheel geometry, job queue, output publication, editor documents and submission. Also covers the linked `CitrusSettings.cs` and `WorkspaceDragAdmission.cs`. | Engine output, WPF rendering, Explorer/OLE, native input, installation. | SDK 10.0.402 and NuGet package restore. No engines, desktop session or opt-in. One Windows delete-sharing test skips on other systems. | Core logic, settings, gestures, queue and publication rules. |
| **Source build and CI subsets** (CI `core` job) | The whole solution restores against lock files and builds with warnings as errors. Runs bounded image-kernel and editor-responsiveness tests, plus five packaging contract scripts. | Engine acquisition, the full conversion or desktop suites, Explorer/OLE, mixed DPI, fidelity, clean-machine installation. | Windows x64, SDK 10.0.402, Python 3.12+. The build copies the media binaries tracked in `packaging/media/win-x64/` into build outputs. | Anything that changes `src/`, project files or lock files. |
| **Conversion** (full): [`Citrus.Conversion.Tests`](Citrus.Conversion.Tests/Citrus.Conversion.Tests.csproj) | Media, image, PDF, archive, subtitle and Office/EPUB/OCR conversion contracts against the pinned engines. | Fidelity on arbitrary real-world files, visual matching, performance on other hardware. | Windows x64 for the pinned packs. `CITRUS_TEST_FFMPEG` and `CITRUS_TEST_FFPROBE` set to the pinned binaries. Restored image and feature packs (ExifTool, HEIC, LibreOffice, Pandoc, tessdata). Linux-surrogate tests skip on Windows. | Conversion behavior, engine arguments, recipes, metadata, staging. |
| **Windows desktop** (full): [`Citrus.Windows.Tests`](Citrus.Windows.Tests/Citrus.Windows.Tests.csproj) | WPF windows on a real STA dispatcher: wheel, menus, editors, previews, glass and backdrop behavior, lifecycle. | Live Explorer/OLE drag, mixed-DPI acceptance, real-installation behavior. Opt-in-only native, audio and performance evidence is skipped unless enabled. | Windows x64, a signed-in, unlocked interactive desktop. No installed Citrus running. The native animation test skips when client-area animation is off or High Contrast is on. | Windows interaction, rendering, editor windows, desktop integration. |
| **Packaging and release**: `packaging/test-*.py` | Manifest, verifier, promotion, acquisition, metadata and installer-script contracts, mostly with tiny fixtures. | Clean-machine installation, signing trust in production, a real release. | Python 3.12+. Several tests need Windows, the verifier-helper tests need SDK 10.0.402, and a few need NSIS 3.11, a published payload or a disposable account (see [below](#packaging-and-release)). | `packaging/`, `Citrus.nsi`, release metadata, update flow. |

Documentation-only changes need none of these groups: check relative links, commands and claims against their sources, as [Contributing](../CONTRIBUTING.md#pick-the-checks-your-change-needs) describes.

## Lightweight core path

This path needs only .NET SDK 10.0.402 and NuGet package restore. It does not acquire engines, build an installer, build the WPF app, launch Citrus or capture the screen. It is the same command pair as in [Contributing](../CONTRIBUTING.md#pick-the-checks-your-change-needs):

```powershell
dotnet restore tests/Citrus.Core.Tests/Citrus.Core.Tests.csproj --locked-mode
dotnet test tests/Citrus.Core.Tests/Citrus.Core.Tests.csproj -c Release --no-restore
```

`./dev.ps1 -Test -BuildOnly` is **not** this path. [`dev.ps1`](../dev.ps1) builds the whole solution, runs the full conversion suite with the bundled media binaries and copies `packaging/media/win-x64/*` beside the app. `-BuildOnly` only suppresses the app launch.

## Prepared machine

The source build and CI subsets match the `core` job in [`ci.yml`](../.github/workflows/ci.yml):

```powershell
dotnet restore Citrus.sln --locked-mode
dotnet build Citrus.sln -c Release --no-restore -m:1
dotnet test tests/Citrus.Core.Tests -c Release --no-build --no-restore
dotnet test tests/Citrus.Conversion.Tests -c Release --no-build --no-restore --filter 'FullyQualifiedName~ImageAdjustmentKernelPerformanceTests'
dotnet test tests/Citrus.Windows.Tests -c Release --no-build --no-restore --filter 'FullyQualifiedName~PdfPreviewBitmapTests|FullyQualifiedName~WorkspaceDragAdmissionTests|FullyQualifiedName~NativeActionsTests.Shelf_holds_references_prunes_missing_and_builds_copy_drag_for_whole_selection'
```

`./dev.ps1` restores and builds the solution, then starts a separate development copy of the app. `-Test` adds the full core and conversion suites, `-DesktopTests` adds the full Windows suite and `-BuildOnly` skips the launch. The script needs SDK 10.0.402 on `PATH`, in `.local/dotnet` or in its other listed location. It sets `CITRUS_TEST_FFMPEG` and `CITRUS_TEST_FFPROBE` to `packaging/media/win-x64/`.

### Conversion

The media binaries are tracked in Git. The image pack (`packaging/image/win-x64/`) and the Office/EPUB/OCR feature pack (`packaging/feature-pack/`) are ignored by Git and must be restored first. [`packaging/bootstrap-engines.py`](../packaging/bootstrap-engines.py) downloads the pinned preview.1 installer (about 1.3 GB), extracts it with 7-Zip (`SEVEN_ZIP`, `7z` on `PATH` or `C:\Program Files\7-Zip\7z.exe`) without running it, and writes only hash-pinned files. Read the script and its locks before running it.

```powershell
python packaging/bootstrap-engines.py
$env:CITRUS_TEST_FFMPEG = "$PWD\packaging\media\win-x64\ffmpeg.exe"
$env:CITRUS_TEST_FFPROBE = "$PWD\packaging\media\win-x64\ffprobe.exe"
dotnet test tests/Citrus.Conversion.Tests -c Release --no-build --no-restore
```

Media tests fall back to `/usr/bin/ffmpeg` and `/usr/bin/ffprobe` when the variables are unset. On Windows that makes them fail, not skip. Tests that need ExifTool or the feature pack also fail when those packs are missing. In CI the full suite runs only in the `engines` job, which runs for pull requests from `release/*` branches and `fix/office-worker-startup`.

### Windows desktop

```powershell
dotnet test tests/Citrus.Windows.Tests -c Release --no-build --no-restore
```

Run it in a signed-in, unlocked desktop session and quit any installed Citrus first. Video crop tests use `CITRUS_TEST_FFMPEG`, falling back to `packaging/media/win-x64/ffmpeg.exe`. This suite writes rendered images, desktop-region captures and JSON evidence under ignored `TestResults` directories. Keep them local; do not commit or attach them to issues or pull requests.

### Packaging and release

The `core` CI job runs these five scripts:

```powershell
python packaging/test-parallel-verification.py
python packaging/test-payload-verifier-native.py
python packaging/test-promote-release.py
python packaging/test-acquire-nsis.py
python packaging/test-bootstrap-engines.py
```

On Windows, `test-parallel-verification.py`, `test-payload-verifier-native.py` and `test-package.py` compile a verifier helper. They need SDK 10.0.402 from the `DOTNET` variable or from `dotnet` on `PATH`; otherwise those tests error with "The exact application SDK is not installed".

The other `packaging/test-*.py` scripts run the same way. Some need more setup or have side effects:

- `test-package.py <payload>` checks a payload produced by `./packaging/publish.ps1 -Phase Payload`. Without the argument, the missing-asset checks skip.
- `test-installer-identity.py` skips unless `MAKENSIS` or `makensis` on `PATH` provides NSIS 3.11.
- `test-setup-hash.py --native --compiler <makensis.exe> --compiler-sha256 <hash>` compiles and runs the NSIS hash macro. Without `--native` it runs only a self-check.
- `test-online-installer.py` compiles a contracts build of the online installer with the .NET Framework `csc.exe` and runs it under `.local/`.
- `test-update-installer.py` builds fixture installers and installs them under a unique `Citrus-UpdateFixture-*` name in `%LOCALAPPDATA%\Programs` and `HKCU`. It needs NSIS and an installed .NET 10 runtime.
- `test-install-recovery.ps1` compiles a fault-injection installer and runs it against the per-user Citrus location. Its header requires a disposable account with no Citrus installation.

## Opt-ins

These variables point tests at inputs or output folders, or enable evidence that is skipped by default. Enable only the one your change needs, not all of them together.

| Variable | Effect | Source |
| --- | --- | --- |
| `CITRUS_TEST_FFMPEG`, `CITRUS_TEST_FFPROBE` | Paths to the pinned media binaries; media-path override. | Conversion media tests, `VideoCropWindowTests`, `dev.ps1` |
| `CITRUS_TEST_PUBLISHED_FEATURE_PACK` | Absolute path to a published feature pack for the relocation smoke test. | [`PublishedFeaturePackSmokeTests`](Citrus.Conversion.Tests/PublishedFeaturePackSmokeTests.cs) |
| `CITRUS_TEST_SECOND_VOLUME` | Writable directory on another physical volume for the real Shell cross-volume move. | [`FolderMoveTests`](Citrus.Windows.Tests/FolderMoveTests.cs) |
| `CITRUS_RUN_AUDIO_DEVICE_TESTS=1` | Native playback through an actual Windows audio endpoint. | [`NativeMediaPlaybackTests`](Citrus.Windows.Tests/NativeMediaPlaybackTests.cs) |
| `CITRUS_RUN_NATIVE_CANCEL_TESTS=1` | Owned native pointer cancellation proof. `CITRUS_NATIVE_FOREGROUND_HANDSHAKE=1` waits up to 30 seconds for an operator to activate the window. | [`NativeWheelCancellationTests`](Citrus.Windows.Tests/NativeWheelCancellationTests.cs) |
| `CITRUS_RUN_WHEEL_PERFORMANCE=1` | Native target and generated-input GPU timing observations. `CITRUS_WHEEL_PERFORMANCE_RESULTS` (absolute folder) and `CITRUS_WHEEL_PERFORMANCE_COLD_ONLY=1` adjust them. | [`NativeWheelPerformanceMeasurementsTests`](Citrus.Windows.Tests/NativeWheelPerformanceMeasurementsTests.cs) |
| `CITRUS_WAVEFORM_EVIDENCE` | File path for a JSON waveform-reuse measurement. | [`MediaWaveformReuseTests`](Citrus.Windows.Tests/MediaWaveformReuseTests.cs) |
| `CITRUS_GLASS_ARTIFACTS` | Redirects glass capture output from `TestResults/glass-native`. | Glass capture tests in `Citrus.Windows.Tests` |

The following produce product captures and are outside the current product-media hold. Do not enable them for routine validation:

- `CITRUS_RUN_LIQUID_GLASS_TESTS=1` runs an owned-scene desktop refraction capture ([`LiquidGlassCaptureProbeTests`](Citrus.Windows.Tests/LiquidGlassCaptureProbeTests.cs)).
- `CITRUS_BUILD_LAUNCH_MEDIA=1`, with `CITRUS_LAUNCH_PHOTO`, `CITRUS_LAUNCH_VIDEO` and an absolute `CITRUS_LAUNCH_OUTPUT`, authors launch screenshots ([`LaunchMediaCaptureTests`](Citrus.Windows.Tests/LaunchMediaCaptureTests.cs)).

## Choose checks for a change

| Change | Run | Report as not run |
| --- | --- | --- |
| Documentation only | Review relative links, commands and claims against source. | Any test suite. |
| `Citrus.Core`, settings, gestures, queue, publication | Lightweight core path. | Conversion and desktop suites, if the code is not used there. |
| Conversion engines or recipes | Core path, then the conversion suite with engines restored. A targeted `--filter` is fine when stated. | Engines you could not restore; fidelity on other files. |
| Windows UI, editors, wheel, Explorer integration | Solution build, CI subsets, relevant `Citrus.Windows.Tests` in an interactive session. | Explorer/OLE, mixed DPI and opt-ins you did not enable. |
| `packaging/`, installer or release scripts | The changed script's `packaging/test-*.py`, plus the five CI scripts. | Native NSIS, payload, installer and clean-machine checks. |
| Project files or lock files | Locked restore and solution build. | Suites you did not run. |

## Reading results

A pass covers only the tests that executed. Report these separately:

- **Skipped**: `dotnet test` reports skips in a separate count. Linux surrogates skip on Windows, Windows-only checks skip elsewhere, and opt-ins skip until enabled.
- **Missing prerequisite**: a missing engine or unset media path makes conversion tests fail rather than skip. Say that the prerequisite was missing; it is not a product regression.
- **Not run**: suites, filters or opt-ins you did not execute.
- **Early return**: some platform-specific tests return early and report Passed on the other platform, for example the Unix process fixtures in `MediaBinaryProcessTests` on Windows. `LaunchMediaCaptureTests` also reports Passed when its opt-in is unset. A pass count is not evidence those paths ran.

Source CI covers only the build and the bounded subsets above. It does not accept Explorer/OLE behavior, mixed DPI, engine output fidelity or clean-machine installation.
