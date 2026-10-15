"""Execute production catalog/file plumbing with a harmless stand-in signer.

Real Ed25519 validation is covered separately; no production private material is
read here. The stand-in also verifies that failed tool output stays suppressed.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class SigningScriptTests(unittest.TestCase):
    def run_script(self, mode):
        self.assertIsNotNone(shutil.which('pwsh'), 'PowerShell 7 is required for release checks')
        temporary = tempfile.TemporaryDirectory(prefix='wyrmwatch signing fixture ')
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        (root / 'scripts').mkdir()
        shutil.copyfile(ROOT / 'scripts/sign-update-catalog.ps1', root / 'scripts/sign-update-catalog.ps1')
        public = root / 'src/Wyrmwatch.Desktop/Assets/UpdateSigning.pub'
        public.parent.mkdir(parents=True)
        public.write_text(base64.b64encode(bytes(32)).decode(), encoding='ascii')
        (root / 'Directory.Build.props').write_text('<Project><PropertyGroup><Version>0.2.8</Version></PropertyGroup></Project>', encoding='utf-8')
        output = root / 'public downloads'
        output.mkdir()
        packages = {'Wyrmwatch-win-x64.zip': b'harmless archive fixture',
                    'Wyrmwatch-0.2.8-win-x64-setup.exe': b'harmless installer fixture'}
        for name, content in packages.items():
            (output / name).write_bytes(content)
        environment = dict(os.environ, SPARKLE_PRIVATE_KEY='nonsecret-stand-in-fixture-value',
                           SIGNING_FIXTURE_MODE=mode, GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM='1')
        subprocess.run(['git', 'init', '--quiet', str(root)], env=environment, check=True, capture_output=True)
        subprocess.run(['git', '-C', str(root), '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
                        '-c', 'commit.gpgsign=false', 'commit', '--quiet', '--allow-empty', '-m', 'Public fixture'],
                       env=environment, check=True, capture_output=True)
        source = subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'], env=environment, text=True).strip()
        signature = base64.b64encode(bytes(64)).decode()
        command = f'''
function Invoke-FixtureSigner {{
    if ($args[0] -eq '--generate-signature') {{
        if ($env:SIGNING_FIXTURE_MODE -eq 'sign-error') {{
            Write-Output $env:SPARKLE_PRIVATE_KEY; $global:LASTEXITCODE=1; return
        }}
        Write-Output 'Signature: {signature}'; $global:LASTEXITCODE=0; return
    }}
    if ($args[0] -eq '--verify' -and $args[2] -eq '--signature' -and $args[3] -ceq '{signature}') {{
        if ($env:SIGNING_FIXTURE_MODE -eq 'verify-error') {{
            Write-Output $env:SPARKLE_PRIVATE_KEY; $global:LASTEXITCODE=1; return
        }}
        Write-Output 'Signature valid'; $global:LASTEXITCODE=0; return
    }}
    $global:LASTEXITCODE=2
}}
& './scripts/sign-update-catalog.ps1' -OutputRoot 'public downloads' -Tool 'Invoke-FixtureSigner'
'''
        result = subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command', command], cwd=root,
                                env=environment, capture_output=True, text=True, timeout=30)
        self.assertNotIn(environment['SPARKLE_PRIVATE_KEY'], result.stdout + result.stderr)
        return result, root, output, packages, source, signature

    def test_catalog_and_signature_both_get_matching_checksums(self):
        result, root, output, packages, source, signature = self.run_script('success')
        self.assertEqual(0, result.returncode, 'Complete signing/checksum script failed')
        catalog = json.loads((output / 'Wyrmwatch-update.json').read_text(encoding='utf-8-sig'))
        self.assertEqual((1, '0.2.8', source), (catalog['Schema'], catalog['Version'], catalog['SourceCommit']))
        self.assertEqual({name: (len(content), hashlib.sha256(content).hexdigest()) for name, content in packages.items()},
                         {a['Name']: (a['Bytes'], a['Sha256']) for a in catalog['Artifacts']})
        self.assertEqual(signature, (output / 'Wyrmwatch-update.json.sig').read_text(encoding='ascii'))
        for name in ('Wyrmwatch-update.json', 'Wyrmwatch-update.json.sig'):
            digest = hashlib.sha256((output / name).read_bytes()).hexdigest()
            self.assertEqual([digest, name], (output / (name + '.sha256')).read_text().split())
        self.assertEqual(set(packages) | {'Wyrmwatch-update.json', 'Wyrmwatch-update.json.sig',
                                        'Wyrmwatch-update.json.sha256', 'Wyrmwatch-update.json.sig.sha256'},
                         {p.name for p in output.iterdir()})
        self.assertFalse((root / '.sig').exists())

    def test_signing_error_cannot_emit_signature_or_checksums(self):
        result, _, output, *_ = self.run_script('sign-error')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('tool output suppressed', result.stderr)
        self.assertFalse((output / 'Wyrmwatch-update.json.sig').exists())
        self.assertFalse(list(output.glob('*.sha256')))

    def test_verification_error_cannot_emit_signature_or_checksums(self):
        result, _, output, *_ = self.run_script('verify-error')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('publication is blocked', result.stderr)
        self.assertFalse((output / 'Wyrmwatch-update.json.sig').exists())
        self.assertFalse(list(output.glob('*.sha256')))
