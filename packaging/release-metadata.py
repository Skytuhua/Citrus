#!/usr/bin/env python3
"""Build public release metadata from final bytes and central version properties."""
import hashlib
import argparse
import re
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[1]

def approved_thumbprints(value):
    """Require exact certificate identities obtained from Foundation onboarding."""
    pins = [part.strip().upper() for part in (value or '').split(',')]
    if not pins or any(not re.fullmatch(r'[0-9A-F]{40}', pin) for pin in pins):
        raise ValueError('Approved signer thumbprints must be comma-separated complete 40-hex certificate thumbprints')
    return set(pins)

def authenticode_signature(path):
    """Ask Windows to validate the final file; never infer trust from configuration."""
    script = ROOT / 'packaging/signpath/inspect-signature.ps1'
    # Pass the literal filename through the environment, never through PowerShell
    # source interpolation. Inline our checked-in script without changing the
    # machine execution policy (local machines may prohibit .ps1 file loading).
    command = '& {\n' + script.read_text(encoding='utf-8') + '\n} -LiteralPath $env:CITRUS_SIGNATURE_PATH'
    environment = {**os.environ, 'CITRUS_SIGNATURE_PATH': str(Path(path).resolve())}
    powershell_home = Path(os.environ.get('SystemRoot', r'C:\Windows')) / 'System32/WindowsPowerShell/v1.0'
    # Python launched from pwsh inherits its module path; Windows PowerShell must
    # load its own security/utility modules rather than incompatible pwsh modules.
    environment['PSMODULEPATH'] = str(powershell_home / 'Modules')
    result = subprocess.run([str(powershell_home / 'powershell.exe'), '-NoProfile', '-NonInteractive', '-Command', command],
                            env=environment, check=True, capture_output=True, text=True, encoding='utf-8', timeout=120)
    return json.loads(result.stdout.lstrip('\ufeff'))

def signature_metadata(probe, require_signed=False, allowed_thumbprints=None):
    status = probe.get('status')
    if status not in ('NotSigned', 'Valid'):
        raise ValueError('Invalid Authenticode signature status: ' + str(status))
    if require_signed and status != 'Valid':
        raise ValueError('A signed release requires a Valid Authenticode signature; unsigned fallback is forbidden')
    if status == 'NotSigned':
        return {'signature': 'NotSigned'}
    signer = probe.get('signer') or {}
    if not signer.get('subject') or not re.fullmatch(r'[0-9A-Fa-f]{40}', signer.get('thumbprint', '')):
        raise ValueError('Valid signature is missing signer certificate details')
    signer = {**signer, 'thumbprint': signer['thumbprint'].upper()}
    timestamp = probe.get('timestamp')
    if require_signed:
        if signer['thumbprint'] not in approved_thumbprints(allowed_thumbprints):
            raise ValueError('Installer signer is not an approved certificate')
        if not timestamp or not timestamp.get('subject') or not re.fullmatch(r'[0-9A-Fa-f]{40}', timestamp.get('thumbprint', '')):
            raise ValueError('A signed release requires a verified timestamp certificate')
        if probe.get('signatureType') != 'Authenticode':
            raise ValueError('Installer requires an embedded Authenticode signature')
    return {'signature': 'Valid', 'signatureScope': 'installer-only', 'signer': signer,
            'timestampCertificate': timestamp}
def sha(path):
    result=hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda:stream.read(1024*1024),b''):result.update(block)
    return result.hexdigest()

def installer_file_version(path):
    api=ctypes.WinDLL('version',use_last_error=True)
    api.GetFileVersionInfoSizeW.argtypes=[wintypes.LPCWSTR,ctypes.POINTER(wintypes.DWORD)]
    api.GetFileVersionInfoSizeW.restype=wintypes.DWORD
    api.GetFileVersionInfoW.argtypes=[wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,ctypes.c_void_p]
    api.GetFileVersionInfoW.restype=wintypes.BOOL
    api.VerQueryValueW.argtypes=[ctypes.c_void_p,wintypes.LPCWSTR,ctypes.POINTER(ctypes.c_void_p),ctypes.POINTER(wintypes.UINT)]
    api.VerQueryValueW.restype=wintypes.BOOL
    length=api.GetFileVersionInfoSizeW(str(path),None)
    if not length:raise RuntimeError('Installer version resource missing')
    data=ctypes.create_string_buffer(length)
    if not api.GetFileVersionInfoW(str(path),0,length,data):raise ctypes.WinError(ctypes.get_last_error())
    pointer=ctypes.c_void_p();characters=wintypes.UINT()
    if not api.VerQueryValueW(data,r'\StringFileInfo\040904b0\FileVersion',ctypes.byref(pointer),ctypes.byref(characters)):
        raise RuntimeError('Installer FileVersion string missing')
    return ctypes.wstring_at(pointer.value)

def write_release(dist=None, source_commit=None, require_signed=False, allowed_thumbprints=None):
    dist=(Path(dist) if dist else ROOT/'dist').resolve()
    if not dist.is_relative_to(ROOT.resolve()) or not dist.is_dir():raise RuntimeError('Release output must be an existing directory inside this workspace')
    version=ET.parse(ROOT/'CitrusVersion.props').getroot().findtext('./PropertyGroup/Version')
    setup=dist/'Citrus-Setup.exe';payload=dist/'Citrus-win-x64'
    if not setup.is_file():raise RuntimeError('Build the final complete installer first')
    signature = signature_metadata(authenticode_signature(setup), require_signed, allowed_thumbprints)
    actual_version=installer_file_version(setup)
    if actual_version!=version:raise RuntimeError('Installer version resource does not match central release version')
    inventory=json.loads((payload/'install-inventory.json').read_text(encoding='utf-8-sig'))
    commit=source_commit or subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
    if not re.fullmatch('[0-9a-f]{40}',commit):raise RuntimeError('Source commit must be a complete lowercase Git SHA')
    subprocess.run(['git','cat-file','-e',commit+'^{commit}'],cwd=ROOT,check=True,capture_output=True)
    committed_props=ET.fromstring(subprocess.check_output(['git','show',commit+':CitrusVersion.props'],cwd=ROOT))
    if committed_props.findtext('./PropertyGroup/Version')!=version:raise RuntimeError('Compiled source commit central version does not match this release')
    manifest={'schema':2,'product':'Citrus','version':version,'platform':'Windows x64','sourceCommit':commit,
              'installer':{'file':setup.name,'bytes':setup.stat().st_size,'sha256':sha(setup),'fileVersion':actual_version,**signature},
              'payload':{'files':len(inventory),'inventorySha256':sha(payload/'install-inventory.json')},
              'sdkLicenseSha256':sha(ROOT/'packaging/notices/presentation/Windows-SDK-License.rtf'),
              'updatePolicy':'Published versioned GitHub releases only; no update for documentation, website or media changes.',
              'qualification':'Automated packaging checks do not establish clean-machine, physical Explorer, visual or performance acceptance.'}
    output=dist/'release-manifest.json';output.write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8',newline='\n')
    public=dist/'release';public.mkdir(exist_ok=True)
    (public/'SHA256SUMS').write_text(manifest['installer']['sha256']+'  Citrus-Setup.exe\n'+sha(output)+'  release-manifest.json\n',encoding='utf-8',newline='\n')
    print(json.dumps(manifest,indent=2))
if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist',type=Path,help='Existing complete release directory inside this workspace; defaults to dist')
    parser.add_argument('--source-commit',help='Exact existing public source commit used for compilation; defaults to HEAD')
    parser.add_argument('--require-signed',action='store_true',help='Require a valid timestamped installer signed by SIGNPATH_ALLOWED_SIGNER_THUMBPRINTS; never fall back to unsigned')
    args=parser.parse_args();write_release(args.dist,args.source_commit,args.require_signed,os.environ.get('SIGNPATH_ALLOWED_SIGNER_THUMBPRINTS'))
