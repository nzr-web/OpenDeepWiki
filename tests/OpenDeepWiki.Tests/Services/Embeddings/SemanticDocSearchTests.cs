using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenDeepWiki.Services.Embeddings;
using OpenDeepWiki.Sqlite;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Embeddings;

public class SemanticDocSearchTests
{
    private static readonly string[] Scope = ["bl-1"];

    [Fact]
    public async Task Search_PageScoreIsBestChunk_AndSnippetComesFromIt()
    {
        using var database = new SqliteTestDatabase();
        await using (var seed = database.CreateContext())
        {
            EmbeddingTestData.AddPage(seed, "A", "page A");
            EmbeddingTestData.AddPage(seed, "B", "page B");
            seed.DocChunkEmbeddings.AddRange(
                EmbeddingTestData.Vector("A", EmbeddingTestData.WithCosine(0.9), chunkIndex: 0, text: "A best chunk"),
                EmbeddingTestData.Vector("A", EmbeddingTestData.WithCosine(0.1), chunkIndex: 1, text: "A weak chunk"));
            for (var i = 0; i < 4; i++)
            {
                seed.DocChunkEmbeddings.Add(EmbeddingTestData.Vector("B", EmbeddingTestData.WithCosine(0.5), chunkIndex: i));
            }

            await seed.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var result = await CreateSearch(context, new FakeEmbeddingClient()).SearchAsync("вопрос", Scope, 10);

        Assert.True(result.IsAvailable);
        Assert.Equal(["A", "B"], result.Hits.Select(hit => hit.DocFileId).ToArray());
        Assert.Equal(0.9, result.Hits[0].Score, 4);
        Assert.Equal(0.5, result.Hits[1].Score, 4);
        Assert.Equal("A best chunk", result.Hits[0].Snippet);
        Assert.Equal("bl-1", result.Hits[0].BranchLanguageId);
    }

    [Fact]
    public async Task Search_IgnoresVectorsOfAnotherModel()
    {
        using var database = new SqliteTestDatabase();
        await using (var seed = database.CreateContext())
        {
            EmbeddingTestData.AddPage(seed, "A", "page A");
            EmbeddingTestData.AddPage(seed, "C", "page C");
            seed.DocChunkEmbeddings.AddRange(
                EmbeddingTestData.Vector("A", EmbeddingTestData.WithCosine(0.6), text: "A chunk"),
                EmbeddingTestData.Vector("C", EmbeddingTestData.WithCosine(0.0), text: "C current"),
                EmbeddingTestData.Vector("C", [1f, 0f, 0f], model: "other-model", chunkIndex: 1, text: "C foreign perfect"));
            await seed.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var result = await CreateSearch(context, new FakeEmbeddingClient()).SearchAsync("вопрос", Scope, 10);

        Assert.True(result.IsAvailable);
        Assert.Equal("A", result.Hits[0].DocFileId);
        Assert.DoesNotContain(result.Hits, hit => hit.Snippet == "C foreign perfect");
        Assert.All(result.Hits, hit => Assert.True(hit.Score < 0.99));
    }

    [Fact]
    public async Task Search_SkipsVectorsOfOtherDimensions()
    {
        using var database = new SqliteTestDatabase();
        await using (var seed = database.CreateContext())
        {
            EmbeddingTestData.AddPage(seed, "A", "page A");
            EmbeddingTestData.AddPage(seed, "D", "page D");
            seed.DocChunkEmbeddings.AddRange(
                EmbeddingTestData.Vector("A", EmbeddingTestData.WithCosine(0.7)),
                EmbeddingTestData.Vector("D", [1f, 0f]));
            await seed.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var result = await CreateSearch(context, new FakeEmbeddingClient()).SearchAsync("вопрос", Scope, 10);

        Assert.True(result.IsAvailable);
        var hit = Assert.Single(result.Hits);
        Assert.Equal("A", hit.DocFileId);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(9, true)]
    public async Task Search_RequiresNinetyPercentCoverage(int indexedPages, bool expectedAvailable)
    {
        using var database = new SqliteTestDatabase();
        await using (var seed = database.CreateContext())
        {
            for (var i = 0; i < 10; i++)
            {
                EmbeddingTestData.AddPage(seed, $"P{i}", $"page {i}");
                if (i < indexedPages)
                {
                    seed.DocChunkEmbeddings.Add(EmbeddingTestData.Vector($"P{i}", EmbeddingTestData.WithCosine(0.5)));
                }
            }

            // Vectors of another model do not count as coverage.
            seed.DocChunkEmbeddings.Add(EmbeddingTestData.Vector("P9", [1f, 0f, 0f], model: "other-model"));
            await seed.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var result = await CreateSearch(context, new FakeEmbeddingClient()).SearchAsync("вопрос", Scope, 10);

        Assert.Equal(expectedAvailable, result.IsAvailable);
    }

    [Fact]
    public async Task Search_Disabled_IsUnavailableWithoutCallingClient()
    {
        using var database = new SqliteTestDatabase();
        await using (var seed = database.CreateContext())
        {
            EmbeddingTestData.AddPage(seed, "A", "page A");
            seed.DocChunkEmbeddings.Add(EmbeddingTestData.Vector("A", [1f, 0f, 0f]));
            await seed.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var client = new FakeEmbeddingClient();
        var search = new SemanticDocSearch(
            context,
            EmbeddingTestData.Options(endpoint: null),
            client,
            NullLogger<SemanticDocSearch>.Instance);

        var result = await search.SearchAsync("вопрос", Scope, 10);

        Assert.False(result.IsAvailable);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Search_ClientFailure_IsUnavailable()
    {
        using var database = new SqliteTestDatabase();
        await using (var seed = database.CreateContext())
        {
            EmbeddingTestData.AddPage(seed, "A", "page A");
            seed.DocChunkEmbeddings.Add(EmbeddingTestData.Vector("A", [1f, 0f, 0f]));
            await seed.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var client = new FakeEmbeddingClient { ThrowOnCall = new EmbeddingServiceException("down") };

        var result = await CreateSearch(context, client).SearchAsync("вопрос", Scope, 10);

        Assert.False(result.IsAvailable);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task Search_SendsQueryPrefix()
    {
        using var database = new SqliteTestDatabase();
        await using (var seed = database.CreateContext())
        {
            EmbeddingTestData.AddPage(seed, "A", "page A");
            seed.DocChunkEmbeddings.Add(EmbeddingTestData.Vector("A", [1f, 0f, 0f]));
            await seed.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var client = new FakeEmbeddingClient();
        await CreateSearch(context, client).SearchAsync("как устроен поиск", Scope, 10);

        var call = Assert.Single(client.Calls);
        Assert.Equal("query: как устроен поиск", Assert.Single(call));
    }

    [Fact]
    public async Task Search_EmptyPageMarker_CountsAsCoveredButIsNeverAHit()
    {
        using var database = new SqliteTestDatabase();
        await using (var seed = database.CreateContext())
        {
            EmbeddingTestData.AddPage(seed, "A", "page A");
            // Live for SQL, but without chunks: the worker stores a marker row for it.
            EmbeddingTestData.AddPage(seed, "E", "\u00A0\u00A0\f");
            seed.DocChunkEmbeddings.Add(EmbeddingTestData.Vector("A", EmbeddingTestData.WithCosine(0.4), text: "A chunk"));
            var marker = EmbeddingTestData.Vector("E", [1f]);
            marker.ChunkIndex = DocEmbeddingIndexWorker.EmptyPageMarkerChunkIndex;
            marker.Text = string.Empty;
            marker.Dimensions = 0;
            marker.Vector = [];
            seed.DocChunkEmbeddings.Add(marker);
            await seed.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var result = await CreateSearch(context, new FakeEmbeddingClient()).SearchAsync("вопрос", Scope, 10);

        // 2 of 2 live pages covered: with the marker ignored, coverage would be 50%.
        Assert.True(result.IsAvailable);
        var hit = Assert.Single(result.Hits);
        Assert.Equal("A", hit.DocFileId);
        Assert.Equal("A chunk", hit.Snippet);
    }

    [Fact]
    public async Task VectorQuery_DoesNotLoadText()
    {
        using var database = new SqliteTestDatabase();
        await using var context = database.CreateContext();

        var sql = SemanticDocSearch.BuildVectorQuery(context, "test-model", Scope).ToQueryString();

        Assert.Contains("\"Vector\"", sql);
        Assert.Contains("\"Dimensions\"", sql);
        Assert.DoesNotContain("\"Text\"", sql);

        // The query is executable as well.
        _ = await SemanticDocSearch.BuildVectorQuery(context, "test-model", Scope).ToListAsync();
    }

    private static SemanticDocSearch CreateSearch(SqliteDbContext context, IEmbeddingClient client)
    {
        return new SemanticDocSearch(
            context,
            EmbeddingTestData.Options(),
            client,
            NullLogger<SemanticDocSearch>.Instance);
    }
}
