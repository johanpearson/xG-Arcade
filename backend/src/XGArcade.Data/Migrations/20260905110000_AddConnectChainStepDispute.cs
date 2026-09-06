using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    // REQ-1412/1413/1414, ADR-0109: ConnectChainStep.HasPendingDispute (the
    // denormalized "does this step have a Pending dispute" cache — see that
    // column's own doc comment), ConnectChainStepDisputes (REQ-1412/1413's
    // dispute-a-failed-step flow), and ConnectDisputeDataCorrectionSuggestions
    // (REQ-1414's admin data-correction-suggestion by-product).
    public partial class AddConnectChainStepDispute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasPendingDispute",
                table: "ConnectChainSteps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ConnectChainStepDisputes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectChainStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedClubName = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RaisedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectChainStepDisputes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectChainStepDisputes_ConnectChainSteps_ConnectChainStepId",
                        column: x => x.ConnectChainStepId,
                        principalTable: "ConnectChainSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectDisputeDataCorrectionSuggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectMatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectChainStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectChainStepDisputeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidatePlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrecedingPlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedClubName = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectDisputeDataCorrectionSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectDisputeDataCorrectionSuggestions_ConnectChainStepDisputes_ConnectChainStepDisputeId",
                        column: x => x.ConnectChainStepDisputeId,
                        principalTable: "ConnectChainStepDisputes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectDisputeDataCorrectionSuggestions_Players_CandidatePlayerId",
                        column: x => x.CandidatePlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectDisputeDataCorrectionSuggestions_Players_PrecedingPlayerId",
                        column: x => x.PrecedingPlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectChainStepDisputes_ConnectChainStepId",
                table: "ConnectChainStepDisputes",
                column: "ConnectChainStepId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectDisputeDataCorrectionSuggestions_ConnectChainStepDisputeId",
                table: "ConnectDisputeDataCorrectionSuggestions",
                column: "ConnectChainStepDisputeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectDisputeDataCorrectionSuggestions_CandidatePlayerId",
                table: "ConnectDisputeDataCorrectionSuggestions",
                column: "CandidatePlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectDisputeDataCorrectionSuggestions_PrecedingPlayerId",
                table: "ConnectDisputeDataCorrectionSuggestions",
                column: "PrecedingPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectDisputeDataCorrectionSuggestions_CreatedAt",
                table: "ConnectDisputeDataCorrectionSuggestions",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectDisputeDataCorrectionSuggestions");

            migrationBuilder.DropTable(
                name: "ConnectChainStepDisputes");

            migrationBuilder.DropColumn(
                name: "HasPendingDispute",
                table: "ConnectChainSteps");
        }
    }
}
