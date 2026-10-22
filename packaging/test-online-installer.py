"""Compile and exercise the downloader with tiny fixtures; never installs Citrus."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'packaging/online-installer'


class OnlineInstallerTests(unittest.TestCase):
    def test_downloader_contracts(self):
        self.assertTrue((SOURCE / 'Installer.cs').is_file(), 'Online installer implementation is missing')
        spec = importlib.util.spec_from_file_location('online_build', SOURCE / 'build.py')
        build = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(build)
        (ROOT / '.local').mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / '.local') as directory:
            destination = Path(directory)
            constants = destination / 'Release.cs'
            constants.write_text(build.constants('0.3.0-preview.2', 3, 'a' * 64), encoding='utf-8')
            output = destination / 'contracts.exe'
            build.compile_installer(output, constants, test=True)
            result = subprocess.run([str(output)], capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn('Downloader contracts passed', result.stdout)

    def test_release_metadata_rejects_untrusted_or_invalid_payload(self):
        self.assertTrue((SOURCE / 'build.py').is_file(), 'Online installer builder is missing')
        spec = importlib.util.spec_from_file_location('online_build', SOURCE / 'build.py')
        build = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(build)
        valid = {'product': 'Citrus', 'platform': 'Windows x64', 'version': '0.3.0-preview.2',
                 'sourceCommit': 'b' * 40,
                 'installer': {'file': 'Citrus-Setup.exe', 'bytes': 3, 'sha256': 'a' * 64,
                               'fileVersion': '0.3.0-preview.2'}}
        self.assertEqual(build.validate_manifest(valid), valid)
        for field, value in [('file', '../evil.exe'), ('bytes', True), ('bytes', 0),
                             ('sha256', 'invalid'), ('fileVersion', '0.0.0')]:
            broken = json.loads(json.dumps(valid))
            broken['installer'][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                build.validate_manifest(broken)
        for field, value in [('version', 'v0.3.0-preview.2'), ('sourceCommit', 'x' * 40),
                             ('product', 'Other'), ('platform', 'Linux')]:
            broken = {**valid, field: value}
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                build.validate_manifest(broken)


if __name__ == '__main__':
    unittest.main()
