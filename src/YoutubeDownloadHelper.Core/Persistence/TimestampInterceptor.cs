using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>
/// Stamps <see cref="Entity.CreatedAt"/> and <see cref="Entity.UpdatedAt"/> on <c>SaveChanges</c>. A timestamp the
/// caller set explicitly is kept. <c>ExecuteUpdate</c> bypasses the change tracker, so it must set
/// <c>updated_at</c> itself.
/// </summary>
internal sealed class TimestampInterceptor(TimeProvider time) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        foreach (var entry in context.ChangeTracker.Entries<Entity>())
        {
            var entity = entry.Entity;
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entity.CreatedAt == default)
                    {
                        entity.CreatedAt = now;
                    }

                    if (entity.UpdatedAt == default)
                    {
                        entity.UpdatedAt = entity.CreatedAt;
                    }

                    break;

                case EntityState.Modified when !entry.Property(e => e.UpdatedAt).IsModified:
                    entity.UpdatedAt = now;
                    break;
            }
        }
    }
}
