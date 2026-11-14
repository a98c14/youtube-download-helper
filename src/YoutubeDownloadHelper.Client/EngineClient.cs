using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using YoutubeDownloadHelper.Client.Events;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Contracts.Discovery;
using YoutubeDownloadHelper.Contracts.Info;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Contracts.Resolve;
using YoutubeDownloadHelper.Contracts.Settings;
using YoutubeDownloadHelper.Contracts.Tools;

namespace YoutubeDownloadHelper.Client;

/// <summary>
/// A typed client of one running engine's API (PLAN.md §6.2), from its discovery file. Every method throws an
/// <see cref="EngineApiException"/> when the engine answers with an error, and an <see cref="HttpRequestException"/>
/// when it cannot be reached. For a live view that survives reconnects and engine restarts, use
/// <see cref="Store.EngineStore"/>.
/// </summary>
public sealed class EngineClient : IDisposable
{
    private const string EventStreamMediaType = "text/event-stream";

    private static readonly IReadOnlyDictionary<string, string> EmptyArgs = new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly HttpClient _http;
    private readonly EngineClientOptions _options;

    public EngineClient(EngineDiscovery engine, EngineClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Engine = engine;
        _options = options ?? new EngineClientOptions();
        _http = _options.Handler is { } handler ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        _http.BaseAddress = new Uri(engine.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);

        // Requests time out on their own (RequestTimeout); the event stream must not.
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", engine.Token);
    }

    /// <summary>The engine this client calls.</summary>
    public EngineDiscovery Engine { get; }

    /// <summary><c>GET /info</c>: versions, tools, login status and queue blocks. Also the health check.</summary>
    public Task<EngineInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
        GetAsync<EngineInfo>(ApiRoutes.Info, cancellationToken);

    /// <summary><c>POST /engine/shutdown</c>: asks the engine to stop gracefully; returns before it has stopped.</summary>
    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.EngineShutdown, null, cancellationToken);

    /// <summary><c>POST /resolve</c>: a video's metadata, or a playlist's or channel's entries. Downloads nothing.</summary>
    public Task<ResolveResponse> ResolveAsync(ResolveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<ResolveResponse>(HttpMethod.Post, ApiRoutes.Resolve, request, cancellationToken);
    }

    /// <summary><c>POST /items</c>: queues URLs or video IDs, with a result per input.</summary>
    public Task<AddItemsResponse> AddItemsAsync(AddItemsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<AddItemsResponse>(HttpMethod.Post, ApiRoutes.Items, request, cancellationToken);
    }

    /// <summary><c>GET /items</c>: one page of the queue, in queue order.</summary>
    /// <param name="states">Only items in these states; all by default.</param>
    /// <param name="search">Only items whose title, channel or URL contains this.</param>
    /// <param name="cursor">The previous page's <see cref="ItemList.NextCursor"/>.</param>
    /// <param name="limit">The page size (the engine's default when null).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task<ItemList> GetItemsAsync(
        IEnumerable<ItemState>? states = null,
        string? search = null,
        string? cursor = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryBuilder()
            .Add("state", states is null ? null : string.Join(',', states.Select(s => JsonNamingPolicy.CamelCase.ConvertName(s.ToString()))))
            .Add("search", search)
            .Add("cursor", cursor)
            .Add("limit", limit?.ToString(CultureInfo.InvariantCulture));
        return GetAsync<ItemList>(ApiRoutes.Items + query, cancellationToken);
    }

    /// <summary>
    /// The whole queue: every page of <c>GET /items</c>, with the first page's <see cref="ItemList.Sequence"/>.
    /// Applying the events after that sequence brings it up to date, also when the queue changed between the pages.
    /// </summary>
    public async Task<ItemList> GetQueueSnapshotAsync(int pageSize = ResolveLimits.MaxEntries, CancellationToken cancellationToken = default)
    {
        var first = await GetItemsAsync(limit: pageSize, cancellationToken: cancellationToken);
        if (first.NextCursor is null)
        {
            return first;
        }

        var items = new List<ItemInfo>(first.Items);
        var seen = items.Select(i => i.Id).ToHashSet();
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await GetItemsAsync(cursor: cursor, limit: pageSize, cancellationToken: cancellationToken);
            items.AddRange(page.Items.Where(i => seen.Add(i.Id)));
            cursor = page.NextCursor;
        }

        return new ItemList(items, first.Sequence, null);
    }

    /// <summary><c>GET /items/{id}</c>: an item with its transitions, runs and output.</summary>
    public Task<ItemDetail> GetItemAsync(long id, CancellationToken cancellationToken = default) =>
        GetAsync<ItemDetail>(ApiRoutes.ItemPath(ApiRoutes.Item, id), cancellationToken);

    /// <summary><c>POST /items/{id}/pause</c>.</summary>
    public Task PauseItemAsync(long id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.ItemPath(ApiRoutes.ItemPause, id), null, cancellationToken);

    /// <summary><c>POST /items/{id}/resume</c>.</summary>
    public Task ResumeItemAsync(long id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.ItemPath(ApiRoutes.ItemResume, id), null, cancellationToken);

    /// <summary><c>POST /items/{id}/retry</c>.</summary>
    public Task RetryItemAsync(long id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.ItemPath(ApiRoutes.ItemRetry, id), null, cancellationToken);

    /// <summary><c>POST /items/{id}/move</c>.</summary>
    public Task MoveItemAsync(long id, MoveItemRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, ApiRoutes.ItemPath(ApiRoutes.ItemMove, id), request, cancellationToken);
    }

    /// <summary><c>DELETE /items/{id}</c>: removes the item; with <paramref name="deleteFiles"/> its downloaded file too.</summary>
    public Task RemoveItemAsync(long id, bool deleteFiles = false, CancellationToken cancellationToken = default)
    {
        var query = new QueryBuilder().Add("deleteFiles", deleteFiles ? "true" : null);
        return SendAsync(HttpMethod.Delete, ApiRoutes.ItemPath(ApiRoutes.Item, id) + query, null, cancellationToken);
    }

    /// <summary><c>POST /queue/pause</c>.</summary>
    public Task PauseQueueAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.QueuePause, null, cancellationToken);

    /// <summary><c>POST /queue/resume</c>.</summary>
    public Task ResumeQueueAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.QueueResume, null, cancellationToken);

    /// <summary><c>GET /history</c>: one page of completed and skipped items, newest first.</summary>
    public Task<HistoryList> GetHistoryAsync(
        string? search = null,
        string? cursor = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryBuilder()
            .Add("search", search)
            .Add("cursor", cursor)
            .Add("limit", limit?.ToString(CultureInfo.InvariantCulture));
        return GetAsync<HistoryList>(ApiRoutes.History + query, cancellationToken);
    }

    /// <summary><c>GET /settings</c>.</summary>
    public Task<SettingsInfo> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<SettingsInfo>(ApiRoutes.Settings, cancellationToken);

    /// <summary><c>PATCH /settings</c>: changes the settings the patch names, all or nothing; answers the new settings.</summary>
    public Task<SettingsInfo> PatchSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return SendAsync<SettingsInfo>(HttpMethod.Patch, ApiRoutes.Settings, patch, cancellationToken);
    }

    /// <summary><c>GET /auth</c>: the YouTube login status.</summary>
    public Task<AuthInfo> GetAuthAsync(CancellationToken cancellationToken = default) =>
        GetAsync<AuthInfo>(ApiRoutes.Auth, cancellationToken);

    /// <summary><c>PUT /auth/cookies</c>: stores the login window's cookies or an imported <c>cookies.txt</c>.</summary>
    public Task<SetCookiesResponse> SetCookiesAsync(SetCookiesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<SetCookiesResponse>(HttpMethod.Put, ApiRoutes.AuthCookies, request, cancellationToken);
    }

    /// <summary><c>DELETE /auth/cookies</c>: logs out.</summary>
    public Task LogOutAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, ApiRoutes.AuthCookies, null, cancellationToken);

    /// <summary><c>GET /tools</c>: the managed tools with their current and previous versions and last update checks.</summary>
    public Task<ToolList> GetToolsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ToolList>(ApiRoutes.Tools, cancellationToken);

    /// <summary>
    /// <c>POST /tools/check</c>: starts a check of every tool (smoke tests, then updates) and returns at once. Installs
    /// show up as <c>tools.install</c> events; <c>tools.changed</c> follows when the check is done.
    /// </summary>
    public Task CheckToolsAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.ToolsCheck, null, cancellationToken);

    /// <summary><c>POST /tools/{tool}/rollback</c>: makes the tool's previous version current again.</summary>
    /// <param name="tool">One of <see cref="ToolNames"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task RollbackToolAsync(string tool, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        return SendAsync(HttpMethod.Post, ApiRoutes.ToolPath(ApiRoutes.ToolRollback, tool), null, cancellationToken);
    }

    /// <summary>
    /// <c>GET /events</c>: opens the event stream. With <paramref name="lastEventId"/> the engine replays every later
    /// event, or starts with <c>resync</c> when it no longer can; without, the stream starts with <c>resync</c>.
    /// </summary>
    public async Task<EventStreamConnection> OpenEventStreamAsync(long? lastEventId = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Relative(ApiRoutes.Events));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(EventStreamMediaType));
        if (lastEventId is { } id)
        {
            request.Headers.Add("Last-Event-ID", id.ToString(CultureInfo.InvariantCulture));
        }

        HttpResponseMessage response;
        using (var timeout = Timeout(cancellationToken))
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }

        try
        {
            await EnsureSuccessAsync(response, cancellationToken);
            var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new EventStreamConnection(response, body, _options.EventStreamIdleTimeout, _options.TimeProvider);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public void Dispose() => _http.Dispose();

    private static string Relative(string route) => route.TrimStart('/');

    private Task<T> GetAsync<T>(string route, CancellationToken cancellationToken) =>
        SendAsync<T>(HttpMethod.Get, route, null, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        using var response = await SendRawAsync(method, route, body, timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
        return await response.Content.ReadFromJsonAsync<T>(EngineJson.Options, timeout.Token)
            ?? throw new JsonException($"The engine answered {method} {route} with an empty body.");
    }

    private async Task SendAsync(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        using var response = await SendRawAsync(method, route, body, timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, Relative(route));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: EngineJson.Options);
        }

        return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    private CancellationTokenSource Timeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        return timeout;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        EngineMessage? error = null;
        try
        {
            if (response.Content.Headers.ContentType?.MediaType is { } mediaType
                && mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase))
            {
                error = await response.Content.ReadFromJsonAsync<EngineMessage>(EngineJson.Options, cancellationToken);
            }
        }
        catch (JsonException)
        {
            // Not an engine error body; the status code says what happened.
        }

        throw new EngineApiException(
            response.StatusCode,
            error is { Code: not null, Message: not null } ? error with { Args = error.Args ?? EmptyArgs } : EngineApiException.Fallback(response.StatusCode));
    }

    /// <summary>A query string; values are escaped, null values left out.</summary>
    private sealed class QueryBuilder
    {
        private readonly StringBuilder _query = new();

        public QueryBuilder Add(string name, string? value)
        {
            if (value is not null)
            {
                _query.Append(_query.Length == 0 ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
            }

            return this;
        }

        public override string ToString() => _query.ToString();
    }
}
