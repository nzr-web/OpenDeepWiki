using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Infrastructure;
using Xunit;

namespace OpenDeepWiki.Tests.Infrastructure;

public class DbInitializerEmbeddingTests
{
    [Fact]
    public async Task InitializeAsync_WhenSqliteDatabaseIsMissingDocChunkEmbeddings_CreatesTableAndIndexes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");

        try
        {
            await using (var setupContext = CreateContext(dbPath))
            {
                await setupContext.Database.EnsureCreatedAsync();
                await setupContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS DocChunkEmbeddings");
                Assert.False(await CountAsync(setupContext, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'DocChunkEmbeddings'") > 0);
            }

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddLogging();
            services.AddScoped<IContext>(_ => CreateContext(dbPath));
            await using (var serviceProvider = services.BuildServiceProvider())
            {
                await DbInitializer.InitializeAsync(serviceProvider);
            }

            await using var verificationContext = CreateContext(dbPath);
            Assert.Equal(1, await CountAsync(verificationContext,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'DocChunkEmbeddings'"));
            foreach (var column in new[]
                     {
                         "Id", "DocFileId", "BranchLanguageId", "ChunkIndex", "Text", "ContentHash", "SourceStamp",
                         "Model", "Dimensions", "Vector", "CreatedAt", "UpdatedAt", "DeletedAt", "IsDeleted", "Version"
                     })
            {
                Assert.Equal(1, await CountAsync(verificationContext,
                    $"SELECT COUNT(*) FROM pragma_table_info('DocChunkEmbeddings') WHERE name = '{column}'"));
            }

            Assert.Equal(2, await CountAsync(verificationContext,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('IX_DocChunkEmbeddings_DocFileId', 'IX_DocChunkEmbeddings_BranchLanguageId')"));

            // The EF model can use the created table.
            Assert.Equal(0, await verificationContext.DocChunkEmbeddings.CountAsync());
        }
        finally
        {
            try
            {
                File.Delete(dbPath);
            }
            catch (IOException)
            {
                // SQLite can keep a file handle alive briefly after context disposal.
            }
        }
    }

    private static SqliteTestDbContext CreateContext(string dbPath)
    {
        var options = new DbContextOptionsBuilder<SqliteTestDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new SqliteTestDbContext(options);
    }

    private static async Task<long> CountAsync(DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class SqliteTestDbContext(DbContextOptions<SqliteTestDbContext> options)
        : MasterDbContext(options);
}
