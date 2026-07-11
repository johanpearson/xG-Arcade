using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDisplayNameUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedDisplayName",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Data fix, in this order, before the unique index below can be
            // added safely: (1) any row that predates S-011's DisplayName
            // column (still "" — AddDisplayNameAndLeagues defaulted existing
            // rows this way, and UserDisplayNameBackfiller only fixes it
            // *after* migrations run, so it can't be relied on to have run
            // yet) falls back to its email's local part, the same rule
            // UserDisplayNameBackfiller applies; (2) any resulting
            // case-insensitive collision — including two users who happen to
            // share an email local part on different domains, or two rows
            // that both fell back to "" before step 1 ran — is resolved by
            // suffixing every row after the first (ordered by CreatedAt,
            // then Id) with a short fragment of its own Id, which is
            // guaranteed unique. This is a one-time, irreversible rename of
            // whichever pre-existing rows collide, not a player-facing
            // feature — Down() does not attempt to reverse it.
            migrationBuilder.Sql(
                "UPDATE \"Users\" SET \"DisplayName\" = split_part(\"Email\", '@', 1) WHERE \"DisplayName\" = '';");

            migrationBuilder.Sql(
                "WITH ranked AS (" +
                "    SELECT \"Id\", ROW_NUMBER() OVER (PARTITION BY lower(\"DisplayName\") ORDER BY \"CreatedAt\", \"Id\") AS rn" +
                "    FROM \"Users\"" +
                ") " +
                "UPDATE \"Users\" u " +
                "SET \"DisplayName\" = u.\"DisplayName\" || '-' || left(replace(u.\"Id\"::text, '-', ''), 8) " +
                "FROM ranked " +
                "WHERE u.\"Id\" = ranked.\"Id\" AND ranked.rn > 1;");

            migrationBuilder.Sql(
                "UPDATE \"Users\" SET \"NormalizedDisplayName\" = lower(\"DisplayName\");");

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedDisplayName",
                table: "Users",
                column: "NormalizedDisplayName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_NormalizedDisplayName",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "NormalizedDisplayName",
                table: "Users");
        }
    }
}
