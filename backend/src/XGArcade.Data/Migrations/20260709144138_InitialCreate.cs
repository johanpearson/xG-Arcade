using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClubDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    WikidataQid = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClubDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CountryDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    WikidataQid = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CountryDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlayerAttributes",
                columns: table => new
                {
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttributeType = table.Column<string>(type: "text", nullable: false),
                    AttributeValue = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerAttributes", x => new { x.PlayerId, x.AttributeType, x.AttributeValue });
                });

            migrationBuilder.CreateTable(
                name: "PlayerData",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Field = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<string>(type: "text", nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerData", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlayerOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Field = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    LockedByAdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Players",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "text", nullable: false),
                    WikidataQid = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Players", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrophyDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IsTeamTrophy = table.Column<bool>(type: "boolean", nullable: false),
                    WikidataQid = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrophyDefinitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClubDefinitions_Name",
                table: "ClubDefinitions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CountryDefinitions_Name",
                table: "CountryDefinitions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerAttributes_AttributeType_AttributeValue",
                table: "PlayerAttributes",
                columns: new[] { "AttributeType", "AttributeValue" });

            migrationBuilder.CreateIndex(
                name: "IX_Players_WikidataQid",
                table: "Players",
                column: "WikidataQid",
                unique: true,
                filter: "\"WikidataQid\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TrophyDefinitions_Name",
                table: "TrophyDefinitions",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClubDefinitions");

            migrationBuilder.DropTable(
                name: "CountryDefinitions");

            migrationBuilder.DropTable(
                name: "PlayerAttributes");

            migrationBuilder.DropTable(
                name: "PlayerData");

            migrationBuilder.DropTable(
                name: "PlayerOverrides");

            migrationBuilder.DropTable(
                name: "Players");

            migrationBuilder.DropTable(
                name: "TrophyDefinitions");
        }
    }
}
