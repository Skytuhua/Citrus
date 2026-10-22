"""Exercise the publish deletion guard without running a build."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


@unittest.skipUnless(os.name == 'nt', 'Windows junction regression')
class PublishPathTests(unittest.TestCase):
    def test_linked_distribution_ancestor_cannot_delete_external_payload(self):
        self.check_link_rejected(target_exists=True)

    def test_dangling_distribution_link_is_also_rejected(self):
        self.check_link_rejected(target_exists=False)

    def check_link_rejected(self, target_exists):
        with tempfile.TemporaryDirectory(prefix='citrus-publish-guard-') as temporary:
            fixture = Path(temporary)
            repo, outside = fixture / 'repo', fixture / 'outside'
            (repo / 'packaging').mkdir(parents=True)
            payload = outside / 'Citrus-win-x64'
            payload.mkdir(parents=True)
            sentinel = payload / 'user-data.txt'
            sentinel.write_text('preserve external data')
            shutil.copyfile(ROOT / 'packaging/publish.ps1', repo / 'packaging/publish.ps1')
            link = repo / 'dist'
            environment = dict(os.environ, CITRUS_GUARD_LINK=str(link), CITRUS_GUARD_TARGET=str(outside))
            result = subprocess.run(['powershell', '-NoProfile', '-Command',
                'New-Item -ItemType Junction -Path $env:CITRUS_GUARD_LINK -Target $env:CITRUS_GUARD_TARGET | Out-Null'],
                env=environment, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            if not target_exists:
                sentinel.unlink()
                payload.rmdir()
                outside.rmdir()
            try:
                result = subprocess.run(['powershell', '-NoProfile', '-File',
                    str(repo / 'packaging/publish.ps1'), '-Phase', 'Payload'],
                    capture_output=True, text=True)
                self.assertNotEqual(0, result.returncode)
                self.assertIn('Refusing linked output path', result.stderr)
                if target_exists: self.assertEqual('preserve external data', sentinel.read_text())
                else: self.assertFalse(outside.exists())
            finally:
                # Remove the junction itself; never recurse through the target.
                self.assertEqual(link.parent.resolve(), repo.resolve())
                os.rmdir(link)


if __name__ == '__main__':
    unittest.main()
