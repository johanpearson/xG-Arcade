using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoundClosedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                table: "Rounds",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "Rounds");
        }
    }
}
