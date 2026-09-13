using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    // REQ-1420 (docs/requirements-document.md §4.15), COMP-17:
    // ConnectChainStepDispute.AllowEarlyView — whether the disputing player
    // opted in, at raise time, to let the match's other participant see this
    // dispute's content before that other participant is themselves
    // terminal. Non-nullable, defaults to false for every pre-existing row
    // (the conservative, no-leak default this REQ requires) — see that
    // column's own doc comment on ConnectChainStepDispute.
    public partial class AddConnectChainStepDisputeAllowEarlyView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowEarlyView",
                table: "ConnectChainStepDisputes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowEarlyView",
                table: "ConnectChainStepDisputes");
        }
    }
}
