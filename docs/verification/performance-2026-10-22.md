# Preview.7 performance verification

The preview.7 source changes reduce repeated processing, pixel copying and temporary-file work. These measurements compare the affected workloads with preview.6 commit `1207aebfc627caa34bcc5f8ede49ac8009a10914` on one Windows machine using .NET 10.0.13. They describe the named operations, not overall application speed, startup latency or peak memory. Rendering quality, output dimensions and source-integrity checks remain enabled.

## Measured workloads

Times below are medians. Allocation figures are managed bytes for the measured operation unless stated otherwise.

| Workload | Baseline | Candidate | Scope |
| --- | ---: | ---: | --- |
| Submission preflight, 10,000 inputs | 359.3226 ms | 10.4296 ms | Inspection reads fell from 50,005,000 to 10,000. No conversion or file I/O is included. |
| Tone-only image adjustments, 1920 × 1080 | 124.6395 ms | 107.9647 ms | Skips an inactive final effects pass. |
| Cached exact image render, 1024 × 1024 | 8.0751 ms | 5.2148 ms | Allocations: 8,500,000 → 4,305,672 bytes. |
| Annotation render, 1024 × 1024 | 58.8785 ms | 51.8986 ms | Allocations: 12,752,792 → 8,558,464 bytes. |
| Complete frame decoder, 1024 × 1024 | 72.90885 ms | 70.9058 ms | Allocations: 14,930,584 → 10,749,068 bytes; total speed improvement is not established. |
| Final decoder pixel materialization | 0.66836 ms | 0.31191 ms | Isolates the removed managed pixel copy. |
| PDF thumbnail render | 9.18415 ms | 3.8502 ms | 72 temporary directories and PNG files → zero; allocations increased from 122,329 to 135,431 bytes per render. |
| Repeated audio trim refresh | 374.13 ms | 1.753 ms | Process-wide managed allocations: 7,657,260 → 70,064 bytes. All 1,536,044 original source bytes are still hashed on every cache hit. |

Submission preflight used 21 samples per variant across three fresh processes. Image adjustments, cached rendering and annotation used seven recorded samples per workload after warmup. The PDF comparison used 72 renders per variant: six page/resolution cases repeated twelve times. All six resulting PNG hashes match exactly. Thirteen image/frame golden hashes also match, including active effects, spatial processing and decoded output.

The decoder follow-up separately verified exact input/output byte hashes. It used eight independent ABBA blocks, with 192 samples across 16 fresh processes per variant and identical harness and media executable hashes. The paired 95% confidence interval for total decoder time spans −6.57% to +3.35%, including zero; the lower pooled median does not establish a total decoding speed improvement. The measured final materialization and allocation reductions are separate results.

The audio fixture was an eight-second, 48 kHz stereo WAV with four repeated trim edits, measured in one quiet run per revision. Cached original display rows avoid repeated staging and decoding while retaining full source verification. These figures do not measure native process memory, playback quality or export speed.

## Glass surface preservation

The same compiled probe exercised real controller code with generated pixels and fake capture sessions, without opening a window. All 15 full-body and source-over composite hash pairs match the baseline exactly across wheel widths of 184, 368, 552 and 736 pixels and an 80 × 60 generic surface. Bootstrap and repeated live frames reuse the retained body bitmap.

Removing the entirely transparent compatibility bitmap reduces retained published surfaces from two to one. At 368 × 454 pixels, their combined pixel payload falls from 1,336,576 to 668,288 bytes: 668,288 bytes removed. This measures bitmap pixel payload, not whole-process native memory or frame rate. Body rendering, glyph order, fallback policies, DPI dimensions and stale-frame/cancellation guards retain their implementations.

## Source verification

| Check | Result |
| --- | --- |
| Full solution Release build | Passed; zero warnings or errors |
| Core suite | 274 passed |
| Full conversion suite | 639 passed, four environment skips, zero failures |
| Selected Release Windows suite | 63 passed, zero skips or failures: 35 glass and 28 media checks |

The conversion skips cover three symbolic-link prerequisites and one published-pack environment case. Windows checks use unshown controls; they do not qualify the full native suite. Packaged smoke checks are qualified separately for the sealed release payload. No new visible UI, capture, clean-PC installation or physical mixed-DPI verification was performed. Existing product media remains unchanged under the [media hold](../MAINTAINING.md).

## Retained local evidence

The detailed logs, compiled probes and raw samples below are private local records under the ignored `.local` directory; they are not part of the public repository or package.

- Submission comparison and sample counts: `.local/performance-20261022/core/comparison.json`; core test record: `.local/performance-20261022/core/core-candidate.trx`.
- Image timings, allocations and 13 fixture hashes: `.local/performance-20261022/image-buffers/comparison.json`; separate decoder ABBA analysis and exact-byte proof: `.local/performance-20261022/image-buffers/decoder-abba/summary.json`.
- PDF timings, temporary-file counts and allocation tradeoff: `.local/performance-20261022/pdf-stream/evidence/comparison.json`.
- Audio refresh measurements and source-verification counts: `.local/performance-20261022/media-waveform/comparison.json`.
- Release glass pixel and surface comparison, tested DLL identities and 63-case test record: `.local/performance-20261022/glass-cleanup/release-comparison.json`, `release-identity.json` and `release-candidate.trx` in the same directory.
- Full conversion test record: `.local/performance-20261022/full-regression/conversion-full.trx`; Release build log: `.local/performance-20261022/release-build.log`.
