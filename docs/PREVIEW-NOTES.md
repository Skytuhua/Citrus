# 0.3 preview notes

The 0.3.0-preview.10 candidate reduces repeated editor validation, inactive image adjustment work, HEIC raster decoding and OCR initialization. PDF bitmap decoding and shelf polling run on bounded background work; actual file drops still validate their selection afresh. See the [performance review and verification boundaries](verification/performance-2026-10-25.md). The measured timings are Linux workloads; Windows runtime and installer qualification remain separate release requirements.

Preview.9 reduced repeated allocations during installed-file verification by reusing SHA-256 contexts and buffers. Every installed file still receives its complete hash check; failed partial hashes are discarded before another file or rollback is checked. Earlier previews without automatic updates require [one manual upgrade](AUTOMATIC-UPDATES.md).

Preview.8 switched to the standard installation wizard directly and omitted unused Office spelling/thesaurus data and help pages. Every conversion engine remains available offline; fonts, hyphenation data and license notices remain included. Setup skips repeated output-folder changes while retaining all file and integrity checks.

Preview.7 reduced repeated work in large submissions, image editing, PDF thumbnails, audio waveform refreshes and glass presentation. Checked image, PDF and glass output bytes remain unchanged. The [performance record](https://github.com/Skytuhua/Citrus/blob/main/docs/verification/performance-2026-10-22.md) gives the measured workloads, allocation tradeoffs and verification scope.

Preview.6 fixed the six repository audit findings and an intermittent installer crash when reading the Windows user identity. Setup reports installed-file verification progress and reuses its hashing provider to shorten the final verification step. PDF image export enforces the 32-megapixel limit; plain-text export skips unnecessary artwork recovery while retaining scan OCR. Media and crop editors confirm before discarding edits. Recent history retains 100 completed jobs plus every active job, with virtualized rows. Publishing refuses linked output ancestors. Uninstall cleans a committed update's previous owned files before removing registration, preserving changed and unknown backup files. The complete package retains preview.5's LZMA compression.

## Current boundaries

- Windows x64 is supplied. The technical minimum is build 19041; Windows 11 is recommended. Other operating systems and native ARM64 are not packaged.
- The installer is unsigned. Windows may identify its publisher as unknown. Verify the release source and checksum before deciding to run it.
- Extension recognition does not guarantee every codec/container combination. H.264 depends on actual Windows Media Foundation capability; baseline MP4 editing uses MPEG-4 Part 2/AAC. Visual editing is SDR.
- Compression goals may be unreachable. The app reports failure rather than quietly changing dimensions, channels or duration to meet one.
- PDF-to-Office and OCR reconstruct recoverable content. Complex tables, fonts and layouts may need correction. English OCR data is included; encrypted PDFs reject. Pages destinations write compatible `.docx`, not fabricated `.pages` files.
- Still-image tools reject animated/multipage inputs where an operation cannot preserve them. Some conversion presets omit secondary streams or metadata; use the Metadata tool when that is the task.
- Archives reject unsafe paths, links, duplicates and unsupported encrypted/incomplete members, with entry-count and expanded-size limits.
- The workspace's static MP4 thumbnail is limited. Actual video editors show decoded, playable video.
- Native glass excludes its windows from capture to prevent feedback. Recordings may omit it. Opaque accessibility/device fallbacks remain available.

## Verification and performance

Preview.9's native hashing checks cover empty and chunk-boundary files, repeated hashes, locked/missing files, partial-read failure recovery and cleanup/reopen. End-to-end installation timing and interactive Finish-page acceptance are qualified separately with the packaged release.

Preview.7 source verification passed a full Release solution build with zero warnings or errors, 274 core tests, 639 conversion tests with four environment skips, and 63 selected Release Windows tests with no skips or failures. The Windows batch contains 35 glass checks and 28 media checks using unshown controls. Thirteen image/frame fixture hashes, six PDF PNG hashes and 15 glass body/composite hash pairs match their baselines; the decoder follow-up separately verifies exact input/output bytes. These checks do not qualify the full native desktop suite. Packaged smoke checks are qualified separately for the sealed release payload.

The [performance record](https://github.com/Skytuhua/Citrus/blob/main/docs/verification/performance-2026-10-22.md) compares preview.7 source changes with preview.6 commit `1207aeb`. Results apply to the named local workloads, not overall app speed or memory. Decoder allocation and final pixel materialization improved; the total decoder timing comparison does not establish a speed improvement. PDF thumbnail rendering removes temporary disk output with a small managed-allocation increase.

### Historical preview.6 and preview.4 evidence

Preview.6's targeted checks passed 60 PDF tests, 36 desktop regression tests and a final 14-test history/progress/navigation batch. These overlap and must not be summed. A guard-page regression reproduced the old installer identity crash and passed with the bounded copy. Full-inventory probes checked the same 21,560 files before and after the hashing change; elapsed time on this machine fell from 451 to 116 seconds. These are local measurements, not a clean-PC performance guarantee.

The broader affected desktop batch passed 100 cases and failed three: two existing foreground-activation fixture failures and one navigation timeout that passed its isolated recheck. The runtime and UI records below are from preview.4; they remain historical evidence.

Local verification recorded **271 core passes**, **609 conversion passes with four capability skips**, **77 editor regression passes**, and **126 focused wheel passes with three opt-in native cancellation skips**. Batches overlap and must not be summed. Isolated installer transitions passed nine update cases and two recovery cases. Source-hash, cancellation and collision-safe publication checks remain enabled.

The full Windows suite was not green: native foreground/compositor proof cases and a symlink privilege case remain unresolved on this machine. A targeted recheck passed three native cases and failed seven. These checks have not been weakened, and their failures do not count as visual or clean-machine acceptance.

Generated-source optical rendering/readback/bitmap publication measured p95 2.60 / 3.30 / 8.06 ms at 1× / 1.5× / 2×. These are bounded GPU experiments, not desktop scanout. Prepared native target-show p95 was 18.17 and 20.54 ms, above the 16.7-ms target. Tiny-file video-editor readiness ranged from 347.3 to 611.7 ms: single observations per tool, not percentiles. Exact source scanning and verification still take work.

Complete five-cold/thirty-warm production first-blur timing, the full Explorer/OLE sequence, physical mixed-DPI transitions and every operation's full native state matrix remain unqualified. There is no zero-latency promise. Package validation is recorded separately with the release.

## Reporting a problem

Include version, Windows build, display scale, format/codec where known and exact steps. Use a small non-sensitive reproduction. For cancellation trouble, say which buttons/modifiers were held and whether normal clicks recovered after release. Do not post private files or unredacted screenshots. [Open an issue](https://github.com/Skytuhua/Citrus/issues/new/choose).
