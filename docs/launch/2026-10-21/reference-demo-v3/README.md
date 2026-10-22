# Four-second PNG → WEBP demonstration

[Watch with sound](citrus-reference-4s.mp4) · [Silent video](citrus-reference-4s-silent.mp4) · [Poster](poster.webp) · [Scene descriptions](citrus-reference-4s.vtt)

Both videos are 1920 × 1080, 60 fps, 240 frames and exactly 4.000 seconds. The README animation is a 960 × 540, 30 fps GIF of the same silent picture. The original launch films remain in the adjacent `media/` directory.

The film follows one continuous interaction: drag `sample.png`, hold Shift, hover BMP → PDF → WEBP, release, show Citrus's native pending notification, and leave `sample.png` beside its real converted `sample.webp`.

Wheel states come from the current production PNG operation catalog and native WPF foreground, with the production Direct3D backing on an owned near-white stage. PNG is excluded as a destination for PNG input. Genuine Windows Shell image thumbnails and a verified 1672 × 941 WEBP are used. The original PNG hash is preserved. The compositor supplies the pointer, Shift keycap and edited timing; this is not an unedited Explorer recording or a conversion-speed benchmark. Native capture exclusion is unchanged.

The citrus photograph is the project's original generated sample image, SHA-256 `2946f37e87f4e79a93de3340492cb021130dcb832ffa4421b28c7d9818e36998`. The owner-supplied reference soundtrack is trimmed to 0–4 seconds with no added material. Aligned decoded waveform correlation is 0.999285, with no clipped samples. Subjective listening was unavailable in the authoring tool context.

Independent verification covers full decoding, exact video contracts, identical sound/silent video bitstreams, reopened WEBP output, unchanged original, audio alignment, descriptive captions and uninterrupted normal-speed playback. The final browser playback reported 240 frames with zero dropped or corrupted frames. The authoring delivery verifier passed 21/21 checks; timeline and verifier regressions passed 9/9. The opt-in native capture test passed 1/1. File hashes and byte sizes are recorded in [manifest.json](manifest.json).

The editable authoring project and raw capture evidence are retained locally under `launch-media/v3/`; this public folder contains only the finished media and concise provenance.
