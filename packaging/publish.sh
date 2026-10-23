#!/usr/bin/env bash
# Linux cross-publish; requires pinned SDK (global.json), Python 3.12+, NSIS 3.11.
# Linux retains serial NSIS payload verification; the parallel helper is built only on Windows.
set -euo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
cd "$repo"
dotnet_cmd=${DOTNET:-dotnet}
nsis_cmd=${MAKENSIS:-makensis}
python_cmd=${PYTHON:-python3}
"$python_cmd" - <<'PY'
from pathlib import Path
import shutil
for path in [Path('dist/Citrus-win-x64'), Path('dist/nsis')]:
    if path.is_symlink(): raise RuntimeError('Refusing linked output directory')
    if path.exists(): shutil.rmtree(path)
PY
"$dotnet_cmd" publish src/Citrus.Windows/Citrus.Windows.csproj -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -p:RestoreLockedMode=true -m:1 -nodeReuse:false -o dist/Citrus-win-x64
"$python_cmd" packaging/package.py prepare dist/Citrus-win-x64
"$nsis_cmd" -V3 "-DGENERATED=$repo/dist/nsis" "-DOUTPUT=$repo/dist/Citrus-Setup.exe" packaging/Citrus.nsi
"$python_cmd" packaging/package.py checksums dist
