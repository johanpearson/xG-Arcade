using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    // REQ-1305/ADR-0097 §2: PredictMatch's grading-state discriminator
    // (GradingStatus, default 0 == Pending, so every pre-existing row is
    // unaffected) plus its nullable actual-score columns, and
    // PredictMatchPrediction's nullable FinalPoints.
    public partial class AddPredictMatchGrading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GradingStatus",
                table: "PredictMatches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ActualHomeGoals",
                table: "PredictMatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActualAwayGoals",
                table: "PredictMatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FinalPoints",
                table: "PredictMatchPredictions",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinalPoints",
                table: "PredictMatchPredictions");

            migrationBuilder.DropColumn(
                name: "GradingStatus",
                table: "PredictMatches");

            migrationBuilder.DropColumn(
                name: "ActualHomeGoals",
                table: "PredictMatches");

            migrationBuilder.DropColumn(
                name: "ActualAwayGoals",
                table: "PredictMatches");
        }
    }
}
