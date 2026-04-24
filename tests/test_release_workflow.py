from __future__ import annotations

import unittest
from pathlib import Path


class ReleaseWorkflowTests(unittest.TestCase):
    def test_release_workflow_builds_and_uploads_portable_zip_for_version_tags(self) -> None:
        root = Path(__file__).resolve().parents[1]
        workflow = (root / ".github" / "workflows" / "release.yml").read_text(encoding="utf-8")

        self.assertIn('name: Release', workflow)
        self.assertIn('tags:', workflow)
        self.assertIn('- "v*"', workflow)
        self.assertIn("runs-on: windows-latest", workflow)
        self.assertIn('python-version: "3.13"', workflow)
        self.assertIn("python -m unittest discover -s tests -v", workflow)
        self.assertIn(r"powershell -ExecutionPolicy Bypass -File .\scripts\build_portable.ps1", workflow)
        self.assertIn(r'Compress-Archive -Path "dist\YouTube Download Helper\*"', workflow)
        self.assertIn("YouTube-Download-Helper-${{ github.ref_name }}-windows-portable.zip", workflow)
        self.assertIn("gh release create", workflow)
        self.assertIn("GH_TOKEN: ${{ github.token }}", workflow)


if __name__ == "__main__":
    unittest.main()
