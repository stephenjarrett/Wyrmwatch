"""Build platform installers from the same complete portable folder."""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('runtime', choices=('win-x64', 'linux-x64'))
parser.add_argument('--output-root', type=Path, default=Path('dist'))
parser.add_argument('--inno-compiler', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
output = (root / args.output_root).resolve()
build = output / args.runtime
version = ET.parse(root / 'Directory.Build.props').findtext('./PropertyGroup/Version')
if not version or any(c not in '0123456789.' for c in version):
    raise RuntimeError('Installer version must be numeric')
for name in ('LICENSE', 'NOTICE', 'Wyrmwatch-source.zip', 'SOURCE.md'):
    if not (build / name).is_file():
        raise RuntimeError(f'Build the complete portable package first: missing {name}')

if args.runtime == 'win-x64':
    compiler = args.inno_compiler or Path(os.environ.get('ProgramFiles(x86)', 'C:/Program Files (x86)')) / 'Inno Setup 6/ISCC.exe'
    subprocess.run([str(compiler), f'/DAppVersion={version}', f'/DBuildDirectory={build}',
                    f'/DPackageDirectory={output}', str(root / 'scripts/windows-installer.iss')], check=True)
    package = output / f'Wyrmwatch-{version}-win-x64-setup.exe'
else:
    package = output / f'Wyrmwatch-{version}-linux-amd64.deb'
    with tempfile.TemporaryDirectory(prefix='wyrmwatch-deb-') as temporary:
        staging = Path(temporary)
        app = staging / 'usr/lib/wyrmwatch'
        shutil.copytree(build, app)
        for path in app.rglob('*'):
            path.chmod(0o755 if path.is_dir() or path.name in ('Wyrmwatch', 'Wyrmwatch.Agent') or path.suffix == '.sh' else 0o644)
        (staging / 'usr/bin').mkdir(parents=True)
        (staging / 'usr/bin/wyrmwatch').symlink_to('../lib/wyrmwatch/Wyrmwatch')
        desktop = staging / 'usr/share/applications/wyrmwatch.desktop'
        desktop.parent.mkdir(parents=True)
        desktop.write_text('[Desktop Entry]\nType=Application\nName=Wyrmwatch\nComment=Dragonwilds server manager\nExec=wyrmwatch\nIcon=wyrmwatch\nTerminal=false\nCategories=Game;Utility;\n', encoding='utf-8')
        icon = staging / 'usr/share/icons/hicolor/scalable/apps/wyrmwatch.svg'
        icon.parent.mkdir(parents=True)
        shutil.copyfile(root / 'src/Wyrmwatch.Desktop/Assets/wyrmwatch.svg', icon)
        metadata = staging / 'DEBIAN'
        metadata.mkdir()
        size = sum(p.stat().st_size for p in app.rglob('*') if p.is_file()) // 1024
        (metadata / 'control').write_text(
            f'Package: wyrmwatch\nVersion: {version}\nSection: games\nPriority: optional\nArchitecture: amd64\n'
            'Maintainer: Stephen Jarrett <stephenjarrett@users.noreply.github.com>\n'
            f'Installed-Size: {size}\n'
            'Depends: libc6 (>= 2.35), libgcc-s1, libstdc++6, zlib1g, libssl3 | libssl3t64, libicu72 | libicu74 | libicu76 | libicu78, libx11-6, libice6, libsm6, libfontconfig1\n'
            'Recommends: libgtk-3-0 | libgtk-3-0t64\n'
            'Homepage: https://github.com/stephenjarrett/Wyrmwatch\n'
            'Description: Desktop manager for Dragonwilds dedicated servers\n'
            ' Manage saved server connections, verified backups, and optional maintenance.\n'
            ' Services and remote access remain disabled until explicitly configured.\n', encoding='utf-8')
        # Refuse package replacement/removal while the packaged manager is running.
        # Never stop a process, enable a service, or remove user/game data here.
        guard = '''#!/bin/sh
set -eu
for process in /proc/[0-9]*/exe; do
    executable=$(readlink "$process" 2>/dev/null || true)
    case "$executable" in
        /usr/lib/wyrmwatch/Wyrmwatch|/usr/lib/wyrmwatch/agent/Wyrmwatch.Agent|'/usr/lib/wyrmwatch/Wyrmwatch (deleted)'|'/usr/lib/wyrmwatch/agent/Wyrmwatch.Agent (deleted)')
            printf '%s\\n' 'Close Wyrmwatch and stop its background manager when idle before changing this package.' >&2
            exit 1 ;;
    esac
done
'''
        for name in ('preinst', 'prerm'):
            (metadata / name).write_text(guard, encoding='utf-8')
            (metadata / name).chmod(0o755)
        subprocess.run(['desktop-file-validate', str(desktop)], check=True)
        subprocess.run(['dpkg-deb', '--root-owner-group', '--build', str(staging), str(package)], check=True)
with package.open('rb') as stream:
    digest = hashlib.file_digest(stream, 'sha256').hexdigest()
package.with_name(package.name + '.sha256').write_text(f'{digest}  {package.name}\n', encoding='utf-8')
print(f'{package} ({package.stat().st_size:,} bytes)\nSHA-256 {digest}')
