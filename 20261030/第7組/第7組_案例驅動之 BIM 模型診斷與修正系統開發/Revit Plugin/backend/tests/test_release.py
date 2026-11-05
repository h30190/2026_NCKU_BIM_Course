"""Packaging boundaries and runtime portability without changing this machine's install."""
import importlib.util
from pathlib import Path
import sys

import pytest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'CaseBase.extension/lib'))
from casegraph.runtime import python_executable

spec = importlib.util.spec_from_file_location('release_builder', ROOT / 'tools/build_release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


def test_runtime_uses_bundled_python_and_keeps_developer_fallback(tmp_path):
    with pytest.raises(ValueError):
        python_executable(str(tmp_path))
    dev = tmp_path / 'backend/.venv/Scripts/python.exe'
    dev.parent.mkdir(parents=True)
    dev.touch()
    assert python_executable(str(tmp_path)) == str(dev)
    bundled = tmp_path / 'runtime/python/python.exe'
    bundled.parent.mkdir(parents=True)
    bundled.touch()
    assert python_executable(str(tmp_path)) == str(bundled)
    windowless = bundled.with_name('pythonw.exe')
    windowless.touch()
    assert python_executable(str(tmp_path), windowless=True) == str(windowless)


@pytest.mark.parametrize('name', ['backend/data/casebase.db', '.mcp.json', '.env', 'model.rvt', 'private.pdb'])
def test_release_rejects_local_data_and_configuration(tmp_path, name):
    path = tmp_path / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text('private content')
    with pytest.raises(ValueError, match='privacy audit failed'):
        release.audit(tmp_path)


def test_release_detects_identity_in_source_binary_and_nested_archive():
    import io
    import zipfile
    identity = ['private-build-user']
    assert release.privacy_findings('a.py', b'private-build-user', identity)
    assert release.privacy_findings('a.dll', 'private-build-user'.encode('utf-16le'), identity)
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w') as zipped:
        zipped.writestr('nested.py', 'private-build-user')
    assert release.privacy_findings('runtime.zip', stream.getvalue(), identity)
    assert not release.privacy_findings('public.txt', b'Third-party attribution', identity)


def test_release_rejects_credentials_without_printing_them():
    key = b'sk-proj-' + b'ExampleOnly' * 4
    found = release.privacy_findings('settings.py', key, [])
    assert found == ['settings.py: credential pattern']
    assert key.decode() not in str(found)
