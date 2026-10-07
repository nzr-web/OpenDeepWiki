using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Services.Embeddings;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Embeddings;

public class DocEmbeddingIndexWorkerTests
{
    [Fact]
    public async Task Pass_IndexesNewPage_WithDocumentPrefixAndTitle()
    {
        using var database = new SqliteTestDatabase();
        await SeedAsync(database, context => EmbeddingTestData.AddPage(context, "A", "Первый абзац.\n\nВторой абзац.", title: "Поиск"));
        var client = new FakeEmbeddingClient();
        using var harness = new WorkerHarness(database, client);

        await harness.Worker.RunPassAsync(CancellationToken.None);

        var call = Assert.Single(client.Calls);
        Assert.All(call, text => Assert.StartsWith("passage: Поиск\n", text));

        await using var verify = database.CreateContext();
        var rows = await verify.DocChunkEmbeddings.ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("A", row.DocFileId);
        Assert.Equal("bl-1", row.BranchLanguageId);
        Assert.Equal("test-model", row.Model);
        Assert.Equal(3, row.Dimensions);
        Assert.Equal(12, row.Vector.Length);
        Assert.StartsWith("Поиск\n", row.Text);
        Assert.DoesNotContain("passage:", row.Text);
        Assert.Equal(Sha256("Первый абзац.\n\nВторой абзац."), row.ContentHash);
    }

    [Fact]
    public async Task SecondPass_WithoutChanges_DoesNotCallClient()
    {
        using var database = new SqliteTestDatabase();
        await SeedAsync(database, context =>
        {
            EmbeddingTestData.AddPage(context, "A", "текст A");
            EmbeddingTestData.AddPage(context, "B", "текст B");
        });
        var client = new FakeEmbeddingClient();
        using var harness = new WorkerHarness(database, client);

        await harness.Worker.RunPassAsync(CancellationToken.None);
        var callsAfterFirstPass = client.CallCount;
        await harness.Worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(2, callsAfterFirstPass);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task ContentChange_WithUpdateTimestamp_Reindexes()
    {
        using var database = new SqliteTestDatabase();
        await SeedAsync(database, context => EmbeddingTestData.AddPage(context, "A", "старый текст"));
        var client = new FakeEmbeddingClient();
        using var harness = new WorkerHarness(database, client);
        await harness.Worker.RunPassAsync(CancellationToken.None);

        List<string> oldIds;
        await using (var edit = database.CreateContext())
        {
            oldIds = await edit.DocChunkEmbeddings.Select(e => e.Id).ToListAsync();
            var file = await edit.DocFiles.SingleAsync(f => f.Id == "A");
            file.Content = "новый текст\n\nещё абзац";
            file.UpdateTimestamp();
            await edit.SaveChangesAsync();
        }

        await harness.Worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(2, client.CallCount);
        await using var verify = database.CreateContext();
        var rows = await verify.DocChunkEmbeddings.ToListAsync();
        Assert.NotEmpty(rows);
        Assert.DoesNotContain(rows, row => oldIds.Contains(row.Id));
        Assert.All(rows, row => Assert.Equal(Sha256("новый текст\n\nещё абзац"), row.ContentHash));
    }

    [Fact]
    public async Task ChangeDuringEmbeddingRequest_IsReindexedOnNextPass()
    {
        using var database = new SqliteTestDatabase();
        await SeedAsync(database, context => EmbeddingTestData.AddPage(context, "A", "версия 1"));
        var client = new FakeEmbeddingClient();
        var changed = false;
        client.OnCall = _ =>
        {
            if (changed)
            {
                return;
            }

            changed = true;
            using var edit = database.CreateContext();
            var file = edit.DocFiles.Single(f => f.Id == "A");
            file.Content = "версия 2";
            file.UpdateTimestamp();
            edit.SaveChanges();
        };
        using var harness = new WorkerHarness(database, client);

        await harness.Worker.RunPassAsync(CancellationToken.None);
        await using (var afterFirst = database.CreateContext())
        {
            Assert.All(await afterFirst.DocChunkEmbeddings.ToListAsync(),
                row => Assert.Equal(Sha256("версия 1"), row.ContentHash));
        }

        await harness.Worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(2, client.CallCount);
        Assert.Contains(client.Calls[1], text => text.Contains("версия 2"));
        await using var verify = database.CreateContext();
        Assert.All(await verify.DocChunkEmbeddings.ToListAsync(),
            row => Assert.Equal(Sha256("версия 2"), row.ContentHash));

        // And it is stable afterwards.
        await harness.Worker.RunPassAsync(CancellationToken.None);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task Client_IsNeverCalledInsideTransaction()
    {
        using var database = new SqliteTestDatabase();
        await SeedAsync(database, context =>
        {
            EmbeddingTestData.AddPage(context, "A", "текст A");
            EmbeddingTestData.AddPage(context, "B", "текст B");
            EmbeddingTestData.AddPage(context, "C", "текст C");
        });
        var client = new FakeEmbeddingClient();
        var transactionSeen = new List<bool>();
        client.OnCall = _ => transactionSeen.Add(database.LastCreated!.Database.CurrentTransaction != null);
        using var harness = new WorkerHarness(database, client);

        await harness.Worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(3, transactionSeen.Count);
        Assert.All(transactionSeen, Assert.False);
        await using var verify = database.CreateContext();
        Assert.Equal(3, await verify.DocChunkEmbeddings.Select(e => e.DocFileId).Distinct().CountAsync());
    }

    [Fact]
    public async Task Pass_IndexesAtMostTwentyPages()
    {
        using var database = new SqliteTestDatabase();
        await SeedAsync(database, context =>
        {
            for (var i = 0; i < 25; i++)
            {
                EmbeddingTestData.AddPage(context, $"P{i:00}", $"страница {i}");
            }
        });
        var client = new FakeEmbeddingClient();
        using var harness = new WorkerHarness(database, client);

        await harness.Worker.RunPassAsync(CancellationToken.None);

        await using (var verify = database.CreateContext())
        {
            Assert.Equal(20, await verify.DocChunkEmbeddings.Select(e => e.DocFileId).Distinct().CountAsync());
        }

        await harness.Worker.RunPassAsync(CancellationToken.None);

        await using var verifyAll = database.CreateContext();
        Assert.Equal(25, await verifyAll.DocChunkEmbeddings.Select(e => e.DocFileId).Distinct().CountAsync());
    }

    [Fact]
    public async Task SeventyChunks_WithBatchSize32_UseThreeCalls()
    {
        var paragraphs = Enumerable.Range(0, 70).Select(i => $"Абзац {i} " + new string('ж', 1000));
        var content = string.Join("\n\n", paragraphs);
        Assert.Equal(70, DocChunker.Split(content, "Title").Count);

        using var database = new SqliteTestDatabase();
        await SeedAsync(database, context => EmbeddingTestData.AddPage(context, "A", content));
        var client = new FakeEmbeddingClient();
        using var harness = new WorkerHarness(database, client, EmbeddingTestData.Options(batchSize: 32));

        await harness.Worker.RunPassAsync(CancellationToken.None);

        Assert.Equal([32, 32, 6], client.Calls.Select(call => call.Count).ToArray());
        await using var verify = database.CreateContext();
        Assert.Equal(Enumerable.Range(0, 70),
            await verify.DocChunkEmbeddings.OrderBy(e => e.ChunkIndex).Select(e => e.ChunkIndex).ToListAsync());
    }

    [Fact]
    public async Task Pass_RemovesVectorsOfPagesThatAreNoLongerLive()
    {
        using var database = new SqliteTestDatabase();
        await SeedAsync(database, context =>
        {
            foreach (var id in new[] { "keep", "soft", "hard", "empty", "catalog-deleted", "catalog-unlinked" })
            {
                EmbeddingTestData.AddPage(context, id, $"текст {id}");
            }
        });
        var client = new FakeEmbeddingClient();
        using var harness = new WorkerHarness(database, client);
        await harness.Worker.RunPassAsync(CancellationToken.None);

        await using (var edit = database.CreateContext())
        {
            Assert.Equal(6, await edit.DocChunkEmbeddings.Select(e => e.DocFileId).Distinct().CountAsync());

            (await edit.DocFiles.SingleAsync(f => f.Id == "soft")).MarkAsDeleted();
            // Physically deleted file; its catalog row still points at it.
            edit.DocFiles.Remove(await edit.DocFiles.SingleAsync(f => f.Id == "hard"));
            var empty = await edit.DocFiles.SingleAsync(f => f.Id == "empty");
            empty.Content = " \n\t\r\n ";
            empty.UpdateTimestamp();
            (await edit.DocCatalogs.SingleAsync(c => c.Id == "catalog-catalog-deleted")).MarkAsDeleted();
            (await edit.DocCatalogs.SingleAsync(c => c.Id == "catalog-catalog-unlinked")).DocFileId = null;
            await edit.SaveChangesAsync();
        }

        await harness.Worker.RunPassAsync(CancellationToken.None);

        await using var verify = database.CreateContext();
        var remaining = await verify.DocChunkEmbeddings.Select(e => e.DocFileId).Distinct().ToListAsync();
        Assert.Equal(["keep"], remaining);
        Assert.Equal(6, client.CallCount);
    }

    [Fact]
    public async Task ClientFailure_PausesIndexing_ThenRecovers()
    {
        using var database = new SqliteTestDatabase();
        await SeedAsync(database, context => EmbeddingTestData.AddPage(context, "A", "текст"));
        var client = new FakeEmbeddingClient { ThrowOnCall = new EmbeddingServiceException("HTTP 503 at http://embeddings.test/v1/embeddings") };
        var logger = new ListLogger<DocEmbeddingIndexWorker>();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var harness = new WorkerHarness(database, client, logger: logger, time: time);

        await harness.Worker.RunPassAsync(CancellationToken.None);
        await harness.Worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(1, client.CallCount);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);

        time.Advance(DocEmbeddingIndexWorker.FailurePause + TimeSpan.FromSeconds(1));
        client.ThrowOnCall = null;
        await harness.Worker.RunPassAsync(CancellationToken.None);
        await harness.Worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(2, client.CallCount);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);
        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.DocChunkEmbeddings.CountAsync());
    }

    [Fact]
    public async Task DisabledFeature_DoesNothing()
    {
        using var database = new SqliteTestDatabase();
        await SeedAsync(database, context => EmbeddingTestData.AddPage(context, "A", "текст"));
        var client = new FakeEmbeddingClient();
        var logger = new ListLogger<DocEmbeddingIndexWorker>();
        using var harness = new WorkerHarness(database, client, EmbeddingTestData.Options(endpoint: ""), logger);

        await harness.Worker.RunPassAsync(CancellationToken.None);
        await harness.Worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(0, client.CallCount);
        Assert.Empty(logger.Entries);
        await using var verify = database.CreateContext();
        Assert.Equal(0, await verify.DocChunkEmbeddings.CountAsync());
    }

    private const string NbspContent = "\u00A0\u00A0 \f\u00A0";

    [Fact]
    public async Task UnchunkablePages_GetMarker_AndDoNotStarveNormalPages()
    {
        using var database = new SqliteTestDatabase();
        await SeedAsync(database, context =>
        {
            for (var i = 0; i < 21; i++)
            {
                EmbeddingTestData.AddPage(context, $"nbsp-{i:00}", NbspContent, createdAt: DateTime.UtcNow.AddHours(-2).AddSeconds(i));
            }

            EmbeddingTestData.AddPage(context, "normal", "обычный текст");
        });
        var client = new FakeEmbeddingClient();
        using var harness = new WorkerHarness(database, client);

        await harness.Worker.RunPassAsync(CancellationToken.None);
        await harness.Worker.RunPassAsync(CancellationToken.None);

        var call = Assert.Single(client.Calls);
        Assert.Contains("обычный текст", Assert.Single(call));

        await using var verify = database.CreateContext();
        var rows = await verify.DocChunkEmbeddings.ToListAsync();
        var markers = rows.Where(row => row.DocFileId.StartsWith("nbsp-")).ToList();
        Assert.Equal(21, markers.Count);
        Assert.All(markers, marker =>
        {
            Assert.Equal(DocEmbeddingIndexWorker.EmptyPageMarkerChunkIndex, marker.ChunkIndex);
            Assert.Equal(string.Empty, marker.Text);
            Assert.Empty(marker.Vector);
            Assert.Equal(0, marker.Dimensions);
            Assert.Equal("test-model", marker.Model);
            Assert.Equal(Sha256(NbspContent), marker.ContentHash);
        });
        Assert.Single(rows, row => row.DocFileId == "normal" && row.Dimensions == 3);
    }

    [Fact]
    public async Task UnchunkablePage_AfterPass_IsNotCandidate()
    {
        using var database = new SqliteTestDatabase();
        await SeedAsync(database, context => EmbeddingTestData.AddPage(context, "nbsp", NbspContent));
        var client = new FakeEmbeddingClient();
        using var harness = new WorkerHarness(database, client);

        await harness.Worker.RunPassAsync(CancellationToken.None);

        await using (var check = database.CreateContext())
        {
            Assert.Empty(await DocEmbeddingIndexWorker.FindCandidatesAsync(check, "test-model", CancellationToken.None));
        }

        await harness.Worker.RunPassAsync(CancellationToken.None);
        Assert.Equal(0, client.CallCount);
        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.DocChunkEmbeddings.CountAsync());
    }

    [Fact]
    public async Task UnchunkablePage_BecomingNormal_IsReindexedAndMarkerRemoved()
    {
        using var database = new SqliteTestDatabase();
        await SeedAsync(database, context => EmbeddingTestData.AddPage(context, "page", NbspContent));
        var client = new FakeEmbeddingClient();
        using var harness = new WorkerHarness(database, client);
        await harness.Worker.RunPassAsync(CancellationToken.None);

        await using (var edit = database.CreateContext())
        {
            var file = await edit.DocFiles.SingleAsync(f => f.Id == "page");
            file.Content = "теперь здесь есть текст";
            file.UpdateTimestamp();
            await edit.SaveChangesAsync();
        }

        await harness.Worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(1, client.CallCount);
        await using var verify = database.CreateContext();
        var row = Assert.Single(await verify.DocChunkEmbeddings.ToListAsync());
        Assert.Equal(0, row.ChunkIndex);
        Assert.Equal(3, row.Dimensions);
        Assert.Equal(Sha256("теперь здесь есть текст"), row.ContentHash);
    }

    private static async Task SeedAsync(SqliteTestDatabase database, Action<IContext> seed)
    {
        await using var context = database.CreateContext();
        seed(context);
        await context.SaveChangesAsync();
    }

    private static string Sha256(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed class WorkerHarness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public WorkerHarness(
            SqliteTestDatabase database,
            IEmbeddingClient client,
            IOptions<EmbeddingOptions>? options = null,
            ILogger<DocEmbeddingIndexWorker>? logger = null,
            TimeProvider? time = null)
        {
            var services = new ServiceCollection();
            services.AddScoped<IContext>(_ => database.CreateContext());
            services.AddSingleton(client);
            _provider = services.BuildServiceProvider();

            Worker = new DocEmbeddingIndexWorker(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                options ?? EmbeddingTestData.Options(),
                logger ?? new ListLogger<DocEmbeddingIndexWorker>(),
                time ?? TimeProvider.System);
        }

        public DocEmbeddingIndexWorker Worker { get; }

        public void Dispose()
        {
            _provider.Dispose();
        }
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
