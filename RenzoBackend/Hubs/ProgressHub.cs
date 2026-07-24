using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services;
using Microsoft.AspNetCore.SignalR;

namespace RenzoBackend.Hubs
{
    public class ProgressHub : Hub
    {
        private readonly ILogger<ProgressHub> _logger;
        public ProgressHub(ILogger<ProgressHub> logger)
        {
            _logger = logger;
        }

        /// <summary>SignalR group a specific user's connections join — owner-scoped progress events (e.g. downloads) broadcast only here.</summary>
        public static string UserGroup(Guid userId) => $"user:{userId}";

        /// <summary>Every Owner-level connection joins this group, so Owner accounts keep seeing every user's activity (consistent with their "view all libraries" access elsewhere).</summary>
        public const string OwnersGroup = "owners";

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation($"SignalR Client connected: {Context.ConnectionId}");
            // The hub's initial HTTP handshake already went through AuthMiddleware
            // (same as any REST request — image-scoped/query token for WS, since
            // browsers can't set an Authorization header on the upgrade), so the
            // resolved user is already sitting on this same HttpContext.
            if (Context.GetHttpContext()?.Items["User"] is UserEntity user)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(user.Id)).ConfigureAwait(false);
                if (user.Level == UserLevel.Owner)
                    await Groups.AddToGroupAsync(Context.ConnectionId, OwnersGroup).ConfigureAwait(false);
            }
            await base.OnConnectedAsync().ConfigureAwait(false);
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation($"SignalR Client disconnected: {Context.ConnectionId}");
            // No explicit RemoveFromGroupAsync needed — SignalR automatically drops
            // a closed connection's group memberships.
            return base.OnDisconnectedAsync(exception);
        }
    }
}
