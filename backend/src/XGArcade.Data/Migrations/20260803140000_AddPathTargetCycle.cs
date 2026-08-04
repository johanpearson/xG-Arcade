using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPathTargetCycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PathTargetCycles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleNumber = table.Column<int>(type: "integer", nullable: false),
                    ObservedPoolSize = table.Column<int>(type: "integer", nullable: false),
                    UsedInCycleCount = table.Column<int>(type: "integer", nullable: false),
                    LastCycleCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PathTargetCycles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PathCycleTargetUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleNumber = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PathCycleTargetUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PathCycleTargetUsages_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PathCycleTargetUsages_CycleNumber",
                table: "PathCycleTargetUsages",
                column: "CycleNumber");

            migrationBuilder.CreateIndex(
                name: "IX_PathCycleTargetUsages_PlayerId_CycleNumber",
                table: "PathCycleTargetUsages",
                columns: new[] { "PlayerId", "CycleNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PathCycleTargetUsages");

            migrationBuilder.DropTable(
                name: "PathTargetCycles");
        }
    }
}
