"""Package a portable build, including matching source and third-party notices."""
import argparse
import hashlib
import tarfile
import zipfile
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('runtime', choices=('win-x64', 'linux-x64'))
parser.add_argument('--output-root', type=Path, default=Path('dist'))
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
output = (root / args.output_root).resolve()
folder = output / args.runtime
executable = 'Wyrmwatch.exe' if args.runtime == 'win-x64' else 'Wyrmwatch'
agent = 'agent/Wyrmwatch.Agent.exe' if args.runtime == 'win-x64' else 'agent/Wyrmwatch.Agent'
required = [executable, agent, 'agent/wwwroot/index.html', 'LICENSE', 'LICENSE.txt', 'NOTICE',
            'SOURCE.md', 'THIRD-PARTY-NOTICES.md', 'Wyrmwatch-source.zip',
            'licenses/third-party/aspnetcore-LICENSE.txt', 'licenses/third-party/aspnetcore-NOTICES.txt',
            'licenses/third-party/dotnet-NOTICES.txt', 'service/install-linux-service.sh']
for name in required:
    if not (folder / name).is_file():
        raise RuntimeError(f'Missing {name}')
with zipfile.ZipFile(folder / 'Wyrmwatch-source.zip') as source:
    if source.testzip() is not None:
        raise RuntimeError('Damaged source archive')
    for name in ('LICENSE', 'NOTICE', 'scripts/build.ps1', 'scripts/package.py',
                 'src/Wyrmwatch.Agent/Program.cs', 'src/Wyrmwatch.Desktop/MainWindow.axaml'):
        if source.read(name) != (root / name).read_bytes():
            raise RuntimeError(f'Stale source: {name}. Stage new files and rebuild first.')
    if any('/bin/' in n or '/obj/' in n or n.startswith(('.git/', '.tools/')) for n in source.namelist()):
        raise RuntimeError('Generated files found in the source archive')
prefix = 'Wyrmwatch-' + args.runtime
if args.runtime == 'win-x64':
    package = output / (prefix + '.zip')
    with zipfile.ZipFile(package, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for file in sorted(folder.rglob('*')):
            if file.is_file():
                archive.write(file, Path(prefix) / file.relative_to(folder))
    with zipfile.ZipFile(package) as archive:
        if archive.testzip() is not None:
            raise RuntimeError('Damaged portable archive')
else:
    package = output / (prefix + '.tar.gz')
    def permissions(info):
        info.uid = info.gid = 0
        info.uname = info.gname = ''
        info.mode = 0o755 if info.isdir() or info.name.endswith(('/Wyrmwatch', '/Wyrmwatch.Agent', '.sh')) else 0o644
        return info
    with tarfile.open(package, 'w:gz') as archive:
        archive.add(folder, arcname=prefix, filter=permissions)
    with tarfile.open(package) as archive:
        for name in (executable, agent):
            if not archive.getmember(prefix + '/' + name).mode & 0o111:
                raise RuntimeError('Executable permission missing')
with package.open('rb') as stream:
    digest = hashlib.file_digest(stream, 'sha256').hexdigest()
package.with_name(package.name + '.sha256').write_text(f'{digest}  {package.name}\n', encoding='utf-8')
print(f'{package} ({package.stat().st_size:,} bytes)\nSHA-256 {digest}')
