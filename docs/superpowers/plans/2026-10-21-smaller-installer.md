# Smaller Citrus installer

The user requested a real reduction in installation traffic and publication on both GitHub and the website. Preserve every existing offline engine and runtime catalog entry. Keep the earlier releases unchanged.

1. Verify the existing complete staging payload, then distribute its exact third-party sources as `Citrus-ThirdParty-Sources.zip` alongside the installer. Bind source bytes, inventory, version and download URL in installed `source-distribution.json` and `SOURCE-DOWNLOADS.md`. Missing, corrupt, linked or mismatched source inputs must still fail packaging.
2. Remove only the four root PDB debugging files from the runtime. Keep non-solid LZMA: the runtime alone still exceeds NSIS's 2 GiB solid cache limit.
3. Release `0.3.0-preview.3` with a fresh self-contained publish, generated ownership inventory, installer, online installer, corresponding sources and checksums. Preserve normal user installation, component terms, transactional upgrade and rollback.
4. Verify package/source integrity, archive extraction, offline engine smoke tests, updater fixtures, source build/core tests, and the online installer's download verification without installing on the user's account. Publish complete verified draft assets, then activate the release.
5. Update GitHub instructions and website metadata using actual artifact sizes and hashes. Show the separate source download and derive setup-transfer copy from current metadata. Match the pushed website commit to Cloudflare success and verify the live desktop/mobile pages and anonymous downloads.

Clean-machine installation and broad physical Explorer/mixed-DPI acceptance remain separate from these packaging checks. Both installers remain unsigned previews.
