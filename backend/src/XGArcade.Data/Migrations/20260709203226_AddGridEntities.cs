using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGridEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GridInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GridInstances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GridTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Size = table.Column<int>(type: "integer", nullable: false),
                    AllowedCategoryTypes = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GridTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GridCells",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GridInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Row = table.Column<int>(type: "integer", nullable: false),
                    Col = table.Column<int>(type: "integer", nullable: false),
                    RowCategoryType = table.Column<string>(type: "text", nullable: false),
                    RowCategoryValue = table.Column<string>(type: "text", nullable: false),
                    ColCategoryType = table.Column<string>(type: "text", nullable: false),
                    ColCategoryValue = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GridCells", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GridCells_GridInstances_GridInstanceId",
                        column: x => x.GridInstanceId,
                        principalTable: "GridInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GridCells_GridInstanceId_Row_Col",
                table: "GridCells",
                columns: new[] { "GridInstanceId", "Row", "Col" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GridCells");

            migrationBuilder.DropTable(
                name: "GridTemplates");

            migrationBuilder.DropTable(
                name: "GridInstances");
        }
    }
}
