using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solace.DB.Migrations
{
    /// <inheritdoc />
    public partial class TileLastAccessed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastAccessed",
                table: "Tiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_Tiles_LastAccessed",
                table: "Tiles",
                column: "LastAccessed");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tiles_LastAccessed",
                table: "Tiles");

            migrationBuilder.DropColumn(
                name: "LastAccessed",
                table: "Tiles");
        }
    }
}
