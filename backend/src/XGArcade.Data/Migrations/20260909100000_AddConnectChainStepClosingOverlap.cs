using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    // Gap-fill, 2026-09-09, REQ-1406 addendum (see requirements-document.md's
    // "Gap-fill status note (2026-09-09)" under REQ-1406): adds
    // ClosingClubName/ClosingOverlapStartYear/ClosingOverlapEndYear —
    // the closing-connection (candidate -> OTHER target player) counterpart
    // of the existing MatchedClubName/MatchedOverlapStartYear/
    // MatchedOverlapEndYear trio (which describes the connection to the
    // PRECEDING chain player instead). See ConnectChainStep's own doc
    // comment for the full "why" (a real playtester report that a
    // chain-closing step only ever rendered as a bare "connects to your
    // target" label, with no club or years shown).
    public partial class AddConnectChainStepClosingOverlap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClosingClubName",
                table: "ConnectChainSteps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClosingOverlapEndYear",
                table: "ConnectChainSteps",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClosingOverlapStartYear",
                table: "ConnectChainSteps",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosingClubName",
                table: "ConnectChainSteps");

            migrationBuilder.DropColumn(
                name: "ClosingOverlapEndYear",
                table: "ConnectChainSteps");

            migrationBuilder.DropColumn(
                name: "ClosingOverlapStartYear",
                table: "ConnectChainSteps");
        }
    }
}
