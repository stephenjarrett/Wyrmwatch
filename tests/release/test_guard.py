"""Execute the actual workflow version/source guard using disposable XML fixtures."""
import os
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(shutil.which("pwsh"), "PowerShell runtime is required for executable workflow guards")
class ReleaseGuardTests(unittest.TestCase):
    def guard(self, props=None, tag=None, source=None, checkout=None):
        workflow = yaml.load((ROOT / ".github/workflows/release.yml").read_text(), Loader=yaml.BaseLoader)
        step = next(step for step in workflow["jobs"]["package"]["steps"] if step.get("name") == "Validate release version")
        xml = props or (ROOT / "Directory.Build.props").read_text()
        version = ET.fromstring(xml).findtext("PropertyGroup/Version")
        environment = dict(os.environ, APPROVED_TAG=tag or "v" + version,
                           APPROVED_SHA=source or "a" * 40, FIXTURE_SHA=checkout or "a" * 40,
                           GITHUB_SHA="c" * 40)
        with tempfile.TemporaryDirectory(prefix="wyrmwatch-release-guard-") as fixture:
            Path(fixture, "Directory.Build.props").write_text(xml)
            script = "$ErrorActionPreference = 'Stop'\nfunction git { $env:FIXTURE_SHA }\n" + step["run"]
            return subprocess.run([shutil.which("pwsh"), "-NoProfile", "-NonInteractive", "-Command", script],
                                  cwd=fixture, env=environment, capture_output=True, text=True, timeout=30)

    def test_real_project_with_conditional_property_group_has_one_exact_version(self):
        result = self.guard()
        self.assertEqual(0, result.returncode, result.stderr)

    def test_repaired_master_can_verify_exact_older_immutable_source(self):
        result = self.guard(source="b" * 40, checkout="b" * 40)
        self.assertEqual(0, result.returncode, result.stderr)

    def test_wrong_tag_or_source_and_malformed_sha_fail_before_packages(self):
        for inputs in ({"tag": "v999.0.0"}, {"checkout": "b" * 40}, {"source": "not-a-sha"}):
            with self.subTest(inputs=inputs):
                self.assertNotEqual(0, self.guard(**inputs).returncode)

    def test_duplicate_or_absent_xml_versions_fail_closed(self):
        xml = (ROOT / "Directory.Build.props").read_text()
        duplicate = xml.replace("</Project>", "<PropertyGroup><Version>9.9.9</Version></PropertyGroup></Project>")
        version = ET.fromstring(xml).findtext("PropertyGroup/Version")
        missing = xml.replace("<Version>" + version + "</Version>", "")
        for fixture in (duplicate, missing):
            with self.subTest(fixture=fixture), tempfile.TemporaryDirectory():
                self.assertNotEqual(0, self.guard(props=fixture, tag="v" + version).returncode)


if __name__ == "__main__":
    unittest.main()
