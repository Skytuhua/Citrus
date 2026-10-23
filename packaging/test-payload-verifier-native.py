"""Read-only Framework verifier contracts in tiny owned temporary directories."""
import ctypes
from ctypes import wintypes
import hashlib
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


@unittest.skipUnless(os.name == 'nt', 'Windows Framework verifier')
class NativeVerifierTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('payload_verifier', ROOT / 'packaging/payload-verifier/build.py')
        cls.builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.builder)

    def fixture(self, parent, names=None):
        root = parent / 'owned ü $ root'
        root.mkdir()
        names = names or {'empty.txt': b'', 'nested/한글 ü $.bin': bytes(range(256)) * 519,
            'last.txt': b'final expected file'}
        for name, content in names.items():
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
        files = {name: hashlib.sha256(content).hexdigest() for name, content in names.items()}
        generated = parent / 'generated'
        generated.mkdir()
        self.builder.prepare(files, generated)
        return root, generated / 'Citrus.PayloadVerifier.exe'

    def run_verifier(self, exe, *args):
        return subprocess.run([str(exe), *map(str, args)], capture_output=True, timeout=30).returncode

    def test_success_empty_unicode_nested_and_unrelated_files_preserved(self):
        with tempfile.TemporaryDirectory(prefix='citrus-verifier-') as temp:
            root, exe = self.fixture(Path(temp))
            unrelated = root / 'user-file.txt'
            unrelated.write_bytes(b'preserve unrelated bytes')
            self.assertEqual(0, self.run_verifier(exe, root))
            self.assertEqual(b'preserve unrelated bytes', unrelated.read_bytes())

    def test_missing_corrupt_malformed_root_and_wrong_argument_count_fail(self):
        with tempfile.TemporaryDirectory(prefix='citrus-verifier-') as temp:
            root, exe = self.fixture(Path(temp))
            for arguments in ((), (root, root), ('relative',), (str(root / '..' / root.name),),
                (str(root) + '.',), (str(root) + ' ',), (root / 'missing',)):
                with self.subTest(arguments=arguments):
                    self.assertNotEqual(0, self.run_verifier(exe, *arguments))
            path = root / 'last.txt'
            path.write_bytes(b'corrupt bytes')
            self.assertNotEqual(0, self.run_verifier(exe, root))
            path.unlink()
            self.assertNotEqual(0, self.run_verifier(exe, root))

    def test_read_failure_fails_and_releases_workers_and_file_handles(self):
        with tempfile.TemporaryDirectory(prefix='citrus-verifier-') as temp:
            root, exe = self.fixture(Path(temp))
            kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
            kernel.CreateFileW.restype = wintypes.HANDLE
            kernel.CloseHandle.argtypes = [wintypes.HANDLE]
            held = kernel.CreateFileW(str(root / 'last.txt'), 0x80000000, 0, None, 3, 0, None)
            self.assertNotEqual(ctypes.c_void_p(-1).value, held)
            try:
                self.assertNotEqual(0, self.run_verifier(exe, root))
            finally:
                kernel.CloseHandle(held)
            self.assertEqual(0, self.run_verifier(exe, root))
            (root / 'last.txt').unlink()

    def test_every_expected_file_is_checked_before_success(self):
        with tempfile.TemporaryDirectory(prefix='citrus-verifier-') as temp:
            names = {'files/%04d.bin' % index: bytes([index % 256]) * (index + 1) for index in range(257)}
            root, exe = self.fixture(Path(temp), names)
            self.assertEqual(0, self.run_verifier(exe, root))
            (root / 'files/0256.bin').write_bytes(b'corrupt last work item')
            self.assertNotEqual(0, self.run_verifier(exe, root))

    def test_owned_paths_longer_than_260_characters_are_verified(self):
        with tempfile.TemporaryDirectory(prefix='citrus-verifier-') as temp:
            name = '/'.join(['nested-' + str(index) + '-' + 'a' * 35 for index in range(7)]) + '/owned.bin'
            root, exe = self.fixture(Path(temp), {name: b'long owned path'})
            self.assertGreater(len(str(root / name)), 260)
            self.assertEqual(0, self.run_verifier(exe, root))
            (root / name).write_bytes(b'corrupt long owned path')
            self.assertNotEqual(0, self.run_verifier(exe, root))

    def test_reparse_root_and_owned_ancestor_fail_without_reading_outside(self):
        with tempfile.TemporaryDirectory(prefix='citrus-verifier-') as temp:
            parent = Path(temp)
            root, exe = self.fixture(parent)
            outside = parent / 'outside'
            outside.mkdir()
            (outside / '한글 ü $.bin').write_bytes(bytes(range(256)) * 519)
            nested = root / 'nested'
            (nested / '한글 ü $.bin').unlink()
            nested.rmdir()
            env = dict(os.environ, CITRUS_VERIFIER_LINK=str(nested), CITRUS_VERIFIER_TARGET=str(outside))
            command = ['powershell', '-NoProfile', '-NonInteractive', '-Command',
                'New-Item -ItemType Junction -Path $env:CITRUS_VERIFIER_LINK -Target $env:CITRUS_VERIFIER_TARGET | Out-Null']
            result = subprocess.run(command, env=env, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            try:
                self.assertNotEqual(0, self.run_verifier(exe, root))
                self.assertNotEqual(0, self.run_verifier(exe, nested))
                self.assertEqual(bytes(range(256)) * 519, (outside / '한글 ü $.bin').read_bytes())
            finally:
                os.rmdir(nested)


if __name__ == '__main__':
    unittest.main()
