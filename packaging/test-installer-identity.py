"""Native regression for reading a short Windows SID at a protected page boundary."""
from pathlib import Path
import os
import shutil
import subprocess
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[1]


@unittest.skipUnless(os.name == 'nt', 'Windows memory protection is required')
class InstallerIdentityTests(unittest.TestCase):
    def test_sid_copy_stops_at_terminator_before_protected_page(self):
        compiler = os.environ.get('MAKENSIS') or shutil.which('makensis')
        if not compiler:
            self.skipTest('Set MAKENSIS to NSIS 3.11 for native installer checks')
        source = (ROOT / 'packaging/Citrus.nsi').read_text()
        copy = [line.strip() for line in source.splitlines()
                if 'System::Call' in line and '$IdentitySidText' in line and 'r3' in line]
        self.assertEqual(len(copy), 1, 'Exercise the actual installer SID-copy instruction')
        work = ROOT / '.local/installer-identity' / uuid.uuid4().hex
        work.mkdir(parents=True)
        executable = work / 'identity.exe'
        script = '\n'.join([
            'Unicode true', '!include "LogicLib.nsh"', 'Name "Citrus identity regression"',
            f'OutFile "{executable}"', 'RequestExecutionLevel user', 'SilentInstall silent',
            'Var IdentitySidText', 'Var ProbeBase', 'Section',
            "System::Call 'kernel32::SetErrorMode(i 2)'",
            "System::Call 'kernel32::VirtualAlloc(p 0, i 8192, i 0x3000, i 4) p.r0'",
            '${If} $0 == 0', 'SetErrorLevel 24', 'Quit', '${EndIf}',
            'StrCpy $ProbeBase $0', 'IntOp $IdentitySidText $ProbeBase + 4080',
            '''System::Call '*$IdentitySidText(&w8 "S-1-5")' ''',
            'IntOp $0 $ProbeBase + 4096',
            "System::Call 'kernel32::VirtualProtect(p r0, i 4096, i 1, *i.r1) i.r2'",
            '${If} $2 == 0', 'SetErrorLevel 24', 'Quit', '${EndIf}',
            copy[0], 'StrCmp $3 "S-1-5" +3', 'SetErrorLevel 25', 'Quit',
            "System::Call 'kernel32::VirtualFree(p $ProbeBase, i 0, i 0x8000)'",
            'SectionEnd'])
        path = work / 'identity.nsi'
        path.write_text(script)
        built = subprocess.run([str(compiler), '/V2', '/NOCD', str(path)], capture_output=True, text=True, timeout=30)
        self.assertEqual(built.returncode, 0, built.stdout + built.stderr)
        result = subprocess.run([str(executable)], capture_output=True, timeout=30)
        self.assertEqual(result.returncode, 0, f'SID read crossed its allocation: exit {result.returncode:#x}')


if __name__ == '__main__':
    unittest.main()
