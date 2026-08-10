using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerSuggestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerSuggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerName = table.Column<string>(type: "text", nullable: false),
                    AssertedNationality = table.Column<string>(type: "text", nullable: false),
                    SubmittingUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CellId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoundId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowCategoryType = table.Column<string>(type: "text", nullable: false),
                    ColCategoryType = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerSuggestions_Rounds_RoundId",
                        column: x => x.RoundId,
                        principalTable: "Rounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlayerSuggestionClubs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerSuggestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClubName = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerSuggestionClubs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerSuggestionClubs_PlayerSuggestions_PlayerSuggestionId",
                        column: x => x.PlayerSuggestionId,
                        principalTable: "PlayerSuggestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSuggestions_RoundId",
                table: "PlayerSuggestions",
                column: "RoundId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSuggestions_Status",
                table: "PlayerSuggestions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSuggestionClubs_PlayerSuggestionId",
                table: "PlayerSuggestionClubs",
                column: "PlayerSuggestionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerSuggestionClubs");

            migrationBuilder.DropTable(
                name: "PlayerSuggestions");
        }
    }
}
