# Citrus for Windows

**0.3.0-preview.3 · Windows x64 · unsigned public preview**

Citrus puts file conversions and small editing tools beside your files. Process
images, video, audio, PDFs, documents, presentations, spreadsheets, EPUBs,
subtitles and archives using the included offline engines.

## Start

Run `Citrus.Windows.exe`. Add local files in the workspace, choose **Convert**,
**Tools** or **Files**, and open the selected operation. Editors use a preview
and tool-specific controls; review them before applying an operation.

While dragging files in Explorer, tap **Shift** for conversions, **Alt** for tools
or **Ctrl** for file actions. Releasing the key leaves the wheel open. Scroll to
change its page. Drop to accept the highlighted choice; use the neutral center,
Escape or right-click to cancel. Ordinary Windows modifier behavior still
applies, so pause or remap drag gestures in Settings if they conflict with a
workflow. Closing Home leaves the tray resident; choose **Quit** from the tray
to stop it.

Conversions and editors create collision-safe copies. Original files remain in
place unless you explicitly choose **Move originals**. PAGES-compatible output
is a genuine `.docx` file that Apple Pages can open, not a renamed `.pages` file.
Reconstructed editable documents may differ from the source layout, especially
for scans, complex tables or fonts. Review the exported copy.

## Install and requirements

Use `Citrus-Setup.exe` on Windows x64 (Windows 10 build 19041 or later, or
Windows 11). The self-contained bundle includes .NET and the offline engines;
it needs several GB of free disk space. Corresponding third-party sources and
rebuild inputs are a separate free download beside the installer; see the
installed SOURCE-DOWNLOADS.md for the exact version, URL and checksum.
No separate Office or codec purchase is required
for the included operations. Desktop-glass availability depends on Windows,
hardware and capture permissions; the readable opaque fallback remains available.

Setup is per-user and does not require administrator rights. Quit Citrus in
every session first. To replace an older preview with a different payload,
uninstall that preview before installing this one. Setup never force-kills work.
Uninstall removes installer-owned files; settings and unrelated outputs remain.

This preview is **not code-signed**. Windows may report an unknown publisher or
show a security warning. Obtain it from the project's official release page and
compare its SHA-256 with that release. Do not disable Windows security tools.

Interactive setup requires acceptance of the separate Microsoft Windows SDK
component terms. For an authorized unattended deployment, review the included
terms first, then pass `/S /ACCEPTSDKTERMS=1`. Silent setup without that explicit
acceptance exits 26 before installation changes.

## Local processing, preview status and feedback

File processing uses the local included engines. There is no account requirement
or file-upload service. The native wheel samples the live desktop only while
displayed; its recording exclusion can make it absent from screen recordings.

This is a preview, not a claim of universal format fidelity or zero latency.
Some native Explorer, accessibility, mixed-DPI and startup timing acceptance
checks remain open. Keep important originals and verify your output copies.

Read the included [quick start](docs/QUICKSTART.md) and
[preview notes](docs/PREVIEW-NOTES.md). Current
[capabilities](https://github.com/Skytuhua/Citrus/blob/main/docs/operations.md),
[support](https://github.com/Skytuhua/Citrus/blob/main/SUPPORT.md), source,
releases and issue reports are available in the
[official project](https://github.com/Skytuhua/Citrus).

## Licenses

Citrus's own code is under the included `LICENSE` (MIT). Other components retain
their separate licenses in `THIRD-PARTY-NOTICES.md`, `notices/`, the engine trees
and the separate corresponding-source archive. In particular, the Microsoft Windows SDK component terms apply
only to those unmodified Microsoft components; they do not replace Citrus's MIT
license or any other open-source permissions.

The bundled SDK terms and official redistribution list are in
`notices/presentation/Windows-SDK-License.rtf` and `Windows-SDK-REDIST.html`.
