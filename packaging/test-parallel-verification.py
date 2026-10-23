"""Portable manifest/fallback contracts and Windows deterministic prepared-helper closure."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


package = load('package', ROOT / 'packaging/package.py')
builder = load('payload_verifier', ROOT / 'packaging/payload-verifier/build.py')


class ManifestTests(unittest.TestCase):
    def test_exact_sorted_embedded_entries_include_inventory_and_escape_unicode(self):
        files = {'nested/ü $.bin': 'a' * 64, 'install-inventory.json': 'b' * 64, 'empty.bin': 'c' * 64}
        source = builder.manifest_source(files)
        self.assertEqual(source, builder.manifest_source(dict(reversed(list(files.items())))))
        self.assertLess(source.index('empty.bin'), source.index('install-inventory.json'))
        self.assertLess(source.index('install-inventory.json'), source.index('nested/'))
        self.assertIn('nested/\\u00fc $.bin', source)
        self.assertEqual(1, source.count('"' + files['install-inventory.json'] + '"'))

    def test_bad_names_hashes_and_case_collisions_rejected_before_compile(self):
        for name in ('../outside', '/absolute', 'C:/outside', 'folder\\file', 'a//b', 'a/../b',
            'file:stream', 'trailing.', 'folder /file', 'line\nfile', 'tab\tfile', None, 7):
            with self.subTest(name=name), self.assertRaises(ValueError):
                builder.manifest_source({name: 'a' * 64})
        for digest in ('A' * 64, 'a' * 63, None, True):
            with self.subTest(digest=digest), self.assertRaises(ValueError):
                builder.manifest_source({'owned.bin': digest})
        with self.assertRaises(ValueError):
            builder.manifest_source({'One.bin': 'a' * 64, 'one.bin': 'b' * 64})
        with self.assertRaises(ValueError):
            builder.manifest_source({})

    def test_non_windows_preparation_and_closure_keep_serial_verification(self):
        files = {'owned.bin': 'a' * 64}
        expected = package.verification_instructions(files)
        with patch.object(package.os, 'name', 'posix'), patch.object(package.runpy, 'run_path') as windows:
            self.assertEqual(expected, package.prepare_verification(files, None))
            self.assertEqual(expected, package.verify_prepared_verification(files, None))
            windows.assert_not_called()

    def test_authenticated_helper_launch_is_quoted_and_fail_closed_for_both_prefixes(self):
        text = builder.instructions('a' * 64)
        self.assertIn('Push "$PLUGINSDIR\\Citrus.PayloadVerifier.exe"\nCall ${PREFIX}HashFile', text)
        self.assertLess(text.index('Call ${PREFIX}HashFile'), text.index('ExecWait'))
        self.assertIn('ExecWait \'$\\"$PLUGINSDIR\\Citrus.PayloadVerifier.exe$\\" $\\"$INSTDIR$\\"\' $0', text)
        self.assertIn('${If} ${Errors}\n${OrIf} $0 != 0\nCall ${PREFIX}Fail', text)
        update = (ROOT / 'packaging/update.nsh').read_text()
        for prefix in ('""', '"un."'):
            self.assertIn('!insertmacro VerifyPackage ' + prefix, update)
        template = (ROOT / 'packaging/payload-verifier/Verifier.cs').read_text()
        self.assertIn('Math.Min(4, PayloadManifest.Paths.Length)', template)
        self.assertIn('finally\n            {\n                for (int index = 0; index < started; index++) workers[index].Join();', template)
        self.assertIn('failed == 0 && completed == PayloadManifest.Paths.Length ? 0 : 23', template)
        self.assertEqual(2, template.count('CheckOpenedPath(stream, path, pathBuffer);'))
        self.assertIn('GetFinalPathNameByHandle', template)
        self.assertIn('length == 0 || length >= buffer.Capacity', template)
        self.assertIn('File.GetAttributes(ExtendedPath(path))', template)
        self.assertIn('new FileStream(ExtendedPath(path)', template)
        self.assertNotIn('ReadAllText', template)
        self.assertNotIn('Process.Start', template)

    def test_compiler_uses_exact_existing_sdk_framework_reference_and_determinism(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            version = json.loads((ROOT / 'global.json').read_bytes())['sdk']['version']
            with patch.dict(os.environ, WINDIR=str(directory)), patch.object(Path, 'is_file', return_value=True), \
                patch.object(builder.subprocess, 'check_output', return_value=version + ' [' + str(directory) + ']\n'), \
                patch.object(builder.subprocess, 'run') as run:
                builder.compile_helper(directory / 'Citrus.PayloadVerifier.exe', directory / 'PayloadManifest.cs', 'existing-dotnet')
            command = run.call_args.args[0]
            self.assertEqual('existing-dotnet', command[0])
            self.assertEqual(str(directory / version / 'Roslyn/bincore/csc.dll'), command[1])
            for option in ('/deterministic+', '/nostdlib+', '/target:winexe', '/platform:x86'):
                self.assertIn(option, command)
            self.assertIn('/reference:' + str(directory / 'Microsoft.NET/Framework64/v4.0.30319/mscorlib.dll'), command)


@unittest.skipUnless(os.name == 'nt', 'Windows deterministic Framework compiler witness')
class PreparedHelperTests(unittest.TestCase):
    def fixture(self, parent):
        payload, generated = parent / 'payload', parent / 'generated'
        payload.mkdir(); generated.mkdir()
        (payload / 'owned.txt').write_bytes(b'exact prepared bytes')
        files = package.inventory(payload)
        (payload / 'install-inventory.json').write_text(json.dumps(files, indent=2) + '\n', encoding='utf-8')
        files['install-inventory.json'] = package.sha(payload / 'install-inventory.json')
        for name, text in zip(('install.nsh', 'uninstall.nsh', 'paths.nsh'), package.instructions(files, payload.resolve())):
            (generated / name).write_text(text, encoding='utf-8')
        for name, text in (('verify.nsh', package.prepare_verification(files, generated)),
            ('identity.nsh', '!define INVENTORY_HASH "' + files['install-inventory.json'] + '"\n'),
            ('version.nsh', package.version_include())):
            (generated / name).write_text(text, encoding='utf-8')
        return payload, generated, files

    def test_independent_directory_witness_is_byte_identical(self):
        with tempfile.TemporaryDirectory(prefix='citrus-verifier-closure-') as temp:
            payload, generated, files = self.fixture(Path(temp))
            package.verify_prepared(payload, generated)
            other = Path(temp) / 'fresh-witness'
            builder.prepare(files, other)
            self.assertEqual((generated / builder.EXECUTABLE).read_bytes(), (other / builder.EXECUTABLE).read_bytes())

    def test_noncanonical_payload_path_uses_canonical_ownership(self):
        with tempfile.TemporaryDirectory(prefix='citrus-verifier-alias-') as temp:
            alias = Path(temp) / 'parent'
            alias.mkdir()
            payload, generated, _ = self.fixture(alias / '..')
            self.assertNotEqual(payload, payload.resolve())
            package.verify_prepared(payload, generated)

    def test_changed_source_executable_include_inventory_and_payload_are_rejected(self):
        with tempfile.TemporaryDirectory(prefix='citrus-verifier-closure-') as temp:
            payload, generated, _ = self.fixture(Path(temp))
            for path in (generated / builder.MANIFEST, generated / builder.EXECUTABLE, generated / 'verify.nsh',
                generated / 'identity.nsh', payload / 'owned.txt', payload / 'install-inventory.json'):
                original = path.read_bytes()
                path.write_bytes(original + b' ')
                try:
                    with self.subTest(path=path.name), self.assertRaises(ValueError):
                        package.verify_prepared(payload, generated)
                finally:
                    path.write_bytes(original)

    def test_helper_and_generated_digest_cannot_be_replaced_together(self):
        with tempfile.TemporaryDirectory(prefix='citrus-verifier-closure-') as temp:
            payload, generated, _ = self.fixture(Path(temp))
            executable, include = generated / builder.EXECUTABLE, generated / 'verify.nsh'
            old = package.sha(executable)
            executable.write_bytes(executable.read_bytes() + b'coordinated untrusted replacement')
            include.write_text(include.read_text().replace(old, package.sha(executable)), encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'compiler witness'):
                package.verify_prepared(payload, generated)


if __name__ == '__main__':
    unittest.main()
