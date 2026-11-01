using System.Globalization;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>The engine runs with the invariant culture (PLAN.md §7.5); only clients format for display.</summary>
public static class EngineCulture
{
    public static void Apply()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
