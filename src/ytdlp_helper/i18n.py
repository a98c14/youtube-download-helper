from __future__ import annotations

from string import Formatter


DEFAULT_LANGUAGE = "tr"
SUPPORTED_LANGUAGES = ("tr", "en")

LANGUAGE_LABELS = {
    "tr": {
        "tr": "Türkçe",
        "en": "İngilizce",
    },
    "en": {
        "tr": "Turkish",
        "en": "English",
    },
}

TRANSLATIONS = {
    "en": {
        "app.title": "YouTube Download Helper",
        "menu.file": "File",
        "menu.settings": "Settings...",
        "menu.activity_log": "Activity Log",
        "menu.help": "Help",
        "menu.update": "Update...",
        "header.subtitle": "Paste a YouTube URL, choose a preset, and download with optional pasted cookies.",
        "field.url": "URL",
        "field.archive": "Archive",
        "field.preset": "Preset",
        "field.cookies": "Cookies",
        "field.downloads_folder": "Downloads Folder",
        "button.check": "Check",
        "button.clear": "Clear",
        "button.paste_cookies": "Paste Cookies",
        "button.browse": "Browse...",
        "button.download": "Download",
        "button.download_playlist": "Download Playlist",
        "button.save": "Save",
        "button.cancel": "Cancel",
        "settings.title": "Settings",
        "settings.language": "Language",
        "preset.best-video": "Best Video",
        "preset.video-1080p": "Video 1080p",
        "preset.video-720p": "Video 720p",
        "preset.video-480p": "Video 480p",
        "preset.audio-mp3": "Audio MP3",
        "preset.audio-m4a": "Audio M4A",
        "archive.not_checked": "Not checked",
        "archive.archived": "Archived",
        "archive.not_archived": "Not archived",
        "archive.unsupported_video_url": "Enter an individual YouTube video URL",
        "status.ready": "Ready",
        "status.speed": "Speed: {speed}",
        "status.speed_empty": "Speed: --",
        "status.cookies_saved": "Cookies saved",
        "status.ready_to_restart": "Ready to restart",
        "status.download_completed": "Download completed",
        "status.updating_runtime_tools": "Updating runtime tools",
        "status.checking_ytdlp": "Checking yt-dlp",
        "status.checking_ffmpeg": "Checking ffmpeg",
        "status.installing_ytdlp": "Installing yt-dlp",
        "status.installing_ffmpeg": "Installing ffmpeg",
        "status.preparing_download": "Preparing download",
        "status.resolving_video": "Resolving video information",
        "status.resolving_playlist": "Resolving playlist",
        "status.finalizing_file": "Finalizing file",
        "status.archive_skipped": "Already downloaded; skipped by archive",
        "status.downloading_percent": "Downloading {percent}%",
        "status.downloading_tool_percent": "Downloading {tool_name} {percent}%",
        "status.downloading_tool_mb": "Downloading {tool_name} {size} MB",
        "status.checking_app_release": "Checking latest app release",
        "status.downloading_app_update": "Downloading app update",
        "cookies.none": "No cookies saved",
        "cookies.saved": "Saved {timestamp}",
        "dialog.clear_archive.title": "Clear archive status",
        "dialog.clear_archive.message": "Remove this video from download-archive.txt? Downloaded media files will not be deleted.",
        "dialog.task_in_progress.title": "Task in progress",
        "dialog.task_in_progress.message": "Please wait for the current task to finish.",
        "dialog.download_failed.title": "Download failed",
        "dialog.download_finished.title": "Download finished",
        "dialog.update_failed.title": "Update failed",
        "dialog.restart_to_update.title": "Restart to update",
        "dialog.restart_to_update.message": "Runtime tools were updated and a new app version is ready. Restart now to finish updating?",
        "dialog.update_ready.title": "Update ready",
        "dialog.update_ready.message": "The app update is staged. Restart later to finish updating.",
        "dialog.update_finished.title": "Update finished",
        "dialog.choose_downloads.title": "Choose Downloads Folder",
        "dialog.downloads_required.title": "Downloads folder required",
        "dialog.downloads_required.message": "Choose a downloads folder before starting.",
        "dialog.downloads_unavailable.title": "Downloads folder unavailable",
        "dialog.downloads_unavailable.message": "Could not use downloads folder: {error}",
        "message.clipboard_no_cookies": "Clipboard does not contain cookies.txt text.",
    },
    "tr": {
        "app.title": "YouTube Download Helper",
        "menu.file": "Dosya",
        "menu.settings": "Ayarlar...",
        "menu.activity_log": "Etkinlik Günlüğü",
        "menu.help": "Yardım",
        "menu.update": "Güncelle...",
        "header.subtitle": "Bir YouTube URL'si yapıştırın, bir ön ayar seçin ve gerekirse yapıştırılmış çerezlerle indirin.",
        "field.url": "URL",
        "field.archive": "Arşiv",
        "field.preset": "Ön Ayar",
        "field.cookies": "Çerezler",
        "field.downloads_folder": "İndirme Klasörü",
        "button.check": "Kontrol Et",
        "button.clear": "Temizle",
        "button.paste_cookies": "Çerez Yapıştır",
        "button.browse": "Gözat...",
        "button.download": "İndir",
        "button.download_playlist": "Oynatma Listesini İndir",
        "button.save": "Kaydet",
        "button.cancel": "İptal",
        "settings.title": "Ayarlar",
        "settings.language": "Dil",
        "preset.best-video": "En İyi Video",
        "preset.video-1080p": "Video 1080p",
        "preset.video-720p": "Video 720p",
        "preset.video-480p": "Video 480p",
        "preset.audio-mp3": "Ses MP3",
        "preset.audio-m4a": "Ses M4A",
        "archive.not_checked": "Kontrol edilmedi",
        "archive.archived": "Arşivde",
        "archive.not_archived": "Arşivde değil",
        "archive.unsupported_video_url": "Tekil bir YouTube video URL'si girin",
        "status.ready": "Hazır",
        "status.speed": "Hız: {speed}",
        "status.speed_empty": "Hız: --",
        "status.cookies_saved": "Çerezler kaydedildi",
        "status.ready_to_restart": "Yeniden başlatmaya hazır",
        "status.download_completed": "İndirme tamamlandı",
        "status.updating_runtime_tools": "Çalışma zamanı araçları güncelleniyor",
        "status.checking_ytdlp": "yt-dlp kontrol ediliyor",
        "status.checking_ffmpeg": "ffmpeg kontrol ediliyor",
        "status.installing_ytdlp": "yt-dlp yükleniyor",
        "status.installing_ffmpeg": "ffmpeg yükleniyor",
        "status.preparing_download": "İndirme hazırlanıyor",
        "status.resolving_video": "Video bilgileri alınıyor",
        "status.resolving_playlist": "Oynatma listesi alınıyor",
        "status.finalizing_file": "Dosya sonlandırılıyor",
        "status.archive_skipped": "Daha önce indirilmiş; arşiv nedeniyle atlandı",
        "status.downloading_percent": "İndiriliyor {percent}%",
        "status.downloading_tool_percent": "{tool_name} indiriliyor {percent}%",
        "status.downloading_tool_mb": "{tool_name} indiriliyor {size} MB",
        "status.checking_app_release": "Son uygulama sürümü kontrol ediliyor",
        "status.downloading_app_update": "Uygulama güncellemesi indiriliyor",
        "cookies.none": "Kayıtlı çerez yok",
        "cookies.saved": "Kaydedildi {timestamp}",
        "dialog.clear_archive.title": "Arşiv durumunu temizle",
        "dialog.clear_archive.message": "Bu videoyu download-archive.txt dosyasından kaldırmak istiyor musunuz? İndirilmiş medya dosyaları silinmez.",
        "dialog.task_in_progress.title": "İşlem devam ediyor",
        "dialog.task_in_progress.message": "Lütfen mevcut işlem bitene kadar bekleyin.",
        "dialog.download_failed.title": "İndirme başarısız",
        "dialog.download_finished.title": "İndirme tamamlandı",
        "dialog.update_failed.title": "Güncelleme başarısız",
        "dialog.restart_to_update.title": "Güncellemek için yeniden başlat",
        "dialog.restart_to_update.message": "Çalışma zamanı araçları güncellendi ve yeni uygulama sürümü hazır. Güncellemeyi tamamlamak için şimdi yeniden başlatılsın mı?",
        "dialog.update_ready.title": "Güncelleme hazır",
        "dialog.update_ready.message": "Uygulama güncellemesi hazırlandı. Tamamlamak için daha sonra yeniden başlatın.",
        "dialog.update_finished.title": "Güncelleme tamamlandı",
        "dialog.choose_downloads.title": "İndirme Klasörü Seç",
        "dialog.downloads_required.title": "İndirme klasörü gerekli",
        "dialog.downloads_required.message": "Başlamadan önce bir indirme klasörü seçin.",
        "dialog.downloads_unavailable.title": "İndirme klasörü kullanılamıyor",
        "dialog.downloads_unavailable.message": "İndirme klasörü kullanılamadı: {error}",
        "message.clipboard_no_cookies": "Panoda cookies.txt metni yok.",
    },
}


def normalize_language(language: str | None) -> str:
    if language in SUPPORTED_LANGUAGES:
        return language
    return DEFAULT_LANGUAGE


def translate(language: str, key: str, **params: object) -> str:
    normalized_language = normalize_language(language)
    template = TRANSLATIONS.get(normalized_language, {}).get(key)
    if template is None:
        template = TRANSLATIONS["en"].get(key, key)
    if not params:
        return template
    return template.format(**_format_params(template, params))


def language_label(language: str, target_language: str) -> str:
    normalized_language = normalize_language(language)
    normalized_target = normalize_language(target_language)
    return LANGUAGE_LABELS.get(normalized_language, LANGUAGE_LABELS["en"])[normalized_target]


def language_options(language: str) -> list[tuple[str, str]]:
    return [(language_label(language, code), code) for code in SUPPORTED_LANGUAGES]


def _format_params(template: str, params: dict[str, object]) -> dict[str, object]:
    names = {field_name for _, field_name, _, _ in Formatter().parse(template) if field_name}
    return {name: value for name, value in params.items() if name in names}
