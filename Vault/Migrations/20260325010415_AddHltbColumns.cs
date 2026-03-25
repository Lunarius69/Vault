using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vault.Migrations
{
    /// <inheritdoc />
    public partial class AddHltbColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HltbCompletionist",
                table: "Games",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "HltbMainPlusExtra",
                table: "Games",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "HltbMainStory",
                table: "Games",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HltbCompletionist",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "HltbMainPlusExtra",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "HltbMainStory",
                table: "Games");
        }
    }
}
