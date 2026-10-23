# Windows automatic updates

Citrus checks published versioned releases from `Skytuhua/Citrus`. Documentation, website and media changes do not trigger installation. Preview builds follow published preview and stable versions; draft releases remain invisible. The first updater-enabled version must be installed once, because earlier installed binaries contain no update client.

## Download and installation contract

The app checks without blocking startup or rendering, downloads one complete installer to a private staging directory, and verifies GitHub's exact asset size and SHA-256 before handing off. It applies only after conversions, native file actions, dragging and editors are idle. A failed version is suppressed until a higher release or an explicit manual installation resolves it.

The downloaded NSIS executable is the external update host. The argument contract is:

```
/AUTOUPDATE=1 /UPDATEPID=<decimal PID> /UPDATESTART=<UTC Windows FILETIME> /PREVIOUSINVENTORY=<64 lowercase hex>
```

The setup waits for the exact parent process creation time and exit, never kills a process, then acquires the existing per-user installation and session gates. It validates the fixed per-user location, registry receipt, actual old inventory hash and reparse-point boundaries. A changed installed inventory is admitted only by this explicit handshake, or an interactive verified upgrade confirmation. Ordinary same-inventory repair remains supported.

The new installer independently compares its compiled SDK license hash with `AcceptedSdkLicenseSha256` in the 64-bit `HKCU\Software\Citrus` key. Automatic silent mode cannot override a changed/missing acceptance receipt through `/ACCEPTSDKTERMS=1`; exit 26 requires interactive license review. Legacy installations therefore use an interactive first update. Interactive/manual silent installation acceptance continues to work as before.

Before replacing files, setup flushes a registry journal and renames the old installation on the same volume to `<InstallLocation>.previous-<old inventory hash>`. It writes the new tree and independently hashes every installed owned file before committing. A write failure restores the old tree and ownership/version/license registration. An interrupted `Prepared` or `Installing` journal is recovered under the same guards before retry. Failed partial trees are preserved in bounded collision-safe sibling names; setup never recursively deletes unrelated files.

The complete journal consists of `UpdateState`, `UpdateBackupPath`, `UpdatePreviousInventory`, `UpdateInstalledInventory`, `UpdatePreviousSdkLicense` and `UpdatePreviousVersion`. Its states are `Prepared`, `Installing` and `Committed`. After a successful new resident startup, managed cleanup validates the committed new identity and old inventory, deletes only unchanged old owned bytes and empty directories, preserves modified or unrelated files, and clears the complete journal. The old generated uninstaller is retained when no separate ownership hash covers it.

A corroborated failed automatic apply relaunches the verified previous resident after releasing installation guards. It records `UpdateLastError` and `UpdateFailedVersion` so the app can explain the failure and avoid repeating that version on every launch. A successful installation clears both values. Settings and user files under `%LOCALAPPDATA%\Citrus` are outside the installation transaction.

## Reproducible release preparation

`CitrusVersion.props` is the authoritative application and installer version. `packaging/package.py prepare` generates `identity.nsh`, `version.nsh`, `paths.nsh`, `install.nsh`, `uninstall.nsh` and `verify.nsh`; `verify-prepared` rejects stale generated metadata or changed payload bytes. `version.nsh` includes the exact new SDK license hash.

On Windows, preparation also builds `Citrus.PayloadVerifier.exe` and its embedded `PayloadManifest.cs` using the already required pinned .NET SDK compiler and installed .NET Framework references. The temporary helper uses up to four joined workers to hash the same owned file list, including the inventory itself. Both setup commit and before-first-boot uninstall authenticate its SHA-256 before launching its quoted absolute path; launch failure or any nonzero exit retains the existing failure/recovery behavior. It reads no external manifest, ignores unrelated user files, and remains outside the installed application. `verify-prepared` independently recompiles trusted source plus the exact inventory and requires identical deterministic executable bytes, so replacing the helper and its generated digest together cannot qualify. Linux cross-publishing retains the existing serial NSIS verification.

The helper targets the .NET Framework 4.8 already included with the minimum supported Windows version. Its filesystem operations use explicit extended absolute paths, preserving owned paths longer than 260 characters without changing Windows policy. Opened file handles are bound to their expected physical paths before and after hashing, and all workers finish before success. See Microsoft's [extended path documentation](https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation) and [.NET Framework long-path support](https://devblogs.microsoft.com/dotnet/announcing-net-framework-4-6-2/).

`packaging/bootstrap-engines.py` supplies missing ignored engine inputs on a clean checkout. It downloads the frozen first preview installer, verifies its exact 1,302,313,670 bytes and SHA-256 `a6d3dfcccf2b6627f3499507795a7ef7c4eacda4274034abc57c7f583c0cdae8`, reads it as a 7-Zip archive without executing it, and restores only files that individually match repository pins. Existing mismatched source/runtime bytes are refused. This bootstrap is deliberately tied to that reviewed engine pack; changing engine pins requires supplying a newly reviewed pack.

Tagged builds in `.github/workflows/release.yml` restore locked dependencies, hydrate the pinned engine inputs, use the exact NSIS 3.11 compiler archive hash, run updater and package checks, build the full offline EXE and upload complete assets to a **draft**. It checks GitHub's uploaded asset digest and size before finishing. The workflow intentionally does not publish: native workflow, clean-machine, visual and public-download acceptance remain promotion gates. A published installer is never overwritten by this workflow.

`python packaging/release-metadata.py` validates the actual installer FileVersion resource against the central version, then writes public metadata and concise checksums. For an isolated build or independently constructed public source commit, pass `--dist <workspace release directory> --source-commit <full existing Git SHA>`; the committed central version must also match.

## Verification scope

`python packaging/test-update-metadata.py` tests central-version/license derivation and generated per-file hash checks. `python packaging/test-update-installer.py` compiles small real NSIS fixtures in unique installation, registry and mutex namespaces, never the user's real Citrus installation. It covers changed-inventory updates, exact parent identity refusal, write-failure rollback, interrupted recovery, repeated failure, consecutive changed updates after a modeled successful boot, SDK silent refusal, and compiled FileVersion consistency. Fake payloads do not execute WPF; managed boot cleanup and native application workflows require their separate tests. Large-pack performance, actual resident handoff and clean-machine acceptance are separate evidence.
