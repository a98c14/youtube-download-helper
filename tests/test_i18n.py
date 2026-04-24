from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.i18n import TRANSLATIONS, language_label, normalize_language, translate


class I18nTests(unittest.TestCase):
    def test_normalizes_supported_language(self) -> None:
        self.assertEqual(normalize_language("en"), "en")
        self.assertEqual(normalize_language("tr"), "tr")

    def test_invalid_language_falls_back_to_turkish(self) -> None:
        self.assertEqual(normalize_language("fr"), "tr")
        self.assertEqual(normalize_language(None), "tr")

    def test_missing_translation_key_falls_back_to_english(self) -> None:
        TRANSLATIONS["en"]["only.in.test"] = "English fallback"
        try:
            self.assertEqual(translate("tr", "only.in.test"), "English fallback")
        finally:
            del TRANSLATIONS["en"]["only.in.test"]

        self.assertEqual(translate("tr", "missing.everywhere"), "missing.everywhere")

    def test_language_labels_are_localized(self) -> None:
        self.assertEqual(language_label("en", "tr"), "Turkish")
        self.assertEqual(language_label("tr", "en"), "İngilizce")


if __name__ == "__main__":
    unittest.main()
