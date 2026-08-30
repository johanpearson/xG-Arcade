using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPredictInstance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PredictTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MatchCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredictTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PredictInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredictInstances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PredictMatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PredictInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalFixtureId = table.Column<int>(type: "integer", nullable: false),
                    HomeTeamName = table.Column<string>(type: "text", nullable: false),
                    AwayTeamName = table.Column<string>(type: "text", nullable: false),
                    KickoffUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredictMatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PredictMatches_PredictInstances_PredictInstanceId",
                        column: x => x.PredictInstanceId,
                        principalTable: "PredictInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PredictMatchPredictions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PredictMatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    HomeGoals = table.Column<int>(type: "integer", nullable: false),
                    AwayGoals = table.Column<int>(type: "integer", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredictMatchPredictions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PredictMatchPredictions_PredictMatches_PredictMatchId",
                        column: x => x.PredictMatchId,
                        principalTable: "PredictMatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PredictMatches_PredictInstanceId",
                table: "PredictMatches",
                column: "PredictInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_PredictMatchPredictions_PredictMatchId_UserId",
                table: "PredictMatchPredictions",
                columns: new[] { "PredictMatchId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PredictMatchPredictions");

            migrationBuilder.DropTable(
                name: "PredictMatches");

            migrationBuilder.DropTable(
                name: "PredictInstances");

            migrationBuilder.DropTable(
                name: "PredictTemplates");
        }
    }
}
