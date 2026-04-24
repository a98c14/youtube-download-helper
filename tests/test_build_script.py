from __future__ import annotations

import unittest
from pathlib import Path


class BuildScriptTests(unittest.TestCase):
    def test_build_script_does_not_require_or_copy_runtime_tools(self) -> None:
        root = Path(__file__).resolve().parents[1]
        script = (root / "scripts" / "build_portable.ps1").read_text(encoding="utf-8")

        self.assertNotIn("Get-Command ffmpeg", script)
        self.assertNotIn("Get-Command ffprobe", script)
        self.assertNotIn("YtDlpPath", script)
        self.assertNotIn("Copy-Item -LiteralPath $ytDlpExe", script)
        self.assertNotIn("ffmpeg.exe", script)
        self.assertNotIn("ffprobe.exe", script)


if __name__ == "__main__":
    unittest.main()
