"""Download the complete pinned package using production code; never execute it."""
import argparse
import importlib.util
from pathlib import Path
import subprocess

SOURCE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('online_build', SOURCE / 'build.py')
build = importlib.util.module_from_spec(spec)
spec.loader.exec_module(build)

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--built-directory', type=Path, required=True)
    arguments = parser.parse_args()
    directory = arguments.built_directory.resolve()
    executable = directory / 'network-check.exe'
    build.compile_installer(executable, directory / 'Release.cs', test='network')
    subprocess.run([str(executable)], check=True)
