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

    def test_organization_setting_label_is_translated(self) -> None:
        self.assertEqual(translate("en", "settings.organize_by_channel"), "Organize by channel/playlist")
        self.assertEqual(translate("tr", "settings.organize_by_channel"), "Kanal/oynatma listesine göre düzenle")

    def test_download_button_labels_are_translated(self) -> None:
        self.assertEqual(translate("en", "button.download"), "Download")
        self.assertEqual(translate("en", "button.download_playlist"), "Download Playlist")
        self.assertEqual(translate("tr", "button.download"), "İndir")
        self.assertEqual(translate("tr", "button.download_playlist"), "Oynatma Listesi İndir")

    def test_queue_filter_labels_are_translated(self) -> None:
        self.assertEqual(translate("en", "queue.filter.all"), "All")
        self.assertEqual(translate("en", "queue.filter.ongoing"), "Ongoing")
        self.assertEqual(translate("en", "queue.filter.queued"), "Queued")
        self.assertEqual(translate("en", "queue.filter.completed"), "Completed")
        self.assertEqual(translate("en", "queue.filter.failed"), "Failed")
        self.assertEqual(translate("tr", "queue.filter.all"), "Tümü")
        self.assertEqual(translate("tr", "queue.filter.ongoing"), "Devam Eden")
        self.assertEqual(translate("tr", "queue.filter.queued"), "Kuyrukta")
        self.assertEqual(translate("tr", "queue.filter.completed"), "Tamamlandı")
        self.assertEqual(translate("tr", "queue.filter.failed"), "Başarısız")

    def test_category_controls_are_translated(self) -> None:
        self.assertEqual(translate("en", "field.category"), "Category")
        self.assertEqual(translate("tr", "field.category"), "Kategori")
        self.assertEqual(translate("en", "queue.column.category"), "Category")
        self.assertEqual(translate("tr", "queue.column.category"), "Kategori")


if __name__ == "__main__":
    unittest.main()
