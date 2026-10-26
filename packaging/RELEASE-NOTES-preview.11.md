**Download the standard Windows installer:** [Citrus-Setup.exe](https://github.com/Skytuhua/Citrus/releases/download/v0.3.0-preview.11/Citrus-Setup.exe).

This preview reduces repeated editor and conversion work while retaining the complete offline engine pack:

- Editor validation reuses type metadata and builds numeric error messages only on failure.
- Image adjustments skip neutral color work and inactive whites/blacks calculations.
- HEIC and HEIF rasters decode once per load instead of twice.
- Multi-page PDF OCR initializes its engine once per extraction.
- PDF bitmap decoding and shelf polling run in background workers, with cancellation and stale-result checks.
- Drag-hover admission is reused within one drag; an actual drop always reads and validates the selection afresh.

Measured Linux Release workloads, on one AMD EPYC 7763 with .NET 10.0.13:

- Validate a 100,000-point stroke: 104.5 → 22.0 ms, with about 49% less managed allocation.
- Apply grain to a 2000 × 1500 image: 370.6 → 119.4 ms. Vignette: 320.5 → 70.2 ms.
- Extract six selected PDF pages, five using OCR: 1457 → 1049 ms; native engine initialization count fell from five to one.

These isolate named operations and are not Windows or whole-application speed claims. One mixed-tone image workload was 1.8% slower. Thirty-six exact/scaled image comparisons and the checked OCR semantic models match their baselines. See the [performance review](https://github.com/Skytuhua/Citrus/blob/v0.3.0-preview.11/docs/verification/performance-2026-10-25.md) for sample counts, runtime differences and verification boundaries.

The unpublished preview.10 candidate stopped at compiler acquisition. This candidate preserves its application changes and adds verified official download fallbacks for the pinned installer compiler.

Publication follows Windows source/core/editor checks, conversion checks with pinned engines, installer/update fixtures and complete package verification. The [release workflow receipts](https://github.com/Skytuhua/Citrus/actions/workflows/release.yml) record their actual outcomes. The uploaded assets are checked against the final local bytes before publication, then their public downloads are hashed again. Automated checks do not establish clean-PC, physical mixed-DPI, full Explorer/OLE or universal output-fidelity acceptance.

Windows x64. **Unsigned preview**; Windows may report an unknown publisher. Review converted outputs and retain important originals. `release-manifest.json` records the exact source revision and signature status; `SHA256SUMS` records the final installer and metadata hashes. Corresponding third-party sources are the separate optional download on this release.
