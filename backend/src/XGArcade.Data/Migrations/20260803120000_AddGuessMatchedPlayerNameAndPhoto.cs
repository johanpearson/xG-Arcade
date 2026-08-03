using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGuessMatchedPlayerNameAndPhoto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MatchedPlayerName",
                table: "Guesses",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchedPlayerPhotoUrl",
                table: "Guesses",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MatchedPlayerName",
                table: "Guesses");

            migrationBuilder.DropColumn(
                name: "MatchedPlayerPhotoUrl",
                table: "Guesses");
        }
    }
}
