using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vault.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    FileSizeGB = table.Column<double>(type: "REAL", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    LibraryType = table.Column<string>(type: "TEXT", nullable: false),
                    ExePath = table.Column<string>(type: "TEXT", nullable: true),
                    EmulatorPath = table.Column<string>(type: "TEXT", nullable: true),
                    BoxArtPath = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Genre = table.Column<string>(type: "TEXT", nullable: true),
                    PlaytimeMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    HltbMain = table.Column<double>(type: "REAL", nullable: true),
                    HltbMainSides = table.Column<double>(type: "REAL", nullable: true),
                    HltbComplete = table.Column<double>(type: "REAL", nullable: true),
                    LastPlayed = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsWishlist = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDownloaded = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Genre = table.Column<string>(type: "TEXT", nullable: true),
                    PosterPath = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    TmdbRating = table.Column<double>(type: "REAL", nullable: true),
                    WatchStatus = table.Column<string>(type: "TEXT", nullable: false),
                    FolderPath = table.Column<string>(type: "TEXT", nullable: true),
                    TotalEpisodes = table.Column<int>(type: "INTEGER", nullable: false),
                    WatchedEpisodes = table.Column<int>(type: "INTEGER", nullable: false),
                    ResumePositionSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    CurrentSeason = table.Column<int>(type: "INTEGER", nullable: true),
                    CurrentEpisode = table.Column<int>(type: "INTEGER", nullable: true),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Episodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: true),
                    IsWatched = table.Column<bool>(type: "INTEGER", nullable: false),
                    ResumePositionSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    IntroEndSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    OutroStartSeconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Episodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Episodes_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_MediaItemId",
                table: "Episodes",
                column: "MediaItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Episodes");

            migrationBuilder.DropTable(
                name: "Games");

            migrationBuilder.DropTable(
                name: "MediaItems");
        }
    }
}
