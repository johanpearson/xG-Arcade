using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    // REQ-1306/ADR-0098: the per-player, independent "confirm and lock" flag
    // for a PredictInstance — its own table (composite primary key on
    // (PredictInstanceId, UserId), cascade FK to PredictInstances), never a
    // column on PredictMatchPredictions. See ADR-0098 for the full reasoning.
    public partial class AddPredictPlayerLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PredictPlayerLocks",
                columns: table => new
                {
                    PredictInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredictPlayerLocks", x => new { x.PredictInstanceId, x.UserId });
                    table.ForeignKey(
                        name: "FK_PredictPlayerLocks_PredictInstances_PredictInstanceId",
                        column: x => x.PredictInstanceId,
                        principalTable: "PredictInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PredictPlayerLocks");
        }
    }
}
