#!/usr/bin/env python3
"""Accept only a timestamped, approved signature of this exact installer build."""
import argparse
import hashlib
import importlib.util
import os
from pathlib import Path
import shutil
import struct
import tempfile
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('release_metadata', ROOT / 'packaging/release-metadata.py')
metadata = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(metadata)


def preflight(environment):
    required = ('SIGNPATH_ORGANIZATION_ID', 'SIGNPATH_PROJECT_SLUG', 'SIGNPATH_SIGNING_POLICY_SLUG',
                'SIGNPATH_ARTIFACT_CONFIGURATION_SLUG', 'SIGNPATH_ALLOWED_SIGNER_THUMBPRINTS', 'SIGNPATH_API_TOKEN')
    missing = [name for name in required if not environment.get(name, '').strip()]
    if missing:
        raise ValueError('Missing SignPath configuration: ' + ', '.join(missing))
    try:
        uuid.UUID(environment['SIGNPATH_ORGANIZATION_ID'])
    except ValueError as error:
        raise ValueError('SIGNPATH_ORGANIZATION_ID must be the UUID provided at onboarding') from error
    metadata.approved_thumbprints(environment['SIGNPATH_ALLOWED_SIGNER_THUMBPRINTS'])


def require_unlinked(path):
    for part in (path, *path.parents):
        if part.exists() and getattr(part.lstat(), 'st_file_attributes', 0) & 0x400:
            raise ValueError('Refusing a linked signing input or output')
        if part.is_symlink():
            raise ValueError('Refusing a linked signing input or output')


def pe_offsets(path):
    """Read only the PE fields that Authenticode signing is allowed to change."""
    with path.open('rb') as stream:
        header = stream.read(64)
        if len(header) != 64 or header[:2] != b'MZ':
            raise ValueError('Invalid installer PE header')
        pe = struct.unpack_from('<I', header, 60)[0]
        if pe > path.stat().st_size - 24:
            raise ValueError('Invalid installer PE offset')
        stream.seek(pe)
        coff = stream.read(24)
        if coff[:4] != b'PE\0\0':
            raise ValueError('Invalid installer PE signature')
        optional_size = struct.unpack_from('<H', coff, 20)[0]
        optional = stream.read(optional_size)
        if len(optional) != optional_size or optional_size < 2:
            raise ValueError('Incomplete installer PE optional header')
        magic = struct.unpack_from('<H', optional)[0]
        directory = {0x10b: 128, 0x20b: 144}.get(magic)
        if directory is None or optional_size < directory + 8:
            raise ValueError('Unsupported installer PE optional header')
        checksum = pe + 24 + 64
        security = pe + 24 + directory
        certificate, size = struct.unpack_from('<II', optional, directory)
        return checksum, security, certificate, size


def body_digest(path, length, omitted):
    result = hashlib.sha256()
    with path.open('rb') as stream:
        position = 0
        for start, count in [*omitted, (length, 0)]:
            remaining = start - position
            while remaining:
                block = stream.read(min(remaining, 1024 * 1024))
                if not block:
                    raise ValueError('Returned installer payload is truncated')
                result.update(block)
                remaining -= len(block)
            stream.seek(count, 1)
            position = start + count
    return result.digest()


def verify_unchanged_payload(original, signed):
    """Forbid rewriting embedded files or replacing this build with another build.

    An unsigned NSIS image may only gain an aligned certificate table and changes
    to its PE checksum/security directory. Compare all other original bytes.
    This deliberately refuses signing services that also repack the installer.
    """
    checksum, security, certificate, size = pe_offsets(original)
    returned_checksum, returned_security, returned_certificate, returned_size = pe_offsets(signed)
    length = original.stat().st_size
    if certificate or size:
        raise ValueError('Expected an unsigned original installer payload')
    if (checksum, security) != (returned_checksum, returned_security):
        raise ValueError('Returned installer payload headers changed')
    expected_certificate = (length + 7) & ~7
    if returned_certificate != expected_certificate or returned_size < 8 or signed.stat().st_size != returned_certificate + returned_size:
        raise ValueError('Returned installer payload has an unexpected certificate layout')
    with signed.open('rb') as stream:
        stream.seek(length)
        if stream.read(expected_certificate - length) != b'\0' * (expected_certificate - length):
            raise ValueError('Returned installer payload has nonzero signing padding')
    omissions = [(checksum, 4), (security, 8)]
    if body_digest(original, length, omissions) != body_digest(signed, length, omissions):
        raise ValueError('Returned installer payload differs from the unsigned build')


def accept_installer(signed_directory, target, version, allowed_thumbprints):
    signed_directory, target = Path(signed_directory).absolute(), Path(target).absolute()
    require_unlinked(signed_directory)
    require_unlinked(target)
    expected = signed_directory / 'Citrus-Setup.exe'
    if not signed_directory.is_dir() or list(signed_directory.iterdir()) != [expected] or not expected.is_file():
        raise ValueError('Signing output must contain exactly Citrus-Setup.exe')
    require_unlinked(expected)
    if target.name != 'Citrus-Setup.exe' or not target.is_file():
        raise ValueError('Existing unsigned Citrus-Setup.exe is required')
    probe = metadata.authenticode_signature(expected)
    metadata.signature_metadata(probe, require_signed=True, allowed_thumbprints=allowed_thumbprints)
    if probe.get('productName') != 'Citrus' or probe.get('fileVersion') != version:
        raise ValueError('Signed installer product/version differs from this Citrus release')
    verify_unchanged_payload(target, expected)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=target.parent, prefix='.citrus-signed-', suffix='.tmp', delete=False) as stream:
            temporary = Path(stream.name)
            with expected.open('rb') as source:
                shutil.copyfileobj(source, stream, 1024 * 1024)
            stream.flush()
            os.fsync(stream.fileno())
        if metadata.sha(temporary) != metadata.sha(expected):
            raise ValueError('Copied signed installer bytes differ')
        require_unlinked(target)
        os.replace(temporary, target)
        temporary = None
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['preflight', 'accept'])
    parser.add_argument('--signed-directory', type=Path)
    parser.add_argument('--installer', type=Path, default=ROOT / 'dist/Citrus-Setup.exe')
    args = parser.parse_args()
    if args.command == 'preflight':
        preflight(os.environ)
        print('SignPath configuration present; this does not establish Foundation acceptance or a signature.')
    else:
        if args.signed_directory is None:
            parser.error('accept requires --signed-directory')
        version = ET.parse(ROOT / 'CitrusVersion.props').getroot().findtext('./PropertyGroup/Version')
        accept_installer(args.signed_directory, args.installer, version, os.environ.get('SIGNPATH_ALLOWED_SIGNER_THUMBPRINTS'))
        print('Verified timestamped installer-only signature and unchanged embedded payload.')
