"""Build an offline, allowlisted Windows installer; never copy a developer environment."""
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
BUILD = ROOT / '.build'
DIST = ROOT / 'dist'
VERSION = '2026.11.04.4'
NAME = 'CaseDrivenBIM-' + VERSION + '-Revit2022-Setup'
LOCK = ROOT / 'installer/release-lock.json'
BACKEND_FILES = ('run_backend.py', 'claude_agent.py', 'casegraph_mcp.py', 'planning_mcp.py',
                 'revit_mcp_client.py', 'requirements.txt')
EXTENSIONS = {'.py', '.xaml', '.yaml', '.png'}
SECRET_PATTERNS = (
    rb'sk-(?:ant-|proj-)?[A-Za-z0-9_-]{24,}', rb'gh[pousr]_[A-Za-z0-9]{30,}',
    rb'AKIA[A-Z0-9]{16}', rb'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    rb'(?i)(?:api[_-]?key|access[_-]?token|password)\s*[=:]\s*[\x22\x27][A-Za-z0-9_+/-]{24,}[\x22\x27]',
)


def sha(path):
    return hashlib.file_digest(path.open('rb'), 'sha256').hexdigest()


def copy(source, destination):
    if source.is_symlink():
        raise ValueError('Release inputs cannot be symlinks: ' + source.name)
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)


def extract(archive, destination):
    with zipfile.ZipFile(archive) as zipped:
        for name in zipped.namelist():
            target = (destination / name).resolve()
            if not target.is_relative_to(destination.resolve()):
                raise ValueError('Unsafe archive entry')
        zipped.extractall(destination)


def privacy_findings(path, data, identity):
    problems = []
    lower = data.lower()
    for value in identity:
        if value and (value.lower().encode('utf-8') in lower or value.lower().encode('utf-16le') in lower):
            problems.append(path + ': local identity/path')
            break
    if any(re.search(pattern, data) for pattern in SECRET_PATTERNS):
        problems.append(path + ': credential pattern')
    if path.endswith('.zip'):
        with zipfile.ZipFile(io.BytesIO(data)) as nested:
            for name in nested.namelist():
                if not name.endswith('/'):
                    problems.extend(privacy_findings(path + '/' + name, nested.read(name), identity))
    return problems


def audit(app):
    identity = [str(ROOT), str(Path.home()), os.environ.get('USERNAME', '')]
    issues = []
    files = []
    for path in sorted(app.rglob('*')):
        if not path.is_file():
            continue
        relative = path.relative_to(app).as_posix()
        if (relative.startswith(('backend/data/', 'samples/', '.git/', 'backend/tests/')) or
                path.name in ('.mcp.json', '.env', 'pyvenv.cfg') or
                path.suffix.lower() in ('.rvt', '.rfa', '.db', '.sqlite', '.log', '.pdb', '.pyc')):
            issues.append(relative + ': excluded data/configuration')
        issues.extend(privacy_findings(relative, path.read_bytes(), identity))
        files.append(dict(path=relative, sha256=sha(path), bytes=path.stat().st_size))
    if issues:
        raise ValueError('Release privacy audit failed:\n' + '\n'.join(issues[:30]))
    return dict(product='CaseDrivenBIM', version=VERSION, revit='2022', files=files,
                privacy=dict(local_identity_matches=0, credential_pattern_matches=0,
                             excluded_data_files=0, third_party_attributions_retained=True))


def prepare_inputs(lock):
    downloads = BUILD / 'downloads'
    wheels = BUILD / 'wheels'
    downloads.mkdir(parents=True, exist_ok=True)
    wheels.mkdir(parents=True, exist_ok=True)
    runtime = downloads / lock['python']['filename']
    if not runtime.exists():
        urllib.request.urlretrieve(lock['python']['url'], runtime)
    if sha(runtime) != lock['python']['sha256']:
        raise ValueError('Python archive checksum mismatch')
    if any(not (wheels / item['filename']).exists() for item in lock['wheels']):
        subprocess.run([sys.executable, '-m', 'pip', 'download', '--only-binary=:all:', '--no-deps',
                        '--platform', 'win_amd64', '--python-version', '3.13', '--implementation', 'cp',
                        '--abi', 'cp313', '--require-hashes', '--dest', str(wheels),
                        '-r', str(ROOT / 'installer/requirements-release.txt')], check=True)
    for item in lock['wheels']:
        if sha(wheels / item['filename']) != item['sha256']:
            raise ValueError('Wheel checksum mismatch: ' + item['filename'])
    return runtime, wheels


def build():
    lock = json.loads(LOCK.read_text(encoding='utf-8'))
    runtime, wheels = prepare_inputs(lock)
    BUILD.mkdir(exist_ok=True)
    DIST.mkdir(exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix='release-', dir=BUILD))
    app = stage / 'app'
    # Explicit source roots/extensions. No working copies of data, .venv or MCP configs.
    for source in (ROOT / 'CaseBase.extension').rglob('*'):
        if source.is_file() and source.suffix in EXTENSIONS:
            copy(source, app / source.relative_to(ROOT))
    for source in (ROOT / 'backend/app').glob('*.py'):
        copy(source, app / source.relative_to(ROOT))
    for name in BACKEND_FILES:
        copy(ROOT / 'backend' / name, app / 'backend' / name)
    copy(ROOT / 'shared/casegraph_contract.py', app / 'shared/casegraph_contract.py')
    for name in ('README.md', 'README.zh-TW.md', '.mcp.example.json', 'Start-CaseGraph.ps1'):
        copy(ROOT / name, app / name)
    for name in ('Verify-Installation.py', 'Uninstall.ps1', 'release-lock.json'):
        copy(ROOT / 'installer' / name, app / name)
    copy(ROOT / 'installer/Install.ps1', stage / 'Install.ps1')
    # PowerShell 5.1 needs BOM for Chinese literals; pyRevit/Python sources use encoding declarations.
    for path in (stage / 'Install.ps1', app / 'Uninstall.ps1'):
        path.write_text(path.read_text(encoding='utf-8-sig'), encoding='utf-8-sig')

    shaft = ROOT / 'ShaftChecker-main/src/ShaftChecker.Revit'
    output = stage / 'shaft-build'
    subprocess.run(['dotnet', 'build', str(shaft / 'ShaftChecker.Revit.csproj'), '-c', 'Release',
                    '-p:RevitVersion=2022', '-p:DeployToRevit=false', '-p:DebugType=None',
                    '-p:DebugSymbols=false', '-p:Deterministic=true', '-p:PathMap=' + str(ROOT) + '=/_/src',
                    '-p:OutputPath=' + str(output) + os.sep, '--no-restore', '--nologo'], check=True)
    for name in ('ShaftChecker.Core.dll', 'ShaftChecker.Revit.dll'):
        copy(output / name, app / 'ShaftChecker-main/src/ShaftChecker.Revit/bin/Release/2022' / name)
    copy(shaft / 'config/shaft-rules.json', app / 'ShaftChecker-main/src/ShaftChecker.Revit/config/shaft-rules.json')

    python = app / 'runtime/python'
    extract(runtime, python)
    site = python / 'Lib/site-packages'
    site.mkdir(parents=True)
    for item in lock['wheels']:
        wheel = wheels / item['filename']
        with zipfile.ZipFile(wheel) as zipped:
            if any('.data/' in name for name in zipped.namelist()):
                raise ValueError('Wheel needs explicit .data relocation: ' + wheel.name)
        extract(wheel, site)
    (python / 'python313._pth').write_text(
        'python313.zip\n.\nLib/site-packages\n../../backend\n../../CaseBase.extension/lib\n../../shared\nimport site\n', encoding='ascii')
    # Disable bytecode on end-user installs, keeping paths out of packaged caches.
    (site / 'sitecustomize.py').write_text('import sys\nsys.dont_write_bytecode = True\n', encoding='ascii')
    subprocess.run([str(python / 'python.exe'), '-B', str(app / 'Verify-Installation.py'), '--self-test'], check=True)
    for cache in app.rglob('*.pyc'):
        cache.unlink()
    manifest = audit(app)
    script_problems = privacy_findings('Install.ps1', (stage / 'Install.ps1').read_bytes(),
                                       [str(ROOT), str(Path.home()), os.environ.get('USERNAME', '')])
    if script_problems:
        raise ValueError('Installer script privacy audit failed: ' + ', '.join(script_problems))
    (app / 'package-manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    payload = stage / 'payload.zip'
    with zipfile.ZipFile(payload, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as zipped:
        zipped.write(stage / 'Install.ps1', 'Install.ps1')
        for path in sorted(app.rglob('*')):
            if path.is_file():
                zipped.write(path, 'app/' + path.relative_to(app).as_posix())
    compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    exe = DIST / (NAME + '.exe')
    subprocess.run([str(compiler), '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/debug-',
                    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
                    '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
                    '/resource:' + str(payload) + ',payload.zip', '/out:' + str(exe), str(ROOT / 'installer/Setup.cs')], check=True)
    if privacy_findings(exe.name, exe.read_bytes(), [str(ROOT), str(Path.home()), os.environ.get('USERNAME', '')]):
        raise ValueError('Installer executable privacy audit failed')
    # Check the actual EXE's embedded archive, not just its staging directory.
    extracted = stage / 'exe-extracted'
    subprocess.run([str(exe), '/extract', str(extracted)], check=True)
    actual = audit(extracted / 'app')
    expected = {item['path']: item['sha256'] for item in manifest['files']}
    observed = {item['path']: item['sha256'] for item in actual['files'] if item['path'] != 'package-manifest.json'}
    if expected != observed:
        raise ValueError('Installer extraction differs from audited files')
    if (extracted / 'Install.ps1').read_bytes() != (stage / 'Install.ps1').read_bytes():
        raise ValueError('Installer script changed during packaging')
    (DIST / (NAME + '.exe.sha256')).write_text(sha(exe) + '  ' + exe.name + '\n', encoding='ascii')
    (DIST / 'release-audit.json').write_text(json.dumps(dict(
        product='CaseDrivenBIM', version=VERSION, revit='2022', installer_sha256=sha(exe),
        file_count=len(manifest['files']), privacy=manifest['privacy'], extracted_payload_verified=True,
        runtime_check='passed', third_party_versions=lock['wheels']), indent=2), encoding='utf-8')
    copy(ROOT / 'README.md', DIST / 'README.md')
    copy(ROOT / 'README.zh-TW.md', DIST / 'README.zh-TW.md')
    print('Installer: ' + str(exe))
    print('Verified payload: ' + str(extracted))
    # Local pointer for the isolated installer regression test; never distributed.
    (BUILD / 'last-release.txt').write_text(str(extracted), encoding='utf-8')


if __name__ == '__main__':
    build()
