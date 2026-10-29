using System.Text.Json;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Auth;
using RenzoBackend.Services.Mcp;
using Microsoft.AspNetCore.Mvc;

namespace RenzoBackend.Controllers;

/// <summary>
/// Controller that exposes the MCP (Model Context Protocol) server at
/// <c>POST /{opdsPath}/mcp</c> (JSON-RPC over HTTP).
///
/// Uses the same OPDS path-based user resolution pattern as <see cref="OpdsController"/>.
/// The resolved user's <see cref="UserEntity.Level"/> is used to enforce
/// per-tool permissions via <see cref="McpPermissionService"/>.
///
/// MCP protocol lifecycle handled:
///   1. initialize  (handshake)
///   2. notifications/initialized  (ack, returns 204)
///   3. ping  (health check)
///   4. tools/list, tools/call  (discovery + operation)
///   5. notifications/cancelled  (optional)
/// </summary>
[ApiController]
public class McpController : ControllerBase
{
    private readonly UserQueryService _userQuery;
    private readonly McpToolService _mcpToolService;
    private readonly ILogger<McpController> _logger;

    public McpController(
        UserQueryService userQuery,
        McpToolService mcpToolService,
        ILogger<McpController> logger)
    {
        _userQuery = userQuery;
        _mcpToolService = mcpToolService;
        _logger = logger;
    }
    [HttpPost("/{opdsPath}/mcp")]
    public async Task<IActionResult> PostMcp(
    string opdsPath,
    [FromBody] JsonElement body,
    CancellationToken token)
    {
        UserEntity? user = await _userQuery.GetByOpdsPathAsync(opdsPath, token);
        if (user == null || !user.IsActive)
            return NotFound();

        HttpContext.Items["User"] = user;

        JsonElement? result = await _mcpToolService.HandleJsonRpcAsync(body, user, token);

        // For JSON-RPC notifications/responses from the client:
        // MCP Streamable HTTP wants 202 Accepted, no body.
        if (result == null)
            return Accepted();

        return new JsonResult(result.Value)
        {
            StatusCode = StatusCodes.Status200OK,
            ContentType = "application/json"
        };
    }
}