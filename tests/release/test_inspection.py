"""The release diagnostic reads metadata only, including when REST hides a draft."""
import contextlib
import importlib.util
import io
import os
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("inspect_release", ROOT / "scripts/inspect-release.py")
inspection = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(inspection)


class InspectionTests(unittest.TestCase):
    def test_hidden_rest_draft_is_checked_by_graphql_id_without_mutation(self):
        calls = []
        def read(args):
            calls.append(args)
            if args[1] == "graphql":
                return {"data": {"repository": {"releases": {
                    "nodes": [{"databaseId": 10, "tagName": inspection.TAG, "isDraft": True}],
                    "pageInfo": {"hasNextPage": False}}}}}
            self.assertEqual(["api", "--method", "GET"], args[:3])
            if args[-1].endswith("?per_page=100"):
                return []
            self.assertTrue(args[-1].endswith("/releases/10"))
            return {"id": 10, "tag_name": inspection.TAG, "draft": True,
                    "body": "wyrmwatch-release-receipt-v1 " + inspection.HEAD, "assets": []}
        output = io.StringIO()
        with patch.dict(os.environ, GITHUB_REPOSITORY=inspection.REPOSITORY), \
                patch.object(inspection, "read", side_effect=read), contextlib.redirect_stdout(output):
            inspection.main()
        self.assertEqual(3, len(calls))
        self.assertIn('"source_receipt_matches": true', output.getvalue())
        self.assertIn("matching IDs: [10]", output.getvalue())

    def test_foreign_repository_makes_no_calls(self):
        with patch.dict(os.environ, GITHUB_REPOSITORY="fixture/other"), \
                patch.object(inspection, "read") as read:
            with self.assertRaises(RuntimeError):
                inspection.main()
            read.assert_not_called()


if __name__ == "__main__":
    unittest.main()
