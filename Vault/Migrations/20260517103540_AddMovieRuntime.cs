using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vault.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // WatchTimeMinutes, SteamAppId, SourceFile, and Achievements were already
            // added to the live DB via VaultContext.EnsureSchema() — skip them here.
            migrationBuilder.AddColumn<int>(
                name: "RuntimeMinutes",
                table: "MediaItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RuntimeMinutes",
                table: "MediaItems");
        }
    }
}
