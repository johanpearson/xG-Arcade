using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConfirmedLowMatchPair : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfirmedLowMatchPairs",
                columns: table => new
                {
                    FirstAttributeType = table.Column<string>(type: "text", nullable: false),
                    FirstAttributeValue = table.Column<string>(type: "text", nullable: false),
                    SecondAttributeType = table.Column<string>(type: "text", nullable: false),
                    SecondAttributeValue = table.Column<string>(type: "text", nullable: false),
                    MatchCount = table.Column<int>(type: "integer", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfirmedLowMatchPairs", x => new { x.FirstAttributeType, x.FirstAttributeValue, x.SecondAttributeType, x.SecondAttributeValue });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfirmedLowMatchPairs_SecondAttributeType_SecondAttributeValue",
                table: "ConfirmedLowMatchPairs",
                columns: new[] { "SecondAttributeType", "SecondAttributeValue" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfirmedLowMatchPairs");
        }
    }
}
