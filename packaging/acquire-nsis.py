#!/usr/bin/env python3
"""Acquire the exact NSIS compiler used by the Windows release workflow."""
import argparse
import hashlib
from http.client import HTTPException
import json
from pathlib import Path
import shutil
import stat
import tempfile
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import urlopen
import zipfile

# Official size/hash: https://sourceforge.net/projects/nsis/files/NSIS%203/3.11/
# The compiler pin is also required by the existing native installer hash tests.
ARCHIVE_SIZE = 2_361_546
ARCHIVE_SHA256 = 'c7d27f780ddb6cffb4730138cd1591e841f4b7edb155856901cdf5f214394fa1'
COMPILER_SHA256 = 'f497e92deb9f179f7b8f7553fcb3bd04f511bb2949e5e4a2aee80a10f7b20431'
URLS = (
    'https://downloads.sourceforge.net/project/nsis/NSIS%203/3.11/nsis-3.11.zip',
    'https://sourceforge.net/projects/nsis/files/NSIS%203/3.11/nsis-3.11.zip/download',
)


def download(url, destination):
    received, digest, status, content_type, complete = 0, hashlib.sha256(), None, '', False
    try:
        # urllib uses the inherited proxy and default verified TLS context.
        with urlopen(url, timeout=60) as response, destination.open('xb') as output:
            status, content_type = response.status, response.headers.get('Content-Type', '')
            if status != 200:
                raise ValueError('Unexpected HTTP status')
            while received <= ARCHIVE_SIZE:
                chunk = response.read(min(65_536, ARCHIVE_SIZE + 1 - received))
                if not chunk:
                    complete = True
                    break
                received += len(chunk)
                digest.update(chunk)
                output.write(chunk)
    except HTTPError as error:
        status, content_type = error.code, error.headers.get('Content-Type', '')
        error.close()
        raise
    finally:
        print('NSIS download ' + json.dumps({
            'url': urlsplit(url)._replace(query='', fragment='').geturl(), 'status': status,
            'content_type': content_type, 'received': received, 'sha256': digest.hexdigest(),
            'sha256_scope': 'complete' if complete else 'received_prefix',
        }), flush=True)
    if received != ARCHIVE_SIZE or digest.hexdigest() != ARCHIVE_SHA256:
        raise ValueError('NSIS archive size or SHA256 mismatch')


def fetch_archive(destination):
    for attempt, url in enumerate((URLS[0], URLS[0], URLS[1], URLS[1]), 1):
        try:
            download(url, destination)
            return
        except (OSError, URLError, HTTPException, ValueError) as error:
            destination.unlink(missing_ok=True)
            # Never include response bodies or server-supplied error text in logs.
            print(f'NSIS attempt {attempt} rejected: {type(error).__name__}', flush=True)
    raise RuntimeError('No official source returned the pinned NSIS 3.11 archive')


def extract_verified_archive(archive, destination):
    with zipfile.ZipFile(archive) as source:
        members, seen = source.infolist(), set()
        reserved = {'CON', 'PRN', 'AUX', 'NUL', *(f'{prefix}{number}' for prefix in ('COM', 'LPT') for number in range(1, 10))}
        for member in members:
            name = member.filename.rstrip('/')
            parts = name.split('/')
            key = name.casefold()
            if ('\\' in name or ':' in name or parts[0] != 'nsis-3.11' or key in seen or
                    any(part in ('', '.', '..') or part != part.rstrip(' .') or part.split('.')[0].upper() in reserved for part in parts) or
                    stat.S_IFMT(member.external_attr >> 16) not in (0, stat.S_IFREG, stat.S_IFDIR)):
                raise ValueError('Unsafe or duplicate NSIS ZIP member')
            seen.add(key)
        for member in members:
            target = destination.joinpath(*member.filename.rstrip('/').split('/'))
            if member.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with source.open(member) as incoming, target.open('xb') as outgoing:
                    shutil.copyfileobj(incoming, outgoing)


def acquire(directory):
    directory = Path(directory).absolute()
    if directory.is_symlink() or (directory.exists() and (not directory.is_dir() or any(directory.iterdir()))):
        raise ValueError('NSIS destination must be a fresh or empty directory')
    directory.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.nsis-', dir=directory) as temporary:
        staging = Path(temporary)
        archive = staging / 'nsis-3.11.zip'
        fetch_archive(archive)
        extract_verified_archive(archive, staging / 'extracted')
        tree = staging / 'extracted/nsis-3.11'
        compiler = tree / 'makensis.exe'
        if not compiler.is_file() or hashlib.sha256(compiler.read_bytes()).hexdigest() != COMPILER_SHA256:
            raise ValueError('NSIS compiler missing or SHA256 mismatch')
        # copytree refuses an existing destination, including one appearing during download.
        shutil.copytree(tree, directory / 'nsis-3.11')
    return directory / 'nsis-3.11/makensis.exe'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        compiler = acquire(args.directory)
    except (OSError, RuntimeError, ValueError, zipfile.BadZipFile) as error:
        parser.exit(1, f'NSIS acquisition failed: {error}\n')
    print(compiler)


if __name__ == '__main__':
    main()
