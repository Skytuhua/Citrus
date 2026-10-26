# Preview.10 performance review

This record describes the preview.10 source changes relative to `43b07f0a8853cd35b9e72efab12a970fd038db29`. It separates measured Linux workloads, output comparisons, source builds and Windows release checks. It does not establish overall application speed, startup latency or peak process memory.

## Changes and preserved contracts

- Editor validation caches immutable reflection/nullability metadata by model type and formats numeric error messages only on failure. It still traverses the complete structure, enforces the same limits and reports the same ordered issues in the current culture. The cache retains no documents.
- Image adjustment skips a neutral tone/color stage for spatial/effect-only edits and omits a whites/blacks power calculation when that control is zero. Q8 stage order and alpha remain unchanged.
- HEIC/HEIF raster loading returns the helper's already bounded, color-normalized decoded image instead of invoking the decoder again for dimensions. Other formats retain their bounded inspection before allocation. A first preview may still inspect its source separately; this change measures one raster load, not complete preview startup.
- PDF previews and thumbnails decode and freeze bitmaps on the shared two-worker preview budget. The dispatcher assigns only current, uncanceled results. Full preview dimensions, thumbnail bounds, rotation and error reporting are retained.
- PDF extraction initializes its OCR engine lazily, at most once per extraction. Page and iterator lifetimes remain scoped, table fallbacks retain their segmentation behavior, and the engine is disposed on success, failure and cancellation. Native-text-only pages do not initialize OCR.
- Shelf polling uses one background metadata scan at a time. Per-admission identities prevent stale scans from removing new, readded or freshly readmitted references. Collection updates resume on the owning context. Copy/drag still revalidate their selected paths and use the existing final file-action validation.
- Workspace hover admission is cached only within one drag identity and mode. Enter, leave, drop and teardown clear it; the actual drop reads and validates the selection afresh. Source availability is never accepted from a hover cache when adding files.

## Measured workloads

Release measurements used Linux x64, an AMD EPYC 7763, .NET SDK 10.0.402/runtime 10.0.13 and the repository's pinned managed packages. These are local measurements, not Windows timing results. Allocation figures are managed bytes per operation.

| Workload | Baseline | Candidate | Scope |
| --- | ---: | ---: | --- |
| Validate a 100,000-point stroke | 104.493 ms | 22.025 ms | 4.74× faster; 93,611,352 → 47,995,600 allocated bytes. |
| Validate a 10,000-page PDF recipe | 7.630 ms | 4.613 ms | 39.5% less time; about 9,403,432 → 6,279,523 allocated bytes. |
| Brightness adjustment, 2000 × 1500 | 262.352 ms | 158.570 ms | Inactive endpoint controls avoid their power calculations. |
| Grain-only adjustment, 2000 × 1500 | 370.613 ms | 119.352 ms | 3.11× faster; 2,072 fewer allocated bytes per render. |
| Vignette-only adjustment, 2000 × 1500 | 320.455 ms | 70.249 ms | 4.56× faster. |
| Invert-only adjustment, 2000 × 1500 | 323.074 ms | 83.449 ms | 3.87× faster. |
| Mixed active tone controls, 2000 × 1500 | 389.007 ms | 395.815 ms | 1.8% slower in this measurement; both endpoint controls remain active. |
| HEIC/HEIF raster load | Two helper decodes | One helper decode | Counted helper-boundary tests; genuine HEVC timing is separate. |
| OCR extraction, six selected pages | 1456.968 ms | 1048.725 ms | Five raster pages and one native-text page; actual engine initializations fell from five to one. |

Validation used five alternating samples after warmup, with tiered compilation disabled. Each stroke sample averaged ten calls; each PDF sample averaged forty. Image measurements used four ABBA cycles, eight calls per variant after four warmups; image creation was excluded. Input pixels covered every channel and alpha byte. Timings are medians. Retained baseline source copies matched the starting Git files after changing only class names.

OCR used three ABBA cycles, six extractions per variant. Native initialization time fell from a median 390.100 to 77.869 ms. Full-page Auto segmentation calls stayed at five and table SingleBlock fallbacks at six. This check used Linux Tesseract 5.5.0, managed wrapper 5.2.0 and the exact pinned English model. It does not substitute for the packaged Windows Tesseract runtime.

## Output and regression checks

- Twenty valid/malformed editor documents in three cultures produced identical ordered validation issues against the original validator.
- Eighteen image recipes at exact and quarter-scale rendering matched all RGBA bytes in thirty-six comparisons, including hidden RGB at zero alpha, positive/negative endpoint controls, mixed tone controls, spatial effects and grain.
- Six HEIC/HEIF profile cases and a typed export reproduced two decodes before the fix and one afterward. Pixel/profile/alpha comparisons, malformed payload bounds, missing sources, cancellation and cleanup checks remained enabled. The counted shell helper tests the managed boundary, not HEVC fidelity.
- OCR semantic-model bytes matched the original implementation with artwork recovery both disabled and enabled. The fixtures include table cell fallbacks, a following paragraph, blank raster, repeated pages and native text. Native instrumentation confirmed disposal after cancellation and exceptions and zero initialization for native-only text.
- Allocation regressions were observed failing on the original validator and image implementation. Shelf regressions reproduced blocking metadata and stale duplicate-readmission removal before their fixes. Drag regressions reproduce repeated hover inspection while final admission remains fresh.

## Verification boundaries

Final local checks built all six solution projects in Release with zero warnings or errors. Core tests passed 326 cases with one Windows-only sharing-violation skip. Conversion tests passed 651 cases, failed 37 and skipped one published-pack check. Every originally passing conversion case still passed, with no newly failing case. Making the pinned English data and compatible Linux OCR libraries available allowed seven previously unavailable OCR cases to pass; that prerequisite improvement is separate from the code optimizations. The remaining failures are recorded as failures, and require the appropriate Windows/offline-pack environment before release qualification.

The new Windows responsiveness coverage contains twelve PDF bitmap cases and eleven routed drag cases; the existing shelf integration case is also in the bounded source/release gate. [Windows source CI](https://github.com/Skytuhua/Citrus/actions/runs/40276690603) for initial candidate `614726e137bd94d52f69f3fb7dffab1b987dd219` passed all 327 core cases and all 24 bounded Windows cases, with no skips or failures. This qualifies those specific contracts rather than the complete native application.

Release preparation also checks all conversion tests against the pinned Windows engines. The explicitly requested preview.10 publication is gated to that exact unsigned tag; other tags and manual runs retain draft-only preparation. Twenty-two promotion tests cover release/tag/source identity, signature policy, the exact five assets, downloaded byte hashes, mutation detection, draft lookup, UTF-8 transport and bounded public downloads. The publisher verifies final local/uploaded bytes before making the preview public, then hashes the public downloads. A failed verification preserves the release for investigation; it never deletes or replaces published assets.

The Linux SDK originally lacked its WindowsDesktop reference pack in the SDK targeting-pack directory. That caused .NET 9 package-pruning fallback and a misleading `System.IO.Packaging` restore error. Provisioning the official matching WindowsDesktop 10.0.13 reference pack repaired the environment. The repository's dependency versions and lock files were not changed. Locked solution restore and Windows test-project cross-compilation succeeded.

WPF runtime tests require Windows. A Linux execution attempt aborted before running tests because `Microsoft.WindowsDesktop.App` is unavailable. PDF bitmap tests are included in source CI and release preparation; full conversion checks with pinned engines are also required before preparing the installer. Missing prerequisites and skips are not passes.

The original Linux conversion suite had forty-four failures, including missing Office/OCR/HEIC/ExifTool inputs, Windows AppContainer requirements and existing acceptance failures. Follow-up verification records the actual remaining failures rather than interpreting the original suite as green. No clean-PC install, physical mixed-DPI or interactive Explorer qualification is implied by the source changes or these measurements.
