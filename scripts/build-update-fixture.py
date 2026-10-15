"""Build native upgrade fixtures with an ephemeral, in-memory signing seed.

Never runs against a user installation. No fixture binary or catalog is uploaded
as a release. The production signing secret is neither read nor used here.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

if os.environ.get('CI') != 'true' or sys.platform != 'win32':
    raise RuntimeError('Native update fixtures require a disposable Windows CI host')
source = Path(__file__).resolve().parents[1]
fixture = Path(tempfile.mkdtemp(prefix='wyrmwatch-update-fixture-'))
clone = fixture / 'source'
subprocess.run(['git', 'clone', '--local', '--no-hardlinks', str(source), str(clone)], check=True)
key = Ed25519PrivateKey.generate()
(clone / 'src/Wyrmwatch.Desktop/Assets/UpdateSigning.pub').write_text(
    base64.b64encode(key.public_key().public_bytes_raw()).decode() + '\n', encoding='utf-8')
current = ET.parse(clone / 'Directory.Build.props').findtext('./PropertyGroup/Version')
parts = [int(p) for p in current.split('.')]
parts[-1] += 1
future = '.'.join(map(str, parts))

def run(*args):
    subprocess.run(args, cwd=clone, check=True)

def build(output):
    run('pwsh', '-NoProfile', '-File', 'scripts/build.ps1', '-Runtime', 'win-x64', '-SkipTests', '-OutputRoot', output)
    run('python', 'scripts/package-installers.py', 'win-x64', '--output-root', output)

build('dist/current')
run('dotnet', 'build', 'tests/Wyrmwatch.UpdateFixture', '-c', 'Release')
driver = fixture / 'driver'
driver.mkdir()
driver_build = clone / 'tests/Wyrmwatch.UpdateFixture/bin/Release/net10.0'
for path in driver_build.glob('Wyrmwatch.UpdateFixture.*'):
    shutil.copyfile(path, driver / path.name)
# The untrimmed SDK-hosted test driver lists design-time facades omitted from the
# compact application. Supply only its missing dependencies in the fixture, never
# overwrite real packaged assemblies or add these files to release downloads.
dependencies = json.loads((driver_build / 'Wyrmwatch.UpdateFixture.deps.json').read_text())
for target in dependencies['targets'].values():
    for library in target.values():
        for asset in library.get('runtime', {}):
            name = Path(asset).name
            if not (clone / 'dist/current/win-x64' / name).is_file():
                candidate = driver_build / name
                if not candidate.is_file():
                    raise RuntimeError('Missing native driver dependency: ' + name)
                shutil.copyfile(candidate, driver / name)
for name in ('Directory.Build.props', 'src/Wyrmwatch.Agent/ManagerHost.cs', 'src/Wyrmwatch.Desktop/Assets/Languages/en.json'):
    path = clone / name
    path.write_text(path.read_text(encoding='utf-8').replace(current, future), encoding='utf-8')
build('dist/future')
shutil.copytree(clone / 'dist/future/win-x64', clone / 'dist/fault/win-x64')
run('python', 'scripts/package-installers.py', 'win-x64', '--output-root', 'dist/fault', '--test-fault-after-files')

def digest(data):
    return hashlib.sha256(data).hexdigest()

for mode in ('success', 'failure', 'bad-signature', 'bad-digest'):
    case = fixture / mode
    case.mkdir()
    setup = clone / ('dist/fault' if mode == 'failure' else 'dist/future') / f'Wyrmwatch-{future}-win-x64-setup.exe'
    destination = case / setup.name
    shutil.copyfile(setup, destination)
    data = destination.read_bytes()
    catalog = json.dumps(dict(Schema=1, Version=future, SourceCommit='a' * 40,
        Artifacts=[dict(Name=setup.name, Bytes=len(data), Sha256=digest(data))]), separators=(',', ':')).encode()
    signature = key.sign(catalog)
    if mode == 'bad-signature':
        signature = bytes([signature[0] ^ 1]) + signature[1:]
    (case / 'Wyrmwatch-update.json').write_bytes(catalog)
    (case / 'Wyrmwatch-update.json.sig').write_text(base64.b64encode(signature).decode(), encoding='utf-8')
    assets = []
    for path in case.iterdir():
        content = path.read_bytes()
        assets.append(dict(name=path.name, size=len(content), digest='sha256:' + digest(content),
            browser_download_url=f'https://github.com/stephenjarrett/Wyrmwatch/releases/download/v{future}/{path.name}'))
    # GitHub's actual field is browser_download_url.
    if mode == 'bad-digest':
        next(asset for asset in assets if asset['name'] == setup.name)['digest'] = 'sha256:' + '0' * 64
    (case / 'release.json').write_text(json.dumps(dict(draft=False, prerelease=False,
        tag_name='v' + future, body='Disposable native fixture only', assets=assets)), encoding='utf-8')
(fixture / 'fixture.json').write_text(json.dumps(dict(Current=current, Future=future,
    Installer=str(clone / f'dist/current/Wyrmwatch-{current}-win-x64-setup.exe'),
    Driver=str(driver))), encoding='utf-8')
with (source / 'dist/native-update-fixture-path.txt').open('w', encoding='utf-8') as output:
    output.write(str(fixture))
print('Native update fixture:', fixture)
