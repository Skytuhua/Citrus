#!/usr/bin/env python3
"""Exercise the actual NSIS SHA-256 macro; native tests run only with --native."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import time
import unittest
from unittest.mock import Mock, mock_open, patch
import uuid


SOURCE = Path(__file__).with_name('update.nsh')
READ = "    System::Call 'kernel32::ReadFile(p $HashFileHandle, p $HashBuffer, i 1048576, *i.r2, p 0) i.r1'"
FAULT = """    IntOp $TestFaultReads $TestFaultReads + 1
    ${If} $TestFaultReads == 2
      ; Force a real ReadFile failure after one chunk reached BCryptHashData.
      System::Call 'kernel32::CloseHandle(p $HashFileHandle)'
      StrCpy $HashFileHandle -1
    ${EndIf}
"""


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def extract(text):
    text = text.replace('\r\n', '\n')
    anchor = '!macro HashFunction PREFIX\n'
    require(text.count(anchor) == 1, 'Expected one production HashFunction macro')
    macro = text.split(anchor, 1)[1].split('!macroend\n', 1)
    require(len(macro) == 2, 'Missing HashFunction end')
    macro = anchor + macro[0] + '!macroend\n'
    require(macro.count(READ) == 1, 'ReadFile instrumentation anchor changed')
    variables = '\n'.join(re.findall(r'^Var Hash\w+$', text, re.MULTILINE)) + '\n'
    require('Var HashContext\n' in variables, 'Missing production hash variables')
    return variables, macro


def fault_macro(macro):
    return macro.replace('!macro HashFunction PREFIX', '!macro FaultHashFunction PREFIX', 1).replace(
        READ, FAULT + READ, 1)


def literal(value):
    return str(value).replace('$', '$$').replace('"', '$\\"')


def native_script(variables, macro, directory, benchmark):
    root = literal(directory)
    body = [
        'FileOpen $TestLog "' + root + '\\results.txt" w',
        '${If} ${Errors}', '  SetErrorLevel 2', '  Quit', '${EndIf}',
        "System::Call 'kernel32::GetTickCount() i.r0'", 'StrCpy $TestStarted $0',
    ]

    def record(name, filename, prefix=''):
        body.extend(['Push "' + root + '\\' + filename + '"', 'Call ' + prefix + 'HashFile',
                     'Pop $R9', 'FileWrite $TestLog "' + name + '|$R9$\\r$\\n"'])

    def elapsed(name):
        body.extend(["System::Call 'kernel32::GetTickCount() i.r0'",
                     'IntOp $0 $0 - $TestStarted',
                     'FileWrite $TestLog "time-' + name + '|$0$\\r$\\n"'])

    for size in (0, 1, 65536, 65537, 1048576, 1048577):
        record('bytes-' + str(size), 'bytes-' + str(size) + '.bin')
    record('repeat', 'bytes-65537.bin')
    record('missing', 'missing.bin')
    body.extend(["System::Call 'kernel32::CreateFileW(w \"" + root +
                 "\\bytes-65537.bin\", i 0x80000000, i 0, p 0, i 3, i 0, p 0) p.r0'",
                 'StrCpy $TestLock $0', '${If} $TestLock == -1',
                 '  SetErrorLevel 3', '  Quit', '${EndIf}'])
    record('locked', 'bytes-65537.bin')
    body.append("System::Call 'kernel32::CloseHandle(p $TestLock)'")
    record('after-lock', 'bytes-65537.bin')
    body.append('Call CloseHashProvider')
    record('reopened', 'bytes-1.bin')
    body.append('StrCpy $TestFaultReads 0')
    record('read-error', 'bytes-1048577.bin', 'fault.')
    body.append('FileWrite $TestLog "fault-reads|$TestFaultReads$\\r$\\n"')
    record('after-read-error', 'bytes-1048577.bin')
    body.extend(['Call CloseHashProvider', 'Call CloseHashProvider'])
    record('reopened-twice', 'bytes-0.bin')
    elapsed('semantics')
    if benchmark:
        body.extend(["System::Call 'kernel32::GetTickCount() i.r0'", 'StrCpy $TestStarted $0'])
        for index in range(1000):
            record('tiny-' + str(index), 'tiny-' + str(index) + '.bin')
        elapsed('1000-tiny')
        body.extend(["System::Call 'kernel32::GetTickCount() i.r0'", 'StrCpy $TestStarted $0'])
        record('large', 'large.bin')
        elapsed('32-MiB')
    body.extend(['Call CloseHashProvider', 'FileClose $TestLog', 'SetErrorLevel 0'])
    return '\n'.join([
        'Unicode true', 'Name "Citrus hash regression"',
        'OutFile "' + root + '\\hash-test.exe"', 'RequestExecutionLevel user',
        'SilentInstall silent', 'AutoCloseWindow true', 'SetCompressor zlib', 'SetCompress off',
        '!include "LogicLib.nsh"', variables,
        'Var TestLog', 'Var TestLock', 'Var TestStarted', 'Var TestFaultReads', macro,
        '!insertmacro HashFunction ""', fault_macro(macro),
        '!insertmacro FaultHashFunction "fault."',
        'Section', *body, 'SectionEnd', '',
    ])


def fixtures(directory, benchmark):
    cases = {}

    def write(name, content):
        path = directory / (name + '.bin')
        with path.open('xb') as stream:
            stream.write(content)
        cases[name] = hashlib.sha256(content).hexdigest()

    for size in (0, 1, 65536, 65537, 1048576, 1048577):
        write('bytes-' + str(size), (bytes(range(256)) * (size // 256 + 1))[:size])
    if benchmark:
        for index in range(1000):
            write('tiny-' + str(index), ('Citrus native SHA256 fixture ' + str(index)).encode())
        write('large', bytes(range(256)) * (32 * 1024 * 1024 // 256))
    return cases


class SelfCheck(unittest.TestCase):
    def test_production_extraction_and_exactly_one_fault_site(self):
        variables, macro = extract(SOURCE.read_text(encoding='utf-8-sig'))
        injected = fault_macro(macro)
        self.assertEqual(injected.count(FAULT), 1)
        self.assertEqual(injected.replace(FAULT, '', 1).replace(
            '!macro FaultHashFunction PREFIX', '!macro HashFunction PREFIX', 1), macro)
        generated = native_script(variables, macro, Path('C:/fresh-fixture'), False)
        self.assertIn(macro, generated)
        self.assertIn('SilentInstall silent', generated)
        for command in ('WriteReg', 'Delete', 'Exec ', 'ExecWait', '$INSTDIR', 'WriteUninstaller'):
            self.assertNotIn(command, generated)

    def test_changed_or_missing_production_anchors_are_rejected(self):
        text = SOURCE.read_text(encoding='utf-8-sig')
        for changed in ('', text.replace('!macro HashFunction PREFIX', '!macro Unknown PREFIX'),
                        text.replace('kernel32::ReadFile', 'kernel32::ChangedReadFile')):
            with self.assertRaises(ValueError):
                extract(changed)

    def test_buffer_covers_the_read_chunk_and_both_boundary_sizes_are_exercised(self):
        variables, macro = extract(SOURCE.read_text(encoding='utf-8-sig'))
        self.assertEqual(macro.count('System::Alloc 1048576'), 1)
        generated = native_script(variables, macro, Path('fixture'), False)
        for size in (65536, 65537, 1048576, 1048577):
            self.assertIn('Push "fixture\\bytes-' + str(size) + '.bin"', generated)
        self.assertIn('Push "fixture\\bytes-1048577.bin"\nCall fault.HashFile', generated)

    def test_spawned_process_is_reaped_when_pid_reporting_fails(self):
        process = Mock(pid=123)
        process.wait.return_value = 0
        with patch.object(Path, 'open', mock_open()), \
                patch.object(subprocess, 'CREATE_NO_WINDOW', 0, create=True), \
                patch.object(subprocess, 'Popen', return_value=process) as launch, \
                patch('builtins.print', side_effect=BrokenPipeError('closed output')):
            with self.assertRaises(BrokenPipeError):
                run_owned(['test'], Path('fresh-fixture'), {}, 'native')
        process.wait.assert_called_once_with()
        self.assertEqual(launch.call_args.kwargs['env']['TEMP'], 'fresh-fixture')


def run_owned(command, directory, result, key):
    with (directory / (key + '.log')).open('w', encoding='utf-8') as log:
        process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT,
                                   cwd=directory, creationflags=subprocess.CREATE_NO_WINDOW,
                                   env={**os.environ, 'TEMP': str(directory), 'TMP': str(directory)})
        try:
            result[key + 'ProcessId'] = process.pid
            print(key + ' process ID: ' + str(process.pid), flush=True)
            code = process.wait()
        finally:
            process.wait()  # Reap this test process; never kill the compiler wrapper alone.
        require(code == 0, key + ' process exit code: ' + str(code))


def run_native(args):
    require(os.name == 'nt', 'Native tests require Windows')
    compiler = args.compiler.absolute()
    require(re.fullmatch('[0-9a-f]{64}', args.compiler_sha256 or '') is not None,
            'Pass the independently pinned --compiler-sha256')
    require(sha(compiler) == args.compiler_sha256, 'Compiler hash differs')
    parent = Path(__file__).absolute().parents[1] / '.local'
    for path in (parent, *parent.parents):
        attributes = path.lstat()
        require(not stat.S_ISLNK(attributes.st_mode) and not (
            getattr(attributes, 'st_file_attributes', 0) & 0x400), 'Reparse ancestor rejected: ' + str(path))
    directory = parent / ('setup-hash-' + uuid.uuid4().hex)
    directory.mkdir()
    result = {'passed': False, 'directory': str(directory), 'benchmark': args.benchmark,
              'compilerSha256': args.compiler_sha256, 'sourceSha256': sha(SOURCE)}
    print('Retained native test directory: ' + str(directory), flush=True)
    try:
        variables, macro = extract(SOURCE.read_text(encoding='utf-8-sig'))
        result['hashMacroSha256'] = hashlib.sha256(macro.encode()).hexdigest()
        cases = fixtures(directory, args.benchmark)
        script = directory / 'hash-test.nsi'
        script.write_text(native_script(variables, macro, directory, args.benchmark), encoding='utf-8-sig')
        for name, expected in cases.items():
            require(sha(directory / (name + '.bin')) == expected, 'Fixture hash differs: ' + name)
        (directory / 'expected.json').write_text(json.dumps(cases, indent=2) + '\n')
        run_owned([str(compiler), '/V2', str(script)], directory, result, 'compile')
        result['executableSha256'] = sha(directory / 'hash-test.exe')
        started = time.monotonic()
        run_owned([str(directory / 'hash-test.exe'), '/S'], directory, result, 'native')
        result['nativeElapsedSeconds'] = time.monotonic() - started
        lines = (directory / 'results.txt').read_text(encoding='utf-8').splitlines()
        pairs = [line.split('|') for line in lines]
        require(all(len(pair) == 2 for pair in pairs), 'Malformed native results')
        actual = dict(pairs)
        require(len(actual) == len(pairs), 'Duplicate native result')
        expected = dict(cases)
        expected.update({name: cases['bytes-65537'] for name in
                         ('repeat', 'after-lock')})
        expected['after-read-error'] = cases['bytes-1048577']
        expected.update({'missing': '', 'locked': '', 'read-error': '', 'fault-reads': '2',
                         'reopened': cases['bytes-1'], 'reopened-twice': cases['bytes-0']})
        timing_names = ['semantics', '1000-tiny', '32-MiB'] if args.benchmark else ['semantics']
        result['timingsMs'] = {}
        for name in timing_names:
            timing = actual.pop('time-' + name)
            require(timing.isdecimal(), 'Invalid elapsed time: ' + name)
            result['timingsMs'][name] = int(timing)
        require(actual == expected, 'Native SHA-256/failure/reopen results differ from Python expectations')
        for name, expected_hash in cases.items():
            require(sha(directory / (name + '.bin')) == expected_hash, 'Fixture changed: ' + name)
        require(sha(SOURCE) == result['sourceSha256'], 'Production source changed during test')
        result.update(passed=True, checkedResults=len(expected), fixtureFiles=len(cases))
    except Exception as error:
        result['error'] = str(error)
        raise
    finally:
        (directory / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(result), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', action='store_true', help='Compile and run the silent hash test; retains all fixtures')
    parser.add_argument('--compiler', type=Path)
    parser.add_argument('--compiler-sha256')
    parser.add_argument('--benchmark', action='store_true', help='Also hash 1,000 distinct tiny files and a 32 MiB file')
    args = parser.parse_args()
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(SelfCheck)
    require(unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful(), 'Self-check failed')
    if args.native:
        require(args.compiler is not None, '--native requires --compiler')
        run_native(args)
