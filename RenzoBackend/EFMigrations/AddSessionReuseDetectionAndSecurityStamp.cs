using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RenzoBackend.Migrations;

/// <summary>
/// Refresh-token reuse detection plus a per-user security stamp.
///
/// <c>RetiredTokenHash</c> / <c>PreviousTokenReplayedAt</c> let a session tell
/// a lost response (forgiven once) from a copied token (session revoked). See
/// <see cref="RenzoBackend.Services.Auth.RefreshSessionService.RotateAsync"/>.
///
/// <c>SecurityStamp</c> is stamped into every access token and compared on each
/// request, so a password change/reset or a level change ends access tokens
/// that were issued before it. It starts at 0 and tokens minted before this
/// column existed carry no stamp, which reads as 0 — nobody is signed out by
/// the upgrade itself.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261115120000_AddSessionReuseDetectionAndSecurityStamp")]
public partial class AddSessionReuseDetectionAndSecurityStamp : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RetiredTokenHash",
            table: "RefreshSessions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "PreviousTokenReplayedAt",
            table: "RefreshSessions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "SecurityStamp",
            table: "Users",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "RetiredTokenHash", table: "RefreshSessions");
        migrationBuilder.DropColumn(name: "PreviousTokenReplayedAt", table: "RefreshSessions");
        migrationBuilder.DropColumn(name: "SecurityStamp", table: "Users");
    }
}
