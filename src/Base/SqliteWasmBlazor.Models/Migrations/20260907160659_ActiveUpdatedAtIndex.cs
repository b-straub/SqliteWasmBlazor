using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqliteWasmBlazor.Models.Migrations
{
    /// <inheritdoc />
    public partial class ActiveUpdatedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TodoItems_Active_UpdatedAt",
                table: "TodoItems",
                column: "UpdatedAt",
                descending: new bool[0],
                filter: "NOT \"IsDeleted\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TodoItems_Active_UpdatedAt",
                table: "TodoItems");
        }
    }
}
