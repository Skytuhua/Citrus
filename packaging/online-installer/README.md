# Citrus online installer

The default user download is the regular **[Citrus-Setup.exe](https://citrous.app/download/)**, which opens the standard installation wizard directly. New releases publish only that installer. This optional downloader is retained for compatibility with earlier releases and links.

`Citrus-Web-Setup.exe` is a small Windows GUI that downloads the complete, pinned Citrus installer from the matching GitHub release. It checks the exact byte count and SHA-256 before opening that installer. It accepts no silent-install or payload-override arguments; the normal installer retains its license acceptance, upgrade checks and uninstall behavior.

The browser download is small. Setup downloads the current complete runtime package; the website and version-pinned manifest show its actual size. Internet is required during setup; the installed app then works offline. The complete `Citrus-Setup.exe` remains available for offline or repeated installs. Corresponding third-party sources are available separately on the same release and are not downloaded during installation. Both previews are unsigned.

The downloader uses Windows .NET Framework 4.8, included with Windows 11. It requires 64-bit Windows, runs without administrator privileges, and deletes its private temporary directory after the full installer exits. Cancelled, corrupt, incomplete, unexpected-host and non-HTTPS downloads never execute. Downloads have no whole-transfer deadline, so slow connections can finish; cancel or retry when needed. There is no resume support.

Build with the Windows Framework compiler and Python 3.12+:

```powershell
python packaging/online-installer/build.py --manifest .local/update-release-20261021/release-manifest.json --output .local/online-installer-20261021
python packaging/test-online-installer.py
```

The offline installer must sit beside its verified manifest and match its size and hash. The builder writes the web installer, `web-installer-manifest.json`, and a separate `SHA256SUMS-web-installer`. Existing release assets are never replaced by this script. Publish only these three public outputs; generated `Release.cs` and test executables are local build files.

Optional real-network verification compiles a separate harness from the production download code. It downloads and verifies the entire package, then deletes it without launching it:

```powershell
python packaging/online-installer/verify-network.py --built-directory .local/online-installer-20261021
```

Contract tests use tiny in-memory HTTP responses and a recording launcher, never the actual installer. Full clean-machine installation remains a separate acceptance check.
