using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    // REQ-1407/1408/S-214: the bust half of slot-based terminal tracking
    // (PlayerABustedAt/PlayerBBustedAt, mirroring PlayerATimedOutAt/
    // PlayerBTimedOutAt's own nullable/idempotent-set-once shape exactly —
    // see AddConnectMatchTimeoutTracking) plus the persisted per-player
    // final score (PlayerAScore/PlayerBScore, null unless that player
    // completed a valid chain) written once at match resolution
    // (ResolveMatchAsync). See ConnectMatch's own doc comment.
    public partial class AddConnectMatchBustAndScoreTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PlayerABustedAt",
                table: "ConnectMatches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlayerBBustedAt",
                table: "ConnectMatches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlayerAScore",
                table: "ConnectMatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlayerBScore",
                table: "ConnectMatches",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlayerABustedAt",
                table: "ConnectMatches");

            migrationBuilder.DropColumn(
                name: "PlayerBBustedAt",
                table: "ConnectMatches");

            migrationBuilder.DropColumn(
                name: "PlayerAScore",
                table: "ConnectMatches");

            migrationBuilder.DropColumn(
                name: "PlayerBScore",
                table: "ConnectMatches");
        }
    }
}
