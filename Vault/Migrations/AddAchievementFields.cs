using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vault.Migrations
{
    public partial class AddAchievementFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AchievementsEarned",
                table: "Games",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AchievementsTotal",
                table: "Games",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetroAchievementsGameId",
                table: "Games",
                type: "INTEGER",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "AchievementsEarned", table: "Games");
            migrationBuilder.DropColumn(name: "AchievementsTotal", table: "Games");
            migrationBuilder.DropColumn(name: "RetroAchievementsGameId", table: "Games");
        }
    }
}