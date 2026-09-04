using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    // Design change, 2026-09-04, REQ-1406, product-owner direction (ADR-0104):
    // ConnectChainStep.ClaimedClubName — required, player-typed free text —
    // is replaced by MatchedClubName/MatchedOverlapStartYear/
    // MatchedOverlapEndYear, all nullable (null together only for an invalid
    // step, where no club was found at all). See ConnectChainStep's own doc
    // comment for the full "why."
    public partial class ReplaceConnectChainStepClaimedClubWithMatchedOverlap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ClaimedClubName",
                table: "ConnectChainSteps",
                newName: "MatchedClubName");

            migrationBuilder.AlterColumn<string>(
                name: "MatchedClubName",
                table: "ConnectChainSteps",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<int>(
                name: "MatchedOverlapStartYear",
                table: "ConnectChainSteps",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MatchedOverlapEndYear",
                table: "ConnectChainSteps",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MatchedOverlapEndYear",
                table: "ConnectChainSteps");

            migrationBuilder.DropColumn(
                name: "MatchedOverlapStartYear",
                table: "ConnectChainSteps");

            // REQ-1406's earlier ClaimedClubName was NOT NULL — a plain
            // AlterColumn back to non-nullable would fail if any row
            // written under the new (nullable) schema has a NULL value
            // (every invalid step, by design). Backfilling a placeholder
            // string is the only way to make this Down migration reversible
            // without data loss of a different kind; going forward again
            // (Up) immediately makes the column nullable again regardless,
            // so this placeholder is never actually read by application
            // code in practice.
            migrationBuilder.Sql(
                "UPDATE \"ConnectChainSteps\" SET \"MatchedClubName\" = '' WHERE \"MatchedClubName\" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "MatchedClubName",
                table: "ConnectChainSteps",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.RenameColumn(
                name: "MatchedClubName",
                table: "ConnectChainSteps",
                newName: "ClaimedClubName");
        }
    }
}
