using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Scheduling;

public static class SchedulingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="Scheduler"/>, also as the <see cref="IQueueBlockProvider"/> (replacing the default of
    /// <c>AddQueue()</c>), the <see cref="SchedulerOptions"/>, and unless already registered <see cref="DriveDiskSpace"/>
    /// as the <see cref="IDiskSpace"/> and <see cref="HttpNetworkProbe"/> as the <see cref="INetworkProbe"/>. Needs
    /// logging, <c>AddPersistence()</c>, <c>AddQueue()</c>, <c>AddErrors()</c>, <c>AddRunner()</c>, <c>AddTools()</c>
    /// and <c>AddAuth()</c>. The host calls <see cref="Scheduler.StartAsync"/> after the queue actor has started, and
    /// <see cref="Scheduler.StopAsync"/> when it stops.
    /// </summary>
    public static IServiceCollection AddScheduling(this IServiceCollection services, SchedulerOptions? options = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options ?? new SchedulerOptions());
        services.TryAddSingleton<IDiskSpace, DriveDiskSpace>();
        services.TryAddSingleton<INetworkProbe, HttpNetworkProbe>();
        services.TryAddSingleton(sp => new SchedulerSeams(
            sp.GetRequiredService<IToolHealth>(),
            sp.GetRequiredService<IToolUpdateTrigger>(),
            sp.GetRequiredService<IAuthVerificationTrigger>(),
            sp.GetRequiredService<IJobCookieProvider>(),
            sp.GetRequiredService<IDiskSpace>(),
            sp.GetRequiredService<INetworkProbe>()));
        services.TryAddSingleton<Scheduler>();
        services.Replace(ServiceDescriptor.Singleton<IQueueBlockProvider>(sp => sp.GetRequiredService<Scheduler>()));
        return services;
    }
}
