using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqliteWasm.Data.Models.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TodoItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    IsCompleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "todoLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_todoLists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TypeTests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ByteValue = table.Column<byte>(type: "INTEGER", nullable: false),
                    NullableByteValue = table.Column<byte>(type: "INTEGER", nullable: true),
                    ShortValue = table.Column<short>(type: "INTEGER", nullable: false),
                    NullableShortValue = table.Column<short>(type: "INTEGER", nullable: true),
                    IntValue = table.Column<int>(type: "INTEGER", nullable: false),
                    NullableIntValue = table.Column<int>(type: "INTEGER", nullable: true),
                    LongValue = table.Column<long>(type: "INTEGER", nullable: false),
                    NullableLongValue = table.Column<long>(type: "INTEGER", nullable: true),
                    FloatValue = table.Column<float>(type: "REAL", nullable: false),
                    NullableFloatValue = table.Column<float>(type: "REAL", nullable: true),
                    DoubleValue = table.Column<double>(type: "REAL", nullable: false),
                    NullableDoubleValue = table.Column<double>(type: "REAL", nullable: true),
                    DecimalValue = table.Column<decimal>(type: "TEXT", nullable: false),
                    NullableDecimalValue = table.Column<decimal>(type: "TEXT", nullable: true),
                    BoolValue = table.Column<bool>(type: "INTEGER", nullable: false),
                    NullableBoolValue = table.Column<bool>(type: "INTEGER", nullable: true),
                    StringValue = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    NullableStringValue = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NullableDateTimeValue = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DateTimeOffsetValue = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    NullableDateTimeOffsetValue = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    TimeSpanValue = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    NullableTimeSpanValue = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    GuidValue = table.Column<Guid>(type: "TEXT", nullable: false),
                    NullableGuidValue = table.Column<Guid>(type: "TEXT", nullable: true),
                    BlobValue = table.Column<byte[]>(type: "BLOB", nullable: true),
                    EnumValue = table.Column<int>(type: "INTEGER", nullable: false),
                    NullableEnumValue = table.Column<int>(type: "INTEGER", nullable: true),
                    CharValue = table.Column<char>(type: "TEXT", nullable: false),
                    NullableCharValue = table.Column<char>(type: "TEXT", nullable: true),
                    IntList = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TypeTests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "todos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "binary(16)", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    DueDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    TodoListId = table.Column<Guid>(type: "binary(16)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_todos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_todos_todoLists_TodoListId",
                        column: x => x.TodoListId,
                        principalTable: "todoLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_todos_TodoListId",
                table: "todos",
                column: "TodoListId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TodoItems");

            migrationBuilder.DropTable(
                name: "todos");

            migrationBuilder.DropTable(
                name: "TypeTests");

            migrationBuilder.DropTable(
                name: "todoLists");
        }
    }
}
