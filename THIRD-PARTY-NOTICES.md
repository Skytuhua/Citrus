# Third-party software in Citrus

The installer carries the component `notices/` and provenance manifests.
Corresponding sources and rebuild inputs are freely available as
`Citrus-ThirdParty-Sources.zip` beside the installer on the same versioned
[GitHub release](https://github.com/Skytuhua/Citrus/releases).
The installed `SOURCE-DOWNLOADS.md` and `source-distribution.json` give the exact
release URL, archive checksum and inventory. The archive preserves paths such as
`source/citrus-media-source.tar.gz`, `source/image/` and `source/feature-pack/`;
references to those source paths below and in archived rebuild instructions
are relative to that source download. It is not required to run Citrus.
Licenses and conversion engines are bundled locally; conversion does not fetch
them or a backend at runtime. Citrus's own MIT license is included as `LICENSE`.

| Component | Pinned version / source | License material |
| --- | --- | --- |
| .NET / Windows Desktop runtime | 10.0.13 from SDK 10.0.402 win-x64 runtime packs | `notices/dotnet/`: Microsoft MIT licenses and .NET third-party notices |
| Magick.NET Q8 x64 / Core | 15.0.0; ImageMagick 7.1.2-33 | `notices/Magick.NET-15.0.0-Notice.txt` includes bundled delegates; `image-backend.lock.json` records package/source/native hashes |
| HEIC: libheif / libde265 / Kvazaar | 1.23.6 / 1.1.3 / 2.3.2 | LGPL-3.0-or-later / LGPL-3.0-or-later / BSD-3-Clause; `notices/Image-Feature-Pack-Notice.txt`, `notices/image-licenses/`, `source/image/` and `image-backend.lock.json` |
| ExifTool and portable Perl | ExifTool 13.59, official linked Windows package | Same terms as Perl (Artistic License or GPL); complete upstream Perl and launcher licenses accompany `image/exiftool/`; `notices/Image-Feature-Pack-Notice.txt` |
| PDFsharp / PDFtoImage / PdfPig | 6.2.4 / 5.4.0 / 0.1.16 | `notices/PDF-Notice.txt`, `notices/pdf-licenses/`, `pdf-backend.lock.json`; MIT / MIT / Apache-2.0 |
| PDFium / SkiaSharp and native Skia | PDFium 7961 / SkiaSharp 4.150.1 | BSD-style and upstream third-party notices under `notices/pdf-licenses/`, including PDFium delegate texts |
| System.Security.Cryptography.Pkcs and Microsoft extension dependencies | Pkcs 10.0.13; exact dependency versions in PDF lock and NuGet lock files | MIT and corresponding notices under `notices/pdf-licenses/` |
| SharpCompress | 0.50.5 | MIT: `notices/archive-licenses/SharpCompress-0.50.5-MIT.txt`, `archive-backend.lock.json` |
| LibreOffice | 26.8.1.1, unmodified official Windows runtime | MPL-2.0 and separate upstream third-party terms in `feature-pack/libreoffice/license.txt`, `LICENSE.html` and `NOTICE`; corresponding sources in `source/feature-pack/` |
| Pandoc | 3.12.1 | GPL-2.0-or-later; `feature-pack/pandoc/pandoc-3.12.1/COPYRIGHT.txt` and `COPYING.rtf`; source in `source/feature-pack/pandoc-3.12.1.tar.gz` |
| Tesseract / .NET wrapper / Leptonica | 5.2.0 / 5.2.0 / 1.82.0 | Apache-2.0 / Apache-2.0 / BSD-style; `notices/Document-Feature-Pack-Notice.txt`, upstream notices and release sources in `source/feature-pack/` |
| English OCR model | tessdata_fast commit `87416418657359cb625c412a48b6e1d6d41c29bd` | Apache-2.0; `feature-pack/tessdata/LICENSE` and pinned model/source in `feature-pack.lock.json` |
| DocumentFormat.OpenXml / Tabula | 3.5.1 / 1.0.1 | MIT; `notices/Document-Feature-Pack-Notice.txt` and corresponding upstream notices |
| Microsoft Visual C++ runtime | Ten x64 CRT files from the pinned official LibreOffice MSI; exact hashes in `feature-pack-runtime.sha256.json` | Separate proprietary Microsoft redistributable terms, retained in `feature-pack/libreoffice/license.txt`; these files are not licensed under Citrus MIT or LibreOffice MPL |
| MinGW-w64 runtime | Exact files and hashes in the image/media locks | GCC runtime: GPLv3 with GCC Runtime Library Exception 3.1; winpthreads: upstream MIT/BSD notices under `notices/image-licenses/` and `notices/media-licenses/` |
| Owned FFmpeg / FFprobe build | Exact six upstream commits, downloads and SHA256s in `media-lock.json` | LGPL-2.1-or-later FFmpeg/LAME, BSD-style Ogg/Vorbis/Opus/libvpx and MinGW runtime notices in `notices/media-licenses/`; summary `notices/Media-Notice.txt` |
| NSIS installer engine | 3.11 | `notices/NSIS-3.11-COPYING.txt`; zlib/libpng, bzip2 and Common Public License components as listed there |

The FFmpeg build uses shared replaceable DLLs; GPL, nonfree and version3 code,
network protocols and capture devices are disabled. LAME is a separate LGPL
shared encoder. The included media source archive contains all upstream source
archives/trees, patch, exact configuration, build scripts/logs, headers and import
libraries. [Media rebuild instructions](docs/media-engine.md#build-and-corresponding-source)
include the complete archive-only reconstruction. Reverse engineering for
debugging modifications to LGPL libraries is permitted. See exact upstream
notices for copyrights, terms and patent notices; this summary does not replace
them. Build/test-only dependencies are pinned in repository NuGet lock files.

HEIC libraries are also dynamically linked and replaceable. Their source
archives, Citrus bridge source and exact build script are included in
`source/image/`. The document engine runtime and source inventories are pinned
in `feature-pack.lock.json` and `feature-pack-runtime.sha256.json`;
`notices/Document-Feature-Pack-Notice.txt` explains the app-private deployment.
The corresponding source archives and rebuild inputs are in the same-release source download,
including LibreOffice release, dictionaries, help and translations sources.
Each component retains its own license and applicable modification and
redistribution rights.

## Windows presentation SDK projection

The documented UISettings API uses the implicit SDK package Microsoft.Windows.SDK.NET.Ref **10.0.19041.57**, selected explicitly with .NET SDK **10.0.402**. Its app-local `Microsoft.Windows.SDK.NET.dll` and `WinRT.Runtime.dll` ship with Citrus; these are runtime dependencies. Exact package hashes, assembly versions/hashes and notice hashes are in `packaging/presentation-backend.lock.json` and are checked by `packaging/verify-presentation-publish.py`.

Microsoft's [Windows SDK REDIST list](https://learn.microsoft.com/en-us/legal/windows-sdk/redist), under `Microsoft.Windows.SDK.NET.Ref`, expressly lists these unmodified `lib/net8.0` runtime files for programs that call Windows Runtime APIs. Distribution remains subject to the [Windows SDK license](https://learn.microsoft.com/en-us/legal/windows-sdk/license), including the Windows-only and protective end-user terms. Citrus adds its primary file-tool functionality, preserves the notices, and presents the exact Microsoft SDK component terms for acceptance in the installer. Silent installation requires explicit `/ACCEPTSDKTERMS=1`; omission exits before installation changes. This acceptance applies to the Microsoft components; other components retain their separate licenses and open-source rights.

The exact locally installed Microsoft SDK license, the official REDIST snapshot and the evidence record are bundled under `notices/presentation/` as `Windows-SDK-License.rtf`, `Windows-SDK-REDIST.html` and `Windows-SDK-Distribution-Review.json`. Their hashes and the declared license sources are pinned in `presentation-backend.lock.json`. See `notices/Presentation-Notice.txt` and the separately retained C#/WinRT MIT notice. The SDK projection as a whole is not described as MIT-licensed. This records the official distribution evidence and implementation conditions; it does not constitute legal advice or product acceptance.
