#!/usr/bin/env python3
"""Portable acquisition contracts; HTTP responses and compiler bytes are fixtures."""
import contextlib
import hashlib
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch
from urllib.error import HTTPError, URLError
import zipfile

spec = importlib.util.spec_from_file_location('acquire_nsis', Path(__file__).with_name('acquire-nsis.py'))
nsis = importlib.util.module_from_spec(spec)
spec.loader.exec_module(nsis)

COMPILER = b'verified NSIS compiler fixture; never executed'


def archive(members=None):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w') as result:
        for name, content in (members or {'nsis-3.11/makensis.exe': COMPILER, 'nsis-3.11/Include/sample.nsh': b'fixture include'}).items():
            result.writestr(name, content)
    return stream.getvalue()


class Response(io.BytesIO):
    def __init__(self, content, content_type='application/zip', status=200):
        super().__init__(content)
        self.headers = {'Content-Type': content_type}
        self.status = status
        self.received = 0

    def read(self, size=-1):
        data = super().read(size)
        self.received += len(data)
        return data


class AcquisitionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='citrus-nsis-tests-')
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name) / 'tool'
        self.payload = archive()
        self.output = io.StringIO()
        self.addCleanup(patch.stopall)
        patch.object(nsis, 'ARCHIVE_SIZE', len(self.payload)).start()
        patch.object(nsis, 'ARCHIVE_SHA256', hashlib.sha256(self.payload).hexdigest()).start()
        patch.object(nsis, 'COMPILER_SHA256', hashlib.sha256(COMPILER).hexdigest()).start()

    def acquire(self, responses):
        with patch.object(nsis, 'urlopen', side_effect=responses) as request, contextlib.redirect_stdout(self.output):
            result = nsis.acquire(self.directory)
        return result, request

    def test_valid_archive_stops_requests_and_exposes_only_verified_compiler(self):
        result, request = self.acquire([Response(self.payload)])
        self.assertEqual(result, self.directory / 'nsis-3.11/makensis.exe')
        self.assertEqual(result.read_bytes(), COMPILER)
        self.assertEqual(request.call_count, 1)
        self.assertEqual({p.name for p in self.directory.iterdir()}, {'nsis-3.11'})

    def test_html_and_same_size_wrong_hash_are_discarded_before_fallback_success(self):
        changed = bytearray(self.payload)
        changed[-1] ^= 1
        result, request = self.acquire([Response(b'<html>DO_NOT_LOG_RESPONSE_BODY</html>', 'text/html'), Response(changed), Response(self.payload)])
        self.assertEqual(result.read_bytes(), COMPILER)
        self.assertEqual(request.call_count, 3)
        self.assertEqual([call.args[0] for call in request.call_args_list], [nsis.URLS[0], nsis.URLS[0], nsis.URLS[1]])
        log = self.output.getvalue()
        for field in ('"status": 200', '"content_type": "text/html"', '"received":', '"sha256":'):
            self.assertIn(field, log)
        self.assertNotIn('DO_NOT_LOG_RESPONSE_BODY', log)

    def test_truncated_and_oversized_responses_fail_without_extracting(self):
        for body in (self.payload[:-1], self.payload + b'oversized' * 100):
            with self.subTest(size=len(body)):
                with patch.object(nsis, 'urlopen', side_effect=lambda *args, **kwargs: Response(body)), contextlib.redirect_stdout(self.output):
                    with self.assertRaises(RuntimeError):
                        nsis.acquire(self.directory)
                self.assertFalse((self.directory / 'nsis-3.11').exists())
                self.assertEqual(list(self.directory.iterdir()), [])
        responses = [Response(self.payload + b'x' * 1_000_000) for _ in range(4)]
        with patch.object(nsis, 'urlopen', side_effect=responses), contextlib.redirect_stdout(self.output):
            with self.assertRaises(RuntimeError):
                nsis.acquire(self.directory)
        self.assertTrue(all(response.received <= len(self.payload) + 1 for response in responses))

    def test_transport_failures_and_all_wrong_hashes_fail_closed(self):
        for responses in ([URLError('fixture transport failure')] * 4, [Response(b'bad') for _ in range(4)]):
            with self.subTest(kind=type(responses[0]).__name__), patch.object(nsis, 'urlopen', side_effect=responses) as request, contextlib.redirect_stdout(self.output):
                with self.assertRaises(RuntimeError):
                    nsis.acquire(self.directory)
                self.assertEqual(request.call_count, 4)
                self.assertFalse((self.directory / 'nsis-3.11').exists())
                self.assertEqual(list(self.directory.iterdir()), [])

    def test_failure_after_partial_read_does_not_reuse_old_bytes(self):
        response = Response(self.payload)
        response.read = Mock(side_effect=[self.payload[:10], OSError('fixture read failure')])
        result, request = self.acquire([response, Response(self.payload)])
        self.assertEqual(result.read_bytes(), COMPILER)
        self.assertEqual(request.call_count, 2)

    def test_nonempty_destination_is_refused_without_network_or_overwrite(self):
        self.directory.mkdir()
        sentinel = self.directory / 'sentinel.txt'
        sentinel.write_bytes(b'preserve existing file')
        with patch.object(nsis, 'urlopen') as request:
            with self.assertRaises(ValueError):
                nsis.acquire(self.directory)
        request.assert_not_called()
        self.assertEqual(sentinel.read_bytes(), b'preserve existing file')

    def test_destination_appearing_during_download_is_not_overwritten(self):
        sentinel = self.directory / 'nsis-3.11/sentinel.txt'

        def incoming(*args, **kwargs):
            sentinel.parent.mkdir()
            sentinel.write_bytes(b'preserve competing directory')
            return Response(self.payload)

        with patch.object(nsis, 'urlopen', side_effect=incoming) as request, contextlib.redirect_stdout(self.output):
            with self.assertRaises(FileExistsError):
                nsis.acquire(self.directory)
        self.assertEqual(request.call_count, 1)
        self.assertEqual(sentinel.read_bytes(), b'preserve competing directory')
        self.assertFalse((sentinel.parent / 'makensis.exe').exists())

    def test_zip_traversal_absolute_windows_aliases_and_symlinks_are_rejected(self):
        names = ('../escaped.txt', '/escaped.txt', 'nsis-3.11/../escaped.txt', 'nsis-3.11\\..\\escaped.txt',
                 'C:/escaped.txt', 'nsis-3.11/alias./escaped.txt', 'nsis-3.11/file:stream', 'nsis-3.11/CON.txt', 'nsis-3.11/NUL')
        for name in names:
            with self.subTest(name=name):
                self.assert_bad_members(archive({name: b'unsafe', 'nsis-3.11/makensis.exe': COMPILER}))
        self.assert_bad_members(archive({'nsis-3.11/makensis.exe': COMPILER, 'nsis-3.11/MAKENSIS.EXE': COMPILER}))
        member = zipfile.ZipInfo('nsis-3.11/link')
        member.create_system = 3
        member.external_attr = 0o120777 << 16
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w') as result:
            result.writestr(member, '../escaped.txt')
        self.assert_bad_members(stream.getvalue())

    def assert_bad_members(self, payload):
        with patch.object(nsis, 'ARCHIVE_SIZE', len(payload)), patch.object(nsis, 'ARCHIVE_SHA256', hashlib.sha256(payload).hexdigest()), \
                patch.object(nsis, 'urlopen', return_value=Response(payload)) as request, contextlib.redirect_stdout(self.output):
            with self.assertRaises(ValueError):
                nsis.acquire(self.directory)
            self.assertEqual(request.call_count, 1)
        self.assertFalse((self.directory / 'nsis-3.11').exists())
        self.assertFalse((self.directory / 'escaped.txt').exists())
        self.assertFalse((self.directory.parent / 'escaped.txt').exists())
        self.assertEqual(list(self.directory.iterdir()), [])

    def test_http_error_is_retried_without_printing_its_body(self):
        error = HTTPError(nsis.URLS[0], 503, 'fixture unavailable', {'Content-Type': 'text/html'}, io.BytesIO(b'DO_NOT_LOG_RESPONSE_BODY'))
        result, request = self.acquire([error, Response(self.payload)])
        self.assertEqual(result.read_bytes(), COMPILER)
        self.assertEqual(request.call_count, 2)
        self.assertIn('"status": 503', self.output.getvalue())
        self.assertNotIn('DO_NOT_LOG_RESPONSE_BODY', self.output.getvalue())

    def test_compiler_mismatch_is_rejected_before_publishing_tree_or_path(self):
        patch.object(nsis, 'COMPILER_SHA256', '0' * 64).start()
        with patch.object(nsis, 'urlopen', return_value=Response(self.payload)), contextlib.redirect_stdout(self.output):
            with self.assertRaisesRegex(ValueError, 'compiler'):
                nsis.acquire(self.directory)
        self.assertFalse((self.directory / 'nsis-3.11').exists())
        self.assertEqual(list(self.directory.iterdir()), [])

    def test_cli_cannot_override_production_pins(self):
        with patch.object(nsis, 'urlopen') as request, contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as error:
                nsis.main(['--directory', str(self.directory), '--sha256', '0' * 64])
        self.assertEqual(error.exception.code, 2)
        request.assert_not_called()


if __name__ == '__main__':
    unittest.main()
