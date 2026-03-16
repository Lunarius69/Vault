using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vault.Migrations
{
    public partial class AddEpisodesTable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Episodes",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaItemId = table.Column<int>(nullable: false),
                    SeasonNumber = table.Column<int>(nullable: false),
                    EpisodeNumber = table.Column<int>(nullable: false),
                    Title = table.Column<string>(nullable: true),
                    Description = table.Column<string>(nullable: true),
                    ThumbnailPath = table.Column<string>(nullable: true),
                    FilePath = table.Column<string>(nullable: true),
                    IsWatched = table.Column<bool>(nullable: false, defaultValue: false),
                    ResumePositionSeconds = table.Column<long>(nullable: false, defaultValue: 0L),
                    WatchedDate = table.Column<DateTime>(nullable: true),
                    RuntimeMinutes = table.Column<int>(nullable: false, defaultValue: 0)
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

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Episodes");
        }
    }
}