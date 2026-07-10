using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGuessAndPlayerNormalizedFullName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedFullName",
                table: "Players",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Guesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoundId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CellId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmittedName = table.Column<string>(type: "text", nullable: false),
                    PlayerAnswerId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsCorrect = table.Column<bool>(type: "boolean", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    FinalUniquenessScore = table.Column<double>(type: "double precision", nullable: true),
                    FinalPoints = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Guesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Guesses_Rounds_RoundId",
                        column: x => x.RoundId,
                        principalTable: "Rounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Players_NormalizedFullName",
                table: "Players",
                column: "NormalizedFullName");

            migrationBuilder.CreateIndex(
                name: "IX_Guesses_CellId",
                table: "Guesses",
                column: "CellId");

            migrationBuilder.CreateIndex(
                name: "IX_Guesses_RoundId_UserId_CellId",
                table: "Guesses",
                columns: new[] { "RoundId", "UserId", "CellId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Guesses");

            migrationBuilder.DropIndex(
                name: "IX_Players_NormalizedFullName",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "NormalizedFullName",
                table: "Players");
        }
    }
}
