using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPairLookupFailure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PairLookupFailures",
                columns: table => new
                {
                    FirstAttributeType = table.Column<string>(type: "text", nullable: false),
                    FirstAttributeValue = table.Column<string>(type: "text", nullable: false),
                    SecondAttributeType = table.Column<string>(type: "text", nullable: false),
                    SecondAttributeValue = table.Column<string>(type: "text", nullable: false),
                    ConsecutiveFailureCount = table.Column<int>(type: "integer", nullable: false),
                    LastFailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairLookupFailures", x => new { x.FirstAttributeType, x.FirstAttributeValue, x.SecondAttributeType, x.SecondAttributeValue });
                });

            migrationBuilder.CreateIndex(
                name: "IX_PairLookupFailures_SecondAttributeType_SecondAttributeValue",
                table: "PairLookupFailures",
                columns: new[] { "SecondAttributeType", "SecondAttributeValue" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PairLookupFailures");
        }
    }
}
