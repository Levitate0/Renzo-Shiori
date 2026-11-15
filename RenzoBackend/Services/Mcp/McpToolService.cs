using System.Text.Json;
using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Downloads;
using RenzoBackend.Services.Jobs;
using RenzoBackend.Services.Mcp.Abstractions;
using RenzoBackend.Services.Providers;
using RenzoBackend.Services.Scrobbling;
using RenzoBackend.Services.Search;
using RenzoBackend.Services.Series;
using RenzoBackend.Services.Settings;
using RenzoBackend.Services.Status;
using Microsoft.EntityFrameworkCore;

namespace RenzoBackend.Services.Mcp;

/// <summary>
/// Core MCP service handling JSON-RPC 2.0 message processing, SSE transport,
/// and routing tool calls to existing Renzo domain services with
/// user-level permission enforcement.
///
/// Supports the standard MCP lifecycle:
///   1. initialize → notifications/initialized (handshake)
///   2. ping (health check)
///   3. tools/list, tools/call (discovery + operation)
///   4. notifications/cancelled (optional cancellation)
/// </summary>
public class McpToolService
{
    private const string McpProtocolVersion = "2024-11-05";
    private const string ServerName = "Renzo MCP Server";
    private const string ServerVersion = "1.0.0";

    // All tool names known to the server. Sent in the initialize response
    // so the client knows what's available. Per-user filtering happens in
    // tools/list.
    private static readonly string[] AllToolNames =
    [
        "list_series", "get_series", "get_latest_series", "add_series", "remove_series",
        "update_series", "update_all_series", "verify_series",
        "search_series", "list_search_sources",
        "install_extension", "uninstall_extension", "list_extensions",
        "list_downloads", "get_download_metrics", "pause_series_downloads",
        "get_health_alerts", "get_series_health", "get_provider_health",
        "list_jobs", "get_job_status", "cancel_job", "enqueue_job",
        "get_settings", "update_settings",
        "list_scrobbler_providers", "get_scrobbler_status"
    ];

    private readonly IMcpPermissionService _permissionService;
    private readonly AppDbContext _db;
    private readonly ILogger<McpToolService> _logger;

    // Injected domain services
    private readonly SeriesQueryService _seriesQuery;
    private readonly SeriesCommandService _seriesCommand;
    private readonly SeriesArchiveService _seriesArchive;
    private readonly SearchQueryService _searchQuery;
    private readonly ProviderManagerService _providerManager;
    private readonly DownloadQueryService _downloadQuery;
    private readonly JobManagementService _jobManagement;
    private readonly SettingsService _settings;
    private readonly ScrobblerProviderFactory _scrobblerFactory;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public McpToolService(
        IMcpPermissionService permissionService,
        AppDbContext db,
        ILogger<McpToolService> logger,
        SeriesQueryService seriesQuery,
        SeriesCommandService seriesCommand,
        SeriesArchiveService seriesArchive,
        SearchQueryService searchQuery,
        ProviderManagerService providerManager,
        DownloadQueryService downloadQuery,
        JobManagementService jobManagement,
        SettingsService settings,
        ScrobblerProviderFactory scrobblerFactory)
    {
        _permissionService = permissionService;
        _db = db;
        _logger = logger;
        _seriesQuery = seriesQuery;
        _seriesCommand = seriesCommand;
        _seriesArchive = seriesArchive;
        _searchQuery = searchQuery;
        _providerManager = providerManager;
        _downloadQuery = downloadQuery;
        _jobManagement = jobManagement;
        _settings = settings;
        _scrobblerFactory = scrobblerFactory;
    }

    // ──────────────────────────────────────────────────────────────
    // JSON-RPC 2.0 Message Handling
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Processes a JSON-RPC 2.0 message and returns a JSON response,
    /// or <c>null</c> for notifications (no response body expected).
    ///
    /// The caller (<see cref="Controllers.McpController"/>) must return
    /// 204 No Content when this returns null.
    /// </summary>
    public async Task<JsonElement?> HandleJsonRpcAsync(JsonElement request, UserEntity user, CancellationToken token)
    {
        try
        {
            if (!request.TryGetProperty("method", out var methodEl))
                return CreateErrorResponse(null, -32600, "Invalid Request: missing 'method'");

            string method = methodEl.GetString() ?? "";
            JsonElement? id = request.TryGetProperty("id", out var idEl) ? idEl : null;

            // If no "id" is present, this is a JSON-RPC notification.
            // Notifications expect no response body from the server.
            bool isNotification = id == null;

            switch (method)
            {
                // ── Init Phase ──
                case "initialize":
                    JsonElement? j = HandleInitialize(user, request, id);
                    return j;

                // ── Notifications (no response) ──
                case "notifications/initialized":
                    _logger.LogInformation("MCP client initialized");
                    return null;

                case "notifications/cancelled":
                    // Optional cancellation — acknowledge silently
                    return null;

                // ── Health ──
                case "ping":
                    return HandlePing(id);

                // ── Tool Discovery ──
                case "tools/list":
                    return await HandleToolsListAsync(user, id, token);

                // ── Tool Operation ──
                case "tools/call":
                    return await HandleToolCallAsync(request, user, id, token);

                // ── Resource Discovery ──
                case "resources/list":
                    return await HandleResourcesListAsync(user, id, token);

                // ── Resource Read (for UI rendering) ──
                case "resources/read":
                    return await HandleResourceReadAsync(request, user, id, token);

                default:
                    // For unknown notifications, return nothing (per spec).
                    // For unknown requests, return an error.
                    if (isNotification)
                    {
                        _logger.LogWarning("Unknown notification method: '{Method}'", method);
                        return null;
                    }
                    return CreateErrorResponse(id, -32601, $"Method not found: '{method}'");
            }
        }
        catch (McpPermissionException ex)
        {
            _logger.LogWarning(ex, "MCP permission denied");
            return CreateErrorResponse(
                request.TryGetProperty("id", out var errId) ? errId : null,
                -32003, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP internal error");
            return CreateErrorResponse(
                request.TryGetProperty("id", out var excId) ? excId : null,
                -32603, $"Internal error: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles SSE transport for the MCP protocol.
    /// Sends only the "endpoint" event (absolute URL) so the client knows
    /// where to POST JSON-RPC messages. Tools are NOT pushed over SSE;
    /// the client discovers them via <c>tools/list</c>.
    /// </summary>
    public async Task HandleSseTransportAsync(HttpContext httpContext, UserEntity user, CancellationToken token)
    {
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers["Cache-Control"] = "no-cache";
        httpContext.Response.Headers["Connection"] = "keep-alive";

        // Build the absolute URL for the message endpoint.
        // MCP spec requires a full URL, not a relative path.
        string baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}";
        string messageUrl = $"{baseUrl}/{user.OpdsPath}/mcp/message";

        _logger.LogInformation("MCP SSE opened for user '{Username}', message endpoint: {MessageUrl}",
            user.Username, messageUrl);

        await httpContext.Response.WriteAsync($"event: endpoint\ndata: {messageUrl}\n\n", token);
        await httpContext.Response.Body.FlushAsync(token);

        // Keep the SSE connection alive until the client disconnects
        try
        {
            // Periodically send keepalive comments to prevent proxies from closing
            // the connection. MCP allows comments (lines starting with ":") in SSE.
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                await httpContext.Response.WriteAsync(": keepalive\n\n", token);
                await httpContext.Response.Body.FlushAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal
            _logger.LogDebug("MCP SSE connection closed for user '{Username}'", user.Username);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // initialize
    // ──────────────────────────────────────────────────────────────

    private JsonElement HandleInitialize(UserEntity user, JsonElement request, JsonElement? id)
    {
        // ── Protocol version negotiation ──
        // Read the client's requested protocol version from params.
        // Per MCP spec, if we support that version, we must echo it back.
        // The current MCP protocol version as of 2025 is "2025-11-25".
        string? clientProtocolVersion = null;
        if (request.TryGetProperty("params", out var paramsEl) &&
            paramsEl.TryGetProperty("protocolVersion", out var verEl))
        {
            clientProtocolVersion = verEl.GetString();
        }

        // Accept any protocol version the client sends. We support the current
        // version "2025-11-25" and earlier "2024-11-05". Echo back what the
        // client requested so it knows we accepted it.
        string resolvedVersion = clientProtocolVersion ?? "2025-11-25";

        // ── Capabilities ──
        // tools: {} means we support tools/list and tools/call.
        // listChanged: true means we notify clients when the tool list changes.
        // resources: {} means we support resources/read (for UI rendering).
        var toolsCap = new Dictionary<string, object>
        {
            ["listChanged"] = true
        };
        var resourcesCap = new Dictionary<string, object>
        {
            ["listChanged"] = true
        };

        // UI extension: allows clients to render HTML UIs for tool results.
        // MIME type text/html;profile=mcp-app is the standard for MCP App UI.
        var uiExt = new Dictionary<string, object>
        {
            ["mimeTypes"] = new List<string> { "text/html;profile=mcp-app" }
        };
        var extensions = new Dictionary<string, object>
        {
            ["io.modelcontextprotocol/ui"] = uiExt
        };

        var capabilities = new Dictionary<string, object>
        {
            ["tools"] = toolsCap,
            ["resources"] = resourcesCap,
            ["extensions"] = extensions
        };

        // ── Server info ──
        // title is a human-readable name for the server (used in ChatGPT UI).
        var serverInfo = new Dictionary<string, object>
        {
            ["name"] = ServerName,
            ["title"] = "Renzo",
            ["version"] = ServerVersion
        };

        var result = new Dictionary<string, object>
        {
            ["protocolVersion"] = resolvedVersion,
            ["capabilities"] = capabilities,
            ["serverInfo"] = serverInfo
        };

        var response = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.GetInt32() ?? 1,
            ["result"] = result
        };

        _logger.LogInformation(
            "MCP initialized: protocol={Protocol}, client={ClientVersion}, user={User}",
            resolvedVersion, clientProtocolVersion ?? "unknown", user.Username);

        return JsonSerializer.SerializeToElement(response, _jsonOptions);
    }

    // ──────────────────────────────────────────────────────────────
    // ping
    // ──────────────────────────────────────────────────────────────

    private static JsonElement HandlePing(JsonElement? id)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id?.GetInt32() ?? 1,
            result = new { }
        };
        return JsonSerializer.SerializeToElement(response, _jsonOptions);
    }

    // ──────────────────────────────────────────────────────────────
    // resources/list
    // ──────────────────────────────────────────────────────────────

    private async Task<JsonElement> HandleResourcesListAsync(UserEntity user, JsonElement? id, CancellationToken token)
    {
        var resources = new List<Dictionary<string, object>>
        {
            new()
            {
                ["uri"] = "renzo://library",
                ["name"] = "Library",
                ["description"] = "Series library overview",
                ["mimeType"] = "text/html;profile=mcp-app"
            },
            new()
            {
                ["uri"] = "renzo://status",
                ["name"] = "Status",
                ["description"] = "System health and status overview",
                ["mimeType"] = "text/html;profile=mcp-app"
            }
        };

        var response = new
        {
            jsonrpc = "2.0",
            id = id?.GetInt32() ?? 1,
            result = new { resources }
        };
        return JsonSerializer.SerializeToElement(response, _jsonOptions);
    }

    // ──────────────────────────────────────────────────────────────
    // resources/read
    // ──────────────────────────────────────────────────────────────

    private async Task<JsonElement> HandleResourceReadAsync(JsonElement request, UserEntity user, JsonElement? id, CancellationToken token)
    {
        if (!request.TryGetProperty("params", out var paramsEl) ||
            !paramsEl.TryGetProperty("uri", out var uriEl))
        {
            return CreateErrorResponse(id, -32602, "Missing required parameter: 'uri'");
        }

        string uri = uriEl.GetString() ?? "";

        if (uri == "renzo://library")
            return await BuildLibraryResourceResponseAsync(user, uri, id, token);

        if (uri == "renzo://status")
            return await BuildStatusResourceResponseAsync(user, uri, id, token);

        if (uri.StartsWith("renzo://series/"))
            return await BuildSeriesResourceResponseAsync(user, uri, id, token);

        return CreateErrorResponse(id, -32602, $"Unknown resource URI: '{uri}'");
    }

    private async Task<JsonElement> BuildLibraryResourceResponseAsync(UserEntity user, string uri, JsonElement? id, CancellationToken token)
    {
        var series = await _seriesQuery.GetLibraryAsync(user.Id, user.Level == UserLevel.Owner, token);
        var items = string.Join("", series.Select(s => $@"
            <div style='padding:8px;border-bottom:1px solid #eee;display:flex;align-items:center;gap:8px'>
                <strong>{System.Net.WebUtility.HtmlEncode(s.Title ?? "Untitled")}</strong>
                <span style='color:#666;font-size:0.85em'>{s.ChapterCount} chapters</span>
            </div>
        "));
        var html = $@"<!DOCTYPE html><html><head><meta charset='utf-8'><title>Renzo Shiori Library</title></head>
            <body style='font-family:-apple-system,BlinkMacSystemFont,sans-serif;margin:0;padding:16px'>
            <h2 style='margin-top:0'>Library ({series.Count})</h2>
            <div style='border:1px solid #ddd;border-radius:8px;overflow:hidden'>
                {items}
            </div>
            </body></html>";

        return BuildResourceResponse(id, uri, html);
    }

    private async Task<JsonElement> BuildSeriesResourceResponseAsync(UserEntity user, string uri, JsonElement? id, CancellationToken token)
    {
        // Parse series ID from uri like renzo://series/{guid}
        var parts = uri.Split('/');
        if (parts.Length < 4 || !Guid.TryParse(parts[3], out var seriesId))
            return CreateErrorResponse(id, -32602, $"Invalid series URI: '{uri}'");

        var seriesDto = await _seriesQuery.GetSeriesAsync(seriesId, user.Id, user.Level == UserLevel.Owner, token);
        if (seriesDto == null || seriesDto.Id == Guid.Empty)
            return CreateErrorResponse(id, -32602, "Series not found");

        var html = $@"<!DOCTYPE html><html><head><meta charset='utf-8'><title>{System.Net.WebUtility.HtmlEncode(seriesDto.Title)}</title></head>
            <body style='font-family:-apple-system,BlinkMacSystemFont,sans-serif;margin:0;padding:16px'>
            <h2 style='margin-top:0'>{System.Net.WebUtility.HtmlEncode(seriesDto.Title)}</h2>
            <p style='color:#666'>{seriesDto.ChapterCount} chapters</p>
            </body></html>";

        return BuildResourceResponse(id, uri, html);
    }

    private async Task<JsonElement> BuildStatusResourceResponseAsync(UserEntity user, string uri, JsonElement? id, CancellationToken token)
    {
        var alerts = await ScopeAlertsAsync(user, await _db.HealthStatuses
            .Where(h => h.IsActive)
            .AsNoTracking()
            .ToListAsync(token), token);
        var alertItems = string.Join("", alerts.Select(a => $@"
            <div style='padding:6px;border-bottom:1px solid #eee'>
                <span style='color:{(a.Level == HealthStatusLevel.Red ? "#d32f2f" : "#f57c00")}'>&#9679;</span>
                {System.Net.WebUtility.HtmlEncode(a.Message ?? "No message")}
            </div>
        "));
        var html = $@"<!DOCTYPE html><html><head><meta charset='utf-8'><title>Renzo Shiori Status</title></head>
            <body style='font-family:-apple-system,BlinkMacSystemFont,sans-serif;margin:0;padding:16px'>
            <h2 style='margin-top:0'>Health Status</h2>
            <div style='border:1px solid #ddd;border-radius:8px;overflow:hidden'>
                {(alerts.Count > 0 ? alertItems : "<div style='padding:8px;color:#666'>All systems healthy</div>")}
            </div>
            </body></html>";

        return BuildResourceResponse(id, uri, html);
    }

    private static JsonElement BuildResourceResponse(JsonElement? id, string uri, string html)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id?.GetInt32() ?? 1,
            result = new
            {
                contents = new[]
                {
                    new
                    {
                        uri,
                        mimeType = "text/html;profile=mcp-app",
                        text = html
                    }
                }
            }
        };
        return JsonSerializer.SerializeToElement(response, _jsonOptions);
    }

    // ──────────────────────────────────────────────────────────────
    // tools/list
    // ──────────────────────────────────────────────────────────────

    private async Task<JsonElement> HandleToolsListAsync(UserEntity user, JsonElement? id, CancellationToken token)
    {
        var permittedTools = _permissionService.GetPermittedTools(user);
        var tools = permittedTools.Select(t => new
        {
            name = t,
            description = GetToolDescription(t),
            inputSchema = GetToolInputSchema(t)
        }).ToList();

        var response = new
        {
            jsonrpc = "2.0",
            id = id?.GetInt32() ?? 1,
            result = new { tools }
        };
        return JsonSerializer.SerializeToElement(response, _jsonOptions);
    }

    // ──────────────────────────────────────────────────────────────
    // tools/call Routing
    // ──────────────────────────────────────────────────────────────

    private async Task<JsonElement> HandleToolCallAsync(JsonElement request, UserEntity user, JsonElement? id, CancellationToken token)
    {
        if (!request.TryGetProperty("params", out var paramsEl))
            return CreateErrorResponse(id, -32602, "Invalid params");

        string? toolName = paramsEl.TryGetProperty("name", out var nameEl)
            ? nameEl.GetString()
            : null;

        if (string.IsNullOrEmpty(toolName))
            return CreateErrorResponse(id, -32602, "Missing tool name");

        var args = paramsEl.TryGetProperty("arguments", out var argsEl)
            ? argsEl
            : JsonSerializer.SerializeToElement(new { });

        // Enforce permission before execution
        _permissionService.EnsureLevel(user, toolName);

        object? result = toolName switch
        {
            "list_series" => await ListSeriesAsync(user, args, token),
            "get_series" => await GetSeriesAsync(user, args, token),
            "get_latest_series" => await GetLatestSeriesAsync(user, args, token),
            "add_series" => await AddSeriesAsync(user, args, token),
            "remove_series" => await RemoveSeriesAsync(user, args, token),
            "update_series" => await UpdateSeriesAsync(user, args, token),
            "update_all_series" => await UpdateAllSeriesAsync(user, args, token),
            "verify_series" => await VerifySeriesAsync(user, args, token),
            "search_series" => await SearchSeriesAsync(user, args, token),
            "list_search_sources" => await ListSearchSourcesAsync(user, args, token),
            "install_extension" => await InstallExtensionAsync(user, args, token),
            "uninstall_extension" => await UninstallExtensionAsync(user, args, token),
            "list_extensions" => await ListExtensionsAsync(user, args, token),
            "list_downloads" => await ListDownloadsAsync(user, args, token),
            "get_download_metrics" => await GetDownloadMetricsAsync(user, args, token),
            "pause_series_downloads" => await PauseSeriesDownloadsAsync(user, args, token),
            "get_health_alerts" => await GetHealthAlertsAsync(user, args, token),
            "get_series_health" => await GetSeriesHealthAsync(user, args, token),
            "get_provider_health" => await GetProviderHealthAsync(user, args, token),
            "list_jobs" => await ListJobsAsync(user, args, token),
            "get_job_status" => await GetJobStatusAsync(user, args, token),
            "cancel_job" => await CancelJobAsync(user, args, token),
            "enqueue_job" => await EnqueueJobAsync(user, args, token),
            "get_settings" => await GetSettingsAsync(user, args, token),
            "update_settings" => await UpdateSettingsAsync(user, args, token),
            "list_scrobbler_providers" => await ListScrobblerProvidersAsync(user, args, token),
            "get_scrobbler_status" => await GetScrobblerStatusAsync(user, args, token),
            _ => throw new McpPermissionException($"Unknown tool: '{toolName}'")
        };

        var mcpResult = FormatToolResult(result);
        var response = new { jsonrpc = "2.0", id = id?.GetInt32() ?? 1, result = mcpResult };
        return JsonSerializer.SerializeToElement(response, _jsonOptions);
    }

    // ──────────────────────────────────────────────────────────────
    // Series Tools
    // ──────────────────────────────────────────────────────────────

    private async Task<object> ListSeriesAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        var series = await _seriesQuery.GetLibraryAsync(user.Id, user.Level == UserLevel.Owner, token);
        return new { success = true, count = series.Count, data = series };
    }

    private async Task<object> GetSeriesAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        if (!args.TryGetProperty("id", out var idEl))
            return new { success = false, error = "Missing required parameter: 'id'" };

        if (!Guid.TryParse(idEl.GetString(), out var guid))
            return new { success = false, error = "'id' must be a valid GUID" };

        var series = await _seriesQuery.GetSeriesAsync(guid, user.Id, user.Level == UserLevel.Owner, token);
        return series != null
            ? new { success = true, data = series }
            : new { success = false, error = "Series not found" };
    }

    private async Task<object> GetLatestSeriesAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        int start = args.TryGetProperty("start", out var startEl) ? startEl.GetInt32() : 0;
        int count = args.TryGetProperty("count", out var countEl) ? countEl.GetInt32() : 20;
        string? sourceId = args.TryGetProperty("sourceId", out var srcEl) ? srcEl.GetString() : null;
        string? keyword = args.TryGetProperty("keyword", out var kwEl) ? kwEl.GetString() : null;

        var latest = await _seriesQuery.GetLatestAsync(RenzoBackend.Extensions.PageLimits.ClampStart(start), RenzoBackend.Extensions.PageLimits.ClampCount(count, RenzoBackend.Extensions.PageLimits.Latest), sourceId, keyword, null, null, user.Id, user.Level == UserLevel.Owner, token);
        return new { success = true, count = latest.Count, data = latest };
    }

    private async Task<object> AddSeriesAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        var seriesJson = args.GetRawText();
        var augmented = JsonSerializer.Deserialize<AugmentedResponseDto>(seriesJson, _jsonOptions);
        if (augmented == null)
            return new { success = false, error = "Invalid series data" };

        var id = await _seriesCommand.AddSeriesAsync(augmented, user.Id, token);
        return new { success = true, seriesId = id.ToString() };
    }

    private async Task<object> RemoveSeriesAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        if (!args.TryGetProperty("id", out var idEl))
            return new { success = false, error = "Missing required parameter: 'id'" };

        if (!Guid.TryParse(idEl.GetString(), out var guid))
            return new { success = false, error = "'id' must be a valid GUID" };

        if (await DenySeriesAsync(user, guid, token) is { } deny) return deny;
        bool alsoPhysical = args.TryGetProperty("alsoPhysical", out var physEl) && physEl.GetBoolean();
        await _seriesCommand.DeleteSeriesAsync(guid, alsoPhysical, token);
        return new { success = true, message = "Series deleted" };
    }

    private async Task<object> UpdateSeriesAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        if (!args.TryGetProperty("id", out var idEl))
            return new { success = false, error = "Missing required parameter: 'id'" };

        if (!Guid.TryParse(idEl.GetString(), out var guid))
            return new { success = false, error = "'id' must be a valid GUID" };

        if (await DenySeriesAsync(user, guid, token) is { } deny) return deny;
        // Same call as POST /api/series/refresh. The old GetChapters { SeriesId }
        // job never ran: GetChapters takes one source's id, not a series id.
        int queued = await _seriesCommand.RefreshSeriesMetadataAsync(guid, null, token);
        return new { success = true, queued, message = "Update enqueued" };
    }

    private async Task<object> UpdateAllSeriesAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        var jobId = await _jobManagement.EnqueueJobAsync(JobType.UpdateAllSeries, new { },
            Priority.Low, token: token);
        return new { success = true, jobId = jobId.ToString(), message = "Update all series enqueued" };
    }

    private async Task<object> VerifySeriesAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        if (!args.TryGetProperty("id", out var idEl))
            return new { success = false, error = "Missing required parameter: 'id'" };

        if (!Guid.TryParse(idEl.GetString(), out var guid))
            return new { success = false, error = "'id' must be a valid GUID" };

        if (await DenySeriesAsync(user, guid, token) is { } deny) return deny;
        var result = await _seriesArchive.VerifyIntegrityAsync(guid, false, token);
        return new { success = true, data = result };
    }

    // ──────────────────────────────────────────────────────────────
    // Search Tools
    // ──────────────────────────────────────────────────────────────

    private async Task<object> SearchSeriesAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        if (!args.TryGetProperty("keyword", out var keywordEl))
            return new { success = false, error = "Missing required parameter: 'keyword'" };

        string keyword = keywordEl.GetString() ?? "";
        var languages = args.TryGetProperty("languages", out var langEl)
            ? JsonSerializer.Deserialize<List<string>>(langEl.GetRawText(), _jsonOptions) ?? new List<string> { "en" }
            : new List<string> { "en" };

        var sources = args.TryGetProperty("sources", out var srcEl)
            ? JsonSerializer.Deserialize<List<string>>(srcEl.GetRawText(), _jsonOptions)
            : null;

        var results = await _searchQuery.SearchSeriesAsync(keyword, languages, sources, 0.1f, user.Id, token);
        return new { success = true, count = results.Count, data = results };
    }

    private async Task<object> ListSearchSourcesAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        var sources = await _searchQuery.GetAvailableSearchSourcesAsync(user.Id, token);
        return new { success = true, count = sources.Count, data = sources };
    }

    // ──────────────────────────────────────────────────────────────
    // Extension Tools
    // ──────────────────────────────────────────────────────────────

    private async Task<object> ListExtensionsAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        var extensions = await _providerManager.GetProvidersAsync(user.Id, token);
        return new { success = true, count = extensions.Count, data = extensions };
    }

    private async Task<object> InstallExtensionAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        if (!args.TryGetProperty("packageName", out var pkgEl))
            return new { success = false, error = "Missing required parameter: 'packageName'" };

        string pkgName = pkgEl.GetString() ?? "";
        string? repoName = args.TryGetProperty("repoName", out var repoEl) ? repoEl.GetString() : null;
        bool force = args.TryGetProperty("force", out var forceEl) && forceEl.GetBoolean();
        // Same rule as POST /api/provider/install: force recompiles over the live build.
        if (force && user.Level < UserLevel.Owner)
            return new { success = false, error = "Only the owner can force a reinstall." };

        var success = await _providerManager.InstallProviderAsync(pkgName, repoName, force, token);
        return new { success, message = success ? "Extension installed" : "Failed to install extension" };
    }

    private async Task<object> UninstallExtensionAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        if (!args.TryGetProperty("packageName", out var pkgEl))
            return new { success = false, error = "Missing required parameter: 'packageName'" };

        string pkgName = pkgEl.GetString() ?? "";
        var success = await _providerManager.DisableProviderAsync(pkgName, token);
        return new { success, message = success ? "Extension uninstalled" : "Failed to uninstall extension" };
    }

    // ──────────────────────────────────────────────────────────────
    // Download Tools
    // ──────────────────────────────────────────────────────────────

    private async Task<object> ListDownloadsAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        var status = args.TryGetProperty("status", out var statusEl)
            ? Enum.Parse<QueueStatus>(statusEl.GetString() ?? "Waiting", true)
            : QueueStatus.Waiting;

        int maxCount = args.TryGetProperty("maxCount", out var countEl) ? countEl.GetInt32() : 50;
        string? keyword = args.TryGetProperty("keyword", out var kwEl) ? kwEl.GetString() : null;

        var downloads = await _downloadQuery.GetDownloadsAsync(status, RenzoBackend.Extensions.PageLimits.ClampCount(maxCount, RenzoBackend.Extensions.PageLimits.Downloads), keyword, user.Id, user.Level == UserLevel.Owner, token);
        return new { success = true, data = downloads };
    }

    private async Task<object> GetDownloadMetricsAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        var metrics = await _downloadQuery.GetDownloadsMetricsAsync(user.Id, user.Level == UserLevel.Owner, token);
        return new { success = true, data = metrics };
    }

    private async Task<object> PauseSeriesDownloadsAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        if (!args.TryGetProperty("seriesId", out var idEl))
            return new { success = false, error = "Missing required parameter: 'seriesId'" };

        if (!Guid.TryParse(idEl.GetString(), out var guid))
            return new { success = false, error = "'seriesId' must be a valid GUID" };

        bool pause = args.TryGetProperty("pause", out var pauseEl) ? pauseEl.GetBoolean() : true;

        var series = await _db.Series.FindAsync(new object[] { guid }, token);
        if (series == null)
            return new { success = false, error = "Series not found" };
        if (!SeriesQueryService.CanAccessSeries(series.OwnerId, user.Id, IsOwner(user)))
            return new { success = false, error = NotYourSeries };

        series.PauseDownloads = pause;
        await _db.SaveChangesAsync(token);
        return new { success = true, message = pause ? "Downloads paused" : "Downloads resumed" };
    }

    // ──────────────────────────────────────────────────────────────
    // Health Tools
    // ──────────────────────────────────────────────────────────────

    private async Task<object> GetHealthAlertsAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        var alerts = await ScopeAlertsAsync(user, await _db.HealthStatuses
            .Where(h => h.IsActive)
            .AsNoTracking()
            .ToListAsync(token), token);

        return new { success = true, count = alerts.Count, data = alerts };
    }

    private async Task<object> GetSeriesHealthAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        if (!args.TryGetProperty("id", out var idEl))
            return new { success = false, error = "Missing required parameter: 'id'" };

        if (!Guid.TryParse(idEl.GetString(), out var guid))
            return new { success = false, error = "'id' must be a valid GUID" };
        if (await DenySeriesAsync(user, guid, token) is { } deny) return deny;

        var alerts = await _db.HealthStatuses
            .Where(h => h.IsActive && h.TargetType == HealthStatusTargetType.Series && h.TargetId == guid)
            .AsNoTracking()
            .ToListAsync(token);

        return new { success = true, data = alerts };
    }

    private async Task<object> GetProviderHealthAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        var alerts = await ScopeAlertsAsync(user, await _db.HealthStatuses
            .Where(h => h.IsActive && h.TargetType == HealthStatusTargetType.Provider)
            .AsNoTracking()
            .ToListAsync(token), token);

        return new { success = true, count = alerts.Count, data = alerts };
    }

    // ──────────────────────────────────────────────────────────────
    // Job Tools
    // ──────────────────────────────────────────────────────────────

    private async Task<object> ListJobsAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        List<JobEntity> jobs;
        if (IsOwner(user))
        {
            jobs = await _db.Jobs
                .AsNoTracking()
                .OrderByDescending(j => j.NextExecution)
                .Take(50)
                .ToListAsync(token);
        }
        else
        {
            // Below Owner: only the chapter checks of series in the caller's own
            // library (the same set /api/status shows them), never server-wide jobs.
            HashSet<Guid> mine = await AccessibleProviderIdsAsync(user, token);
            jobs = (await _db.Jobs
                    .AsNoTracking()
                    .Where(j => j.JobType == JobType.GetChapters)
                    .ToListAsync(token))
                .Where(j => Guid.TryParse(j.Key, out Guid pid) && mine.Contains(pid))
                .OrderByDescending(j => j.NextExecution)
                .Take(50)
                .ToList();
        }

        return new { success = true, count = jobs.Count, data = jobs };
    }

    private async Task<object> GetJobStatusAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        if (!args.TryGetProperty("jobId", out var idEl))
            return new { success = false, error = "Missing required parameter: 'jobId'" };

        if (!Guid.TryParse(idEl.GetString(), out var guid))
            return new { success = false, error = "'jobId' must be a valid GUID" };

        var job = await _db.Jobs.FindAsync(new object[] { guid }, token);
        return job != null && await CanAccessJobAsync(user, job, token)
            ? new { success = true, data = job }
            : new { success = false, error = "Job not found" };
    }

    private async Task<object> CancelJobAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        if (!args.TryGetProperty("jobId", out var idEl))
            return new { success = false, error = "Missing required parameter: 'jobId'" };

        if (!Guid.TryParse(idEl.GetString(), out var guid))
            return new { success = false, error = "'jobId' must be a valid GUID" };

        var job = await _db.Jobs.FindAsync(new object[] { guid }, token);
        if (job == null)
            return new { success = false, error = "Job not found" };
        // These are the recurring schedules (daily update, extension updates, every
        // source's chapter check). Below Owner only a chapter check of a series in
        // the caller's own library may be removed; the server-wide ones are Owner's.
        if (!await CanAccessJobAsync(user, job, token))
            return new { success = false, error = "Only the owner can cancel server-wide jobs." };

        _db.Jobs.Remove(job);
        await _db.SaveChangesAsync(token);
        return new { success = true, message = "Job cancelled" };
    }

    private async Task<object> EnqueueJobAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        if (!args.TryGetProperty("jobType", out var typeEl))
            return new { success = false, error = "Missing required parameter: 'jobType'" };

        if (!Enum.TryParse<JobType>(typeEl.GetString(), true, out var jobType) || !Enum.IsDefined(jobType))
            return new { success = false, error = $"Invalid jobType. Valid values: {string.Join(", ", Enum.GetNames<JobType>())}" };

        var priority = Priority.Normal;
        if (args.TryGetProperty("priority", out var prioEl) &&
            (!Enum.TryParse(prioEl.GetString(), true, out priority) || !Enum.IsDefined(priority)))
            return new { success = false, error = $"Invalid priority. Valid values: {string.Join(", ", Enum.GetNames<Priority>())}" };

        // Raw job parameters reach the job runners unchecked (ScanLocalFiles takes a
        // path, Download a whole chapter record, ImportSeries/UpdateExtensions act
        // server-wide), so below Owner only the jobs that have a Manager-level REST
        // twin are allowed, with parameters rebuilt here rather than passed through.
        if (!IsOwner(user))
            return await EnqueueManagerJobAsync(user, jobType, priority, args, token);

        string? key = args.TryGetProperty("key", out var keyEl) ? keyEl.GetString() : null;

        var parameters = args.TryGetProperty("parameters", out var paramEl)
            ? paramEl.GetRawText()
            : "{}";

        var jobId = await _jobManagement.EnqueueJobAsIsAsync(jobType, parameters, priority, key, token: token);
        return new { success = true, jobId = jobId.ToString(), message = $"Job '{jobType}' enqueued" };
    }

    // ──────────────────────────────────────────────────────────────
    // Settings Tools
    // ──────────────────────────────────────────────────────────────

    private async Task<object> GetSettingsAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        var settings = await _settings.GetSettingsAsync(token);
        return new { success = true, data = settings.WithoutSecrets() };
    }

    private async Task<object> UpdateSettingsAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        // Merge onto the current settings. Deserializing args alone replaced EVERY
        // setting, so {"theme":…} also reset the rest, sign-in included, to defaults.
        if (args.ValueKind != JsonValueKind.Object)
            return new { success = false, error = "Invalid settings data" };
        EditableSettingsDto current = await _settings.GetSettingsAsync(token);
        var merged = JsonSerializer.SerializeToNode(current, _jsonOptions)!.AsObject();
        foreach (var prop in args.EnumerateObject())
        {
            string key = merged.Select(kv => kv.Key)
                .FirstOrDefault(k => k.Equals(prop.Name, StringComparison.OrdinalIgnoreCase)) ?? prop.Name;
            merged[key] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
        }
        var editable = merged.Deserialize<EditableSettingsDto>(_jsonOptions);
        if (editable == null)
            return new { success = false, error = "Invalid settings data" };

        await _settings.SaveSettingsAsync(editable, false, token);
        return new { success = true, message = "Settings updated" };
    }

    // ──────────────────────────────────────────────────────────────
    // Scrobbling Tools
    // ──────────────────────────────────────────────────────────────

    private async Task<object> ListScrobblerProvidersAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        var providers = _scrobblerFactory.GetAllProviders();
        return new { success = true, data = providers.Select(p => new { name = p.ToString(), connected = false }) };
    }

    private async Task<object> GetScrobblerStatusAsync(UserEntity user, JsonElement args, CancellationToken token)
    {
        var configs = await _db.UserScrobblerConfigs
            .Where(c => c.UserId == user.Id)
            .AsNoTracking()
            .ToListAsync(token);

        return new { success = true, data = configs };
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private const string NotYourSeries = "This series belongs to another user's library.";

    private static bool IsOwner(UserEntity user) => user.Level == UserLevel.Owner;

    /// <summary>
    /// The MCP side of SeriesController.DenyAccessAsync: null when the caller may
    /// act on the series, otherwise the tool's error result. Tool levels alone
    /// don't isolate libraries — a Manager could otherwise refresh, pause or
    /// verify (and an Admin delete) another user's series by id.
    /// </summary>
    private async Task<object?> DenySeriesAsync(UserEntity user, Guid seriesId, CancellationToken token)
    {
        Guid? ownerId = await _db.Series.Where(s => s.Id == seriesId).Select(s => (Guid?)s.OwnerId)
            .FirstOrDefaultAsync(token);
        if (ownerId == null)
            return new { success = false, error = "Series not found" };
        if (!SeriesQueryService.CanAccessSeries(ownerId.Value, user.Id, IsOwner(user)))
            return new { success = false, error = NotYourSeries };
        return null;
    }

    /// <summary>Ids of every source of every series the caller can access.</summary>
    private async Task<HashSet<Guid>> AccessibleProviderIdsAsync(UserEntity user, CancellationToken token)
    {
        Guid me = user.Id;
        return new HashSet<Guid>(await _db.SeriesProviders.AsNoTracking()
            .Where(sp => _db.Series.Any(s => s.Id == sp.SeriesId && (s.OwnerId == me || s.OwnerId == Guid.Empty)))
            .Select(sp => sp.Id)
            .ToListAsync(token));
    }

    private async Task<bool> CanAccessProviderAsync(UserEntity user, Guid providerId, CancellationToken token)
    {
        Guid? seriesId = await _db.SeriesProviders.Where(sp => sp.Id == providerId).Select(sp => (Guid?)sp.SeriesId)
            .FirstOrDefaultAsync(token);
        return seriesId != null && await DenySeriesAsync(user, seriesId.Value, token) == null;
    }

    /// <summary>
    /// A recurring job belongs to a library only when it is one source's chapter
    /// check (keyed by that source's id); every other job is server-wide and
    /// visible to the Owner only.
    /// </summary>
    private async Task<bool> CanAccessJobAsync(UserEntity user, JobEntity job, CancellationToken token)
    {
        if (IsOwner(user))
            return true;
        return job.JobType == JobType.GetChapters && Guid.TryParse(job.Key, out Guid providerId)
               && await CanAccessProviderAsync(user, providerId, token);
    }

    /// <summary>
    /// Health alerts limited to the caller's library, the same rule as
    /// GET /api/status/summary: series alerts for their series, source alerts for
    /// sources of their series. The Owner sees everything.
    /// </summary>
    private async Task<List<HealthStatusEntity>> ScopeAlertsAsync(UserEntity user, List<HealthStatusEntity> alerts, CancellationToken token)
    {
        if (IsOwner(user) || alerts.Count == 0)
            return alerts;
        Guid me = user.Id;
        var seriesIds = new HashSet<Guid>(await _db.Series.AsNoTracking()
            .Where(s => s.OwnerId == me || s.OwnerId == Guid.Empty)
            .Select(s => s.Id)
            .ToListAsync(token));
        HashSet<Guid> providerIds = await AccessibleProviderIdsAsync(user, token);
        return alerts.Where(h =>
            (h.TargetType == HealthStatusTargetType.Series && seriesIds.Contains(h.TargetId)) ||
            (h.TargetType == HealthStatusTargetType.Provider && providerIds.Contains(h.TargetId))).ToList();
    }

    /// <summary>
    /// enqueue_job below Owner: the library-wide update and scan (POST
    /// /api/series/update-all and /scan-all are Manager) and one source's chapter
    /// check for a series in the caller's library. Parameters and keys are built
    /// here; nothing the caller sent is passed to the job as-is.
    /// </summary>
    private async Task<object> EnqueueManagerJobAsync(UserEntity user, JobType jobType, Priority priority, JsonElement args, CancellationToken token)
    {
        switch (jobType)
        {
            case JobType.UpdateAllSeries:
            {
                var jobId = await _jobManagement.EnqueueJobAsync(JobType.UpdateAllSeries, (string?)null, priority, token: token);
                return new { success = true, jobId = jobId.ToString(), message = $"Job '{jobType}' enqueued" };
            }
            case JobType.LibraryScan:
            {
                var jobId = await _jobManagement.EnqueueJobAsync(JobType.LibraryScan, (string?)null, priority, "LibraryScan", token: token);
                return new { success = true, jobId = jobId.ToString(), message = $"Job '{jobType}' enqueued" };
            }
            case JobType.GetChapters:
            {
                // GetChapters takes one source id: accept it as the bare parameter
                // ("<guid>") or as { "providerId": "<guid>" }.
                Guid providerId = Guid.Empty;
                if (args.TryGetProperty("parameters", out var p))
                {
                    if (p.ValueKind == JsonValueKind.String) Guid.TryParse(p.GetString(), out providerId);
                    else if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("providerId", out var pid)
                             && pid.ValueKind == JsonValueKind.String) Guid.TryParse(pid.GetString(), out providerId);
                }
                if (providerId == Guid.Empty)
                    return new { success = false, error = "GetChapters needs 'parameters' set to a source id." };
                if (!await CanAccessProviderAsync(user, providerId, token))
                    return new { success = false, error = "Source not found in your library." };
                var jobId = await _jobManagement.EnqueueJobAsync(JobType.GetChapters, providerId, priority,
                    key: providerId.ToString(), token: token);
                return new { success = true, jobId = jobId.ToString(), message = $"Job '{jobType}' enqueued" };
            }
            default:
                return new { success = false, error = $"Only the owner can enqueue '{jobType}' jobs. Allowed: UpdateAllSeries, LibraryScan, GetChapters." };
        }
    }

    /// <summary>
    /// Wraps a raw tool result object into the MCP standard response format:
    /// <c>content: [{ type: "text", text: "..." }]</c>.
    /// If the result contains an "error" property, sets <c>isError: true</c>.
    /// </summary>
    private static Dictionary<string, object> FormatToolResult(object? rawResult)
    {
        // Serialize the result to JSON text
        string text = rawResult != null
            ? JsonSerializer.Serialize(rawResult, _jsonOptions)
            : "{}";

        // Check if the result signals an error by looking for "success: false"
        bool isError = false;
        if (rawResult != null)
        {
            var json = JsonSerializer.SerializeToElement(rawResult, _jsonOptions);
            if (json.TryGetProperty("success", out var successEl) && successEl.GetBoolean() == false)
                isError = true;
        }

        return new Dictionary<string, object>
        {
            ["content"] = new List<Dictionary<string, object>>
            {
                new()
                {
                    ["type"] = "text",
                    ["text"] = text
                }
            },
            ["isError"] = isError
        };
    }

    private static JsonElement CreateErrorResponse(JsonElement? id, int code, string message)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id?.GetInt32() ?? null as int?,
            error = new { code, message }
        };
        return JsonSerializer.SerializeToElement(response, _jsonOptions);
    }

    private static string GetToolDescription(string toolName)
    {
        return toolName switch
        {
            "list_series" => "List all series in the library",
            "get_series" => "Get detailed information about a specific series by ID",
            "get_latest_series" => "Get recently published series from providers (cloud latest)",
            "add_series" => "Add a new series to the library from search results",
            "remove_series" => "Delete a series from the library (Admin only)",
            "update_series" => "Trigger a chapter update for a specific series",
            "update_all_series" => "Trigger updates for all tracked series",
            "verify_series" => "Verify archive integrity for a series",
            "search_series" => "Search for series across installed providers",
            "list_search_sources" => "List all available search sources/providers",
            "install_extension" => "Install a provider extension by package name",
            "uninstall_extension" => "Uninstall a provider extension (Admin only)",
            "list_extensions" => "List all installed and available extensions",
            "list_downloads" => "List current download queue items",
            "get_download_metrics" => "Get download statistics",
            "pause_series_downloads" => "Pause or resume downloads for a series",
            "get_health_alerts" => "Get active health alerts for series and providers",
            "get_series_health" => "Get health status for a specific series",
            "get_provider_health" => "Get health status for all providers",
            "list_jobs" => "List recent and queued jobs",
            "get_job_status" => "Get the status of a specific job",
            "cancel_job" => "Cancel a queued job",
            "enqueue_job" => "Enqueue a new job of a specific type (below Owner: UpdateAllSeries, LibraryScan, or GetChapters for one of your sources)",
            "get_settings" => "Get all application settings",
            "update_settings" => "Update application settings (Owner only)",
            "list_scrobbler_providers" => "List available scrobbler/tracker providers",
            "get_scrobbler_status" => "Get scrobbler sync status for the current user",
            _ => "No description available"
        };
    }

    private static object GetToolInputSchema(string toolName)
    {
        return toolName switch
        {
            "list_series" => new
            {
                type = "object",
                properties = new { },
                required = new string[] { }
            },
            "get_series" => new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Series GUID ID" }
                },
                required = new[] { "id" }
            },
            "get_latest_series" => new
            {
                type = "object",
                properties = new
                {
                    start = new { type = "number", description = "Starting index for pagination (default: 0)" },
                    count = new { type = "number", description = "Number of items to return (default: 20)" },
                    sourceId = new { type = "string", description = "Optional source/provider ID filter" },
                    keyword = new { type = "string", description = "Optional keyword filter" }
                },
                required = new string[] { }
            },
            "search_series" => new
            {
                type = "object",
                properties = new
                {
                    keyword = new { type = "string", description = "Search keyword" },
                    languages = new { type = "array", items = new { type = "string" }, description = "Language codes (e.g. en, ja)" },
                },
                required = new[] { "keyword" }
            },
            "install_extension" => new
            {
                type = "object",
                properties = new
                {
                    packageName = new { type = "string", description = "Package name of the extension" },
                    repoName = new { type = "string", description = "Optional repository name" },
                    force = new { type = "boolean", description = "Force reinstall" }
                },
                required = new[] { "packageName" }
            },
            "remove_series" => new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Series GUID ID" },
                    alsoPhysical = new { type = "boolean", description = "Also delete physical files" }
                },
                required = new[] { "id" }
            },
            // Default schema for tools with just an id parameter
            _ => new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Resource GUID ID" }
                },
                required = new[] { "id" }
            }
        };
    }
}