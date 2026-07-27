using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPathInstance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PathTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PuzzleCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PathTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PathInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PathInstances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PathPuzzles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PathInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetPlayerId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PathPuzzles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PathPuzzles_PathInstances_PathInstanceId",
                        column: x => x.PathInstanceId,
                        principalTable: "PathInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PathPuzzles_Players_TargetPlayerId",
                        column: x => x.TargetPlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PathPuzzles_PathInstanceId_TargetPlayerId",
                table: "PathPuzzles",
                columns: new[] { "PathInstanceId", "TargetPlayerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PathPuzzles_TargetPlayerId",
                table: "PathPuzzles",
                column: "TargetPlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PathPuzzles");

            migrationBuilder.DropTable(
                name: "PathInstances");

            migrationBuilder.DropTable(
                name: "PathTemplates");
        }
    }
}
