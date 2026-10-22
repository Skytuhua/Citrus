#!/usr/bin/env python3
"""Pinned payload verification, literal NSIS ownership instructions, release hashes."""
import argparse
import hashlib
import json
import os
import re
import stat
from pathlib import Path, PurePosixPath
import shutil
import tarfile
import zipfile
import xml.etree.ElementTree as ET

REPO = Path(__file__).resolve().parent.parent

# Native files from the integrity-locked Tesseract 5.2.0 NuGet package.
OCR_NATIVE_HASHES = {
    'x64/tesseract50.dll': 'de4d04ec75095374d98f5dd7a60d14d7e2e0f76589db693eccf7ae658be8cb2b',
    'x64/leptonica-1.82.0.dll': 'dfcb3e6ed0b16bc55bfdbcf53543cfe42a354b87c3e35bd3a95eebf005d73e76',
}
VC_RUNTIME_NAMES = ('concrt140.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll',
                    'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll', 'vccorlib140.dll',
                    'vcruntime140.dll', 'vcruntime140_1.dll', 'vcruntime140_threads.dll')

def sha(path):
    with path.open('rb') as source:
        digest = hashlib.sha256()
        for block in iter(lambda: source.read(1024 * 1024), b''): digest.update(block)
        return digest.hexdigest()

def safe(name):
    parts = name.split('/')
    if not name or any(p in ('', '.', '..') or p.endswith((' ', '.')) for p in parts) or any(c in name for c in '\\:"<>|?*\r\n\x00'):
        raise ValueError('Unsafe inventory path: ' + repr(name))
    return name

def checked_path(root, name):
    """Reject Windows reparse points before following a manifest-owned path."""
    safe(name)
    path = root
    for part in [None] + name.split('/'):
        if part is not None: path = path / part
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError('Linked package asset: ' + str(path))
    return path

def checked_files(root, name):
    directory = checked_path(root, name)
    pending, result = [directory], set()
    while pending:
        for path in pending.pop().iterdir():
            relative = path.relative_to(root).as_posix()
            checked_path(root, relative)
            if path.is_dir(): pending.append(path)
            elif path.is_file(): result.add(relative)
            else: raise ValueError('Unsupported package asset: ' + relative)
    return result

def verify_record(root, name, record):
    path = checked_path(root, name)
    digest = record['sha256']
    if not isinstance(digest, str) or not re.fullmatch('[0-9a-f]{64}', digest):
        raise ValueError('Invalid SHA-256: ' + name)
    size = record.get('bytes')
    if size is not None and (type(size) is not int or size < 0 or path.stat().st_size != size):
        raise ValueError('Package asset size mismatch: ' + name)
    if not path.is_file() or sha(path) != digest:
        raise ValueError('Package asset hash mismatch: ' + name)

def verify_offline_feature_packs(root, source_root=None):
    """Verify published mappings against repository-pinned provenance, never candidate-provided trust."""
    source_root = source_root or root
    for name in ['Tesseract.dll', 'Tabula.dll', 'DocumentFormat.OpenXml.dll', 'DocumentFormat.OpenXml.Framework.dll',
                 'image/heic/citrus-heic.exe', 'image/exiftool/exiftool.exe', 'feature-pack/office-worker.py',
                 'feature-pack/libreoffice/program/python.exe', 'feature-pack/libreoffice/program/mergedlo.dll',
                 'feature-pack/libreoffice/program/soffice.com', 'feature-pack/pandoc/pandoc-3.12.1/pandoc.exe',
                 'feature-pack/tessdata/eng.traineddata', 'x64/tesseract50.dll', 'x64/leptonica-1.82.0.dll']:
        path = checked_path(root, name)
        if not path.is_file() or not path.stat().st_size:
            raise ValueError('Missing/empty feature engine essential: ' + name)
    manifests = {}
    for name in ['image-backend.lock.json', 'feature-pack.lock.json', 'feature-pack-runtime.sha256.json']:
        published = checked_path(root, name).read_bytes()
        if published != (REPO / 'packaging' / name).read_bytes():
            raise ValueError('Changed feature-pack provenance: ' + name)
        manifests[name] = json.loads(published)
    image = manifests['image-backend.lock.json']['offlineFeaturePack']
    lock = manifests['feature-pack.lock.json']
    catalog = manifests['feature-pack-runtime.sha256.json']
    if image['schemaVersion'] != 1 or lock['schema'] != 1 or catalog['schema'] != 1:
        raise ValueError('Unsupported feature-pack schema')
    # These aliases use only the pinned upstream x64 redist, never System32.
    # Check them before hashing the large Office pack so a missing CRT fails fast.
    vc_sources = {entry['path'].casefold(): entry for entry in catalog['files']}
    aliases = set()
    for name in VC_RUNTIME_NAMES:
        source = vc_sources.get('libreoffice/system64/' + name)
        if source is None: raise ValueError('Missing pinned x64 CRT source: ' + name)
        for prefix in ['', 'feature-pack/libreoffice/program/']:
            destination = prefix + name
            verify_record(root, destination, source)
            if prefix: aliases.add(destination)
    expected_image, expected_runtime, seen = set(), aliases, set()
    if not image['files'] or not catalog['files'] or not lock['sources'] or not lock['trustedWorkers']:
        raise ValueError('Incomplete feature-pack inventory')
    for entry in image['files']:
        name = safe(entry['path'])
        if name.startswith('win-x64/'): destination = 'image/' + name[len('win-x64/'):]
        elif name.startswith('source/'): destination = 'source/image/' + name[len('source/'):]
        elif name in ('heic-helper.cpp', 'build-image-pack.ps1'): destination = 'source/image/' + name
        else: raise ValueError('Unrecognized image-pack mapping: ' + name)
        if destination.casefold() in seen: raise ValueError('Duplicate image-pack asset: ' + name)
        seen.add(destination.casefold())
        verify_record(source_root if destination.startswith('source/') else root, destination, entry)
        if destination.startswith('image/'): expected_image.add(destination)
    runtime = {}
    for entry in catalog['files']:
        name = safe(entry['path'])
        if name.casefold() in runtime: raise ValueError('Duplicate feature-pack asset: ' + name)
        runtime[name.casefold()] = entry
        destination = 'feature-pack/' + name
        verify_record(root, destination, entry)
        expected_runtime.add(destination)
    source_names = set()
    for entry in lock['sources']:
        name = safe(entry['file'])
        if not name.startswith('source/') or name.casefold() in source_names:
            raise ValueError('Invalid feature-pack source mapping: ' + name)
        source_names.add(name.casefold())
        verify_record(source_root, 'source/feature-pack/' + name[len('source/'):], entry)
    workers = set()
    for entry in lock['trustedWorkers']:
        name = safe(entry['file'])
        if name.casefold() in workers or runtime.get(name.casefold(), {}).get('sha256') != entry['sha256']:
            raise ValueError('Trusted worker/catalog mismatch: ' + name)
        workers.add(name.casefold())
        verify_record(root, 'feature-pack/' + name, entry)
    if len(lock['workers']) != len(workers) or {safe(n).casefold() for n in lock['workers']} != workers:
        raise ValueError('Missing or duplicate trusted worker provenance')
    for source, destination in [('image/heic-helper.cpp', 'source/image/heic-helper.cpp'),
                                ('image/build-image-pack.ps1', 'source/image/build-image-pack.ps1'),
                                ('build-feature-pack.ps1', 'source/feature-pack/build-feature-pack.ps1')]:
        if checked_path(source_root, destination).read_bytes() != (REPO / 'packaging' / source).read_bytes():
            raise ValueError('Stale corresponding feature source: ' + destination)
    for name, digest in OCR_NATIVE_HASHES.items(): verify_record(root, name, {'sha256': digest})
    if checked_files(root, 'image') != expected_image:
        raise ValueError('Uninventoried or missing image runtime files')
    if checked_files(root, 'feature-pack') != expected_runtime:
        raise ValueError('Uninventoried or missing document runtime files')

def inventory(root):
    if root.is_symlink(): raise ValueError("Linked payload root")
    result = {}
    for path in sorted(root.rglob('*')):
        if path.is_symlink(): raise ValueError('Links are forbidden in payload: ' + str(path))
        if path.is_file(): result[safe(path.relative_to(root).as_posix())] = sha(path)
    return result


def source_records(source_root):
    """Source trust comes from repository locks and build scripts, not a candidate manifest."""
    records = {}
    def add(name, record):
        safe(name)
        if not name.startswith('source/') or any(old.casefold() == name.casefold() for old in records):
            raise ValueError('Invalid or duplicate corresponding source: ' + name)
        verify_record(source_root, name, record)
        records[name] = {'bytes': (source_root / name).stat().st_size, 'sha256': record['sha256']}
    for name, digest in json.loads((REPO / 'packaging/media/sha256.json').read_bytes()).items():
        if name.startswith('source/'): add(name, {'sha256': digest})
    image = json.loads((REPO / 'packaging/image-backend.lock.json').read_bytes())['offlineFeaturePack']['files']
    for entry in image:
        name = entry['path']
        if name.startswith('source/'): add('source/image/' + name[len('source/'):], entry)
        elif name in ('heic-helper.cpp', 'build-image-pack.ps1'): add('source/image/' + name, entry)
    for entry in json.loads((REPO / 'packaging/feature-pack.lock.json').read_bytes())['sources']:
        name = safe(entry['file'])
        if not name.startswith('source/'): raise ValueError('Invalid feature-pack source mapping')
        add('source/feature-pack/' + name[len('source/'):], entry)
    add('source/feature-pack/build-feature-pack.ps1', {'sha256': sha(REPO / 'packaging/build-feature-pack.ps1')})
    for source in ['heic-helper.cpp', 'build-image-pack.ps1']:
        if records['source/image/' + source]['sha256'] != sha(REPO / 'packaging/image' / source):
            raise ValueError('Stale corresponding image build source: ' + source)
    if checked_files(source_root, 'source') != set(records):
        raise ValueError('Uninventoried or missing corresponding sources')
    return records


def source_notice(metadata):
    archive = metadata['archive']
    return ('# Corresponding third-party sources\n\n'
            'All conversion engines and their license texts remain installed locally.\n'
            'The complete corresponding source archives and exact build scripts are available, without an account or fee, beside this version of the installer:\n\n'
            + archive['url'] + '\n\nSHA-256: ' + archive['sha256'] + '\n\n'
            'Extract Citrus-ThirdParty-Sources.zip to obtain its source/ directory.\n'
            'Paths beginning source/ in archived build documentation refer to that extracted directory, rather than the installed application.\n'
            'The replaceable LGPL libraries remain in image/heic; their source and bridge build script are in source/image. You may modify and relink them under their included licenses.\n')


def source_bundle_records(source_root):
    records = source_records(source_root)
    for name in ['LICENSE', 'THIRD-PARTY-NOTICES.md']:
        records[name] = {'bytes': (REPO / name).stat().st_size, 'sha256': sha(REPO / name)}
    return records


def verify_source_archive(path, records):
    with zipfile.ZipFile(checked_path(path.parent, path.name)) as archive:
        entries = archive.infolist()
        if len(entries) != len(records) or {entry.filename for entry in entries} != set(records):
            raise ValueError('Source archive inventory differs from corresponding sources')
        for entry in entries:
            safe(entry.filename)
            expected = records[entry.filename]
            if stat.S_ISLNK(entry.external_attr >> 16) or entry.file_size != expected['bytes']:
                raise ValueError('Invalid corresponding-source archive member: ' + entry.filename)
            digest = hashlib.sha256()
            with archive.open(entry) as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b''): digest.update(block)
            if digest.hexdigest() != expected['sha256']:
                raise ValueError('Changed corresponding-source archive member: ' + entry.filename)


def verify_source_distribution(root, source_root=None):
    source_root = source_root or root.parent / 'third-party-sources'
    if (root / 'source').exists(): raise ValueError('Separated runtime still contains source archives')
    if any((root / name).exists() for name in ['Citrus.Windows.pdb', 'Citrus.Core.pdb', 'Citrus.Conversion.pdb', 'libSkiaSharp.pdb']):
        raise ValueError('Separated runtime still contains debug symbols')
    records = source_bundle_records(source_root)
    metadata = json.loads(checked_path(root, 'source-distribution.json').read_bytes())
    version = ET.parse(REPO / 'CitrusVersion.props').getroot().findtext('./PropertyGroup/Version')
    archive = root.parent / 'Citrus-ThirdParty-Sources.zip'
    expected_archive = {'file': archive.name, 'bytes': checked_path(archive.parent, archive.name).stat().st_size,
                        'sha256': sha(archive),
                        'url': f'https://github.com/Skytuhua/Citrus/releases/download/v{version}/{archive.name}'}
    expected = {'schema': 1, 'version': version, 'archive': expected_archive, 'files': records}
    if metadata != expected or type(metadata['schema']) is not int or type(metadata['archive']['bytes']) is not int:
        raise ValueError('Invalid corresponding-source distribution metadata')
    for record in metadata['files'].values():
        if type(record['bytes']) is not int: raise ValueError('Invalid corresponding-source size')
    if checked_path(root, 'SOURCE-DOWNLOADS.md').read_text(encoding='utf-8') != source_notice(metadata):
        raise ValueError('Missing or changed corresponding-source download instructions')
    verify_source_archive(archive, records)
    return metadata


def separate_sources(root):
    source_root = root.parent / 'third-party-sources'
    if not (root / 'source').exists(): return verify_source_distribution(root, source_root)
    records = source_bundle_records(root)
    if source_root.exists():
        if source_bundle_records(source_root) != records: raise ValueError('Different source staging already exists')
    else:
        source_root.mkdir()
        shutil.copytree(root / 'source', source_root / 'source')
    archive = root.parent / 'Citrus-ThirdParty-Sources.zip'
    if not archive.exists():
        with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_STORED) as bundle:
            for name in sorted(records):
                bundle.write((source_root if name.startswith('source/') else REPO) / name, name)
    verify_source_archive(archive, records)
    version = ET.parse(REPO / 'CitrusVersion.props').getroot().findtext('./PropertyGroup/Version')
    metadata = {'schema': 1, 'version': version,
                'archive': {'file': archive.name, 'bytes': archive.stat().st_size, 'sha256': sha(archive),
                            'url': f'https://github.com/Skytuhua/Citrus/releases/download/v{version}/{archive.name}'},
                'files': records}
    encoded = json.dumps(metadata, indent=2) + '\n'
    (root / 'source-distribution.json').write_text(encoded, encoding='utf-8')
    (root.parent / 'source-distribution.json').write_text(encoded, encoding='utf-8')
    (root / 'SOURCE-DOWNLOADS.md').write_text(source_notice(metadata), encoding='utf-8')
    shutil.rmtree(checked_path(root, 'source'))
    for name in ['Citrus.Windows.pdb', 'Citrus.Core.pdb', 'Citrus.Conversion.pdb', 'libSkiaSharp.pdb']:
        if (root / name).exists(): checked_path(root, name).unlink()
    verify_source_distribution(root, source_root)
    return metadata

def quote(value):
    return '"' + str(value).replace('$', '$$').replace('"', '$\\"') + '"'

def instructions(files, root):
    install, delete, checks = [], [], []
    dirs = set()
    for index, name in enumerate(sorted(files)):
        safe(name)
        relative = name.replace('/', '\\')
        parent = PurePosixPath(name).parent
        while str(parent) != '.':
            dirs.add(str(parent)); parent = parent.parent
        folder = str(PurePosixPath(name).parent).replace('/', '\\')
        install += ['SetOutPath "$INSTDIR' + ('' if folder == '.' else '\\' + folder.replace('$','$$')) + '"', 'ClearErrors', 'File ' + quote(root / name), 'IfErrors 0 +2', 'Call Fail']
        if index == 0:
            # Only a separately compiled fault-test installer enables this boundary.
            install += ['!ifdef CITRUS_TEST_FAIL_AFTER_FIRST_FILE', 'SetErrorLevel 25', 'Quit', '!endif', '!ifdef CITRUS_TEST_ROLLBACK_AFTER_FIRST_FILE', 'Call Fail', '!endif']
        delete += ['ClearErrors', 'Delete "$INSTDIR\\' + relative.replace('$','$$') + '"', 'IfErrors 0 +2', 'StrCpy $DeleteFailed 1']
    # Check root, all directory ancestors and owned file paths for reparse points.
    for name in sorted(dirs | set(files), key=lambda n: (n.count('/'), n)):
        checks += ['Push "$INSTDIR\\' + name.replace('/', '\\').replace('$','$$') + '"', 'Call ${PREFIX}CheckPath']
    for name in sorted(dirs, key=lambda n: (n.count('/'), n), reverse=True):
        delete += ['RMDir "$INSTDIR\\' + name.replace('/', '\\').replace('$','$$') + '"']
    return '\n'.join(install)+'\n', '\n'.join(delete)+'\n', '\n'.join(checks)+'\n'

def version_include():
    properties = ET.parse(REPO / 'CitrusVersion.props').getroot()
    version = properties.findtext('./PropertyGroup/Version', '')
    file_version = properties.findtext('./PropertyGroup/FileVersion', '')
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?', version):
        raise ValueError('Invalid central release version')
    if not re.fullmatch(r'[0-9]+(?:\.[0-9]+){3}', file_version) or any(int(part) > 65535 for part in file_version.split('.')):
        raise ValueError('Invalid central Windows file version')
    sdk = sha(REPO / 'packaging/notices/presentation/Windows-SDK-License.rtf')
    return ('!define CITRUS_VERSION "' + version + '"\n'
            '!define CITRUS_FILE_VERSION "' + file_version + '"\n'
            '!define SDK_LICENSE_SHA256 "' + sdk + '"\n')

def verification_instructions(files):
    total = len(files)
    checks = [f'DetailPrint "Verifying installed files (0/{total})..."']
    for index, (name, digest) in enumerate(sorted(files.items()), 1):
        safe(name)
        if not re.fullmatch('[0-9a-f]{64}', digest):
            raise ValueError('Invalid installed-byte verification hash')
        literal = name.replace('/', '\\').replace('$', '$$')
        checks += ['Push "$INSTDIR\\' + literal + '"', 'Call ${PREFIX}HashFile', 'Pop $0',
                   'StrCmp $0 "' + digest + '" +2', 'Call ${PREFIX}Fail']
        if index % 100 == 0 or index == total:
            checks += [f'DetailPrint "Verifying installed files ({index}/{total})..."']
    checks += ['DetailPrint "Installed files verified."']
    return '\n'.join(checks) + '\n'

def verify(root, source_root=None):
    if (root / 'source').exists() and (root / 'source-distribution.json').exists():
        raise ValueError('Mixed inline and separate corresponding-source distributions')
    source_root = source_root or (root if (root / 'source').exists() else root.parent / 'third-party-sources')
    required = ['Citrus.Windows.exe','Citrus.Windows.dll','Citrus.Core.dll','Citrus.Conversion.dll',
        'Citrus.Windows.deps.json','Citrus.Windows.runtimeconfig.json','hostfxr.dll','hostpolicy.dll','coreclr.dll',
        'System.Private.CoreLib.dll','PresentationFramework.dll','PresentationNative_cor3.dll',
        'Magick.NET-Q8-x64.dll','Magick.NET.Core.dll','THIRD-PARTY-NOTICES.md','README.md','LICENSE',
        'docs/operations.md','docs/QUICKSTART.md','docs/PREVIEW-NOTES.md','docs/AUTOMATIC-UPDATES.md']
    for name in required:
        if not (root / name).is_file() or not (root / name).stat().st_size: raise ValueError('Missing/empty package asset: ' + name)
    for name in ['image-backend.lock.json','media-lock.json','pdf-backend.lock.json','archive-backend.lock.json','presentation-backend.lock.json']:
        if (root / name).read_bytes() != (REPO / 'packaging' / name).read_bytes(): raise ValueError('Changed provenance: ' + name)
    image = json.loads((root / 'image-backend.lock.json').read_bytes())['windowsNative']
    if sha(root / Path(image['path']).name) != image['sha256']: raise ValueError('Image native hash mismatch')
    media = json.loads((REPO / 'packaging/media/sha256.json').read_bytes())
    if json.loads((root / 'media-sha256.json').read_bytes()) != media: raise ValueError('Media manifest mismatch')
    for name, digest in media.items():
        destination = name.replace('win-x64/', 'media/', 1)
        location = source_root if destination.startswith('source/') else root
        if sha(checked_path(location, destination)) != digest: raise ValueError('Media/source hash mismatch: ' + name)
    for source in (REPO / 'packaging/notices').rglob('*'):
        if source.is_file() and sha(root / 'notices' / source.relative_to(REPO / 'packaging/notices')) != sha(source):
            raise ValueError('Missing/changed license: ' + source.name)
    with tarfile.open(checked_path(source_root, 'source/citrus-media-source.tar.gz'), 'r:gz') as archive:
        for source, member in [('packaging/build-media.sh','provenance/build-media.sh'),('packaging/media-lock.json','provenance/media-lock.json'),('packaging/verify-media.py','provenance/verify-media.py'),('docs/media-engine.md','provenance/media-engine.md')]:
            if archive.extractfile(member).read() != (REPO / source).read_bytes(): raise ValueError('Stale corresponding source: ' + member)
        for source in (REPO / 'packaging/notices/media-licenses').iterdir():
            if archive.extractfile('provenance/media-licenses/' + source.name).read() != source.read_bytes(): raise ValueError('Stale source license: ' + source.name)
    for script in ['verify-pdf-publish.py','verify-archive-publish.py','verify-presentation-publish.py']:
        import subprocess, sys
        subprocess.run([sys.executable, str(REPO / 'packaging' / script), str(root)], check=True)
    verify_offline_feature_packs(root, source_root)
    if source_root == root: source_records(root)
    else: verify_source_distribution(root, source_root)
    inventory(root)
    print('PASS complete application, self-contained runtime, image/Office/EPUB/OCR/PDF/archive/media engines, sources and licenses')

def prepare(root, generated):
    shutil.copyfile(REPO / 'packaging/Distribution-README.md', root / 'README.md')
    for name in ['LICENSE','THIRD-PARTY-NOTICES.md']:
        shutil.copyfile(REPO / name, root / name)
    for name in ['operations.md','QUICKSTART.md','PREVIEW-NOTES.md','AUTOMATIC-UPDATES.md']:
        shutil.copyfile(REPO / 'docs' / name, root / 'docs' / name)
    shutil.copyfile(REPO / 'packaging/image-backend.lock.json', root / 'image-backend.lock.json')
    shutil.copytree(REPO / 'packaging/notices', root / 'notices', dirs_exist_ok=True)
    (root / 'install-inventory.json').unlink(missing_ok=True)
    verify(root)
    separate_sources(root)
    files = inventory(root)
    (root / 'install-inventory.json').write_text(json.dumps(files, indent=2)+'\n', encoding='utf-8')
    files['install-inventory.json'] = sha(root / 'install-inventory.json')
    install, delete, checks = instructions(files, root.resolve())
    generated.mkdir(parents=True, exist_ok=True)
    for name, text in [('install.nsh',install),('uninstall.nsh',delete),('paths.nsh',checks),
                       ('verify.nsh',verification_instructions(files)),('version.nsh',version_include())]:
        (generated / name).write_text(text, encoding='utf-8')
    (generated / 'identity.nsh').write_text('!define INVENTORY_HASH "' + files['install-inventory.json'] + '"\n')
    print('PASS generated embedded ownership for', len(files), 'files')

def verify_prepared(root, generated):
    """Read-only ownership closure; the CLI also runs full backend verification."""
    files = inventory(root)
    inventory_hash = files.pop('install-inventory.json')
    if files != json.loads((root / 'install-inventory.json').read_text(encoding='utf-8')):
        raise ValueError('Prepared payload changed after ownership inventory generation')
    files['install-inventory.json'] = inventory_hash
    install, delete, checks = instructions(files, root.resolve())
    expected = {'install.nsh': install, 'uninstall.nsh': delete, 'paths.nsh': checks,
                'identity.nsh': '!define INVENTORY_HASH "' + inventory_hash + '"\n',
                'verify.nsh': verification_instructions(files), 'version.nsh': version_include()}
    if generated.is_symlink(): raise ValueError('Linked generated include directory')
    for name, text in expected.items():
        path = generated / name
        if path.is_symlink() or path.read_bytes() != text.replace('\n', os.linesep).encode('utf-8'):
            raise ValueError('Changed generated ownership include: ' + name)
    print('PASS unchanged prepared payload, inventory and generated ownership includes')

def checksums(dist):
    files = [dist / 'Citrus-Setup.exe'] + sorted(p for p in (dist / 'Citrus-win-x64').rglob('*') if p.is_file())
    if not files[0].is_file(): raise ValueError('Missing installer')
    for name in ['Citrus-ThirdParty-Sources.zip', 'source-distribution.json']:
        if (dist / name).exists(): files.append(checked_path(dist, name))
    (dist / 'SHA256SUMS').write_text(''.join(sha(p)+'  '+p.relative_to(dist).as_posix()+'\n' for p in files), encoding='utf-8')
    print('PASS SHA256SUMS:', len(files), 'artifacts')

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['verify','prepare','verify-prepared','checksums'])
    parser.add_argument('directory', type=Path)
    parser.add_argument('--generated', type=Path, default=REPO / 'dist/nsis')
    parser.add_argument('--sources', type=Path, help='External staging root containing source/; defaults to the sibling third-party-sources directory')
    args = parser.parse_args()
    if args.command == 'verify': verify(args.directory, args.sources)
    elif args.command == 'prepare': prepare(args.directory, args.generated)
    elif args.command == 'verify-prepared':
        verify(args.directory, args.sources)
        verify_prepared(args.directory, args.generated)
    else: checksums(args.directory)
