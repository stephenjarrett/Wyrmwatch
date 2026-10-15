"""Ephemeral in-memory signing fixtures; production private material is never used."""
import base64
import json
import unittest
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from test_release import release, HEAD


class SignatureTests(unittest.TestCase):
    def setUp(self):
        self.key = Ed25519PrivateKey.generate()
        self.public = self.key.public_key().public_bytes_raw()
        self.files = {'Wyrmwatch-win-x64.zip': b'archive fixture', 'Wyrmwatch-0.2.8-win-x64-setup.exe': b'installer fixture'}
        self.catalog = dict(Schema=1, Version='0.2.8', SourceCommit=HEAD, Artifacts=[
            dict(Name=name, Bytes=len(data), Sha256=release.sha(data)) for name, data in self.files.items()])

    def sign(self):
        content = json.dumps(self.catalog).encode()
        self.files['Wyrmwatch-update.json'] = content
        self.files['Wyrmwatch-update.json.sig'] = base64.b64encode(self.key.sign(content))

    def test_exact_signed_candidate_passes_and_legacy_inventory_is_preserved(self):
        self.sign()
        release.verify_update_catalog(self.files, '0.2.8', HEAD, self.public)
        self.assertEqual(8, len(release.expected_files('0.2.7')))
        self.assertEqual(12, len(release.expected_files('0.2.8')))

    def test_wrong_key_missing_signature_and_changed_bytes_fail_closed(self):
        self.sign()
        with self.assertRaises(release.ReleaseError):
            release.verify_update_catalog(self.files, '0.2.8', HEAD, Ed25519PrivateKey.generate().public_key().public_bytes_raw())
        self.files['Wyrmwatch-win-x64.zip'] += b'tampered'
        with self.assertRaises(release.ReleaseError): release.verify_update_catalog(self.files, '0.2.8', HEAD, self.public)
        del self.files['Wyrmwatch-update.json.sig']
        with self.assertRaises(release.ReleaseError): release.verify_update_catalog(self.files, '0.2.8', HEAD, self.public)

    def test_signed_catalog_cannot_authorize_another_source_or_package(self):
        self.catalog['SourceCommit'] = 'b' * 40
        self.sign()
        with self.assertRaises(release.ReleaseError): release.verify_update_catalog(self.files, '0.2.8', HEAD, self.public)
        self.catalog['SourceCommit'] = HEAD
        self.catalog['Artifacts'][1] = self.catalog['Artifacts'][0]
        self.sign()
        with self.assertRaises(release.ReleaseError): release.verify_update_catalog(self.files, '0.2.8', HEAD, self.public)
