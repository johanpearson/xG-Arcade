using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerSuggestionGameKeyContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-0076/S-144: GameKey added nullable first, same
            // "backfill per-row before tightening" shape
            // AddRoundSequenceNumber's SequenceNumber already uses — every
            // pre-existing row is backfilled to "xg-grid" below (the only
            // game with a real submission path before this migration), then
            // the column is tightened to non-nullable.
            migrationBuilder.AddColumn<string>(
                name: "GameKey",
                table: "PlayerSuggestions",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE \"PlayerSuggestions\" SET \"GameKey\" = 'xg-grid' WHERE \"GameKey\" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "GameKey",
                table: "PlayerSuggestions",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            // ADR-0076: CellId/RowCategoryType/ColCategoryType become
            // xg-grid-only context — nullable so an xg-path row can leave
            // them unset rather than fabricating a value.
            migrationBuilder.AlterColumn<System.Guid>(
                name: "CellId",
                table: "PlayerSuggestions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(System.Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "RowCategoryType",
                table: "PlayerSuggestions",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "ColCategoryType",
                table: "PlayerSuggestions",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            // ADR-0076: xG Path's equivalent of CellId — see
            // PlayerSuggestion.PathPuzzleId's own doc comment.
            migrationBuilder.AddColumn<System.Guid>(
                name: "PathPuzzleId",
                table: "PlayerSuggestions",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PathPuzzleId",
                table: "PlayerSuggestions");

            migrationBuilder.AlterColumn<string>(
                name: "ColCategoryType",
                table: "PlayerSuggestions",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "RowCategoryType",
                table: "PlayerSuggestions",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<System.Guid>(
                name: "CellId",
                table: "PlayerSuggestions",
                type: "uuid",
                nullable: false,
                defaultValue: System.Guid.Empty,
                oldClrType: typeof(System.Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "GameKey",
                table: "PlayerSuggestions");
        }
    }
}
