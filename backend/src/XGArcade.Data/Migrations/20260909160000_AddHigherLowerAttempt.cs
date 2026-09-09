using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHigherLowerAttempt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HigherLowerAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HigherLowerInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    StreakLength = table.Column<int>(type: "integer", nullable: false),
                    CurrentBaselinePlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentBaselineValue = table.Column<int>(type: "integer", nullable: false),
                    HasEnded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HigherLowerAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HigherLowerAttempts_HigherLowerInstances_HigherLowerInstanceId",
                        column: x => x.HigherLowerInstanceId,
                        principalTable: "HigherLowerInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HigherLowerAttempts_Players_CurrentBaselinePlayerId",
                        column: x => x.CurrentBaselinePlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HigherLowerAttempts_CurrentBaselinePlayerId",
                table: "HigherLowerAttempts",
                column: "CurrentBaselinePlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_HigherLowerAttempts_HigherLowerInstanceId_UserId",
                table: "HigherLowerAttempts",
                columns: new[] { "HigherLowerInstanceId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HigherLowerAttempts");
        }
    }
}
