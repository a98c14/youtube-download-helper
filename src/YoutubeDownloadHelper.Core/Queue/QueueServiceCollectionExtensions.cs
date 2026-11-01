using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YoutubeDownloadHelper.Core.Events;

namespace YoutubeDownloadHelper.Core.Queue;

public static class QueueServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="EventBus"/> and the <see cref="QueueActor"/>. Needs <c>AddPersistence()</c> and logging.
    /// The host calls <see cref="QueueActor.StartAsync"/> once, after the database is initialized.
    /// </summary>
    public static IServiceCollection AddQueue(this IServiceCollection services)
    {
        services.TryAddSingleton<EventBus>();
        services.AddSingleton<QueueActor>();
        return services;
    }
}
