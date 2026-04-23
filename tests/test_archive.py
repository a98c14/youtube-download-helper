from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.archive import clear_archive_entry, is_archived, parse_youtube_video_id


class ArchiveTests(unittest.TestCase):
    def test_parses_supported_youtube_video_urls(self) -> None:
        cases = {
            "https://www.youtube.com/watch?v=abc123XYZ_1": "abc123XYZ_1",
            "https://youtube.com/watch?v=abc123XYZ_1&list=PL123": "abc123XYZ_1",
            "https://youtu.be/abc123XYZ_1?si=test": "abc123XYZ_1",
            "https://www.youtube.com/shorts/abc123XYZ_1": "abc123XYZ_1",
            "https://www.youtube.com/embed/abc123XYZ_1": "abc123XYZ_1",
            "https://www.youtube.com/live/abc123XYZ_1": "abc123XYZ_1",
        }

        for url, expected_video_id in cases.items():
            with self.subTest(url=url):
                self.assertEqual(parse_youtube_video_id(url), expected_video_id)

    def test_rejects_playlist_only_and_unsupported_urls(self) -> None:
        cases = [
            "",
            "notaurl",
            "https://www.youtube.com/playlist?list=PL123",
            "https://example.com/watch?v=abc123XYZ_1",
        ]

        for url in cases:
            with self.subTest(url=url):
                self.assertIsNone(parse_youtube_video_id(url))

    def test_is_archived_matches_youtube_archive_entry(self) -> None:
        archive_file = _archive_file()
        archive_file.write_text(
            "vimeo abc123XYZ_1\n"
            "youtube other_video\n"
            "  youtube   abc123XYZ_1  \n",
            encoding="utf-8",
        )

        self.assertTrue(is_archived(archive_file, "abc123XYZ_1"))

    def test_is_archived_reports_false_for_missing_or_unrelated_entries(self) -> None:
        archive_file = _archive_file()

        self.assertFalse(is_archived(archive_file, "abc123XYZ_1"))

        archive_file.write_text(
            "\n"
            "youtube other_video\n"
            "youtube abc123XYZ_1 extra\n"
            "youtubeabc123XYZ_1\n",
            encoding="utf-8",
        )

        self.assertFalse(is_archived(archive_file, "abc123XYZ_1"))

    def test_clear_removes_all_matching_entries_and_preserves_unrelated_entries(self) -> None:
        archive_file = _archive_file()
        archive_file.write_text(
            "youtube abc123XYZ_1\n"
            "youtube other_video\n"
            "vimeo abc123XYZ_1\n"
            "  youtube   abc123XYZ_1  \n"
            "youtube abc123XYZ_1 extra\n",
            encoding="utf-8",
        )

        removed_count = clear_archive_entry(archive_file, "abc123XYZ_1")

        self.assertEqual(removed_count, 2)
        self.assertEqual(
            archive_file.read_text(encoding="utf-8"),
            "youtube other_video\n"
            "vimeo abc123XYZ_1\n"
            "youtube abc123XYZ_1 extra\n",
        )

    def test_clear_missing_archive_file_removes_zero_entries(self) -> None:
        self.assertEqual(clear_archive_entry(_archive_file(), "abc123XYZ_1"), 0)


def _archive_file() -> Path:
    return Path(tempfile.mkdtemp()) / "download-archive.txt"


if __name__ == "__main__":
    unittest.main()
