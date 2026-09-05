using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    // ADR-0107: PlayerNameIndex.WikidataQid — nullable, since a row indexed
    // before this column existed has no value until the next
    // `import-player-name-index` run backfills it. See that entity's own
    // doc comment for the full "why" (a real, reported same-name-collision
    // incident this column exists to let xG Connect resolve unambiguously).
    public partial class AddPlayerNameIndexWikidataQid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WikidataQid",
                table: "PlayerNameIndexEntries",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WikidataQid",
                table: "PlayerNameIndexEntries");
        }
    }
}
