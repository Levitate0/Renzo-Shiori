using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RenzoBackend.Migrations;

/// <summary>
/// Per-user source visibility: which installed sources a given user has enabled
/// for their own Search/Browse/Add-series. The underlying extension install stays
/// one shared JVM bridge; this table is the per-user opt-in layer on top.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260720220000_AddUserProviders")]
public partial class AddUserProviders : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserProviders",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                MihonProviderId = table.Column<string>(type: "TEXT", nullable: false, collation: "BINARY"),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserProviders", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserProvider_UserId_MihonProviderId",
            table: "UserProviders",
            columns: new[] { "UserId", "MihonProviderId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "UserProviders");
    }
}
