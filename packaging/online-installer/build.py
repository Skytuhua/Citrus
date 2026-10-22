"""Build the small, version-pinned Windows downloader with the Windows compiler."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
SOURCE = Path(__file__).resolve().parent


def validate_manifest(value):
    version = value.get('version', '')
    installer = value.get('installer', {})
    if (value.get('product') != 'Citrus' or value.get('platform') != 'Windows x64'
            or not re.fullmatch(r'\d+\.\d+\.\d+-preview\.\d+', version)
            or not re.fullmatch(r'[0-9a-f]{40}', value.get('sourceCommit', ''))
            or installer.get('file') != 'Citrus-Setup.exe'
            or installer.get('fileVersion') != version
            or type(installer.get('bytes')) is not int or not 0 < installer['bytes'] < 2**31
            or not re.fullmatch(r'[0-9a-f]{64}', installer.get('sha256', ''))):
        raise ValueError('Expected verified Citrus Windows x64 preview installer metadata')
    return value


def constants(version, size, digest):
    url = f'https://github.com/Skytuhua/Citrus/releases/download/v{version}/Citrus-Setup.exe'
    numeric = version.split('-')[0] + '.' + version.rsplit('.', 1)[1]
    return ('using System.Reflection;\n'
            '[assembly: AssemblyTitle("Citrus online installer")]\n'
            '[assembly: AssemblyProduct("Citrus")]\n'
            f'[assembly: AssemblyVersion("{numeric}")]\n'
            f'[assembly: AssemblyFileVersion("{numeric}")]\n'
            f'[assembly: AssemblyInformationalVersion("{version}")]\n'
            'namespace CitrusOnline { internal static class Release {\n'
            f'public const string Version = "{version}";\n'
            f'public const string Url = "{url}";\n'
            f'public const long Bytes = {size}L;\n'
            f'public const string Sha256 = "{digest}";\n'
            '} }\n')


def compile_installer(output, generated, test=False):
    compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    command = [str(compiler), '/nologo', '/optimize+', '/platform:x86',
               '/target:exe' if test else '/target:winexe', '/out:' + str(output),
               '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
               '/reference:System.Net.Http.dll', '/win32manifest:' + str(SOURCE / 'app.manifest'),
               '/win32icon:' + str(ROOT / 'src/Citrus.Windows/Resources/Citrus.ico'),
               str(SOURCE / 'Installer.cs'), str(generated)]
    if test:
        entry = 'NetworkCheck' if test == 'network' else 'Contracts'
        command += ['/main:CitrusOnline.' + entry, str(SOURCE / (entry + '.cs'))]
    subprocess.run(command, check=True)


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def build(manifest_path, output):
    manifest_path = manifest_path.resolve()
    release = validate_manifest(json.loads(manifest_path.read_text(encoding='utf-8-sig')))
    payload = release['installer']
    offline = manifest_path.parent / payload['file']
    if offline.stat().st_size != payload['bytes'] or sha(offline) != payload['sha256']:
        raise ValueError('Adjacent offline installer does not match the verified release manifest')
    output.mkdir(parents=True, exist_ok=True)
    generated = output / 'Release.cs'
    generated.write_text(constants(release['version'], payload['bytes'], payload['sha256']), encoding='utf-8')
    executable = output / 'Citrus-Web-Setup.exe'
    compile_installer(executable, generated)
    if executable.stat().st_size >= 1048576:
        raise ValueError('Online installer unexpectedly exceeds 1 MiB')
    result = {'schema': 1, 'product': 'Citrus', 'version': release['version'],
              'platform': 'Windows x64', 'kind': 'online-installer',
              'installer': {'file': executable.name, 'bytes': executable.stat().st_size,
                            'sha256': sha(executable), 'signature': 'NotSigned'},
              'payload': {**payload, 'url': f'https://github.com/Skytuhua/Citrus/releases/download/v{release["version"]}/Citrus-Setup.exe',
                          'sourceCommit': release['sourceCommit']},
              'releaseManifestSha256': sha(manifest_path),
              'bootstrapSource': {path.name: sha(path) for path in [SOURCE / 'Installer.cs', SOURCE / 'app.manifest', SOURCE / 'build.py']},
              'requirements': 'Windows x64 with .NET Framework 4.8 (included in Windows 11); internet during setup',
              'qualification': 'Contract tests cover verified download and handoff; clean-machine installation is separate.'}
    metadata = output / 'web-installer-manifest.json'
    metadata.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    (output / 'SHA256SUMS-web-installer').write_text(
        f'{result["installer"]["sha256"]}  {executable.name}\n{sha(metadata)}  {metadata.name}\n', encoding='ascii')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--release-manifest', '--manifest', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    arguments = parser.parse_args()
    build(arguments.release_manifest, arguments.output.resolve())
