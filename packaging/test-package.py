#!/usr/bin/env python3
"""Real package negative checks; run against the candidate produced by publish."""
import importlib.util
from pathlib import Path
import tempfile
import sys
import shutil
import os
import subprocess
import hashlib
import json
import zipfile
from unittest.mock import patch
import xml.etree.ElementTree as ET

VC_RUNTIME_NAMES = ['concrt140.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll',
                    'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll', 'vccorlib140.dll',
                    'vcruntime140.dll', 'vcruntime140_1.dll', 'vcruntime140_threads.dll']

PAYLOAD = Path(sys.argv.pop(1)).resolve() if len(sys.argv) > 1 else None
import unittest

spec = importlib.util.spec_from_file_location('package', Path(__file__).with_name('package.py'))
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)

class PackageTests(unittest.TestCase):
    def test_distribution_metadata_preserves_citrus_license_without_embedded_marketing_references(self):
        with tempfile.TemporaryDirectory() as temp:
            repo, payload, generated = [Path(temp) / name for name in ['repo', 'payload', 'generated']]
            for directory in [repo / 'packaging/notices', repo / 'docs', payload / 'docs']:
                directory.mkdir(parents=True)
            (repo / 'README.md').write_text('Private design references must not become the installed guide.')
            guide = b'Curated offline distribution guide.'
            license_bytes = (package.REPO / 'LICENSE').read_bytes()
            (repo / 'packaging/Distribution-README.md').write_bytes(guide)
            (repo / 'LICENSE').write_bytes(license_bytes)
            (repo / 'THIRD-PARTY-NOTICES.md').write_text('Separate component notices.')
            (repo / 'packaging/image-backend.lock.json').write_text('{}')
            (repo / 'CitrusVersion.props').write_bytes((package.REPO / 'CitrusVersion.props').read_bytes())
            (repo / 'packaging/notices/presentation').mkdir()
            (repo / 'packaging/notices/presentation/Windows-SDK-License.rtf').write_bytes((package.REPO / 'packaging/notices/presentation/Windows-SDK-License.rtf').read_bytes())
            for name in ['operations.md', 'QUICKSTART.md', 'PREVIEW-NOTES.md', 'AUTOMATIC-UPDATES.md']:
                (repo / 'docs' / name).write_text('Public product documentation.')
            for name in ['windows-acceptance.md', 'verification.md']:
                (repo / 'docs' / name).write_text('Historical evidence must remain in the source repository.')
            with patch.object(package, 'REPO', repo), patch.object(package, 'verify'), patch.object(package, 'separate_sources'):
                package.prepare(payload, generated)
            self.assertEqual(guide, (payload / 'README.md').read_bytes())
            self.assertEqual(license_bytes, (payload / 'LICENSE').read_bytes())
            self.assertIn('LICENSE', json.loads((payload / 'install-inventory.json').read_text()))
            for name in ['QUICKSTART.md', 'PREVIEW-NOTES.md', 'AUTOMATIC-UPDATES.md']:
                self.assertTrue((payload / 'docs' / name).is_file())
            self.assertFalse((payload / 'docs/windows-acceptance.md').exists())
            self.assertFalse((payload / 'docs/verification.md').exists())

    @unittest.skipIf(PAYLOAD is None, 'Pass actual candidate directory for missing-asset checks')
    def test_real_payload_missing_each_asset_class_is_rejected(self):
        # A permanent unrelated verifier failure must never qualify negative cases.
        package.verify(PAYLOAD)
        with tempfile.TemporaryDirectory(dir=PAYLOAD.parent) as temp:
            root = Path(temp) / 'payload'
            shutil.copytree(PAYLOAD, root, copy_function=os.link)
            sources = root
            if not (root / 'source').exists():
                sources = root.parent / 'third-party-sources'
                shutil.copytree(PAYLOAD.parent / 'third-party-sources', sources, copy_function=os.link)
                for name in ['Citrus-ThirdParty-Sources.zip', 'source-distribution.json']:
                    os.link(PAYLOAD.parent / name, root.parent / name)
            package.verify(root)
            image_files = json.loads((PAYLOAD / 'image-backend.lock.json').read_bytes())['offlineFeaturePack']['files']
            image_source = next(entry['path'].replace('source/', 'source/image/', 1)
                                for entry in image_files if entry['path'].startswith('source/'))
            perl_runtime = next(entry['path'].replace('win-x64/', 'image/', 1)
                                for entry in image_files if '/exiftool/' in entry['path'] and entry['path'].endswith('.dll'))
            for name in ['Citrus.Windows.exe', 'LICENSE', 'coreclr.dll', 'Magick.Native-Q8-x64.dll',
                         'pdfium.dll', 'libSkiaSharp.dll', 'SharpCompress.dll', 'media/ffmpeg.exe',
                         'media/ffprobe.exe', 'media/avcodec-63.dll', 'notices/Media-Notice.txt',
                         'notices/dotnet/NET-10.0.13-LICENSE.txt', 'source/citrus-media-source.tar.gz',
                         'image/heic/citrus-heic.exe', 'image/exiftool/exiftool.exe',
                         image_source, perl_runtime,
                         'source/image/heic-helper.cpp', 'source/image/build-image-pack.ps1',
                         'feature-pack.lock.json', 'feature-pack-runtime.sha256.json',
                         'feature-pack/office-worker.py', 'feature-pack/libreoffice/program/mergedlo.dll',
                         'feature-pack/libreoffice/program/python.exe',
                         'feature-pack/pandoc/pandoc-3.12.1/pandoc.exe', 'feature-pack/tessdata/eng.traineddata',
                         'source/feature-pack/libreoffice-26.8.1.1.tar.xz',
                         'source/feature-pack/build-feature-pack.ps1',
                         'x64/tesseract50.dll', 'x64/leptonica-1.82.0.dll',
                         'Tesseract.dll', 'Tabula.dll', 'DocumentFormat.OpenXml.dll',
                         'DocumentFormat.OpenXml.Framework.dll',
                         'notices/Document-Feature-Pack-Notice.txt']:
                with self.subTest(name=name):
                    path = (sources if name.startswith('source/') else root) / name
                    held = path.with_name(path.name + '.held')
                    path.rename(held)
                    try:
                        with self.assertRaises((ValueError, FileNotFoundError, AssertionError, subprocess.CalledProcessError)):
                            package.verify(root)
                    finally: held.rename(path)

            for prefix in ['', 'feature-pack/libreoffice/program/']:
                for name in VC_RUNTIME_NAMES:
                    with self.subTest(crt=prefix + name):
                        path = root / (prefix + name)
                        held = path.with_name(path.name + '.held')
                        path.rename(held)
                        try:
                            with self.assertRaises((ValueError, FileNotFoundError)):
                                package.verify_offline_feature_packs(root, sources)
                        finally: held.rename(path)

    def test_empty_payload_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            with self.assertRaises((ValueError, FileNotFoundError)):
                package.verify(Path(root))

    def test_missing_legacy_native_is_rejected_before_large_feature_scan(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name in ['Citrus.Windows.exe', 'Citrus.Windows.dll', 'Citrus.Core.dll', 'Citrus.Conversion.dll',
                         'Citrus.Windows.deps.json', 'Citrus.Windows.runtimeconfig.json', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll',
                         'System.Private.CoreLib.dll', 'PresentationFramework.dll', 'PresentationNative_cor3.dll',
                         'Magick.NET-Q8-x64.dll', 'Magick.NET.Core.dll', 'THIRD-PARTY-NOTICES.md', 'README.md', 'LICENSE',
                         'docs/operations.md', 'docs/QUICKSTART.md', 'docs/PREVIEW-NOTES.md', 'docs/AUTOMATIC-UPDATES.md',
                         'docs/windows-acceptance.md', 'docs/verification.md']:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b'fixture')
            for name in ['image-backend.lock.json', 'media-lock.json', 'pdf-backend.lock.json',
                         'archive-backend.lock.json', 'presentation-backend.lock.json']:
                shutil.copyfile(package.REPO / 'packaging' / name, root / name)
            with patch.object(package, 'verify_offline_feature_packs') as large_scan:
                with self.assertRaises(FileNotFoundError): package.verify(root)
                large_scan.assert_not_called()

    def test_owned_inventory_preserves_unknown_outputs(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'media').mkdir()
            (root / 'app.exe').write_bytes(b'app')
            (root / 'media' / 'codec.dll').write_bytes(b'codec')
            files = package.inventory(root)
            (root / 'notes.txt').write_text('user output')
            (root / 'media' / 'converted.mp4').write_bytes(b'user output')
            _, delete, _ = package.instructions(files, root)
            self.assertNotIn('notes.txt', delete)
            self.assertNotIn('converted.mp4', delete)
            self.assertNotIn('RMDir /r', delete)
            self.assertNotIn('*', delete)
            # Execute precisely the literal Delete instructions, not the input list.
            owned_deletes = [line[len('Delete "$INSTDIR\\'):-1].replace('\\', '/')
                             for line in delete.splitlines() if line.startswith('Delete "$INSTDIR\\')]
            self.assertEqual(set(files), set(owned_deletes))
            for path in owned_deletes:
                (root / path).unlink()
            for directory in sorted(root.rglob('*'), reverse=True):
                if directory.is_dir():
                    try: directory.rmdir()
                    except OSError: pass
            self.assertEqual('user output', (root / 'notes.txt').read_text())
            self.assertEqual(b'user output', (root / 'media/converted.mp4').read_bytes())

    def test_unsafe_inventory_paths_rejected(self):
        for name in ['../outside', '/absolute', 'C:/outside', 'stream:ads', 'a/../b', 'a\\b', 'a\nFile x', 'wild*card', 'trailing.']:
            with self.subTest(name=name), self.assertRaises(ValueError):
                package.instructions({name: '0' * 64}, Path('/tmp/payload'))

    def test_symlink_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'target').write_text('data')
            try: (root / 'link').symlink_to(root / 'target')
            except OSError: self.skipTest('Host cannot create symbolic links')
            with self.assertRaises(ValueError): package.inventory(root)

    def test_publish_inputs_use_real_builder_and_exclude_generated_office_files(self):
        project = ET.parse(package.REPO / 'src/Citrus.Conversion/Citrus.Conversion.csproj')
        items = {node.attrib['Include']: node for node in project.iter('Content')}
        self.assertIn('../../packaging/image/build-image-pack.ps1', items)
        self.assertNotIn('../../packaging/build-image-pack.ps1', items)
        self.assertIn('../../packaging/notices/Document-Feature-Pack-Notice.txt', items)
        excluded = items['../../packaging/feature-pack/libreoffice/**/*'].attrib.get('Exclude', '')
        for suffix in ['**/__pycache__/**', '**/*.pyc', '**/*.msi']:
            self.assertIn(suffix, excluded)

    def test_publish_places_pinned_vc_runtime_beside_both_executables(self):
        project = ET.parse(package.REPO / 'src/Citrus.Conversion/Citrus.Conversion.csproj')
        links = [node.attrib.get('Link') for node in project.iter('Content')
                 if node.attrib['Include'] == '../../packaging/feature-pack/libreoffice/System64/*.dll']
        self.assertIn('%(Filename)%(Extension)', links)
        self.assertIn('feature-pack/libreoffice/program/%(Filename)%(Extension)', links)


class SourceDistributionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.dist = Path(self.temp.name)
        self.root = self.dist / 'payload'
        self.repo = self.dist / 'repo'
        self.sources = self.dist / 'third-party-sources'
        self.root.mkdir()
        records = {'source/citrus-media-source.tar.gz': b'media source',
                   'source/image/libheif.tar.gz': b'image source',
                   'source/image/heic-helper.cpp': b'bridge source',
                   'source/image/build-image-pack.ps1': b'image builder',
                   'source/feature-pack/office.tar.xz': b'office source',
                   'source/feature-pack/build-feature-pack.ps1': b'office builder'}
        for name, data in records.items():
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        self.records = records
        packaging = self.repo / 'packaging'
        (packaging / 'media').mkdir(parents=True)
        (packaging / 'image').mkdir()
        (packaging / 'media/sha256.json').write_text(json.dumps({'source/citrus-media-source.tar.gz': self.digest(records['source/citrus-media-source.tar.gz'])}))
        image = [{'path': 'source/libheif.tar.gz', **self.record(records['source/image/libheif.tar.gz'])},
                 {'path': 'heic-helper.cpp', **self.record(records['source/image/heic-helper.cpp'])},
                 {'path': 'build-image-pack.ps1', **self.record(records['source/image/build-image-pack.ps1'])}]
        (packaging / 'image-backend.lock.json').write_text(json.dumps({'offlineFeaturePack': {'files': image}}))
        (packaging / 'feature-pack.lock.json').write_text(json.dumps({'sources': [{'file': 'source/office.tar.xz', **self.record(records['source/feature-pack/office.tar.xz'])}]}))
        for source, destination in [('source/image/heic-helper.cpp', 'image/heic-helper.cpp'),
                                    ('source/image/build-image-pack.ps1', 'image/build-image-pack.ps1'),
                                    ('source/feature-pack/build-feature-pack.ps1', 'build-feature-pack.ps1')]:
            (packaging / destination).write_bytes(records[source])
        (self.repo / 'CitrusVersion.props').write_bytes((package.REPO / 'CitrusVersion.props').read_bytes())
        for name, data in [('LICENSE', b'MIT application license'), ('THIRD-PARTY-NOTICES.md', b'Component notices')]:
            (self.repo / name).write_bytes(data)
            (self.root / name).write_bytes(data)
        self.addCleanup(patch.stopall)
        patch.object(package, 'REPO', self.repo).start()

    @staticmethod
    def digest(data): return hashlib.sha256(data).hexdigest()

    def record(self, data): return {'bytes': len(data), 'sha256': self.digest(data)}

    def stage(self):
        self.assertTrue(hasattr(package, 'separate_sources'), 'Source separation is not implemented')
        return package.separate_sources(self.root)

    def test_exact_sources_are_separate_and_debug_files_never_become_owned(self):
        for name in ['Citrus.Windows.pdb', 'Citrus.Core.pdb', 'Citrus.Conversion.pdb', 'libSkiaSharp.pdb']:
            (self.root / name).write_bytes(b'debug information')
        (self.root / 'runtime.dll').write_bytes(b'unchanged engine')
        metadata = self.stage()
        self.assertFalse((self.root / 'source').exists())
        owned = package.inventory(self.root)
        self.assertFalse(any(name.startswith('source/') or name.endswith('.pdb') for name in owned))
        self.assertEqual(b'unchanged engine', (self.root / 'runtime.dll').read_bytes())
        self.assertEqual(metadata, json.loads((self.root / 'source-distribution.json').read_text()))
        self.assertEqual(metadata, json.loads((self.dist / 'source-distribution.json').read_text()))
        with zipfile.ZipFile(self.dist / 'Citrus-ThirdParty-Sources.zip') as archive:
            self.assertEqual(set(self.records) | {'LICENSE', 'THIRD-PARTY-NOTICES.md'}, set(archive.namelist()))
            for name, data in self.records.items(): self.assertEqual(data, archive.read(name))
        self.assertIn(metadata['archive']['url'], (self.root / 'SOURCE-DOWNLOADS.md').read_text())
        package.verify_source_distribution(self.root, self.sources)
        self.assertEqual(metadata, self.stage())

    def test_missing_corrupt_extra_source_and_archive_are_rejected(self):
        self.stage()
        source = self.sources / next(iter(self.records))
        original = source.read_bytes()
        source.unlink()
        with self.assertRaises((ValueError, FileNotFoundError)): package.verify_source_distribution(self.root, self.sources)
        source.write_bytes(b'wrong source')
        with self.assertRaises(ValueError): package.verify_source_distribution(self.root, self.sources)
        source.write_bytes(original)
        extra = self.sources / 'source/unknown.tar.gz'
        extra.write_bytes(b'not pinned')
        with self.assertRaises(ValueError): package.verify_source_distribution(self.root, self.sources)
        extra.unlink()
        archive = self.dist / 'Citrus-ThirdParty-Sources.zip'
        archive.write_bytes(b'not the source archive')
        with self.assertRaises(ValueError): package.verify_source_distribution(self.root, self.sources)

    def test_candidate_cannot_repin_source_metadata_or_use_other_release(self):
        self.stage()
        path = self.root / 'source-distribution.json'
        metadata = json.loads(path.read_text())
        for field, value in [('url', 'https://example.com/sources.zip'), ('sha256', 'a' * 64), ('bytes', True)]:
            altered = json.loads(json.dumps(metadata))
            altered['archive'][field] = value
            path.write_text(json.dumps(altered))
            with self.subTest(field=field), self.assertRaises(ValueError): package.verify_source_distribution(self.root, self.sources)
        altered = json.loads(json.dumps(metadata))
        altered['version'] = '0.0.0-preview.1'
        path.write_text(json.dumps(altered))
        with self.assertRaises(ValueError): package.verify_source_distribution(self.root, self.sources)
        path.write_text(json.dumps(metadata))
        source = self.sources / next(iter(self.records))
        source.write_bytes(b'repinned malicious source')
        metadata['files'][next(iter(self.records))] = self.record(source.read_bytes())
        path.write_text(json.dumps(metadata))
        with self.assertRaises(ValueError): package.verify_source_distribution(self.root, self.sources)

    def test_linked_source_root_is_rejected(self):
        self.stage()
        linked = self.dist / 'linked-source'
        try: linked.symlink_to(self.sources, target_is_directory=True)
        except OSError: self.skipTest('Host cannot create symbolic links')
        with self.assertRaises(ValueError): package.verify_source_distribution(self.root, linked)

    def test_mixed_inline_sources_and_debug_symbols_are_rejected(self):
        self.stage()
        (self.root / 'libSkiaSharp.pdb').write_bytes(b'not a runtime engine')
        with self.assertRaises(ValueError): package.verify_source_distribution(self.root, self.sources)
        (self.root / 'libSkiaSharp.pdb').unlink()
        (self.root / 'source').mkdir()
        with self.assertRaisesRegex(ValueError, 'Mixed inline'): package.verify(self.root)


class OfflineFeaturePackTests(unittest.TestCase):
    """Tiny actual files prove mappings and rejection without native engines or downloads."""
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name) / 'repo'
        self.root = Path(self.temp.name) / 'payload'
        self.root.mkdir()
        for name in ['Tesseract.dll', 'Tabula.dll', 'DocumentFormat.OpenXml.dll', 'DocumentFormat.OpenXml.Framework.dll']:
            self.put(self.root, name, b'managed dependency')
        self.packaging = self.repo / 'packaging'
        self.packaging.mkdir(parents=True)
        self.ocr = {'x64/tesseract50.dll': self.digest(b'ocr'),
                    'x64/leptonica-1.82.0.dll': self.digest(b'lept')}
        for name, data in [('x64/tesseract50.dll', b'ocr'), ('x64/leptonica-1.82.0.dll', b'lept')]:
            self.put(self.root, name, data)
        image_files = [self.record('win-x64/heic/citrus-heic.exe', b'heic'),
                       self.record('win-x64/exiftool/perl/lib.dll', b'perl'),
                       self.record('source/libheif.tar.gz', b'heic source'),
                       self.record('heic-helper.cpp', b'image/heic-helper.cpp'),
                       self.record('build-image-pack.ps1', b'image/build-image-pack.ps1')]
        for item, data in zip(image_files, [b'heic', b'perl', b'heic source', b'image/heic-helper.cpp', b'image/build-image-pack.ps1']):
            dest = item['path'].replace('win-x64/', 'image/', 1).replace('source/', 'source/image/', 1)
            if '/' not in dest: dest = 'source/image/' + dest
            self.put(self.root, dest, data)
        self.lock = {'schema': 1, 'sources': [dict(file='source/office.tar.xz', **self.record_fields(b'source'))],
                     'workers': ['office-worker.py'],
                     'trustedWorkers': [{'file': 'office-worker.py', 'sha256': self.digest(b'worker')}]}
        self.catalog = {'schema': 1, 'files': [self.record('libreoffice/program/mergedlo.dll', b'office'),
                        self.record('pandoc/pandoc-3.12.1/pandoc.exe', b'pandoc'),
                        self.record('tessdata/eng.traineddata', b'model'),
                        self.record('office-worker.py', b'worker')]}
        for name in VC_RUNTIME_NAMES:
            data = ('pinned VC runtime ' + name).encode()
            self.catalog['files'].append(self.record('libreoffice/System64/' + name, data))
            for prefix in ['', 'feature-pack/libreoffice/System64/', 'feature-pack/libreoffice/program/']:
                self.put(self.root, prefix + name, data)
        for name, data in [('feature-pack/libreoffice/program/mergedlo.dll', b'office'),
                           ('feature-pack/pandoc/pandoc-3.12.1/pandoc.exe', b'pandoc'),
                           ('feature-pack/tessdata/eng.traineddata', b'model'),
                           ('feature-pack/office-worker.py', b'worker'),
                           ('source/feature-pack/office.tar.xz', b'source')]:
            self.put(self.root, name, data)
        for name in ['libreoffice/program/python.exe', 'libreoffice/program/soffice.com']:
            self.catalog['files'].append(self.record(name, b'engine entry'))
            self.put(self.root, 'feature-pack/' + name, b'engine entry')
        image_files.append(self.record('win-x64/exiftool/exiftool.exe', b'exiftool'))
        self.put(self.root, 'image/exiftool/exiftool.exe', b'exiftool')
        self.image = {'offlineFeaturePack': {'schemaVersion': 1, 'files': image_files}}
        self.write_manifests()
        for source, dest in [('image/heic-helper.cpp', 'source/image/heic-helper.cpp'),
                             ('image/build-image-pack.ps1', 'source/image/build-image-pack.ps1'),
                             ('build-feature-pack.ps1', 'source/feature-pack/build-feature-pack.ps1')]:
            self.put(self.packaging, source, source.encode())
            self.put(self.root, dest, source.encode())
        self.addCleanup(patch.stopall)
        patch.object(package, 'REPO', self.repo).start()
        patch.object(package, 'OCR_NATIVE_HASHES', self.ocr, create=True).start()

    @staticmethod
    def digest(data): return hashlib.sha256(data).hexdigest()

    def record_fields(self, data): return {'sha256': self.digest(data), 'bytes': len(data)}

    def record(self, name, data): return dict(path=name, **self.record_fields(data))

    @staticmethod
    def put(root, name, data):
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def write_manifests(self):
        for name, document in [('image-backend.lock.json', self.image), ('feature-pack.lock.json', self.lock),
                               ('feature-pack-runtime.sha256.json', self.catalog)]:
            data = json.dumps(document).encode()
            self.put(self.packaging, name, data)
            self.put(self.root, name, data)

    def verify(self): package.verify_offline_feature_packs(self.root)

    def test_complete_pack_uses_published_mappings(self): self.verify()

    def test_external_feature_sources_remain_bound_to_repository_pins(self):
        external = self.root.parent / 'sources'
        external.mkdir()
        shutil.move(self.root / 'source', external / 'source')
        package.verify_offline_feature_packs(self.root, external)
        (external / 'source/image/libheif.tar.gz').write_bytes(b'changed source')
        with self.assertRaises(ValueError): package.verify_offline_feature_packs(self.root, external)

    def test_missing_engine_essential_fails_before_any_large_hashing(self):
        self.verify()
        for name in ['image/heic/citrus-heic.exe', 'image/exiftool/exiftool.exe', 'feature-pack/office-worker.py',
                     'feature-pack/libreoffice/program/python.exe', 'feature-pack/libreoffice/program/mergedlo.dll',
                     'feature-pack/libreoffice/program/soffice.com', 'feature-pack/pandoc/pandoc-3.12.1/pandoc.exe',
                     'feature-pack/tessdata/eng.traineddata', 'x64/tesseract50.dll', 'x64/leptonica-1.82.0.dll']:
            with self.subTest(name=name):
                path = self.root / name
                data = path.read_bytes()
                path.unlink()
                try:
                    with patch.object(package, 'sha', wraps=package.sha) as hashes:
                        with self.assertRaises((ValueError, FileNotFoundError)): self.verify()
                        hashes.assert_not_called()
                finally: path.write_bytes(data)

    def test_missing_managed_parser_or_writer_dependency_is_rejected(self):
        for name in ['Tesseract.dll', 'Tabula.dll', 'DocumentFormat.OpenXml.dll', 'DocumentFormat.OpenXml.Framework.dll']:
            with self.subTest(name=name):
                path = self.root / name
                data = path.read_bytes()
                path.unlink()
                try:
                    with self.assertRaises((ValueError, FileNotFoundError)): self.verify()
                finally: path.write_bytes(data)

    def test_all_app_local_crt_aliases_are_required_and_hash_pinned(self):
        self.verify()
        for prefix in ['', 'feature-pack/libreoffice/program/']:
            for name in VC_RUNTIME_NAMES:
                with self.subTest(alias=prefix + name):
                    path = self.root / (prefix + name)
                    data = path.read_bytes()
                    path.unlink()
                    try:
                        with self.assertRaises((ValueError, FileNotFoundError)): self.verify()
                        path.write_bytes(b'unpinned system runtime')
                        with self.assertRaises(ValueError): self.verify()
                    finally: path.write_bytes(data)

    def test_pinned_empty_upstream_files_are_valid_but_changed_empty_files_are_rejected(self):
        self.catalog['files'].append(self.record('libreoffice/program/python-core/email/__init__.py', b''))
        self.image['offlineFeaturePack']['files'].append(self.record('win-x64/exiftool/expected-empty.out', b''))
        self.put(self.root, 'feature-pack/libreoffice/program/python-core/email/__init__.py', b'')
        self.put(self.root, 'image/exiftool/expected-empty.out', b'')
        self.write_manifests()
        self.verify()
        (self.root / 'image/heic/citrus-heic.exe').write_bytes(b'')
        with self.assertRaises(ValueError): self.verify()

    def test_missing_or_corrupt_asset_in_every_new_class_is_rejected(self):
        for name in ['image/heic/citrus-heic.exe', 'image/exiftool/perl/lib.dll',
                     'source/image/libheif.tar.gz', 'feature-pack/libreoffice/program/mergedlo.dll',
                     'feature-pack/pandoc/pandoc-3.12.1/pandoc.exe', 'feature-pack/tessdata/eng.traineddata',
                     'feature-pack/office-worker.py', 'source/feature-pack/office.tar.xz',
                     'source/image/heic-helper.cpp', 'source/image/build-image-pack.ps1',
                     'source/feature-pack/build-feature-pack.ps1', *self.ocr]:
            with self.subTest(name=name):
                path = self.root / name
                original = path.read_bytes()
                path.unlink()
                try:
                    with self.assertRaises((ValueError, FileNotFoundError)): self.verify()
                    path.write_bytes(b'corrupt')
                    with self.assertRaises(ValueError): self.verify()
                finally: path.write_bytes(original)

    def test_altered_lock_or_catalog_is_rejected_even_if_new_hash_matches(self):
        for name in ['image-backend.lock.json', 'feature-pack.lock.json', 'feature-pack-runtime.sha256.json']:
            with self.subTest(name=name):
                path = self.root / name
                old = path.read_bytes()
                path.write_bytes(old + b' ')
                try:
                    with self.assertRaises(ValueError): self.verify()
                finally: path.write_bytes(old)

    def test_trusted_worker_must_match_both_catalog_and_lock(self):
        self.lock['trustedWorkers'][0]['sha256'] = self.digest(b'other')
        self.write_manifests()
        with self.assertRaises(ValueError): self.verify()

    def test_manifest_paths_duplicates_and_size_are_validated(self):
        for change in ['../escape', 'win-x64/../../escape', 'unknown/place', 'duplicate', 'size']:
            with self.subTest(change=change):
                original = json.loads(json.dumps(self.image))
                entry = self.image['offlineFeaturePack']['files'][0]
                if change == 'duplicate': self.image['offlineFeaturePack']['files'].append(dict(entry))
                elif change == 'size': entry['bytes'] += 1
                else: entry['path'] = change
                self.write_manifests()
                try:
                    with self.assertRaises(ValueError): self.verify()
                finally: self.image = original
        self.write_manifests()

    def test_uninventoried_runtime_and_generated_caches_are_rejected(self):
        for name in ['feature-pack/libreoffice/program/__pycache__/x.pyc',
                     'feature-pack/libreoffice/installer.msi', 'image/heic/unpinned.dll']:
            with self.subTest(name=name):
                self.put(self.root, name, b'unpinned')
                try:
                    with self.assertRaises(ValueError): self.verify()
                finally: (self.root / name).unlink()

class InstalledVerificationTests(unittest.TestCase):
    def test_verification_reports_progress_and_supports_uninstall_prefix(self):
        files = {f'item-{index}.bin': 'a' * 64 for index in range(201)}
        checks = package.verification_instructions(files)
        self.assertEqual(checks.count('Call ${PREFIX}HashFile'), 201)
        self.assertEqual(checks.count('Call ${PREFIX}Fail'), 201)
        for count in [0, 100, 200, 201]:
            self.assertIn(f'Verifying installed files ({count}/201)', checks)
        self.assertIn('Installed files verified.', checks)

if __name__ == '__main__': unittest.main()
