# Start with Citrus

Download [Citrus-Setup.exe](https://github.com/Skytuhua/Citrus/releases/download/v0.3.0-preview.12/Citrus-Setup.exe) from the [official preview.12 release](https://github.com/Skytuhua/Citrus/releases/tag/v0.3.0-preview.12). Check the asset size and [SHA256SUMS](https://github.com/Skytuhua/Citrus/releases/download/v0.3.0-preview.12/SHA256SUMS). The installer is unsigned; a checksum establishes consistency with the published file, not a general guarantee of safety.

## Install

Download **Citrus-Setup.exe** and open it to start the standard installation wizard directly. The complete package includes its runtime and conversion engines, so setup needs no Internet connection after the download. Its download size and checksum are on the same release.

Close any running Citrus instance through its tray menu. Run the installer only after reviewing its origin and notices. It installs for your current Windows user under `%LOCALAPPDATA%\Programs\Citrus`; the complete package includes its runtime and offline engines. Read the Windows-component agreement and bundled open-source notices. Citrus works offline after installation. Allow space for the complete installed payload, the downloaded installer and temporary files.

[Corresponding third-party sources and rebuild inputs](https://github.com/Skytuhua/Citrus/releases/download/v0.3.0-preview.12/Citrus-ThirdParty-Sources.zip) are a separate free download on the same release. The installed `SOURCE-DOWNLOADS.md` gives their exact URL, checksum and preserved source paths. This source download is not needed for normal installation or use.

## Choose a task

- Open Citrus and choose **Add files** or **Open workspace**. Pick a format or tool, review its options and create the output.
- Or start a local file drag in Explorer and tap **Shift** for Convert, **Alt** for Tools or **Ctrl** for Files. A key already held also works once the normal Windows drag threshold is crossed.
- Move over a choice, then release. Gaps retain the last choice, and release outside the wheel accepts it. Move into the neutral center, press Escape or right-click to cancel. Scroll for another page.

Modifiers retain ordinary Windows behavior outside Citrus. Pause gestures or remap keys in Settings when they conflict with your workflow. Cloud placeholders must be available locally before use.

## Choose a file action

When using **Ctrl → Files** (or the keyboard-accessible operation picker in Home), Citrus provides resident native actions for your selection:

| Action | What it does | Typical use |
| --- | --- | --- |
| **Copy files** | Places physical file references on the Windows clipboard with copy semantics. | Paste into another Explorer folder to create duplicate copies. |
| **Copy paths** | Copies plain-text file paths (separated by newlines) to the clipboard. | Paste path strings into notes, tickets, or terminal commands. |
| **Reveal** | Highlights the selected item in File Explorer. | Locate where a file lives on disk without opening it. |
| **Open Terminal** | Opens Windows Terminal (or PowerShell) at the item's containing folder, or directly inside the selected folder. | Run commands or scripts in the working directory. |
| **Float on shelf** | Gathers items into a topmost floating reference window without copying disk storage. | Stage files from multiple locations to drag or copy together later; removing a shelf entry only discards the reference, never the underlying file. |
| **Move originals…** | Prompts for an explicit destination folder and relocates the original files. | Relocate files to an archive or project folder. Unlike conversion or editing actions, this modifies the location of your original source files. |

Canceling the destination folder picker leaves originals unchanged. Closing Home leaves Citrus running. Quit waits for an in-progress move and worker cleanup instead of interrupting it. If a move fails partway through, already-relocated items remain at the destination, and the error reports known completed destinations.

### Examples

- **Collecting receipts:** Drag several expense PDFs from different folders, tap **Ctrl**, and select **Float on shelf** to hold them in a temporary palette while preparing a report.
- **Moving project assets:** Select finished render files in Explorer, hold **Ctrl** during the drag to open the Files wheel, and choose **Move originals…** to select an archive folder on an external drive.

For the full technical contract and shell interaction details, see [native actions and resident settings](native-actions-settings.md).

## Edit a copy

Video tools show a playable picture. Trim uses range handles; Crop keeps the full source visible while its selection changes. Image adjustments offer an Original comparison. Audio tools provide waveforms and audition. **Apply**, **Compress** and other save actions create separate outputs; closing an unsubmitted editor discards its draft.

Check the returned file and destination, especially for lossy formats, reconstructed documents and compression goals. A target size can be unreachable. **Move originals** is a separate action.

## Quit, update or uninstall

Closing the launcher leaves the tray app running. Choose **Quit** from the tray before updating or uninstalling. Setup refuses to replace a running app and does not force-kill it.

Use Windows Installed apps or the installed `Uninstall.exe`. The uninstaller removes its owned files and shortcuts; settings and unrelated outputs remain. Follow release notes if a different preview requires removing the previous payload first. Never delete a directory containing your files as a substitute for uninstalling.

Read [preview notes](PREVIEW-NOTES.md) and [online support](https://citrous.app/support/).
