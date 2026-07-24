using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RenzoBackend.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260713120000_AddSiteCredentials")]
public partial class AddSiteCredentials : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SiteCredentials",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Provider = table.Column<string>(type: "TEXT", nullable: false, collation: "BINARY"),
                Username = table.Column<string>(type: "TEXT", nullable: false, collation: "BINARY"),
                EncryptedPassword = table.Column<string>(type: "TEXT", nullable: true),
                EncryptedCookies = table.Column<string>(type: "TEXT", nullable: true),
                LastLoginAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                Status = table.Column<string>(type: "TEXT", nullable: false, collation: "BINARY"),
                StatusDetail = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SiteCredentials", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SiteCredential_UserId_Provider",
            table: "SiteCredentials",
            columns: new[] { "UserId", "Provider" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SiteCredentials");
    }
}
