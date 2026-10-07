using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenDeepWiki.Sqlite;

#nullable disable

namespace OpenDeepWiki.Sqlite.Migrations
{
    [DbContext(typeof(SqliteDbContext))]
    [Migration("20261007000000_AddDocChunkEmbeddings")]
    public partial class AddDocChunkEmbeddings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocChunkEmbeddings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DocFileId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    BranchLanguageId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ChunkIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceStamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Dimensions = table.Column<int>(type: "INTEGER", nullable: false),
                    Vector = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocChunkEmbeddings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocChunkEmbeddings_DocFileId",
                table: "DocChunkEmbeddings",
                column: "DocFileId");

            migrationBuilder.CreateIndex(
                name: "IX_DocChunkEmbeddings_BranchLanguageId",
                table: "DocChunkEmbeddings",
                column: "BranchLanguageId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "DocChunkEmbeddings");
        }
    }
}
