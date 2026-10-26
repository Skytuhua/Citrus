**Download the standard Windows installer:** [Citrus-Setup.exe](https://github.com/Skytuhua/Citrus/releases/download/v0.3.0-preview.12/Citrus-Setup.exe).

This preview reduces repeated editor and conversion work while retaining the complete offline engine pack:

- Editor validation reuses type metadata and builds numeric error messages only on failure.
- Image adjustments skip neutral color work and inactive whites/blacks calculations.
- HEIC and HEIF rasters decode once per load instead of twice.
- Multi-page PDF OCR initializes its engine once per extraction.
- PDF bitmap decoding and shelf polling run in background workers, with cancellation and stale-result checks.
- Drag-hover admission is reused within one drag; an actual drop always reads and validates the selection afresh.
- Fresh engine hydration preserves the destination permissions needed by isolated Office workers. Interpreter startup captures conversion diagnostics and prevents unrelated processes from retaining private logs.

Measured Linux Release workloads, on one AMD EPYC 7763 with .NET 10.0.13:

- Validate a 100,000-point stroke: 104.5 → 22.0 ms, with about 49% less managed allocation.
- Apply grain to a 2000 × 1500 image: 370.6 → 119.4 ms. Vignette: 320.5 → 70.2 ms.
- Extract six selected PDF pages, five using OCR: 1457 → 1049 ms; native engine initialization count fell from five to one.

These isolate named operations and are not Windows or whole-application speed claims. One mixed-tone image workload was 1.8% slower. Thirty-six exact/scaled image comparisons and the checked OCR semantic models match their baselines. See the [performance review](https://github.com/Skytuhua/Citrus/blob/v0.3.0-preview.12/docs/verification/performance-2026-10-25.md) for sample counts, runtime differences and verification boundaries.

Preview.10 stopped at compiler acquisition, and preview.11 stopped during native conversion qualification before Python worker entry. Neither published an installer. Preview.12 retains the application changes and verified official download fallbacks for the pinned installer compiler.

The [final release workflow](https://github.com/Skytuhua/Citrus/actions/runs/40287957612) recorded zero failures across 327 core, 26 managed-updater, 664 conversion and 24 editor checks; 10 Linux helper/link cases and the opt-in published-pack relocation smoke test were skipped. Fourteen isolated installer-transition tests, 36 package tests and 15 native hash results passed, along with the complete engine/source/license inventory and sealed-installer checks for released source `71ee22a218be4a74b41706a8030dd5937464ddb1`. All five uploaded assets matched their final local bytes before publication, and their public download hashes matched afterward: [publisher receipt](https://github.com/Skytuhua/Citrus/actions/runs/40287957612/job/121456025639#step:17:1). Automated checks do not establish clean-PC, physical mixed-DPI, full Explorer/OLE or universal output-fidelity acceptance.

Windows x64. **Unsigned preview**; Windows may report an unknown publisher. Review converted outputs and retain important originals. `release-manifest.json` records the exact source revision and signature status; `SHA256SUMS` records the final installer and metadata hashes. Corresponding third-party sources are the separate optional download on this release.
