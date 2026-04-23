from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))


class BrowserProfilesTests(unittest.TestCase):
    def test_discovers_default_and_named_profiles(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            chrome_dir = root / "Google" / "Chrome" / "User Data"
            (chrome_dir / "Default").mkdir(parents=True)
            (chrome_dir / "Profile 1").mkdir(parents=True)
            (chrome_dir / "System Profile").mkdir(parents=True)
            (chrome_dir / "Local State").write_text(
                json.dumps(
                    {
                        "profile": {
                            "info_cache": {
                                "Default": {"name": "Main"},
                                "Profile 1": {"name": "Work"},
                            }
                        }
                    }
                ),
                encoding="utf-8",
            )

            with patch.dict("os.environ", {"LOCALAPPDATA": str(root)}):
                from importlib import reload
                import ytdlp_helper.browser_profiles as browser_profiles

                reload(browser_profiles)
                profiles = browser_profiles.discover_profiles("chrome")

            self.assertEqual([profile.profile_id for profile in profiles], ["Default", "Profile 1"])
            self.assertEqual([profile.display_name for profile in profiles], ["Main", "Work"])


if __name__ == "__main__":
    unittest.main()
