"""Release promotion contracts; only GitHub/network boundaries are replaced."""
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).with_name('promote-release.py')
if SCRIPT.exists():
    spec = importlib.util.spec_from_file_location('promote_release', SCRIPT)
    promotion = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(promotion)
else:
    promotion = types.SimpleNamespace()

TAG = 'v0.3.0-preview.12'
COMMIT = '1234567890abcdef1234567890abcdef12345678'
REPO = 'Skytuhua/Citrus'
BASE = 'https://github.com/Skytuhua/Citrus/releases/download/' + TAG + '/'


class PromotionTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(SCRIPT.exists(), 'promotion helper implementation is missing')
        # Successful fixture promotions must not look like a real published release in CI logs.
        output = patch('sys.stdout', new=io.StringIO())
        output.start()
        self.addCleanup(output.stop)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.dist = self.root / 'dist'
        self.dist.mkdir()
        (self.dist / 'release').mkdir()
        (self.root / 'CitrusVersion.props').write_text('<Project><PropertyGroup><Version>0.3.0-preview.12</Version></PropertyGroup></Project>')
        self.notes = self.root / 'notes.md'
        self.notes.write_text('Unsigned preview; verified build.')
        (self.dist / 'Citrus-Setup.exe').write_bytes(b'installer fixture')
        (self.dist / 'Citrus-ThirdParty-Sources.zip').write_bytes(b'complete sources fixture')
        self.manifest = {'schema': 2, 'product': 'Citrus', 'platform': 'Windows x64', 'version': '0.3.0-preview.12', 'sourceCommit': COMMIT,
                         'installer': {'file': 'Citrus-Setup.exe', 'bytes': 17, 'sha256': hashlib.sha256(b'installer fixture').hexdigest(), 'fileVersion': '0.3.0-preview.12', 'signature': 'NotSigned'},
                         'sources': {'file': 'Citrus-ThirdParty-Sources.zip', 'bytes': 24, 'sha256': hashlib.sha256(b'complete sources fixture').hexdigest(), 'url': BASE + 'Citrus-ThirdParty-Sources.zip'}}
        self.write_metadata()

    def write_metadata(self):
        (self.dist / 'release-manifest.json').write_text(json.dumps(self.manifest))
        (self.dist / 'source-distribution.json').write_text(json.dumps({'schema': 1, 'version': '0.3.0-preview.12', 'archive': self.manifest['sources'], 'files': {}}))
        names = ['Citrus-Setup.exe', 'release-manifest.json', 'Citrus-ThirdParty-Sources.zip', 'source-distribution.json']
        (self.dist / 'release/SHA256SUMS').write_text(''.join(hashlib.sha256((self.dist / name).read_bytes()).hexdigest() + '  ' + name + '\n' for name in names))

    def feature(self, name):
        result = getattr(promotion, name, None)
        self.assertTrue(callable(result), name + ' implementation is missing')
        return result

    def local(self, **kwargs):
        return self.feature('local_assets')(self.root, self.dist, TAG, COMMIT, kwargs.get('signature', 'NotSigned'))

    def release(self):
        names = ['Citrus-Setup.exe', 'release-manifest.json', 'SHA256SUMS', 'Citrus-ThirdParty-Sources.zip', 'source-distribution.json']
        return {'id': 123, 'tag_name': TAG, 'draft': True, 'prerelease': True, 'target_commitish': COMMIT, 'html_url': 'https://github.com/Skytuhua/Citrus/releases/tag/' + TAG,
                'updated_at': '2026-10-26T01:00:00Z', 'published_at': None,
                'assets': [{'id': i + 20, 'name': name, 'state': 'uploaded', 'size': self.path(name).stat().st_size, 'digest': 'sha256:' + hashlib.sha256(self.path(name).read_bytes()).hexdigest(), 'updated_at': '2026-10-26T01:00:00Z', 'browser_download_url': BASE + name} for i, name in enumerate(names)]}

    def path(self, name):
        return self.dist / ('release/SHA256SUMS' if name == 'SHA256SUMS' else name)

    def test_final_bytes_and_four_entry_checksums_are_validated(self):
        assets = self.local()
        self.assertEqual(set(assets), {'Citrus-Setup.exe', 'release-manifest.json', 'SHA256SUMS', 'Citrus-ThirdParty-Sources.zip', 'source-distribution.json'})
        self.assertEqual(assets['Citrus-Setup.exe']['size'], 17)
        self.path('Citrus-Setup.exe').write_bytes(b'corrupt installer')
        with self.assertRaises(ValueError): self.local()

    def test_local_identity_and_signature_mismatches_fail(self):
        cases = [('version', '0.3.0-preview.9'), ('sourceCommit', 'a' * 40), ('schema', 1), ('product', 'Other'), ('platform', 'Linux')]
        for key, value in cases:
            with self.subTest(key=key):
                before = self.manifest[key]
                self.manifest[key] = value
                self.write_metadata()
                with self.assertRaises(ValueError): self.local()
                self.manifest[key] = before
        self.write_metadata()
        with self.assertRaises(ValueError): self.local(signature='Valid')
        self.manifest['installer'].update(signature='Valid', signatureScope='installer-only', signer={'subject': 'CN=Citrus', 'thumbprint': 'A' * 40}, timestampCertificate={'subject': 'CN=Timestamp', 'thumbprint': 'B' * 40})
        self.write_metadata()
        self.assertEqual(self.local(signature='Valid')['Citrus-Setup.exe']['size'], 17)
        del self.manifest['installer']['signer']['thumbprint']
        self.write_metadata()
        with self.assertRaises(ValueError): self.local(signature='Valid')

    def test_checksum_missing_extra_or_wrong_line_is_rejected(self):
        sums = self.path('SHA256SUMS').read_text()
        for bad in [sums.splitlines()[0] + '\n', sums + 'a' * 64 + '  extra.zip\n', sums.replace('Citrus-Setup.exe', '../Citrus-Setup.exe'), sums.replace(sums[:64], '0' * 64)]:
            self.path('SHA256SUMS').write_text(bad)
            with self.subTest(bad=bad), self.assertRaises(ValueError): self.local()

    def test_refuses_wrong_expected_tag_commit_and_signature_policy(self):
        for tag, commit, signature in [('v0.3.0-preview.9', COMMIT, 'NotSigned'), (TAG, 'main', 'NotSigned'), (TAG, COMMIT, 'Unknown')]:
            with self.subTest(tag=tag, commit=commit, signature=signature), self.assertRaises(ValueError):
                self.feature('local_assets')(self.root, self.dist, tag, commit, signature)

    def test_remote_inventory_requires_all_five_exact_hashes_sizes_and_uploaded_state(self):
        assets = self.local()
        self.feature('release_snapshot')(self.release(), TAG, assets)
        mutations = [lambda r: r['assets'].pop(), lambda r: r['assets'].append(copy.deepcopy(r['assets'][0])), lambda r: r.update(tag_name='vwrong'), lambda r: r.update(prerelease=False), lambda r: r['assets'][0].update(digest='sha256:' + '0' * 64), lambda r: r['assets'][0].update(size=True), lambda r: r['assets'][0].update(state='new'), lambda r: r['assets'][0].update(browser_download_url='http://example.com/setup.exe')]
        for mutate in mutations:
            release = self.release()
            mutate(release)
            with self.subTest(release=release), self.assertRaises(ValueError): self.feature('release_snapshot')(release, TAG, assets)

    def test_tag_resolution_peels_annotated_tag_and_rejects_cycles_wrong_commit(self):
        responses = [{'object': {'type': 'tag', 'sha': 'a' * 40}}, {'object': {'type': 'commit', 'sha': COMMIT}}]
        requests = []
        def api(arguments):
            requests.append(arguments)
            return responses.pop(0)
        self.feature('resolve_tag')(REPO, TAG, COMMIT, api)
        self.assertEqual(requests, [['api', 'repos/Skytuhua/Citrus/git/ref/tags/' + TAG], ['api', 'repos/Skytuhua/Citrus/git/tags/' + 'a' * 40]])
        for obj in [{'type': 'commit', 'sha': 'b' * 40}, {'type': 'tree', 'sha': COMMIT}, {'type': 'tag', 'sha': 'a' * 40}]:
            with self.subTest(obj=obj), self.assertRaises(ValueError): self.feature('resolve_tag')(REPO, TAG, COMMIT, lambda args: {'object': obj})

    def test_draft_lookup_resolves_graphql_identity_then_reads_rest_release_by_id(self):
        calls = []
        def gh(args):
            calls.append(args)
            if args[:2] == ['api', 'graphql']:
                return {'data': {'repository': {'release': {'databaseId': 123}}}}
            self.assertEqual(args, ['api', 'repos/Skytuhua/Citrus/releases/123'])
            return self.release()
        actual = self.feature('fetch_release')(REPO, TAG, gh)
        self.assertTrue(actual['draft'])
        self.assertIn('tag=' + TAG, calls[0])
        self.assertIn('owner=Skytuhua', calls[0])
        with self.assertRaises(ValueError): self.feature('fetch_release')(REPO, TAG, lambda args: {'data': {'repository': {'release': None}}})

    def test_gh_json_uses_utf8_when_the_windows_default_encoding_differs(self):
        def run(args, **kwargs):
            self.assertEqual(args, ['gh', 'api', 'fixture'])
            payload = '{"body":"Bytes 2→1; 4.74× faster"}'.encode('utf-8')
            return types.SimpleNamespace(returncode=0, stdout=payload.decode(kwargs.get('encoding', 'cp1252')))
        self.feature('github')
        with patch.object(promotion.subprocess, 'run', side_effect=run):
            self.assertEqual(promotion.github(['api', 'fixture']), {'body': 'Bytes 2→1; 4.74× faster'})

    def test_gh_failure_does_not_expose_captured_credentials(self):
        self.feature('github')
        with patch.object(promotion.subprocess, 'run', return_value=types.SimpleNamespace(returncode=1, stdout='secret-token', stderr='secret-token')):
            with self.assertRaises(ValueError) as raised: promotion.github(['api', 'fixture'])
        self.assertNotIn('secret-token', str(raised.exception))

    def run_promotion(self, mutate=None, published=False, public_error=False, corrupt_download=False, move_tag_at=None, change_during_public=False, edit_error=False, pending_urls=False):
        release = self.release()
        release['draft'] = not published
        if pending_urls:
            for asset in release['assets']: asset['browser_download_url'] = BASE.replace(TAG, 'untagged-pending') + asset['name']
        state = {'edits': [], 'downloads': 0, 'public': [], 'fetches': 0, 'refs': 0}
        def gh(args):
            if args[0] == 'api':
                if args[1] == 'graphql': return {'data': {'repository': {'release': {'databaseId': release['id']}}}}
                if '/git/ref/' in args[1]:
                    state['refs'] += 1
                    return {'object': {'type': 'commit', 'sha': 'b' * 40 if state['refs'] == move_tag_at else COMMIT}}
                self.assertEqual(args[1], 'repos/Skytuhua/Citrus/releases/' + str(release['id']))
                state['fetches'] += 1
                if mutate and state['fetches'] == 2: mutate(release)
                return copy.deepcopy(release)
            if args[:2] == ['release', 'download']:
                state['downloads'] += 1
                destination = Path(args[args.index('--dir') + 1])
                for name in [a['name'] for a in release['assets']]: (destination / name).write_bytes(self.path(name).read_bytes())
                if corrupt_download: (destination / 'Citrus-Setup.exe').write_bytes(b'corrupt installer')
                return None
            if args[:2] == ['release', 'edit']:
                state['edits'].append(args)
                release['draft'] = False
                release['published_at'] = '2026-10-26T01:01:00Z'
                for asset in release['assets']: asset['browser_download_url'] = BASE + asset['name']
                if edit_error: raise ValueError('Connection lost after edit')
                return None
            self.fail('Unexpected GitHub call: ' + str(args))
        def public(url, size, digest):
            state['public'].append(url)
            path = self.path(url.rsplit('/', 1)[1])
            self.assertEqual(size, path.stat().st_size)
            self.assertEqual(digest, hashlib.sha256(path.read_bytes()).hexdigest())
            if change_during_public: release['assets'][0]['updated_at'] = '2026-10-26T03:00:00Z'
            if public_error: raise ValueError('CDN bytes differ')
        try:
            self.feature('promote')(self.root, self.dist, REPO, TAG, COMMIT, 'NotSigned', self.notes, gh=gh, public=public)
        except Exception as error:
            error.state = state
            raise
        return state

    def test_verified_draft_is_published_once_with_notes_and_public_checks(self):
        state = self.run_promotion()
        self.assertEqual(len(state['edits']), 1)
        edit = state['edits'][0]
        self.assertIn('--draft=false', edit)
        self.assertIn('--prerelease', edit)
        self.assertIn('--latest=false', edit)
        self.assertEqual(edit[edit.index('--notes-file') + 1], str(self.notes))
        self.assertEqual(set(state['public']), {BASE + name for name in self.local()})
        self.assertEqual(state['downloads'], 1)

    def test_changed_remote_identity_or_assets_prevents_any_publication(self):
        for mutate in [lambda r: r.update(id=456), lambda r: r.update(draft=False), lambda r: r['assets'][0].update(id=99), lambda r: r['assets'][0].update(updated_at='2026-10-26T02:00:00Z')]:
            with self.subTest(mutate=mutate):
                with self.assertRaises(ValueError) as raised: self.run_promotion(mutate=mutate)
                self.assertEqual(raised.exception.state['edits'], [])

    def test_matching_already_published_release_is_read_only_and_verifies_public_bytes(self):
        state = self.run_promotion(published=True)
        self.assertEqual(state['edits'], [])
        self.assertEqual(len(state['public']), 5)

    def test_pending_draft_urls_can_transition_to_the_canonical_public_tag(self):
        state = self.run_promotion(pending_urls=True)
        self.assertEqual(len(state['edits']), 1)
        self.assertEqual(len(state['public']), 5)

    def test_corrupt_download_and_moved_tag_prevent_publication(self):
        for options in [{'corrupt_download': True}, {'move_tag_at': 2}]:
            with self.subTest(options=options):
                with self.assertRaises(ValueError) as raised: self.run_promotion(**options)
                self.assertEqual(raised.exception.state['edits'], [])
                self.assertEqual(raised.exception.state['public'], [])

    def test_published_retry_refuses_changed_bytes_without_mutation(self):
        with self.assertRaises(ValueError) as raised: self.run_promotion(published=True, corrupt_download=True)
        self.assertEqual(raised.exception.state['edits'], [])

    def test_postpublication_tag_or_asset_change_fails_without_replacement(self):
        for options in [{'move_tag_at': 3}, {'change_during_public': True}]:
            with self.subTest(options=options):
                with self.assertRaisesRegex(RuntimeError, 'published.*verification') as raised: self.run_promotion(**options)
                self.assertEqual(len(raised.exception.state['edits']), 1)

    def test_lost_publish_response_reports_unknown_outcome_and_never_rolls_back(self):
        with self.assertRaisesRegex(RuntimeError, 'outcome is unknown') as raised: self.run_promotion(edit_error=True)
        self.assertEqual(len(raised.exception.state['edits']), 1)

    def test_boolean_source_schema_and_zero_public_attempts_are_rejected(self):
        notice = json.loads(self.path('source-distribution.json').read_text())
        notice['schema'] = True
        self.path('source-distribution.json').write_text(json.dumps(notice))
        sums = self.path('SHA256SUMS').read_text().splitlines()
        sums = [hashlib.sha256(self.path('source-distribution.json').read_bytes()).hexdigest() + '  source-distribution.json' if line.endswith('  source-distribution.json') else line for line in sums]
        self.path('SHA256SUMS').write_text('\n'.join(sums) + '\n')
        with self.assertRaises(ValueError): self.local()

    def test_zero_public_attempts_cannot_claim_success_without_reading(self):
        with self.assertRaises(ValueError): self.feature('verify_public')(BASE + 'SHA256SUMS', 3, hashlib.sha256(b'abc').hexdigest(), attempts=0)

    def test_public_failure_keeps_release_published_and_reports_that_state(self):
        with self.assertRaisesRegex(RuntimeError, 'published.*verification') as raised: self.run_promotion(public_error=True)
        self.assertEqual(len(raised.exception.state['edits']), 1)

    def test_repository_is_pinned_before_any_github_access(self):
        with self.assertRaises(ValueError): self.feature('promote')(self.root, self.dist, 'attacker/Citrus', TAG, COMMIT, 'NotSigned', self.notes, gh=lambda args: self.fail('No GitHub access permitted'))

    def test_public_hash_stream_is_bounded_and_rejects_truncation_extra_bytes_and_bad_redirect(self):
        digest = hashlib.sha256(b'abc').hexdigest()
        class Response(io.BytesIO):
            def __init__(self, data, url=BASE + 'SHA256SUMS'): super().__init__(data); self.url = url
            def geturl(self): return self.url
            def read(self, count=-1):
                self_test.assertGreater(count, 0)
                self_test.assertLessEqual(count, 1024 * 1024)
                return super().read(count)
        self_test = self
        self.feature('verify_public')
        with patch.object(promotion, 'open_public', return_value=Response(b'abc')): promotion.verify_public(BASE + 'SHA256SUMS', 3, digest, attempts=1)
        for response in [Response(b'ab'), Response(b'abcd'), Response(b'xyz'), Response(b'abc', 'http://example.com/file')]:
            with self.subTest(response=response), patch.object(promotion, 'open_public', return_value=response), self.assertRaises(ValueError): promotion.verify_public(BASE + 'SHA256SUMS', 3, digest, attempts=1)


if __name__ == '__main__': unittest.main()
