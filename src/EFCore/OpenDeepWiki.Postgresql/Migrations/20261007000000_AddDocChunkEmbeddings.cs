using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenDeepWiki.Postgresql;

#nullable disable

namespace OpenDeepWiki.Postgresql.Migrations
{
    [DbContext(typeof(PostgresqlDbContext))]
    [Migration("20261007000000_AddDocChunkEmbeddings")]
    public partial class AddDocChunkEmbeddings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocChunkEmbeddings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", maxLength: 36, nullable: false),
                    DocFileId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    BranchLanguageId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    ChunkIndex = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceStamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Dimensions = table.Column<int>(type: "integer", nullable: false),
                    Vector = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
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
