using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyCalendar.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskEmbeddings",
                columns: table => new
                {
                    TaskItemId = table.Column<int>(type: "int", nullable: false),
                    Embedding = table.Column<byte[]>(type: "blob", nullable: false),
                    Model = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InputSha256 = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EmbeddedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskEmbeddings", x => x.TaskItemId);
                    table.ForeignKey(
                        name: "FK_TaskEmbeddings_Tasks_TaskItemId",
                        column: x => x.TaskItemId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskEmbeddings");
        }
    }
}
