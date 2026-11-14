using System.Globalization;

namespace YoutubeDownloadHelper.Contracts;

/// <summary>
/// The engine API's routes (PLAN.md §6). Every route is absolute, relative to the discovery file's
/// <see cref="Discovery.EngineDiscovery.BaseUrl"/>, and needs <c>Authorization: Bearer &lt;token&gt;</c>.
/// </summary>
public static class ApiRoutes
{
    /// <summary>The API version in the routes and in <see cref="Discovery.EngineDiscovery.ApiVersion"/>.</summary>
    public const int Version = 1;

    public const string Base = "/api/v1";

    /// <summary><c>GET</c>: an <see cref="Info.EngineInfo"/>.</summary>
    public const string Info = Base + "/info";

    /// <summary>
    /// <c>POST</c>: stops the engine gracefully (running jobs go back to the queue, partial files are kept). Answers
    /// <c>202 Accepted</c> before it stops.
    /// </summary>
    public const string EngineShutdown = Base + "/engine/shutdown";

    /// <summary>
    /// <c>POST</c> <see cref="Resolve.ResolveRequest"/>: a video's metadata, or a playlist's or channel's entries, with
    /// duplicate flags (<see cref="Resolve.ResolveResponse"/>). Downloads nothing.
    /// </summary>
    public const string Resolve = Base + "/resolve";

    /// <summary>
    /// <c>GET</c> (<c>?state=&amp;search=&amp;cursor=&amp;limit=</c>): the queue, an <see cref="Queue.ItemList"/>.
    /// <c>POST</c>: adds items (<see cref="Queue.AddItemsRequest"/>, answered with an <see cref="Queue.AddItemsResponse"/>).
    /// </summary>
    public const string Items = Base + "/items";

    /// <summary><c>GET</c>: an <see cref="Queue.ItemDetail"/>. <c>DELETE</c> (<c>?deleteFiles=true</c>): removes the item.</summary>
    public const string Item = Items + "/{id}";

    /// <summary><c>POST</c>: pauses the item.</summary>
    public const string ItemPause = Item + "/pause";

    /// <summary><c>POST</c>: resumes a paused item.</summary>
    public const string ItemResume = Item + "/resume";

    /// <summary><c>POST</c>: requeues a failed or waiting item, with its retry count reset.</summary>
    public const string ItemRetry = Item + "/retry";

    /// <summary><c>POST</c> <see cref="Queue.MoveItemRequest"/>: moves the item in the queue.</summary>
    public const string ItemMove = Item + "/move";

    /// <summary><c>POST</c>: pauses the whole queue (the <c>pausedByUser</c> block).</summary>
    public const string QueuePause = Base + "/queue/pause";

    /// <summary><c>POST</c>: resumes the whole queue.</summary>
    public const string QueueResume = Base + "/queue/resume";

    /// <summary><c>GET</c> (<c>?search=&amp;cursor=&amp;limit=</c>): completed and skipped items, a <see cref="Queue.HistoryList"/>.</summary>
    public const string History = Base + "/history";

    /// <summary><c>GET</c>: the <see cref="Settings.SettingsInfo"/>. <c>PATCH</c> <see cref="Settings.SettingsPatch"/>: changes them.</summary>
    public const string Settings = Base + "/settings";

    /// <summary><c>GET</c>: the YouTube login status, an <see cref="Auth.AuthInfo"/>.</summary>
    public const string Auth = Base + "/auth";

    /// <summary>
    /// <c>PUT</c> <see cref="Auth.SetCookiesRequest"/>: stores new cookies (a login or a <c>cookies.txt</c> import),
    /// answered with a <see cref="Auth.SetCookiesResponse"/>. <c>DELETE</c>: logs out (deletes the stored cookies).
    /// </summary>
    public const string AuthCookies = Auth + "/cookies";

    /// <summary><c>GET</c>: the managed tools in detail, a <see cref="Tools.ToolList"/>.</summary>
    public const string Tools = Base + "/tools";

    /// <summary>
    /// <c>POST</c>: checks every tool (smoke tests, then updates) in the background. Answers <c>202 Accepted</c> at once;
    /// installs report on the event stream, and <see cref="Events.EventTypes.ToolsChanged"/> follows when the check is done.
    /// </summary>
    public const string ToolsCheck = Tools + "/check";

    /// <summary>
    /// <c>POST</c>: makes the tool's previous version current again. <c>204</c>; <c>404</c>
    /// <see cref="Tools.ToolCodes.UnknownTool"/>; <c>409</c> <see cref="Tools.ToolCodes.NotManaged"/>,
    /// <see cref="Tools.ToolCodes.NoPreviousVersion"/> or <see cref="Tools.ToolCodes.PreviousVersionBroken"/>.
    /// </summary>
    public const string ToolRollback = Tools + "/{tool}/rollback";

    /// <summary><c>GET</c>: the event stream (<c>text/event-stream</c>, PLAN.md §6.3); see <see cref="Events.EventTypes"/>.</summary>
    public const string Events = Base + "/events";

    /// <summary>The OpenAPI document, for non-.NET clients.</summary>
    public const string OpenApiDocument = "/openapi/v1.json";

    /// <summary>One tool's path of a tool route, e.g. <c>ToolPath(ToolRollback, "yt-dlp")</c> is <c>/api/v1/tools/yt-dlp/rollback</c>.</summary>
    public static string ToolPath(string route, string tool)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(tool);
        return route.Replace("{tool}", Uri.EscapeDataString(tool), StringComparison.Ordinal);
    }

    /// <summary>One item's path of an item route, e.g. <c>ItemPath(ItemPause, 5)</c> is <c>/api/v1/items/5/pause</c>.</summary>
    public static string ItemPath(string route, long id)
    {
        ArgumentNullException.ThrowIfNull(route);
        return route.Replace("{id}", id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
