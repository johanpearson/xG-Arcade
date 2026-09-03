using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    // REQ-1405/S-212: per-SLOT (not per-UserId — see ConnectMatch's own doc
    // comment) forfeit-timeout tracking, backing
    // ConnectMatchLifecycleService.RunForfeitSweepAsync. Both columns are
    // nullable, set exactly once each (idempotent ??= write), never a real
    // FK — same shape as every other nullable timestamp column already on
    // this table (StartedAt/DeadlineUtc/ResolvedAt).
    public partial class AddConnectMatchTimeoutTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PlayerATimedOutAt",
                table: "ConnectMatches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlayerBTimedOutAt",
                table: "ConnectMatches",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlayerATimedOutAt",
                table: "ConnectMatches");

            migrationBuilder.DropColumn(
                name: "PlayerBTimedOutAt",
                table: "ConnectMatches");
        }
    }
}
