#!/usr/bin/env python3
"""Portable, source-driven install-order/fault contract checks; NOT NSIS execution.

Exercise actual filesystem writes in the order emitted by Citrus.nsi, substituting
an in-memory registry with an explicit flush boundary and uninstaller bytes.
The separate Windows harness executes the real installer and recovery flow.
"""
import importlib.util
from pathlib import Path, PureWindowsPath
import re
import shlex
import shutil
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('package', ROOT / 'packaging/package.py')
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)
NSIS = (ROOT / 'packaging/Citrus.nsi').read_text()
IDENTITY = 'fixture-inventory-hash'


def mutations(payload):
    """Read real top-level mutation ordering, expanding ordinary helper calls/includes."""
    main = NSIS.split('Section "Citrus (required)" MainSection\n', 1)[1].split('SectionEnd', 1)[0]
    files = package.inventory(payload)
    install, _, _ = package.instructions(files, payload)
    def expand(text):
        for line in text.splitlines():
            line = line.strip()
            if line == '!include "${GENERATED}\\install.nsh"':
                yield from expand(install)
            elif line.startswith('Call '):
                name = line[5:]
                if name == 'FlushOwnership': yield ('flush',)
                else:
                    match = re.search(r'^Function ' + re.escape(name) + r'\n(.*?)^FunctionEnd', NSIS, re.M | re.S)
                    if match: yield from expand(match.group(1))
            elif line.startswith('WriteRegStr HKCU "${CITRUS_REGKEY}" '):
                parts = shlex.split(line, posix=False)
                yield ('registry', parts[3].strip('"'), parts[4].strip('"'))
            elif line.startswith(('SetOutPath "$INSTDIR', 'CreateDirectory "$INSTDIR')):
                yield ('directory', shlex.split(line, posix=False)[1].strip('"'))
            elif line.startswith('WriteUninstaller '):
                yield ('uninstaller', shlex.split(line, posix=False)[1].strip('"'))
            elif line.startswith('File '):
                yield ('file', shlex.split(line, posix=False)[1].strip('"'))
    return list(expand(main))


class Attempt:
    def __init__(self, root):
        self.root = root
        self.registry = {}
        self.durable = {}
        self.output = root

    def value(self, value):
        return value.replace('$INSTDIR', str(self.root)).replace('${INVENTORY_HASH}', IDENTITY)

    def path(self, value):
        # Interpret NSIS path separators only at filesystem boundaries. Registry
        # receipts/locations must retain the native root spelling used by NSIS.
        return Path(self.value(value).replace('\\', '/'))

    def apply(self, operation):
        kind, *args = operation
        if kind == 'registry': self.registry[args[0]] = self.value(args[1])
        elif kind == 'flush': self.durable = dict(self.registry)
        elif kind == 'directory':
            self.output = self.path(args[0]); self.output.mkdir(parents=True, exist_ok=True)
        elif kind == 'uninstaller': self.path(args[0]).write_bytes(b'embedded-uninstaller-placeholder')
        elif kind == 'file': shutil.copyfile(args[0], self.output / Path(args[0]).name)

    def assert_recoverable(self, test):
        if self.root.exists():
            test.assertEqual(str(self.root) + '|' + IDENTITY, self.durable.get('RecoveryIdentity'),
                             'An existing first-install tree must have a flushed matching receipt')


class RecoveryContractTests(unittest.TestCase):
    def fixture(self, parent):
        payload = parent / 'payload'; payload.mkdir()
        (payload / 'a.dll').write_bytes(b'first owned payload')
        (payload / 'media').mkdir(); (payload / 'media/b.dll').write_bytes(b'second owned payload')
        return payload, mutations(payload)

    def test_every_bootstrap_interruption_has_durable_recovery_before_tree_creation(self):
        # Registry values preserve the literal Windows root, even on a Linux host.
        # Only registry mutations run with PureWindowsPath; these are string checks.
        for root in [PureWindowsPath("C:/Users/runner name/AppData/Local/Programs/Citrus"),
                     PureWindowsPath("//server/share/Citrus")]:
            with self.subTest(windows_root=str(root)):
                attempt = Attempt(root)
                attempt.apply(('registry', 'RecoveryIdentity', '$INSTDIR|${INVENTORY_HASH}'))
                attempt.apply(('registry', 'InstallLocation', '$INSTDIR'))
                attempt.apply(('flush',))
                self.assertEqual(str(root) + '|' + IDENTITY, attempt.durable['RecoveryIdentity'])
                self.assertEqual(str(root), attempt.durable['InstallLocation'])
                self.assertEqual(root.as_posix() + '/media/output.mp4',
                                 attempt.path('$INSTDIR\\media\\output.mp4').as_posix())
        with tempfile.TemporaryDirectory() as temp:
            parent = Path(temp); _, steps = self.fixture(parent)
            first_file = next(i for i, step in enumerate(steps) if step[0] == 'file')
            for boundary in range(first_file + 1):
                with self.subTest(boundary=boundary):
                    attempt = Attempt(parent / ('install-' + str(boundary)))
                    for operation in steps[:boundary + 1]: attempt.apply(operation)
                    attempt.assert_recoverable(self)
                    if boundary == first_file:
                        self.assertTrue((attempt.root / 'Uninstall.exe').is_file())
                        self.assertEqual(IDENTITY, attempt.durable.get('PackageInventory'))
                        self.assertEqual(str(attempt.root), attempt.durable.get('InstallLocation'))

    def test_early_payload_fault_then_retry_and_owned_cleanup_preserve_unknown_outputs(self):
        with tempfile.TemporaryDirectory() as temp:
            parent = Path(temp); payload, steps = self.fixture(parent)
            attempt = Attempt(parent / 'install')
            # Inject an interruption immediately after the actual first File statement.
            for operation in steps:
                attempt.apply(operation)
                if operation[0] == 'file': break
            attempt.assert_recoverable(self)
            self.assertTrue((attempt.root / 'Uninstall.exe').is_file())
            (attempt.root / 'notes.txt').write_bytes(b'user output')
            (attempt.root / 'media').mkdir(exist_ok=True)
            (attempt.root / 'media/converted.mp4').write_bytes(b'user media')
            # Same embedded recipe retries with its durable receipt; no arbitrary scan/delete.
            for operation in steps: attempt.apply(operation)
            for name, digest in package.inventory(payload).items():
                self.assertEqual(digest, package.sha(attempt.root / name))
            _, deletion, _ = package.instructions(package.inventory(payload), payload)
            for line in deletion.splitlines():
                if line.startswith('Delete '): attempt.path(shlex.split(line, posix=False)[1].strip('"')).unlink(missing_ok=True)
                elif line.startswith('RMDir '):
                    try: attempt.path(shlex.split(line, posix=False)[1].strip('"')).rmdir()
                    except OSError: pass
            self.assertEqual(b'user output', (attempt.root / 'notes.txt').read_bytes())
            self.assertEqual(b'user media', (attempt.root / 'media/converted.mp4').read_bytes())


if __name__ == '__main__': unittest.main()
