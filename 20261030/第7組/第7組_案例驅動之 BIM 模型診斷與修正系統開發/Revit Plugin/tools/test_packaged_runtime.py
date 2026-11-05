"""Run source regression tests with the release runtime/dependencies, without shipping pytest."""
import importlib.metadata
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
tools = ROOT / '.build/pytest-tools'
tools.mkdir(parents=True, exist_ok=True)
for name in ('pytest', 'pluggy', 'iniconfig', 'packaging', 'pygments', 'colorama'):
    distribution = importlib.metadata.distribution(name)
    for item in distribution.files:
        if '..' in item.parts or item.suffix in ('.pyc', '.exe'):
            continue
        source = Path(distribution.locate_file(item))
        if source.is_file():
            destination = tools / str(item)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination)
payload = Path((ROOT / '.build/last-release.txt').read_text(encoding='utf-8').strip()) / 'app'
python = payload / 'runtime/python/python.exe'
code = '''import os, sys
sys.path.append({tools!r})
sys.path.insert(0, {backend!r})
os.chdir({backend!r})
import pytest
raise SystemExit(pytest.main(['tests', '-q', '-p', 'no:cacheprovider', '--basetemp', {temp!r}]))
'''.format(tools=str(tools), backend=str(ROOT / 'backend'), temp=str(ROOT / '.build/pytest-runtime-data'))
env = dict(os.environ, PYTEST_DISABLE_PLUGIN_AUTOLOAD='1', PYTHONDONTWRITEBYTECODE='1')
sys.exit(subprocess.call([str(python), '-B', '-c', code], env=env))
