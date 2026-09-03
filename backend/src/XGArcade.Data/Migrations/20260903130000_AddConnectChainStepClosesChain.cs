using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    // S-213/REQ-1406: ConnectChainStep.ClosesChain — true only on a step
    // that is also IsValid, where the candidate additionally connects to
    // the match's OTHER target pick (never the one the chain started
    // from). Non-nullable, defaults to false for every pre-existing row
    // (there are none in practice yet — chain-step submission itself ships
    // in this same story) — see ConnectChainStep's own doc comment.
    public partial class AddConnectChainStepClosesChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ClosesChain",
                table: "ConnectChainSteps",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosesChain",
                table: "ConnectChainSteps");
        }
    }
}
