# Start with Citrus

Download the Windows x64 preview from the [official download page](https://citrous.app/download/). Check the release's size and SHA-256. The installer is unsigned; a checksum establishes consistency with the published file, not a general guarantee of safety.

## Install

Download **Citrus-Setup.exe** and open it to start the standard installation wizard directly. The complete package includes its runtime and conversion engines, so setup needs no Internet connection after the download. Its current size and checksum are on the download page.

Close any running Citrus instance through its tray menu. Run the installer only after reviewing its origin and notices. It installs for your current Windows user under `%LOCALAPPDATA%\Programs\Citrus`; the complete package includes its runtime and offline engines. Read the Windows-component agreement and bundled open-source notices. Citrus works offline after installation. Allow the installed size shown on the download page, plus space for the downloaded installer and temporary files.

Corresponding third-party sources and rebuild inputs are a separate free download on the same release. The installed `SOURCE-DOWNLOADS.md` gives their exact URL, checksum and preserved source paths. This source download is not needed for normal installation or use.

## Choose a task

- Open Citrus and choose **Add files** or **Open workspace**. Pick a format or tool, review its options and create the output.
- Or start a local file drag in Explorer and tap **Shift** for Convert, **Alt** for Tools or **Ctrl** for Files. A key already held also works once the normal Windows drag threshold is crossed.
- Move over a choice, then release. Gaps retain the last choice, and release outside the wheel accepts it. Move into the neutral center, press Escape or right-click to cancel. Scroll for another page.

Modifiers retain ordinary Windows behavior outside Citrus. Pause gestures or remap keys in Settings when they conflict with your workflow. Cloud placeholders must be available locally before use.

## Edit a copy

Video tools show a playable picture. Trim uses range handles; Crop keeps the full source visible while its selection changes. Image adjustments offer an Original comparison. Audio tools provide waveforms and audition. **Apply**, **Compress** and other save actions create separate outputs; closing an unsubmitted editor discards its draft.

Check the returned file and destination, especially for lossy formats, reconstructed documents and compression goals. A target size can be unreachable. **Move originals** is a separate action.

## Quit, update or uninstall

Closing the launcher leaves the tray app running. Choose **Quit** from the tray before updating or uninstalling. Setup refuses to replace a running app and does not force-kill it.

Use Windows Installed apps or the installed `Uninstall.exe`. The uninstaller removes its owned files and shortcuts; settings and unrelated outputs remain. Follow release notes if a different preview requires removing the previous payload first. Never delete a directory containing your files as a substitute for uninstalling.

Read [preview notes](PREVIEW-NOTES.md) and [online support](https://citrous.app/support/).
