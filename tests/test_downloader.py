from __future__ import annotations

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.config import AppPaths
from ytdlp_helper.dependencies import RuntimeToolContext
from ytdlp_helper.downloader import (
    DownloadRequest,
    DownloadService,
    _humanize_error,
    _hidden_subprocess_kwargs,
    _output_template,
    _sanitize_path_segment,
    _update_status_from_output,
)
from ytdlp_helper.worker_status import DownloadPhase, DownloadStatus


class FakeProcess:
    def __init__(self, lines: list[str], return_code: int = 0) -> None:
        self.stdout = lines
        self._return_code = return_code

    def wait(self) -> int:
        return self._return_code


class FakeResolver:
    def __init__(self, context: RuntimeToolContext) -> None:
        self.context = context
        self.calls = 0

    def resolve(self, *_args: object) -> RuntimeToolContext:
        self.calls += 1
        return self.context


class DownloaderTests(unittest.TestCase):
    def test_builds_cookie_and_best_video_command(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.cookies_file.write_text("# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSID\tabc\n")
        service = DownloadService(paths)

        with (
            patch("ytdlp_helper.downloader.find_ytdlp_executable", return_value="C:/tools/yt-dlp.exe"),
            patch("ytdlp_helper.downloader.find_ffmpeg_location", return_value="C:/ffmpeg"),
        ):
            command = service._build_command(
                DownloadRequest(
                    url="https://www.youtube.com/watch?v=abc123",
                    preset="best-video",
                )
            )

        self.assertEqual(command[0], "C:/tools/yt-dlp.exe")
        self.assertIn("--no-playlist", command)
        self.assertNotIn("--yes-playlist", command)
        self.assert_option(
            command,
            "--output",
            "%(channel,uploader&{}|.)s/%(title)s.%(ext)s",
        )
        self.assertIn(f"home:{service._paths.download_dir}", command)
        self.assertNotIn("--download-archive", command)
        self.assert_option(command, "--cookies", str(service._paths.cookies_file))
        self.assertNotIn("--cookies-from-browser", command)
        self.assert_option(command, "--ffmpeg-location", "C:/ffmpeg")
        self.assert_option(command, "--format", "bv*+ba/b")
        self.assert_option(command, "--merge-output-format", "mp4")
        self.assertEqual(command[-1], "https://www.youtube.com/watch?v=abc123")

    def test_builds_flat_output_when_organization_is_disabled(self) -> None:
        service = DownloadService(_paths(), "%(upload_date)s - %(title)s.%(ext)s", organize_by_channel=False)

        with (
            patch("ytdlp_helper.downloader.find_ytdlp_executable", return_value="yt-dlp.exe"),
            patch("ytdlp_helper.downloader.find_ffmpeg_location", return_value=None),
        ):
            single_command = service._build_command(
                DownloadRequest(
                    url="https://www.youtube.com/watch?v=abc123",
                    preset="best-video",
                )
            )

        self.assert_option(single_command, "--output", "%(upload_date)s - %(title)s.%(ext)s")

    def test_builds_audio_mp3_command(self) -> None:
        service = DownloadService(_paths())

        with (
            patch("ytdlp_helper.downloader.ensure_runtime_tools"),
            patch("ytdlp_helper.downloader.find_ytdlp_executable", return_value="yt-dlp.exe"),
            patch("ytdlp_helper.downloader.find_ffmpeg_location", return_value=None),
        ):
            command = service._build_command(
                DownloadRequest(
                    url="https://www.youtube.com/watch?v=abc123",
                    preset="audio-mp3",
                )
            )

        self.assertNotIn("--cookies", command)
        self.assertNotIn("--cookies-from-browser", command)
        self.assert_option(command, "--format", "bestaudio/best")
        self.assertIn("--extract-audio", command)
        self.assert_option(command, "--audio-format", "mp3")
        self.assert_option(command, "--audio-quality", "192K")

    def test_builds_capped_video_commands(self) -> None:
        cases = {
            "video-1080p": "bv*[height<=1080]+ba/b[height<=1080]",
            "video-720p": "bv*[height<=720]+ba/b[height<=720]",
            "video-480p": "bv*[height<=480]+ba/b[height<=480]",
        }

        for preset, expected_format in cases.items():
            with self.subTest(preset=preset):
                service = DownloadService(_paths())

                with (
                    patch("ytdlp_helper.downloader.find_ytdlp_executable", return_value="yt-dlp.exe"),
                    patch("ytdlp_helper.downloader.find_ffmpeg_location", return_value=None),
                ):
                    command = service._build_command(
                        DownloadRequest(
                            url="https://www.youtube.com/watch?v=abc123",
                            preset=preset,
                        )
                    )

                self.assert_option(command, "--format", expected_format)
                self.assert_option(command, "--merge-output-format", "mp4")

    def test_builds_video_command_with_managed_deno_ejs_support(self) -> None:
        paths = _paths()
        paths.deno_dir.mkdir(parents=True)
        paths.deno_executable.write_bytes(b"deno")
        service = DownloadService(paths)

        with (
            patch("ytdlp_helper.downloader.find_ytdlp_executable", return_value="yt-dlp.exe"),
            patch("ytdlp_helper.downloader.find_ffmpeg_location", return_value=None),
        ):
            command = service._build_command(
                DownloadRequest(
                    url="https://www.youtube.com/watch?v=abc123",
                    preset="best-video",
                )
            )

        self.assert_option(command, "--js-runtimes", f"deno:{paths.deno_executable}")
        self.assert_option(command, "--remote-components", "ejs:github")

    def test_rejects_invalid_url(self) -> None:
        service = DownloadService(_paths())

        with self.assertRaises(ValueError):
            service.download(
                DownloadRequest(url="notaurl", preset="best-video"),
                lambda *_args: None,
                lambda *_args: None,
            )

    def test_download_without_cookie_file_runs_as_public_session(self) -> None:
        service = DownloadService(_paths())
        logs: list[str] = []

        with (
            patch("ytdlp_helper.downloader.ensure_runtime_tools") as ensure_tools,
            patch("ytdlp_helper.downloader.find_ytdlp_executable", return_value="yt-dlp.exe"),
            patch("ytdlp_helper.downloader.find_ffmpeg_location", return_value=None),
            patch("ytdlp_helper.downloader.subprocess.Popen", return_value=FakeProcess([])) as popen,
        ):
            service.download(
                DownloadRequest(url="https://www.youtube.com/watch?v=abc123", preset="best-video"),
                lambda *_args: None,
                logs.append,
            )

        command = popen.call_args.args[0]
        ensure_tools.assert_called_once()
        self.assertNotIn("--cookies", command)
        self.assertIn("No saved cookies; downloading as public session.", logs)

    def test_download_streams_progress_and_completes(self) -> None:
        service = DownloadService(_paths())
        statuses: list[tuple[str, object]] = []
        logs: list[str] = []

        with (
            patch("ytdlp_helper.downloader.ensure_runtime_tools"),
            patch("ytdlp_helper.downloader.find_ytdlp_executable", return_value="yt-dlp.exe"),
            patch("ytdlp_helper.downloader.find_ffmpeg_location", return_value=None),
            patch(
                "ytdlp_helper.downloader.subprocess.Popen",
                return_value=FakeProcess(
                    ["[download] 42.3% of 10.00MiB at 1.23MiB/s ETA 00:12\n", "[Merger] Merging formats\n"]
                ),
            ) as popen,
        ):
            service.download(
                DownloadRequest(
                    url="https://www.youtube.com/watch?v=abc123",
                    preset="best-video",
                ),
                lambda status, event: statuses.append((status, event)),
                logs.append,
            )

        popen.assert_called_once()
        self.assertIn(("downloading", DownloadStatus(DownloadPhase.DOWNLOADING, percent=42)), statuses)
        self.assertIn(("speed", "1.23MiB/s"), statuses)
        self.assertIn(("postprocessing", DownloadStatus(DownloadPhase.FINALIZING)), statuses)
        self.assertIn("[Merger] Merging formats", logs)

    def test_download_uses_cached_runtime_context_without_rediscovery(self) -> None:
        paths = _paths()
        context = RuntimeToolContext(
            ytdlp_executable="C:/tools/yt-dlp.exe",
            ffmpeg_location="C:/tools/ffmpeg",
            deno_executable="C:/tools/deno/deno.exe",
        )
        resolver = FakeResolver(context)
        service = DownloadService(paths, runtime_tools=resolver)

        with (
            patch("ytdlp_helper.downloader.find_ytdlp_executable", side_effect=AssertionError("rediscovered yt-dlp")),
            patch("ytdlp_helper.downloader.find_ffmpeg_location", side_effect=AssertionError("rediscovered ffmpeg")),
            patch("ytdlp_helper.downloader.find_deno_executable", side_effect=AssertionError("rediscovered deno")),
            patch("ytdlp_helper.downloader.subprocess.Popen", return_value=FakeProcess([])) as popen,
        ):
            service.download(
                DownloadRequest(url="https://www.youtube.com/watch?v=abc123", preset="best-video"),
                lambda *_args: None,
                lambda *_args: None,
            )

        command = popen.call_args.args[0]
        self.assertEqual(resolver.calls, 1)
        self.assertEqual(command[0], "C:/tools/yt-dlp.exe")
        self.assert_option(command, "--ffmpeg-location", "C:/tools/ffmpeg")
        self.assert_option(command, "--js-runtimes", "deno:C:/tools/deno/deno.exe")

    def test_progress_line_without_speed_does_not_report_speed(self) -> None:
        statuses: list[tuple[str, object]] = []

        _update_status_from_output(
            "[download] 42.3% of 10.00MiB",
            lambda status, event: statuses.append((status, event)),
        )

        self.assertEqual(statuses, [("downloading", DownloadStatus(DownloadPhase.DOWNLOADING, percent=42))])

    def test_download_auth_error_points_to_fresh_pasted_cookies(self) -> None:
        service = DownloadService(_paths())

        with (
            patch("ytdlp_helper.downloader.ensure_runtime_tools"),
            patch("ytdlp_helper.downloader.find_ytdlp_executable", return_value="yt-dlp.exe"),
            patch("ytdlp_helper.downloader.find_ffmpeg_location", return_value=None),
            patch(
                "ytdlp_helper.downloader.subprocess.Popen",
                return_value=FakeProcess(
                    ["ERROR: Could not copy Chrome cookie database. See https://github.com/yt-dlp/yt-dlp/issues/7271\n"],
                    return_code=1,
                ),
            ),
        ):
            with self.assertRaisesRegex(RuntimeError, "Paste updated cookies.txt text"):
                service.download(
                    DownloadRequest(
                        url="https://www.youtube.com/watch?v=abc123",
                        preset="best-video",
                    ),
                    lambda *_args: None,
                    lambda *_args: None,
                )

    def test_youtube_format_token_error_points_to_runtime_update_and_logs(self) -> None:
        message = _humanize_error(
            "ERROR: [youtube] abc: Requested format is not available. "
            "Some formats were skipped because a GVS PO Token was not provided."
        )

        self.assertIn("Help > Update", message)
        self.assertIn("activity log", message)

    def test_missing_ytdlp_executable_is_actionable(self) -> None:
        service = DownloadService(_paths())

        with patch("ytdlp_helper.downloader.find_ytdlp_executable", return_value=None):
            with self.assertRaisesRegex(RuntimeError, "could not be installed"):
                service._build_command(
                    DownloadRequest(
                        url="https://www.youtube.com/watch?v=abc123",
                        preset="best-video",
                    )
                )

    def test_get_ytdlp_version_runs_executable(self) -> None:
        service = DownloadService(_paths())
        completed = subprocess.CompletedProcess(
            args=["yt-dlp.exe", "--version"],
            returncode=0,
            stdout="2026.03.17\n",
            stderr="",
        )

        with (
            patch("ytdlp_helper.downloader.find_ytdlp_executable", return_value="yt-dlp.exe"),
            patch("ytdlp_helper.downloader.subprocess.run", return_value=completed) as run,
        ):
            version = service.get_ytdlp_version()

        run.assert_called_once_with(
            ["yt-dlp.exe", "--version"],
            capture_output=True,
            text=True,
            check=False,
            **_hidden_subprocess_kwargs(),
        )
        self.assertEqual(version, "2026.03.17")

    def test_hidden_subprocess_kwargs_uses_create_no_window_when_available(self) -> None:
        with patch("ytdlp_helper.downloader.subprocess.CREATE_NO_WINDOW", 134217728, create=True):
            self.assertEqual(_hidden_subprocess_kwargs(), {"creationflags": 134217728})

    def test_builds_normal_video_without_playlist_context(self) -> None:
        service = DownloadService(_paths(), organize_by_channel=True)

        with (
            patch("ytdlp_helper.downloader.find_ytdlp_executable", return_value="yt-dlp.exe"),
            patch("ytdlp_helper.downloader.find_ffmpeg_location", return_value=None),
        ):
            command = service._build_command(
                DownloadRequest(
                    url="https://www.youtube.com/watch?v=abc123",
                    preset="best-video",
                )
            )

        self.assert_option(
            command,
            "--output",
            "%(channel,uploader&{}|.)s/%(title)s.%(ext)s",
        )

    def test_output_template(self) -> None:
        self.assertEqual(
            _output_template("%(title)s.%(ext)s", True),
            "%(channel,uploader&{}|.)s/%(title)s.%(ext)s",
        )

    def test_output_template_without_organization(self) -> None:
        self.assertEqual(
            _output_template("%(title)s.%(ext)s", False),
            "%(title)s.%(ext)s",
        )

    def test_sanitize_path_segment_replaces_invalid_chars(self) -> None:
        self.assertEqual(_sanitize_path_segment('A/B:C<D>E"F|G?H*I'), "A-B-C-D-E-F-G-H-I")

    def test_sanitize_path_segment_trims_trailing_dots_and_spaces(self) -> None:
        self.assertEqual(_sanitize_path_segment("My Playlist... "), "My Playlist")

    def test_sanitize_path_segment_falls_back_for_empty_result(self) -> None:
        self.assertEqual(_sanitize_path_segment(""), "")
        self.assertEqual(_sanitize_path_segment("..."), "_")
        self.assertEqual(_sanitize_path_segment("/\\"), "--")

    def test_sanitize_path_segment_strips_control_characters(self) -> None:
        self.assertEqual(_sanitize_path_segment("Hello\x00\x1fWorld"), "Hello--World")

    def assert_option(self, command: list[str], option: str, expected_value: str) -> None:
        self.assertIn(option, command)
        self.assertEqual(command[command.index(option) + 1], expected_value)


def _paths() -> AppPaths:
    root = Path(tempfile.mkdtemp())
    return AppPaths(
        data_dir=root / "data",
        settings_file=root / "data" / "settings.json",
        archive_file=root / "data" / "download-archive.txt",
        cookies_file=root / "data" / "cookies.txt",
        logs_dir=root / "data" / "logs",
        activity_log_file=root / "data" / "logs" / "activity.log",
        tools_dir=root / "data" / "tools",
        ytdlp_executable=root / "data" / "tools" / "yt-dlp.exe",
        ffmpeg_dir=root / "data" / "tools" / "ffmpeg",
        ffmpeg_executable=root / "data" / "tools" / "ffmpeg" / "ffmpeg.exe",
        ffprobe_executable=root / "data" / "tools" / "ffmpeg" / "ffprobe.exe",
        deno_dir=root / "data" / "tools" / "deno",
        deno_executable=root / "data" / "tools" / "deno" / "deno.exe",
        download_dir=root / "downloads",
    )


if __name__ == "__main__":
    unittest.main()
