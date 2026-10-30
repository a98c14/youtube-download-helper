using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> only. Regenerate the single <c>Initial</c> migration (until the first public release)
/// from the repo root:
/// <code>
/// dotnet tool restore
/// dotnet ef migrations remove --project src/YoutubeDownloadHelper.Core
/// dotnet ef migrations add Initial --project src/YoutubeDownloadHelper.Core --output-dir Persistence/Migrations
/// </code>
/// </summary>
internal sealed class DesignTimeStateDbContextFactory : IDesignTimeDbContextFactory<StateDbContext>
{
    public StateDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<StateDbContext>()
            .UseSqlite(StateDatabase.ConnectionString("design-time.db"))
            .Options);
}
