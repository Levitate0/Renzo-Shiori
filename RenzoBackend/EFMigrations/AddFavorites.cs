using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RenzoBackend.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260711130000_AddFavorites")]
public partial class AddFavorites : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "FavoriteLists",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                Name = table.Column<string>(type: "TEXT", nullable: false, collation: "BINARY"),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FavoriteLists", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "FavoriteItems",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                SeriesId = table.Column<Guid>(type: "TEXT", nullable: false),
                AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FavoriteItems", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_FavoriteList_UserId",
            table: "FavoriteLists",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_FavoriteItem_ListId_SeriesId",
            table: "FavoriteItems",
            columns: new[] { "ListId", "SeriesId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "FavoriteItems");
        migrationBuilder.DropTable(name: "FavoriteLists");
    }
}
