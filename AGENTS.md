# Repository Guidelines

## Project Structure & Module Organization
- Application code lives in `src/ytdlp_helper/`.
- `app.py` contains the Tkinter desktop UI, `downloader.py` wraps `yt-dlp`, `browser_profiles.py` discovers local Chrome/Edge profiles, and `config.py` manages app data paths and settings.
- Tests live in `tests/` and mirror the runtime modules with `test_*.py` files.
- Build automation lives in `scripts/`, currently `scripts/build_portable.ps1`.
- Generated artifacts belong in `build/` and `dist/`; do not hand-edit them.

## Build, Test, and Development Commands
- `python -m pip install -r requirements.txt`: install runtime and packaging dependencies.
- `python -m ytdlp_helper`: run the desktop app locally from source.
- `python -m unittest discover -s tests -v`: run the test suite.
- `powershell -ExecutionPolicy Bypass -File .\scripts\build_portable.ps1`: build the portable Windows bundle in `dist/YouTube Download Helper/`.

## Coding Style & Naming Conventions
- Target Python 3.13+ and keep code ASCII unless an existing file already uses Unicode.
- Use 4-space indentation and standard library-first imports.
- Prefer small focused modules and explicit dataclasses for structured state.
- Use `snake_case` for functions, variables, and module names; use `PascalCase` for classes.
- Keep UI strings clear and user-facing errors actionable.

## Testing Guidelines
- Use `unittest` with files named `test_*.py` and test methods named `test_*`.
- Add tests for new downloader options, browser/profile discovery behavior, and config persistence.
- Keep tests deterministic; mock filesystem or `yt-dlp` interactions instead of hitting external services.
- Run `python -m unittest discover -s tests -v` before shipping changes.

## Commit & Pull Request Guidelines
- Use concise imperative commit messages such as `Fix packaged launcher import` or `Add playlist archive coverage`.
- Keep commits focused on one logical change.
- PRs should include a short summary, test results, and screenshots for UI-visible changes.
- Call out packaging changes explicitly when they affect `dist/`, `build/`, or bundled `ffmpeg`.
- After completing work, update `todo.md` so it reflects the current state before handing off.

## Security & Configuration Tips
- Do not commit exported cookies, local browser data, or `%LOCALAPPDATA%` app-state files.
- Authenticated downloads should rely only on `--cookies-from-browser` against the user’s local profile.
- Treat `dist/` output as disposable build output; rebuild instead of patching packaged files manually.

## Agent skills

### Issue tracker

Issues and PRDs are tracked in GitHub Issues. See `docs/agents/issue-tracker.md`.

### Triage labels

Use the five canonical triage labels documented in `docs/agents/triage-labels.md`.

### Domain docs

This is a single-context repository. See `docs/agents/domain.md`.
