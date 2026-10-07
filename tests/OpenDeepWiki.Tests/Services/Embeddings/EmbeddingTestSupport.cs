using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Embeddings;
using OpenDeepWiki.Sqlite;

namespace OpenDeepWiki.Tests.Services.Embeddings;

/// <summary>
/// In-memory SQLite database (real relational provider, real transactions) shared by
/// every context created from it while the connection stays open.
/// </summary>
internal sealed class SqliteTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<SqliteDbContext> _options;

    public SqliteTestDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<SqliteDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = CreateContext();
        context.Database.EnsureCreated();
        // Tests seed pages without the whole repository chain.
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys=OFF");
    }

    /// <summary>
    /// The most recently created context (the worker's scope context during a pass).
    /// </summary>
    public SqliteDbContext? LastCreated { get; private set; }

    public SqliteDbContext CreateContext()
    {
        var context = new SqliteDbContext(_options);
        LastCreated = context;
        return context;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}

internal sealed class FakeEmbeddingClient : IEmbeddingClient
{
    private readonly Func<string, float[]> _vectorFor;

    public FakeEmbeddingClient(Func<string, float[]>? vectorFor = null)
    {
        _vectorFor = vectorFor ?? (_ => [1f, 0f, 0f]);
    }

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public int CallCount => Calls.Count;

    public Exception? ThrowOnCall { get; set; }

    public Action<IReadOnlyList<string>>? OnCall { get; set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        Calls.Add(texts.ToList());
        OnCall?.Invoke(texts);
        if (ThrowOnCall != null)
        {
            throw ThrowOnCall;
        }

        IReadOnlyList<float[]> result = texts
            .Select(text => EmbeddingVector.Normalize(_vectorFor(text).ToArray()))
            .ToList();
        return Task.FromResult(result);
    }
}

internal static class EmbeddingTestData
{
    public static IOptions<EmbeddingOptions> Options(
        string? endpoint = "http://embeddings.test/v1",
        string model = "test-model",
        int batchSize = 32,
        string? apiKey = null)
    {
        return Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions
        {
            Endpoint = endpoint,
            Model = model,
            BatchSize = batchSize,
            ApiKey = apiKey
        });
    }

    public static void AddPage(
        OpenDeepWiki.EFCore.IContext context,
        string id,
        string content,
        string branchLanguageId = "bl-1",
        string? title = null,
        DateTime? createdAt = null,
        bool withCatalog = true)
    {
        context.DocFiles.Add(new DocFile
        {
            Id = id,
            BranchLanguageId = branchLanguageId,
            Content = content,
            CreatedAt = createdAt ?? DateTime.UtcNow.AddHours(-1)
        });

        if (withCatalog)
        {
            context.DocCatalogs.Add(new DocCatalog
            {
                Id = $"catalog-{id}",
                BranchLanguageId = branchLanguageId,
                Title = title ?? $"Title {id}",
                Path = $"path/{id}",
                DocFileId = id,
                Order = 1
            });
        }
    }

    public static DocChunkEmbedding Vector(
        string docFileId,
        float[] vector,
        string model = "test-model",
        string branchLanguageId = "bl-1",
        int chunkIndex = 0,
        string? text = null)
    {
        var normalized = EmbeddingVector.Normalize(vector.ToArray());
        return new DocChunkEmbedding
        {
            Id = Guid.NewGuid().ToString(),
            DocFileId = docFileId,
            BranchLanguageId = branchLanguageId,
            ChunkIndex = chunkIndex,
            Text = text ?? $"{docFileId} chunk {chunkIndex}",
            ContentHash = "hash",
            SourceStamp = DateTime.UtcNow,
            Model = model,
            Dimensions = normalized.Length,
            Vector = EmbeddingVector.ToBytes(normalized)
        };
    }

    /// <summary>
    /// Unit vector in the (x, y) plane whose cosine with (1, 0, 0) equals <paramref name="cosine"/>.
    /// </summary>
    public static float[] WithCosine(double cosine)
    {
        return [(float)cosine, (float)Math.Sqrt(1 - cosine * cosine), 0f];
    }
}
