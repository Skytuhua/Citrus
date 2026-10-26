#!/usr/bin/env python3
"""Pinned byte materialization and Windows destination-inheritance contracts."""
import ctypes
from ctypes import wintypes
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('bootstrap_engines', Path(__file__).with_name('bootstrap-engines.py'))
bootstrap = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bootstrap)


class MaterializationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='citrus-engine-materialization-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / 'source.bin'
        self.destination = self.root / 'destination.bin'

    def test_exact_bytes_are_copied_and_verified_source_is_removed(self):
        payload = bytes(range(256)) * 8193 + b'final pinned bytes'
        self.source.write_bytes(payload)

        bootstrap.materialize(self.source, self.destination)

        self.assertEqual(self.destination.read_bytes(), payload)
        self.assertFalse(self.source.exists())

    def test_preexisting_destination_is_never_overwritten(self):
        self.source.write_bytes(b'pinned source')
        self.destination.write_bytes(b'existing developer file')

        with self.assertRaises(FileExistsError):
            bootstrap.materialize(self.source, self.destination)

        self.assertEqual(self.destination.read_bytes(), b'existing developer file')
        self.assertEqual(self.source.read_bytes(), b'pinned source')

    def test_failed_copy_removes_its_partial_file_and_preserves_source(self):
        self.source.write_bytes(b'pinned source')

        def interrupted(incoming, outgoing, length):
            outgoing.write(incoming.read(3))
            raise OSError('injected copy failure')

        with patch.object(bootstrap.shutil, 'copyfileobj', side_effect=interrupted):
            with self.assertRaisesRegex(OSError, 'injected copy failure'):
                bootstrap.materialize(self.source, self.destination)

        self.assertFalse(self.destination.exists())
        self.assertEqual(self.source.read_bytes(), b'pinned source')

    def test_copy_uses_a_one_mebibyte_bounded_buffer(self):
        payload = b'p' * (2 * 1024 * 1024 + 1)
        self.source.write_bytes(payload)
        requests = []
        original = Path.open

        class RecordingReader:
            def __init__(self, stream): self.stream = stream
            def __enter__(self): return self
            def __exit__(self, *args): return self.stream.__exit__(*args)
            def read(self, size=-1):
                requests.append(size)
                return self.stream.read(size)

        def opened(path, mode='r', *args, **kwargs):
            stream = original(path, mode, *args, **kwargs)
            return RecordingReader(stream) if path == self.source and mode == 'rb' else stream

        with patch.object(Path, 'open', opened):
            bootstrap.materialize(self.source, self.destination)
        self.assertGreater(len(requests), 2)
        self.assertTrue(all(0 < size <= 1024 * 1024 for size in requests))
        self.assertEqual(self.destination.read_bytes(), payload)

    @unittest.skipUnless(os.name == 'nt', 'Windows file security descriptors are required')
    def test_protected_extraction_file_inherits_destination_acl_instead_of_preserving_protection(self):
        security = WindowsSecurity()
        source_directory = self.root / 'protected-extraction'
        destination_directory = self.root / 'intended-bundle'
        source_directory.mkdir()
        destination_directory.mkdir()
        self.source = source_directory / 'pinned.exe'
        self.destination = destination_directory / 'pinned.exe'
        self.source.write_bytes(b'pinned executable fixture; never executed')
        # Match the protected owner/SYSTEM/Admin lineage seen in the real engine probe.
        security.set(self.source, 'D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;OW)', protected=True)
        security.set(destination_directory, 'D:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;OW)(A;OICI;GRGX;;;BU)', protected=False)
        self.assertTrue(security.protected(self.source))
        self.assertNotIn(';;;BU)', security.descriptor(self.source))
        self.assertFalse(security.protected(destination_directory))

        bootstrap.materialize(self.source, self.destination)

        self.assertFalse(security.protected(self.destination))
        self.assertIn(';;;BU)', security.descriptor(self.destination))
        self.assertEqual(self.destination.read_bytes(), b'pinned executable fixture; never executed')
        self.assertFalse(self.source.exists())


class WindowsSecurity:
    """Test-only Win32 ACL operations; production never edits existing ACLs."""
    def __init__(self):
        self.api = ctypes.WinDLL('advapi32', use_last_error=True)
        self.kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        self.api.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(wintypes.DWORD)]
        self.api.ConvertStringSecurityDescriptorToSecurityDescriptorW.restype = wintypes.BOOL
        self.api.SetFileSecurityW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, ctypes.c_void_p]
        self.api.SetFileSecurityW.restype = wintypes.BOOL
        self.api.GetFileSecurityW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
        self.api.GetFileSecurityW.restype = wintypes.BOOL
        self.api.GetSecurityDescriptorControl.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.WORD), ctypes.POINTER(wintypes.DWORD)]
        self.api.GetSecurityDescriptorControl.restype = wintypes.BOOL
        self.api.ConvertSecurityDescriptorToStringSecurityDescriptorW.argtypes = [ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(wintypes.DWORD)]
        self.api.ConvertSecurityDescriptorToStringSecurityDescriptorW.restype = wintypes.BOOL
        self.kernel.LocalFree.argtypes = [ctypes.c_void_p]
        self.kernel.LocalFree.restype = ctypes.c_void_p

    @staticmethod
    def checked(result):
        if not result:
            raise ctypes.WinError(ctypes.get_last_error())

    def set(self, path, sddl, protected):
        descriptor = ctypes.c_void_p()
        self.checked(self.api.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, ctypes.byref(descriptor), None))
        try:
            self.checked(self.api.SetFileSecurityW(str(path), 4 | (0x80000000 if protected else 0x20000000), descriptor))
        finally:
            self.kernel.LocalFree(descriptor)

    def read(self, path):
        needed = wintypes.DWORD()
        if self.api.GetFileSecurityW(str(path), 4, None, 0, ctypes.byref(needed)) or ctypes.get_last_error() != 122:
            raise RuntimeError('Expected a required security-descriptor size')
        descriptor = ctypes.create_string_buffer(needed.value)
        self.checked(self.api.GetFileSecurityW(str(path), 4, descriptor, len(descriptor), ctypes.byref(needed)))
        return descriptor

    def protected(self, path):
        descriptor = self.read(path)
        control, revision = wintypes.WORD(), wintypes.DWORD()
        self.checked(self.api.GetSecurityDescriptorControl(descriptor, ctypes.byref(control), ctypes.byref(revision)))
        return bool(control.value & 0x1000)

    def descriptor(self, path):
        descriptor, text = self.read(path), ctypes.c_void_p()
        self.checked(self.api.ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, 1, 4, ctypes.byref(text), None))
        try:
            return ctypes.wstring_at(text)
        finally:
            self.kernel.LocalFree(text)


if __name__ == '__main__':
    unittest.main()
