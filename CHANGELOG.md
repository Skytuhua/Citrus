# Changelog

## 0.3.0-preview.9 — 2026-10-23

- Reuse SHA-256 hash contexts and memory buffers during setup verification while checking every installed file.
- Use a 1-MiB unpacking dictionary and 1-MiB verification reads while retaining every installed-file hash check.
- Discard failed hash state before checking another file or rolling back, and release all cached hashing resources when setup exits.
- Add native release checks for empty and chunk-boundary files, repeated hashes, locked/missing files, partial-read failure recovery and cleanup/reopen.
- Omit unused Office message catalogs and localized extension help while retaining the existing English engine behavior, document locale libraries, fonts and conversion engines.

## 0.3.0-preview.8 — 2026-10-22

- Use the standard Windows installer directly, without a separate download window.
- Omit unused Office spelling dictionaries, thesaurus data and help pages while retaining every offline conversion engine, font, hyphenation dictionary and license notice.
- Skip repeated installer output-folder changes while preserving every file write, error check and installed-file hash verification.

## 0.3.0-preview.7 — 2026-10-22

- Reduced repeated submission checks for large file batches.
- Removed unnecessary image-processing passes and pixel copies while preserving the checked output bytes.
- Rendered PDF thumbnails without temporary PNG files and reused verified original audio waveforms during trim and bleep edits.
- Removed an empty glass bitmap and overlay while preserving the body pixels and retained foreground.
- Removed unused glass compatibility methods. See the [measured results and verification scope](docs/verification/performance-2026-10-22.md).

## 0.3.0-preview.6 — 2026-10-22

- Fixed an intermittent installer completion crash when reading the Windows user identity. The existing setup wizard now reports installed-file verification progress and reuses its hashing provider.
- Rejected linked output ancestors during publishing and enforced the 32-megapixel limit for PDF image export.
- Skipped unnecessary artwork recovery for PDF text export while retaining scan OCR.
- Added discard confirmation to media and crop editors.
- Cleaned committed update backups during uninstall while preserving changed and unknown files.
- Kept 100 completed jobs plus every active job in recent history, with virtualized rows.

## 0.3.0-preview.5 — 2026-10-22

- Use non-solid LZMA compression for the complete installer to reduce its download size.
- Preserve every offline conversion engine and the application behavior from preview.4.

## 0.3.0-preview.4 — 2026-10-21

- Filled the wheel backing continuously through its center, retained the selected-format label and center cancellation, and removed the file preview.
- Hid menu scrollbars, simplified unnecessary interface copy, and improved job-progress and workspace selection responsiveness.
- Fixed PDF deletion while editing text, stale audio auto-trim results, and fields not being applied when submitting with Enter.
- Streamed long audio exports beyond the preview memory limit, reused verified video sources for seeking, and reduced image-to-PDF peak memory.
- Fixed interrupted-upgrade recovery after restoring the previous installation and one-pixel screenshot crop rounding.

## 0.3.0-preview.3 — 2026-10-21

- Reduced the actual complete installer download by distributing corresponding third-party sources separately and omitting debugging symbols.
- Preserved every offline runtime engine, license notice and source integrity check. The same release offers the complete source archive, checksums and installed download instructions.
- Kept the small online installer, full offline installer and existing automatic-update behavior.

## 0.3.0-preview.2 — 2026-10-21

- Fixed square black corners around the rounded video and audio editor windows.
- Added background checks for published GitHub versions, verified installer downloads, idle-only installation and update controls in Settings and the tray.
- Added transactional upgrades, failed-install rollback, interrupted-install recovery and protection against repeated update failures.
- Preserved files, settings and existing conversion workflows. The first updater-enabled release requires one manual installation for earlier previews.

## 0.3.0-preview.1 — 2026-10-21

Public Windows preview launch, not a stable-release or universal acceptance claim.

- Compact wheel with pale petals, solid orange selection and shared glass backing/page control; about 8% smaller than the preceding local revision.
- Smaller white menus, centered titles, right-side close buttons, scrolling tools and fixed actions.
- Real video preview, direct trim/crop handles, source-frame navigation, presets and tool-specific controls. Updates keep picture geometry stable.
- Image adjustment, annotation, backgrounds, cropping, redaction, measured compression and metadata.
- Audio trimming, normalization, channels, bleep ranges, visualization and audition.
- PDF organization, split/merge, metadata and reconstructed exports with supported OCR.
- Offline document, spreadsheet, presentation and EPUB conversion; Pages-compatible DOCX.
- Archive member repacking/extraction and subtitle conversion.
- Reduced paused-preview work, exact-frame navigation, faster verified late seeks and safer source-switch/retry recovery.
- Launch website, product imagery, edited workflow films and public download information.

See [capabilities](docs/operations.md) and [preview limits](docs/PREVIEW-NOTES.md). Earlier development checkpoints were not public releases.
