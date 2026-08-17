using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoundSequenceNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // REQ-304/S-135: added nullable first, same "backfill per-row
            // before tightening" shape as AddUserLastActiveAt's LastActiveAt
            // — SequenceNumber can't be expressed as a single AddColumn
            // `defaultValue` literal since every existing row needs its own,
            // GameKey-scoped value.
            migrationBuilder.AddColumn<int>(
                name: "SequenceNumber",
                table: "Rounds",
                type: "integer",
                nullable: true);

            // Backfill: number every existing row 1, 2, 3, ... per GameKey,
            // ordered by StartTime ascending — the exact ordering
            // RoundGenerationService's own chronological chain (StartTime =
            // predecessor's EndTime) already produces, so this is
            // indistinguishable from a sequence generated entirely by the
            // assignment behavior going forward (REQ-304's own acceptance
            // criteria).
            migrationBuilder.Sql(
                "WITH ranked AS (" +
                "    SELECT \"Id\", ROW_NUMBER() OVER (PARTITION BY \"GameKey\" ORDER BY \"StartTime\") AS rn" +
                "    FROM \"Rounds\"" +
                ") " +
                "UPDATE \"Rounds\" r " +
                "SET \"SequenceNumber\" = ranked.rn " +
                "FROM ranked " +
                "WHERE r.\"Id\" = ranked.\"Id\";");

            migrationBuilder.AlterColumn<int>(
                name: "SequenceNumber",
                table: "Rounds",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            // REQ-304: the actual race guard behind SequenceNumber's
            // uniqueness — see XGArcadeDbContext.OnModelCreating's matching
            // comment on this index.
            migrationBuilder.CreateIndex(
                name: "IX_Rounds_GameKey_SequenceNumber",
                table: "Rounds",
                columns: new[] { "GameKey", "SequenceNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Rounds_GameKey_SequenceNumber",
                table: "Rounds");

            migrationBuilder.DropColumn(
                name: "SequenceNumber",
                table: "Rounds");
        }
    }
}
