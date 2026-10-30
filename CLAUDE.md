# Youtube İndirgeç

A Windows download manager for YouTube built around a headless engine (`YoutubeDownloadHelper.Engine.exe`) and a
WPF client (`YoutubeDownloadHelper.exe`). The design lives in [`docs/PLAN.md`](docs/PLAN.md); implementation is
tracked in issue #30.

"Youtube İndirgeç" is the only user-facing name. Everything internal (projects, assemblies, namespaces, data
folder, pack ID) uses `YoutubeDownloadHelper`. Names come from `YoutubeDownloadHelper.Contracts.AppIdentity`;
never derive a path or ID from the display name (it is non-ASCII).

## Build and test

SDK pinned in `global.json` (10.0.403, `latestFeature`). Tests are xUnit v3 on Microsoft.Testing.Platform.

```powershell
dotnet build -c Release
dotnet test -c Release

# Core, Engine and Client tests also run under tr-TR (as CI does). The canary test fails if the culture
# switch did not take effect. Environment variables can't set the culture on Windows; --culture can.
$env:EXPECTED_TEST_CULTURE = 'tr-TR'
foreach ($p in 'Core', 'Engine', 'Client') {
    dotnet test -c Release --no-build --project "tests/YoutubeDownloadHelper.$p.Tests" --culture tr-TR
}
Remove-Item Env:EXPECTED_TEST_CULTURE

# One project, or a filter
dotnet test --project tests/YoutubeDownloadHelper.Core.Tests --filter-method "*Queue*"
```

Regenerate the single `Initial` migration after changing an entity (a test fails while the model and migration
differ):

```powershell
dotnet tool restore
dotnet ef migrations remove --project src/YoutubeDownloadHelper.Core
dotnet ef migrations add Initial --project src/YoutubeDownloadHelper.Core --output-dir Persistence/Migrations
```

Build warnings are errors, and that includes analyzers and the enforced style rules. NuGet audit warnings
(NU1901–NU1904) stay warnings so a new advisory doesn't break unrelated PRs.

## Layout

| Project | TFM | Purpose |
|---|---|---|
| `src/YoutubeDownloadHelper.Contracts` | `net10.0` | DTOs, enums, event types, route constants, `AppIdentity`. No dependencies. |
| `src/YoutubeDownloadHelper.Core` | `net10.0-windows` | Engine logic and persistence. No ASP.NET, no WPF/WinForms. |
| `src/YoutubeDownloadHelper.Engine` | `net10.0-windows` | Web SDK `WinExe` host → `YoutubeDownloadHelper.Engine.exe`. |
| `src/YoutubeDownloadHelper.Client` | `net10.0` | Typed API client. |
| `src/YoutubeDownloadHelper.App` | `net10.0-windows10.0.19041.0` | WPF app → `YoutubeDownloadHelper.exe`. |
| `tests/YoutubeDownloadHelper.{Core,Engine,Client,App}.Tests` | as tested project | Unit and integration tests. |
| `tests/YoutubeDownloadHelper.ArchTests` | `net10.0` | Dependency rules. |
| `tools/YoutubeDownloadHelper.FakeYtDlp` | `net10.0` | Fake yt-dlp for tests and UI development. |
| `spikes/` | | Throwaway M0 harnesses. Opted out of the repo's props, CPM and analyzers. |

Core and Engine target `net10.0-windows` for Job Objects, DPAPI, file ACLs and named mutexes.

**Allowed references:** App → Contracts + Client. Engine → Core + Contracts. Core → Contracts. Client → Contracts.
Test projects may reference anything. `YoutubeDownloadHelper.ArchTests` enforces this against the restore graph
(`obj/project.assets.json`), so an unused reference still fails:
- App and Client never reference Core or Engine, not even transitively.
- Core uses no `Microsoft.AspNetCore.*` package or framework, and neither Core nor Engine uses WPF/WinForms.
- Contracts has no project or package dependencies.
- Nothing in `src/` references Engine, `tests/` or `tools/`.

## Code organisation

- Feature folders; the namespace equals the folder (`YoutubeDownloadHelper.Core.Queue`).
  - Core: `Queue/`, `Scheduling/`, `Runner/`, `Presets/`, `Errors/`, `Archive/`, `Tools/`, `Auth/`, `Watchers/`,
    `Persistence/`.
  - Engine: `Hosting/`, `Endpoints/`, `Events/`.
  - Contracts: one folder per feature, plus `AppIdentity` and the routes class at the root.
  - App: `Views/`, `ViewModels/`, `Services/`, `Resources/`.
- Each Core feature exposes an `IServiceCollection` extension (`AddQueue()`, …) that `Engine/Program.cs` calls.
  Each API area is a `Map<Area>Endpoints()` extension in `Engine/Endpoints/`.
- Core may use Contracts types (enums, codes, DTOs it returns). EF entities never become Contracts types.
- Create folders when the first file goes into them, not up front.

## Build conventions

- `Directory.Build.props`: nullable, implicit usings, warnings as errors, latest analyzers, code style enforced in
  build, `Product` = `Youtube İndirgeç` (tested against `AppIdentity.DisplayName`), default `Version`.
- `Directory.Packages.props`: central package management with transitive pinning. Add versions there, never in a
  csproj. No NuGet lock files.
- `tests/Directory.Build.props`: test projects are `Exe`, reference `xunit.v3` and have `global using Xunit;`. Use
  xUnit's built-in `Assert` (no commercially licensed assertion library). `Verify.XunitV3` is available for
  snapshot tests.
- `.editorconfig`: errors for CA1304, CA1305, CA1307, CA1309, CA1310, CA1311 (culture), IDE0161 (file-scoped
  namespaces) and IDE0005 (unused usings). Other style rules are IDE suggestions.

## Shared conventions (every PR)

1. **Branching:** branch from `rewrite`, open the PR against `rewrite`, one issue per PR, `Closes #N` in the PR
   body.
2. **Plan first:** read the `docs/PLAN.md` sections cited in the issue. If the implementation forces a change to
   the plan, update `PLAN.md` in the same PR and say so in the PR description.
3. **Schema:** until the first public release there is a single `Initial` EF Core migration. Regenerate it instead
   of stacking new migrations.
4. **Contracts:** add DTOs in new files per feature. Route constants live in the routes class. Avoid reshaping
   DTOs owned by other features.
5. **Culture:** all non-display string operations are ordinal/invariant (the CA rules above are errors). Core,
   Engine and Client tests must pass under `tr-TR`.
6. **Tests:** no real network or YouTube in automated tests. Use `YoutubeDownloadHelper.FakeYtDlp`, fake HTTP
   feeds and an injectable `TimeProvider`.
7. **Language-neutral engine:** user-facing information from the engine is `{ code, args, message }`. UI strings
   go in both `.resx` files.
8. **Seams for later chunks:** when a later issue owns a piece (e.g. `IJobCookieProvider`, `IToolUpdateTrigger`,
   `IToolHealth`), define a narrow interface with a no-op default rather than stubbing behaviour ad hoc. Name it
   in the PR.
9. **Spikes:** results go in `docs/spikes/<topic>.md`, and resulting decisions are merged into `PLAN.md`.
