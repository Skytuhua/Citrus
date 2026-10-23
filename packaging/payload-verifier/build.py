"""Build a deterministic Framework-only verifier from the exact embedded inventory."""
import json
import os
from pathlib import Path
import re
import runpy
import shutil
import subprocess
import tempfile

SOURCE = Path(__file__).resolve().parent
ROOT = SOURCE.parents[1]
PACKAGE = runpy.run_path(str(ROOT / 'packaging/package.py'))
safe, sha = PACKAGE['safe'], PACKAGE['sha']
EXECUTABLE = 'Citrus.PayloadVerifier.exe'
MANIFEST = 'PayloadManifest.cs'


def manifest_source(files):
    if any(not isinstance(name, str) or any(ord(char) < 32 for char in name) for name in files):
        raise ValueError('Invalid verifier inventory path')
    if not files or len({name.casefold() for name in files}) != len(files):
        raise ValueError('Empty or case-colliding verifier inventory')
    for name, digest in files.items():
        safe(name)
        if not isinstance(digest, str) or not re.fullmatch('[0-9a-f]{64}', digest):
            raise ValueError('Invalid installed-byte verification hash')
    entries = sorted(files.items())
    return ('internal static class PayloadManifest {\n'
        'internal static readonly string[] Paths = {\n' +
        ',\n'.join(json.dumps(name, ensure_ascii=True) for name, _ in entries) + '\n};\n'
        'internal static readonly string[] Digests = {\n' +
        ',\n'.join('"' + digest + '"' for _, digest in entries) + '\n};\n}\n')


def compile_helper(output, manifest, dotnet=None):
    dotnet = dotnet or os.environ.get('DOTNET') or shutil.which('dotnet')
    if not dotnet:
        raise ValueError('The application\'s pinned .NET SDK is required to build its verifier')
    version = json.loads((ROOT / 'global.json').read_bytes())['sdk']['version']
    installed = subprocess.check_output([str(dotnet), '--list-sdks'], text=True)
    matches = [match.group(1) for line in installed.splitlines()
        if (match := re.fullmatch(re.escape(version) + r' \[(.+)\]', line.strip()))]
    if len(matches) != 1:
        raise ValueError('The exact application SDK is not installed: ' + version)
    compiler = Path(matches[0]) / version / 'Roslyn/bincore/csc.dll'
    reference = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/mscorlib.dll'
    for path in (compiler, reference):
        if not path.is_file():
            raise ValueError('Missing existing verifier compiler/reference: ' + str(path))
    subprocess.run([str(dotnet), str(compiler), '/nologo', '/noconfig', '/nostdlib+', '/langversion:5',
        '/optimize+', '/debug-', '/deterministic+', '/platform:x86', '/target:winexe',
        '/reference:' + str(reference), '/out:' + str(output),
        '/win32manifest:' + str(SOURCE / 'app.manifest'),
        '/pathmap:' + str(manifest.parent) + '=/_generated,' + str(SOURCE) + '=/_source',
        str(SOURCE / 'Verifier.cs'), str(manifest)], check=True)


def instructions(digest):
    return ('DetailPrint "Verifying installed files..."\n'
        'InitPluginsDir\nClearErrors\n'
        'File "/oname=$PLUGINSDIR\\Citrus.PayloadVerifier.exe" "${GENERATED}\\Citrus.PayloadVerifier.exe"\n'
        'IfErrors 0 +2\nCall ${PREFIX}Fail\n'
        'Push "$PLUGINSDIR\\Citrus.PayloadVerifier.exe"\nCall ${PREFIX}HashFile\nPop $0\n'
        'StrCmp $0 "' + digest + '" +2\nCall ${PREFIX}Fail\nClearErrors\n'
        'ExecWait \'$\\"$PLUGINSDIR\\Citrus.PayloadVerifier.exe$\\" $\\"$INSTDIR$\\"\' $0\n'
        '${If} ${Errors}\n${OrIf} $0 != 0\nCall ${PREFIX}Fail\n${EndIf}\n'
        'DetailPrint "Installed files verified."\n')


def prepare(files, generated, dotnet=None):
    source = manifest_source(files)
    generated.mkdir(parents=True, exist_ok=True)
    manifest = generated / MANIFEST
    manifest.write_text(source, encoding='utf-8', newline='\n')
    executable = generated / EXECUTABLE
    compile_helper(executable, manifest, dotnet)
    return instructions(sha(executable))


def verify_prepared(files, generated, dotnet=None):
    source = manifest_source(files).encode('utf-8')
    manifest, executable = generated / MANIFEST, generated / EXECUTABLE
    for name in (MANIFEST, EXECUTABLE):
        PACKAGE['checked_path'](generated, name)
    if manifest.read_bytes() != source:
        raise ValueError('Changed generated verifier source')
    with tempfile.TemporaryDirectory(prefix='citrus-verifier-witness-') as temporary:
        witness = Path(temporary)
        expected_manifest = witness / MANIFEST
        expected_manifest.write_bytes(source)
        compile_helper(witness / EXECUTABLE, expected_manifest, dotnet)
        if executable.read_bytes() != (witness / EXECUTABLE).read_bytes():
            raise ValueError('Generated verifier differs from the trusted deterministic compiler witness')
    return instructions(sha(executable))
