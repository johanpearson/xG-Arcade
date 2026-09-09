using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHigherLowerInstance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HigherLowerInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    StatCategory = table.Column<string>(type: "text", nullable: false),
                    BaselinePlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineValue = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HigherLowerInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HigherLowerInstances_Players_BaselinePlayerId",
                        column: x => x.BaselinePlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HigherLowerComparators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HigherLowerInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SequencePosition = table.Column<int>(type: "integer", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HigherLowerComparators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HigherLowerComparators_HigherLowerInstances_HigherLowerInstanceId",
                        column: x => x.HigherLowerInstanceId,
                        principalTable: "HigherLowerInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HigherLowerComparators_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HigherLowerInstances_BaselinePlayerId",
                table: "HigherLowerInstances",
                column: "BaselinePlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_HigherLowerComparators_HigherLowerInstanceId_SequencePosition",
                table: "HigherLowerComparators",
                columns: new[] { "HigherLowerInstanceId", "SequencePosition" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HigherLowerComparators_PlayerId",
                table: "HigherLowerComparators",
                column: "PlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HigherLowerComparators");

            migrationBuilder.DropTable(
                name: "HigherLowerInstances");
        }
    }
}
