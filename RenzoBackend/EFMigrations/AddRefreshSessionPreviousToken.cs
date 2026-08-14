using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RenzoBackend.Migrations;

/// <summary>
/// Keeps the token a rotation replaced, so a client that never received the
/// replacement can still refresh instead of being signed out. See
/// <see cref="RenzoBackend.Models.Database.RefreshSessionEntity.PreviousTokenHash"/>.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260814230000_AddRefreshSessionPreviousToken")]
public partial class AddRefreshSessionPreviousToken : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PreviousTokenHash",
            table: "RefreshSessions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "PreviousTokenValidUntil",
            table: "RefreshSessions",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PreviousTokenHash", table: "RefreshSessions");
        migrationBuilder.DropColumn(name: "PreviousTokenValidUntil", table: "RefreshSessions");
    }
}
