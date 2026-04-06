using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqliteWasmBlazor.Models.NoteMigrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "BLOB", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    Tag = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TodoId = table.Column<Guid>(type: "BLOB", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notes", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notes");
        }
    }
}
