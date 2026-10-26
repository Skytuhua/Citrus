#!/usr/bin/env python3
"""Promote the explicitly authorized preview only after final-byte verification."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
REPO = 'Skytuhua/Citrus'
TAG = 'v0.3.0-preview.11'
NAMES = ('Citrus-Setup.exe', 'release-manifest.json', 'SHA256SUMS', 'Citrus-ThirdParty-Sources.zip', 'source-distribution.json')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def hash_file(path):
    before = path.stat()
    require(path.is_file() and not path.is_symlink() and before.st_size > 0, 'Release asset must be a nonempty regular file: ' + path.name)
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    after = path.stat()
    require((before.st_size, before.st_mtime_ns) == (after.st_size, after.st_mtime_ns), 'Local asset changed during hashing')
    return {'size': after.st_size, 'sha256': digest.hexdigest()}


def local_assets(root, dist, tag, commit, signature):
    require(tag == TAG and re.fullmatch(r'[0-9a-f]{40}', commit) and signature in ('NotSigned', 'Valid'), 'Unexpected tag, source commit or signature policy')
    version = ET.parse(root / 'CitrusVersion.props').getroot().findtext('./PropertyGroup/Version')
    require(tag == 'v' + str(version), 'Tag differs from the central release version')
    require(dist.resolve().is_relative_to(root.resolve()) and dist.is_dir(), 'Release directory must be inside the workspace')
    paths = {name: dist / ('release/SHA256SUMS' if name == 'SHA256SUMS' else name) for name in NAMES}
    assets = {name: hash_file(path) for name, path in paths.items()}
    require(all(assets[name]['size'] <= 4 * 1024 * 1024 for name in ('release-manifest.json', 'source-distribution.json', 'SHA256SUMS')), 'Release metadata exceeds its size bound')
    manifest = json.loads(paths['release-manifest.json'].read_text(encoding='utf-8'))
    require(manifest.get('schema') == 2 and manifest.get('product') == 'Citrus' and manifest.get('platform') == 'Windows x64' and manifest.get('version') == version and manifest.get('sourceCommit') == commit, 'Manifest identity differs from the expected release')
    installer = manifest.get('installer', {})
    sources = manifest.get('sources', {})
    for record, name in ((installer, NAMES[0]), (sources, NAMES[3])):
        require(record.get('file') == name and type(record.get('bytes')) is int and record['bytes'] == assets[name]['size'] and record.get('sha256') == assets[name]['sha256'], 'Manifest asset differs from final local bytes: ' + name)
    require(installer.get('fileVersion') == version and installer.get('signature') == signature, 'Installer version or signature policy differs')
    if signature == 'Valid':
        require(installer.get('signatureScope') == 'installer-only', 'Signed installer scope is missing')
        for field in ('signer', 'timestampCertificate'):
            certificate = installer.get(field) or {}
            require(bool(certificate.get('subject')) and bool(re.fullmatch(r'[0-9A-Fa-f]{40}', certificate.get('thumbprint', ''))), 'Signed installer certificate details are missing')
    else:
        require(not any(installer.get(field) for field in ('signer', 'timestampCertificate', 'signatureScope')), 'Unsigned installer claims signature details')
    url = f'https://github.com/{REPO}/releases/download/{tag}/'
    require(sources.get('url') == url + NAMES[3], 'Corresponding source URL differs')
    notice = json.loads(paths[NAMES[4]].read_text(encoding='utf-8'))
    require(type(notice.get('schema')) is int and notice['schema'] == 1 and notice.get('version') == version and notice.get('archive') == sources, 'Corresponding source notice differs from manifest')
    lines = paths['SHA256SUMS'].read_text(encoding='utf-8').splitlines()
    expected = {assets[name]['sha256'] + '  ' + name for name in NAMES if name != 'SHA256SUMS'}
    require(len(lines) == 4 and set(lines) == expected, 'Public checksum inventory differs from final bytes')
    # Parsing and hashing must describe the same metadata, even if another local writer runs.
    require(assets == {name: hash_file(path) for name, path in paths.items()}, 'Local release changed during validation')
    return assets


def github(arguments):
    environment = {**os.environ, 'GH_PROMPT_DISABLED': '1'}
    environment.pop('GH_DEBUG', None)
    result = subprocess.run(['gh', *arguments], check=False, capture_output=True, text=True, encoding='utf-8', env=environment,
                            timeout=1800 if arguments[:2] == ['release', 'download'] else 120)
    require(result.returncode == 0, 'GitHub command failed; credentials and command output withheld')
    return json.loads(result.stdout) if arguments[0] == 'api' else None


def resolve_tag(repo, tag, commit, gh=github):
    value = gh(['api', f'repos/{repo}/git/ref/tags/{tag}'])
    seen = set()
    for _ in range(8):
        obj = value.get('object', {})
        sha = obj.get('sha', '')
        require(bool(re.fullmatch(r'[0-9a-f]{40}', sha)) and sha not in seen, 'Invalid or cyclic Git tag')
        seen.add(sha)
        if obj.get('type') == 'commit':
            require(sha == commit, 'Release tag does not identify the expected source commit')
            return
        require(obj.get('type') == 'tag', 'Release tag does not identify a commit')
        value = gh(['api', f'repos/{repo}/git/tags/{sha}'])
    raise ValueError('Git tag nesting exceeds its bound')


def fetch_release(repo, tag, gh=github):
    # REST's tag lookup does not resolve pending drafts. GraphQL supplies the
    # stable database ID; repeat this lookup so release replacement is detected.
    owner, name = repo.split('/')
    query = 'query($owner:String!,$name:String!,$tag:String!){repository(owner:$owner,name:$name){release(tagName:$tag){databaseId}}}'
    value = gh(['api', 'graphql', '-f', 'query=' + query, '-f', 'owner=' + owner, '-f', 'name=' + name, '-f', 'tag=' + tag])
    release = ((value.get('data') or {}).get('repository') or {}).get('release') or {}
    identity = release.get('databaseId')
    require(type(identity) is int and identity > 0, 'Expected GitHub release is missing')
    value = gh(['api', f'repos/{repo}/releases/{identity}'])
    require(value.get('id') == identity, 'Resolved release identity differs')
    return value


def release_snapshot(release, tag, assets):
    require(type(release.get('id')) is int and release['id'] > 0 and release.get('tag_name') == tag and type(release.get('draft')) is bool and release.get('prerelease') is True, 'Unexpected release identity or publication policy')
    remote = release.get('assets', [])
    require(len(remote) == 5 and {asset.get('name') for asset in remote} == set(NAMES), 'Release must have exactly the five expected assets')
    require(len({asset.get('id') for asset in remote}) == 5, 'Release asset identities are duplicated')
    for asset in remote:
        name = asset['name']
        require(type(asset.get('id')) is int and asset['id'] > 0 and asset.get('state') == 'uploaded' and type(asset.get('size')) is int and asset['size'] == assets[name]['size'] and asset.get('digest') == 'sha256:' + assets[name]['sha256'], 'Remote asset differs from final bytes: ' + name)
        url = asset.get('browser_download_url', '')
        prefix = f'https://github.com/{REPO}/releases/download/'
        # Pending draft URLs may use an untagged-* segment. Only canonical tag URLs
        # are acceptable once published; actual public requests are always computed.
        require(isinstance(url, str) and url.startswith(prefix) and len(url[len(prefix):].split('/')) == 2 and url.endswith('/' + name), 'Unexpected asset URL')
        require(release['draft'] or url == prefix + tag + '/' + name, 'Unexpected public asset URL')
    fields = ('id', 'tag_name', 'draft', 'prerelease', 'target_commitish', 'created_at', 'updated_at', 'published_at', 'immutable')
    asset_fields = ('id', 'name', 'state', 'size', 'digest', 'created_at', 'updated_at', 'browser_download_url')
    return {**{key: release.get(key) for key in fields}, 'assets': sorted([{key: item.get(key) for key in asset_fields} for item in remote], key=lambda item: item['name'])}


class HttpsRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, newurl):
        require(urllib.parse.urlsplit(newurl).scheme == 'https', 'Public asset redirected outside HTTPS')
        return super().redirect_request(request, fp, code, message, headers, newurl)


def open_public(url):
    require(urllib.parse.urlsplit(url).scheme == 'https', 'Public asset requires HTTPS')
    request = urllib.request.Request(url, headers={'User-Agent': 'Citrus-release-verifier', 'Accept-Encoding': 'identity'})
    return urllib.request.build_opener(HttpsRedirects()).open(request, timeout=30)


def verify_public(url, size, digest, attempts=3):
    require(type(size) is int and size > 0 and bool(re.fullmatch(r'[0-9a-f]{64}', digest)) and type(attempts) is int and 1 <= attempts <= 3, 'Invalid public asset bounds')
    for attempt in range(attempts):
        try:
            count = 0
            actual = hashlib.sha256()
            deadline = time.monotonic() + 900
            with open_public(url) as response:
                require(urllib.parse.urlsplit(response.geturl()).scheme == 'https', 'Public asset requires HTTPS')
                while True:
                    require(time.monotonic() < deadline, 'Public download exceeded its time bound')
                    block = response.read(min(1024 * 1024, size - count + 1))
                    if not block:
                        break
                    count += len(block)
                    require(count <= size, 'Public asset exceeds its expected size')
                    actual.update(block)
            require(count == size and actual.hexdigest() == digest, 'Public asset size or SHA-256 differs')
            return
        except (OSError, ValueError) as error:
            if attempt + 1 == attempts:
                raise ValueError('Public download verification failed: ' + url) from error
            time.sleep(2 * (attempt + 1))


def promote(root, dist, repo, tag, commit, signature, notes, gh=github, public=verify_public):
    require(repo == REPO, 'Repository is not authorized for this publisher')
    assets = local_assets(root, dist, tag, commit, signature)
    require(notes.is_file() and 0 < notes.stat().st_size <= 1024 * 1024, 'Explicit release notes are required')
    resolve_tag(repo, tag, commit, gh)
    initial = release_snapshot(fetch_release(repo, tag, gh), tag, assets)
    with tempfile.TemporaryDirectory(prefix='citrus-release-promotion-') as temp:
        gh(['release', 'download', tag, '--repo', repo, '--dir', temp])
        downloaded = Path(temp)
        require({path.name for path in downloaded.iterdir()} == set(NAMES), 'Downloaded asset inventory differs')
        require({name: hash_file(downloaded / name) for name in NAMES} == assets, 'Downloaded draft assets differ from final local bytes')
    require(local_assets(root, dist, tag, commit, signature) == assets, 'Final local assets changed before publication')
    resolve_tag(repo, tag, commit, gh)
    require(release_snapshot(fetch_release(repo, tag, gh), tag, assets) == initial, 'Release changed during pre-publication verification')
    if initial['draft']:
        try:
            gh(['release', 'edit', tag, '--repo', repo, '--draft=false', '--prerelease', '--latest=false', '--notes-file', str(notes)])
        except Exception as error:
            raise RuntimeError('Publication outcome is unknown; preserve the release and rerun verification') from error
    published = not initial['draft']
    try:
        current = release_snapshot(fetch_release(repo, tag, gh), tag, assets)
        require(current['draft'] is False, 'Release is still a draft')
        published = True
        changing = {'draft', 'published_at', 'updated_at', 'immutable'}
        stable = []
        for snapshot in (initial, current):
            value = {k: v for k, v in snapshot.items() if k not in changing}
            value['assets'] = [{k: v for k, v in asset.items() if k != 'browser_download_url'} for asset in snapshot['assets']]
            stable.append(value)
        require(stable[0] == stable[1], 'Published release identity or assets changed')
        resolve_tag(repo, tag, commit, gh)
        for name, asset in assets.items():
            public(f'https://github.com/{repo}/releases/download/{tag}/{name}', asset['size'], asset['sha256'])
        require(release_snapshot(fetch_release(repo, tag, gh), tag, assets) == current, 'Release changed during public verification')
        resolve_tag(repo, tag, commit, gh)
    except Exception as error:
        status = 'Release is published' if published else 'Publication state could not be confirmed'
        raise RuntimeError(status + '; public verification failed. Preserve it for investigation; no assets were replaced or deleted.') from error
    print(f'PASS published release and all five public asset hashes: https://github.com/{repo}/releases/tag/{tag}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', required=True)
    parser.add_argument('--tag', required=True)
    parser.add_argument('--source-commit', required=True)
    parser.add_argument('--signature', required=True, choices=('NotSigned', 'Valid'))
    parser.add_argument('--dist', type=Path, default=ROOT / 'dist')
    parser.add_argument('--notes-file', type=Path, required=True)
    args = parser.parse_args()
    try:
        promote(ROOT, args.dist.resolve(), args.repo, args.tag, args.source_commit, args.signature, args.notes_file.resolve())
    except (ValueError, RuntimeError, OSError, ET.ParseError) as error:
        parser.exit(1, str(error) + '\n')
