using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerPoolSweptAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // REQ-110/ADR-0078/S-160: both nullable — a row with no
            // PlayerPoolSweptAt yet (or invalidated back to null by
            // StaleClubAttributeCleaner/purge-player-pool) falls through to
            // PlayerCacheWarmingService's existing live-query behavior
            // unchanged. See CountryDefinition.PlayerPoolSweptAt/
            // ClubDefinition.PlayerPoolSweptAt's own doc comments.
            migrationBuilder.AddColumn<DateTime>(
                name: "PlayerPoolSweptAt",
                table: "CountryDefinitions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlayerPoolSweptAt",
                table: "ClubDefinitions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlayerPoolSweptAt",
                table: "ClubDefinitions");

            migrationBuilder.DropColumn(
                name: "PlayerPoolSweptAt",
                table: "CountryDefinitions");
        }
    }
}
