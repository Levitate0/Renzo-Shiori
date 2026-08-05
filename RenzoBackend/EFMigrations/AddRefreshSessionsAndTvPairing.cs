using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System;

#nullable disable

namespace RenzoBackend.EFMigrations
{
    /// <summary>
    /// Per-device refresh sessions + TV pairing requests.
    ///
    /// The existing single <c>Users.RefreshTokenHash</c> is carried into the new
    /// table so nobody is signed out by the upgrade; the old columns are left in
    /// place (unused) rather than dropped, so a rollback to the previous build
    /// still finds what it expects.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260804120000_AddRefreshSessionsAndTvPairing")]
    public partial class AddRefreshSessionsAndTvPairing : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RefreshSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false, collation: "BINARY"),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedIp = table.Column<string>(type: "TEXT", nullable: true),
                    IsTvPairing = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshSessions_UserId",
                table: "RefreshSessions",
                column: "UserId");

            migrationBuilder.CreateTable(
                name: "TvPairingRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserCode = table.Column<string>(type: "TEXT", nullable: false, collation: "BINARY"),
                    DeviceCodeHash = table.Column<string>(type: "TEXT", nullable: false, collation: "BINARY"),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: true),
                    RequestIp = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ApprovedUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Claimed = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TvPairingRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TvPairingRequests_UserCode",
                table: "TvPairingRequests",
                column: "UserCode",
                unique: true);

            // Carry existing remember-me sessions over, so upgrading doesn't sign
            // everyone out. Device name is unknown for these — the UI shows them
            // as "Existing session".
            migrationBuilder.Sql(@"
                INSERT INTO RefreshSessions (Id, UserId, TokenHash, ExpiresAt, CreatedAt, LastSeenAt, RevokedAt, DeviceName, CreatedIp, IsTvPairing)
                SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
                       substr(lower(hex(randomblob(2))),2) || '-a' || substr(lower(hex(randomblob(2))),2) || '-' ||
                       lower(hex(randomblob(6))),
                       Id, RefreshTokenHash, RefreshTokenExpiresAt,
                       CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, NULL, NULL, NULL, 0
                FROM Users
                WHERE RefreshTokenHash IS NOT NULL
                  AND RefreshTokenHash <> ''
                  AND RefreshTokenExpiresAt IS NOT NULL;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "TvPairingRequests");
            migrationBuilder.DropTable(name: "RefreshSessions");
        }
    }
}
