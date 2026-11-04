"""Verify this distribution without touching Revit, credentials or the case database."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parent


def check():
    required = ['CaseBase.extension/startup.py', 'CaseBase.extension/lib/casegraph/Workspace.xaml',
                'CaseBase.extension/lib/casegraph/ShaftChecker.xaml',
                'ShaftChecker-main/src/ShaftChecker.Revit/bin/Release/2022/ShaftChecker.Core.dll',
                'ShaftChecker-main/src/ShaftChecker.Revit/bin/Release/2022/ShaftChecker.Revit.dll',
                'ShaftChecker-main/src/ShaftChecker.Revit/config/shaft-rules.json',
                'backend/claude_agent.py', 'backend/casegraph_mcp.py', 'shared/casegraph_contract.py']
    for name in required:
        if not (ROOT / name).is_file():
            raise RuntimeError('Missing file: ' + name)
    assert sys.version_info >= (3, 13), 'Packaged Python 3.13 is required.'
    assert sys.maxsize > 2**32, '64-bit Python is required.'
    import ifcopenshell
    import networkx
    import uvicorn
    import pydantic
    import httpx
    assert ifcopenshell.file(schema='IFC4').schema == 'IFC4'
    # A child gets a private database and no AI credentials. Never import app.main
    # in this process before setting its data directory.
    with tempfile.TemporaryDirectory(prefix='bim-install-check-', dir=ROOT) as data:
        env = dict(os.environ, CASEBASE_DATA_DIR=data)
        for key in list(env):
            if key.startswith(('CASEGRAPH_LLM_', 'ANTHROPIC_', 'OPENAI_')):
                env.pop(key)
        code = """from fastapi.testclient import TestClient
from app.main import app
with TestClient(app) as client:
    health = client.get('/graph/health')
    assert health.status_code == 200, health.text
    assert health.json()['api_version'] >= 7
    cases = client.get('/graph/cases')
    assert cases.status_code == 200 and cases.json() == [], cases.text
    assert client.get('/').status_code == 200
print('Backend, empty case library and IFC checks passed.')
"""
        subprocess.run([sys.executable, '-B', '-c', code], cwd=str(ROOT / 'backend'), env=env, check=True)
    print('PASS: package files, Python, dependencies, IFC and isolated backend.')
    print('Revit geometry/UI and optional Claude/MCP require a live Revit session.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--self-test', action='store_true')
    parser.add_argument('--pause', action='store_true')
    args = parser.parse_args()
    result = 0
    try:
        check()
    except Exception as error:
        print('FAILED: ' + str(error))
        result = 1
    if args.pause:
        input('Press Enter to close...')
    sys.exit(result)
