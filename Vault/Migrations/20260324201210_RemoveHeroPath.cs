using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vault.Migrations
{
    /// <inheritdoc />
    public partial class RemoveHeroPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HeroPath",
                table: "Games");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HeroPath",
                table: "Games",
                type: "TEXT",
                nullable: true);
        }
    }
}
