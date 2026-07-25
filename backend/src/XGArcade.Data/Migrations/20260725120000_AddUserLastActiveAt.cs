using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserLastActiveAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // REQ-718/ADR-0038: added nullable first purely so every
            // pre-existing row can be backfilled below from its own
            // CreatedAt value (a per-row backfill, which AddColumn's own
            // `defaultValue` can't express — that's a single fixed literal
            // applied to every existing row, not "this row's own CreatedAt")
            // — then tightened to NOT NULL once no row is null anymore. The
            // entity itself (User.LastActiveAt) is a plain, non-nullable
            // DateTime, matching CreatedAt's own convention; see that
            // property's doc comment for why this ends up NOT NULL rather
            // than staying nullable at the column level the way ADR-0038's
            // decision record anticipated.
            migrationBuilder.AddColumn<DateTime>(
                name: "LastActiveAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE \"Users\" SET \"LastActiveAt\" = \"CreatedAt\" WHERE \"LastActiveAt\" IS NULL;");

            migrationBuilder.AlterColumn<DateTime>(
                name: "LastActiveAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastActiveAt",
                table: "Users");
        }
    }
}
