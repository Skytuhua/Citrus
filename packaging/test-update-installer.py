"""Native tiny-payload updater integration; never targets the production installation."""
import ctypes
from ctypes import wintypes
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
import uuid
import xml.etree.ElementTree as ET
import winreg

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('package', ROOT / 'packaging/package.py')
package = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(package)
METADATA_SPEC=importlib.util.spec_from_file_location('release_metadata',ROOT/'packaging/release-metadata.py')
metadata=importlib.util.module_from_spec(METADATA_SPEC);METADATA_SPEC.loader.exec_module(metadata)

class InstallerUpdateTests(unittest.TestCase):
    retired = {'source/test-source.tar.gz': b'old source archive', 'Citrus.Windows.pdb': b'old debug symbols'}

    @classmethod
    def setUpClass(cls):
        source = (ROOT / 'packaging/Citrus.nsi').read_text()
        # This assertion runs before compilation/execution: an old installer must
        # never be allowed to silently redirect a fixture into the real app tree.
        if 'CITRUS_TEST_FIXTURE' not in source:
            raise AssertionError('Missing isolated updater fixture support; no installer executed')
        cls.identity = uuid.uuid4().hex
        cls.name = 'Citrus-UpdateFixture-' + cls.identity
        cls.install = Path(os.environ['LOCALAPPDATA']) / 'Programs' / cls.name
        cls.reg = 'Software\\' + cls.name
        cls.work = ROOT / '.local/update-installer' / cls.identity
        cls.work.mkdir(parents=True)
        if cls.install.exists(): raise AssertionError('Fixture destination already exists')
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, cls.reg, 0, winreg.KEY_READ | winreg.KEY_WOW64_64KEY):
                raise AssertionError('Fixture registry already exists')
        except FileNotFoundError: pass
        compiler = os.environ.get('MAKENSIS') or ROOT / '.local/launch-release-20261021/toolchain/nsis-3.11/makensis.exe'
        cls.hashes = {}
        cls.version = ET.parse(ROOT/'CitrusVersion.props').getroot().findtext('./PropertyGroup/Version')
        for version, defines in [('old', []), ('new', []), ('newer', []), ('corrupt', ['/DCITRUS_TEST_CORRUPT_BEFORE_VERIFY']), ('rollback', ['/DCITRUS_TEST_ROLLBACK_AFTER_FIRST_FILE']), ('interrupted', ['/DCITRUS_TEST_FAIL_AFTER_FIRST_FILE'])]:
            payload = cls.work / version
            generated = cls.work / (version + '-generated')
            payload.mkdir(); generated.mkdir()
            (payload / 'Citrus.Windows.exe').write_text('old app' if version == 'old' else 'new app')
            (payload / 'owned.txt').write_text('old payload' if version == 'old' else 'third payload' if version == 'newer' else 'new payload')
            (payload / 'chunks.bin').write_bytes(bytes(range(256))*300)
            if version == 'old':
                for name, contents in cls.retired.items():
                    (payload / name).parent.mkdir(parents=True, exist_ok=True)
                    (payload / name).write_bytes(contents)
            files = package.inventory(payload)
            (payload / 'install-inventory.json').write_text(json.dumps(files, indent=2) + '\n')
            files['install-inventory.json'] = package.sha(payload / 'install-inventory.json')
            cls.hashes[version] = files['install-inventory.json']
            install, delete, checks = package.instructions(files, payload.resolve())
            for name, content in [('install.nsh', install), ('uninstall.nsh', delete), ('paths.nsh', checks)]:
                (generated / name).write_text(content, encoding='utf-8')
            (generated / 'verify.nsh').write_text(''.join('Push \"$INSTDIR\\' + name.replace('/', '\\') + '\"\nCall HashFile\nPop $0\nStrCmp $0 \"' + digest + '\" +2\nCall Fail\n' for name, digest in files.items()))
            (generated / 'identity.nsh').write_text('!define INVENTORY_HASH "' + files['install-inventory.json'] + '"\n')
            sdk = package.sha(ROOT / 'packaging/notices/presentation/Windows-SDK-License.rtf')
            (generated / 'version.nsh').write_text(package.version_include())
            cmd = [str(compiler), '/V2', '/NOCD', '/DGENERATED=' + str(generated), '/DOUTPUT=' + str(cls.work / (version + '.exe')), '/DCITRUS_TEST_FIXTURE=' + cls.identity] + defines + [str(ROOT / 'packaging/Citrus.nsi')]
            result = subprocess.run(cmd, capture_output=True, text=True)
            if result.returncode: raise AssertionError(result.stdout + result.stderr)
            if metadata.installer_file_version(cls.work / (version+'.exe'))!=cls.version:
                raise AssertionError('Compiled installer FileVersion resource differs from central version')

    def regvalue(self, name):
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, self.reg, 0, winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as key:
            return winreg.QueryValueEx(key, name)[0]

    def run_setup(self, version, *args, expected=0):
        result = subprocess.run([str(self.work / (version + '.exe')), '/S', *args], capture_output=True, timeout=25)
        self.assertEqual(result.returncode, expected, (version, args, result.stdout, result.stderr))

    def initialize(self):
        self.run_setup('old', '/ACCEPTSDKTERMS=1')
        self.assertEqual((self.install / 'owned.txt').read_text(), 'old payload')
        for name, contents in self.retired.items():
            self.assertTrue((self.install / name).is_file(), name)
            self.assertEqual((self.install / name).read_bytes(), contents)
        (self.install / 'user-keep.txt').write_text('user data')

    def parent_args(self, inventory="old"):
        process = subprocess.Popen([sys.executable, '-c', 'import time;time.sleep(0.6)'])
        self.addCleanup(lambda: process.wait(timeout=5))
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.OpenProcess.restype = wintypes.HANDLE
        handle = kernel.OpenProcess(0x1000, False, process.pid)
        creation, exit_time, kernel_time, user_time = [wintypes.FILETIME() for _ in range(4)]
        if not kernel.GetProcessTimes(wintypes.HANDLE(handle), ctypes.byref(creation), ctypes.byref(exit_time), ctypes.byref(kernel_time), ctypes.byref(user_time)):
            raise ctypes.WinError(ctypes.get_last_error())
        kernel.CloseHandle(wintypes.HANDLE(handle))
        value = (creation.dwHighDateTime << 32) | creation.dwLowDateTime
        return process, ['/AUTOUPDATE=1', '/UPDATEPID=' + str(process.pid), '/UPDATESTART=' + str(value), '/PREVIOUSINVENTORY=' + self.hashes[inventory]]

    def test_changed_inventory_upgrade_preserves_user_files_and_journals_backup(self):
        self.initialize()
        parent, args = self.parent_args()
        self.run_setup('new', *args)
        parent.wait()
        self.assertEqual((self.install / 'owned.txt').read_text(), 'new payload')
        self.assertEqual(self.regvalue('PackageInventory'), self.hashes['new'])
        self.assertEqual(self.regvalue('UpdateState'), 'Committed')
        backup = Path(self.regvalue('UpdateBackupPath'))
        self.assertEqual((backup / 'owned.txt').read_text(), 'old payload')
        self.assertEqual((backup / 'user-keep.txt').read_text(), 'user data')
        inventory = json.loads((self.install / 'install-inventory.json').read_text())
        for name, contents in self.retired.items():
            self.assertFalse((self.install / name).exists(), name)
            self.assertNotIn(name, inventory)
            self.assertEqual((backup / name).read_bytes(), contents)

    def test_wrong_parent_start_or_inventory_never_changes_install(self):
        self.initialize()
        parent, args = self.parent_args()
        args[2] = '/UPDATESTART=1'
        self.run_setup('new', *args, expected=23)
        parent.wait()
        self.assertEqual((self.install / 'owned.txt').read_text(), 'old payload')
        parent, args = self.parent_args()
        args[3] = '/PREVIOUSINVENTORY=' + '0' * 64
        self.run_setup('new', *args, expected=23)
        parent.wait()
        self.assertEqual(self.regvalue('PackageInventory'), self.hashes['old'])

    def test_failure_after_payload_write_restores_previous_tree_and_receipt(self):
        self.initialize()
        parent, args = self.parent_args()
        self.run_setup('rollback', *args, expected=23)
        parent.wait()
        self.assertEqual((self.install / 'owned.txt').read_text(), 'old payload')
        self.assertEqual((self.install / 'user-keep.txt').read_text(), 'user data')
        self.assertEqual(self.regvalue('PackageInventory'), self.hashes['old'])
        self.assertEqual(self.regvalue('RecoveryIdentity'), str(self.install) + '|' + self.hashes['old'])
        self.assertEqual(self.regvalue('UpdateTestRestarted'), 1)
        for name, contents in self.retired.items():
            self.assertEqual((self.install / name).read_bytes(), contents)

    def test_installed_byte_corruption_rolls_back_before_commit(self):
        self.initialize()
        parent, args = self.parent_args()
        self.run_setup('corrupt', *args, expected=23); parent.wait()
        self.assertEqual((self.install / 'owned.txt').read_text(), 'old payload')
        self.assertEqual((self.install / 'chunks.bin').read_bytes(), bytes(range(256))*300)
        self.assertEqual(self.regvalue('PackageInventory'), self.hashes['old'])
        self.assertEqual(self.regvalue('UpdateTestRestarted'), 1)

    def test_interrupted_upgrade_is_recovered_before_retry(self):
        self.initialize()
        parent, args = self.parent_args()
        self.run_setup('interrupted', *args, expected=25)
        parent.wait()
        self.assertEqual(self.regvalue('UpdateState'), 'Installing')
        parent, args = self.parent_args()
        self.run_setup('new', *args)
        parent.wait()
        self.assertEqual((self.install / 'owned.txt').read_text(), 'new payload')
        self.assertEqual(self.regvalue('UpdateState'), 'Committed')
        backup = Path(self.regvalue('UpdateBackupPath'))
        self.assertEqual((backup / 'user-keep.txt').read_text(), 'user data')
        for name, contents in self.retired.items():
            self.assertFalse((self.install / name).exists(), name)
            self.assertEqual((backup / name).read_bytes(), contents)

    def test_interrupted_rollback_after_restore_finishes_registration_before_retry(self):
        self.initialize()
        parent, args = self.parent_args()
        self.run_setup('interrupted', *args, expected=25); parent.wait()
        backup = Path(self.regvalue('UpdateBackupPath'))
        failed = self.install.with_name(self.name + '.failed-' + self.hashes['new'] + '-0')
        self.assertEqual(backup, self.install.with_name(self.name + '.previous-' + self.hashes['old']))
        for path in [self.install, backup, failed]:
            self.assertEqual(path.parent.resolve(), self.install.parent.resolve())
            self.assertTrue(path.name.startswith(self.name))
            self.assertFalse(path.is_symlink())
        # Model power loss after the rollback's restore rename, before its registry writes.
        self.install.rename(failed)
        backup.rename(self.install)
        self.assertEqual(self.regvalue('UpdateState'), 'Installing')
        self.assertEqual(self.regvalue('PackageInventory'), self.hashes['new'])
        self.assertFalse(backup.exists())
        parent, args = self.parent_args()
        self.run_setup('new', *args); parent.wait()
        self.assertEqual((self.install / 'owned.txt').read_text(), 'new payload')
        self.assertEqual(self.regvalue('UpdateState'), 'Committed')
        self.assertEqual((Path(self.regvalue('UpdateBackupPath')) / 'user-keep.txt').read_text(), 'user data')
        self.assertEqual((failed / 'Citrus.Windows.exe').read_text(), 'new app')

    def test_second_changed_update_after_boot_confirmation(self):
        self.initialize()
        parent, args = self.parent_args()
        self.run_setup('new', *args); parent.wait()
        # Fake payload has no runnable WPF application. Model only its successful
        # boot acknowledgement; managed cleanup has independent integration tests.
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, self.reg, 0, winreg.KEY_SET_VALUE | winreg.KEY_WOW64_64KEY) as key:
            for name in ['UpdateState','UpdateBackupPath','UpdatePreviousInventory','UpdateInstalledInventory','UpdatePreviousSdkLicense','UpdatePreviousVersion']:
                winreg.DeleteValue(key, name)
        parent, args = self.parent_args('new')
        self.run_setup('newer', *args); parent.wait()
        self.assertEqual((self.install / 'owned.txt').read_text(), 'third payload')
        self.assertEqual(self.regvalue('PackageInventory'), self.hashes['newer'])

    def test_repeated_failed_update_retains_old_install_and_both_failed_trees(self):
        self.initialize()
        for _ in range(2):
            parent, args = self.parent_args()
            self.run_setup('rollback', *args, expected=23); parent.wait()
            self.assertEqual((self.install / 'owned.txt').read_text(), 'old payload')
            self.assertEqual(self.regvalue('PackageInventory'), self.hashes['old'])
        self.assertEqual(len(list(self.install.parent.glob(self.name + '.failed-*'))), 2)

    def test_changed_sdk_terms_cannot_be_silently_accepted_by_automatic_flag(self):
        self.initialize()
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, self.reg, 0, winreg.KEY_SET_VALUE | winreg.KEY_WOW64_64KEY) as key:
            winreg.SetValueEx(key, 'AcceptedSdkLicenseSha256', 0, winreg.REG_SZ, '0'*64)
        parent, args = self.parent_args()
        self.run_setup('new', *args, '/ACCEPTSDKTERMS=1', expected=26); parent.wait()
        self.assertEqual((self.install / 'owned.txt').read_text(), 'old payload')
        self.assertEqual(self.regvalue('PackageInventory'), self.hashes['old'])
        self.assertEqual(self.regvalue('UpdateTestRestarted'), 1)

    @staticmethod
    def remove_fixture_tree(directory):
        # Explorer/Defender can briefly retain a newly created .lnk or executable
        # after setup exits. Retry only this already-validated fixture tree.
        deadline=time.monotonic()+5
        while directory.exists():
            try:
                shutil.rmtree(directory)
            except PermissionError:
                if time.monotonic()>=deadline:raise
                time.sleep(0.1)

    def tearDown(self):
        # Fixture-only exact paths, verified before recursive removal. Production
        # Citrus and user settings directories are never enumerated or removed.
        programs = Path(os.environ['LOCALAPPDATA']) / 'Programs'
        for candidate in programs.glob(self.name + '*'):
            if candidate.parent.resolve() != programs.resolve() or not candidate.name.startswith(self.name):
                raise AssertionError('Unsafe fixture cleanup target')
            if candidate.is_symlink(): raise AssertionError('Linked fixture cleanup target')
            self.remove_fixture_tree(candidate)
        for key_name in [self.reg, 'Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\' + self.name]:
            try: winreg.DeleteKeyEx(winreg.HKEY_CURRENT_USER, key_name, winreg.KEY_WOW64_64KEY)
            except FileNotFoundError: pass
        shortcuts = Path(os.environ['APPDATA']) / 'Microsoft/Windows/Start Menu/Programs' / self.name
        if shortcuts.exists() and shortcuts.name == self.name: self.remove_fixture_tree(shortcuts)

if __name__ == '__main__': unittest.main()
