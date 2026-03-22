using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vault.Migrations
{
    /// <inheritdoc />
    public partial class AddAchievementFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IntroEndSeconds",
                table: "Episodes");

            migrationBuilder.RenameColumn(
                name: "OutroStartSeconds",
                table: "Episodes",
                newName: "RuntimeMinutes");

            migrationBuilder.AddColumn<string>(
                name: "BannerPath",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalSeasons",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

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

            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "Episodes",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Episodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThumbnailPath",
                table: "Episodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WatchedDate",
                table: "Episodes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BannerPath",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "TotalSeasons",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "AchievementsEarned",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "AchievementsTotal",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "RetroAchievementsGameId",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Episodes");

            migrationBuilder.DropColumn(
                name: "ThumbnailPath",
                table: "Episodes");

            migrationBuilder.DropColumn(
                name: "WatchedDate",
                table: "Episodes");

            migrationBuilder.RenameColumn(
                name: "RuntimeMinutes",
                table: "Episodes",
                newName: "OutroStartSeconds");

            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "Episodes",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AddColumn<long>(
                name: "IntroEndSeconds",
                table: "Episodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }
    }
}
