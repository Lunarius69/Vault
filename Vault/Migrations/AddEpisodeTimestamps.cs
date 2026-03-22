using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vault.Migrations
{
    public partial class AddEpisodeTimestamps : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intro / recap segment
            migrationBuilder.AddColumn<double>(
                name: "IntroStart",
                table: "Episodes",
                nullable: false,
                defaultValue: -1.0);

            migrationBuilder.AddColumn<double>(
                name: "IntroEnd",
                table: "Episodes",
                nullable: false,
                defaultValue: -1.0);

            // Outro / credits segment
            migrationBuilder.AddColumn<double>(
                name: "OutroStart",
                table: "Episodes",
                nullable: false,
                defaultValue: -1.0);

            // Whether fingerprinting has been attempted for this episode
            migrationBuilder.AddColumn<bool>(
                name: "FingerprintProcessed",
                table: "Episodes",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "IntroStart", table: "Episodes");
            migrationBuilder.DropColumn(name: "IntroEnd", table: "Episodes");
            migrationBuilder.DropColumn(name: "OutroStart", table: "Episodes");
            migrationBuilder.DropColumn(name: "FingerprintProcessed", table: "Episodes");
        }
    }
}