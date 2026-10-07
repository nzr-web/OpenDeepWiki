using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;

namespace OpenDeepWiki.Services.Embeddings;

/// <summary>
/// Semantic search over indexed wiki pages.
/// </summary>
public interface ISemanticDocSearch
{
    /// <summary>
    /// Searches pages of the given branch languages. Never throws for service
    /// failures: returns <see cref="SemanticDocSearchResult.Unavailable"/> instead.
    /// </summary>
    Task<SemanticDocSearchResult> SearchAsync(
        string query,
        IReadOnlyCollection<string> branchLanguageIds,
        int topK,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One page found by the semantic search. <see cref="Score"/> is the cosine
/// similarity of the best chunk, <see cref="Snippet"/> is that chunk's text.
/// </summary>
public sealed record SemanticDocHit(
    string DocFileId,
    string BranchLanguageId,
    double Score,
    string Snippet);

public sealed record SemanticDocSearchResult(bool IsAvailable, IReadOnlyList<SemanticDocHit> Hits)
{
    public static SemanticDocSearchResult Unavailable { get; } = new(false, []);

    public static SemanticDocSearchResult Available(IReadOnlyList<SemanticDocHit> hits) => new(true, hits);
}

/// <summary>
/// Brute-force cosine search over vectors stored in <c>DocChunkEmbeddings</c>.
/// </summary>
public sealed class SemanticDocSearch(
    IContext context,
    IOptions<EmbeddingOptions> options,
    IEmbeddingClient embeddingClient,
    ILogger<SemanticDocSearch> logger) : ISemanticDocSearch
{
    /// <summary>
    /// Minimal share of live pages in scope that must be indexed with the current model.
    /// </summary>
    internal const double MinCoverage = 0.9;

    public async Task<SemanticDocSearchResult> SearchAsync(
        string query,
        IReadOnlyCollection<string> branchLanguageIds,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.IsEnabled || string.IsNullOrWhiteSpace(query) || branchLanguageIds.Count == 0 || topK <= 0)
        {
            return SemanticDocSearchResult.Unavailable;
        }

        var scope = branchLanguageIds.Distinct().ToList();
        var model = settings.Model;

        // Coverage: a partly indexed scope silently loses pages, keyword search is better then.
        var livePages = LiveDocFiles.Query(context).Where(file => scope.Contains(file.BranchLanguageId));
        var liveCount = await livePages.CountAsync(cancellationToken);
        if (liveCount == 0)
        {
            return SemanticDocSearchResult.Unavailable;
        }

        var indexedCount = await livePages
            .CountAsync(file => context.DocChunkEmbeddings.Any(e => e.DocFileId == file.Id && e.Model == model),
                cancellationToken);
        if (indexedCount < liveCount * MinCoverage)
        {
            return SemanticDocSearchResult.Unavailable;
        }

        float[] queryVector;
        try
        {
            var vectors = await embeddingClient.EmbedAsync([settings.QueryPrefix + query.Trim()], cancellationToken);
            if (vectors.Count != 1 || vectors[0].Length == 0)
            {
                logger.LogWarning("Embedding client returned no vector for the search query");
                return SemanticDocSearchResult.Unavailable;
            }

            queryVector = vectors[0];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Semantic search is unavailable, falling back to keywords: {Error}", ex.Message);
            return SemanticDocSearchResult.Unavailable;
        }

        var rows = await BuildVectorQuery(context, model, scope).ToListAsync(cancellationToken);

        var best = new Dictionary<string, (string ChunkId, string BranchLanguageId, double Score)>(StringComparer.Ordinal);
        var expectedBytes = queryVector.Length * sizeof(float);
        foreach (var row in rows)
        {
            if (row.Dimensions != queryVector.Length || row.Vector == null || row.Vector.Length != expectedBytes)
            {
                continue;
            }

            var score = EmbeddingVector.Dot(queryVector, row.Vector);
            if (!best.TryGetValue(row.DocFileId, out var current) || score > current.Score)
            {
                best[row.DocFileId] = (row.Id, row.BranchLanguageId, score);
            }
        }

        var top = best
            .OrderByDescending(pair => pair.Value.Score)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(topK)
            .ToList();
        if (top.Count == 0)
        {
            return SemanticDocSearchResult.Available([]);
        }

        var chunkIds = top.Select(pair => pair.Value.ChunkId).ToList();
        var texts = await context.DocChunkEmbeddings
            .AsNoTracking()
            .Where(e => chunkIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Text })
            .ToDictionaryAsync(e => e.Id, e => e.Text, cancellationToken);

        var hits = top
            .Select(pair => new SemanticDocHit(
                pair.Key,
                pair.Value.BranchLanguageId,
                pair.Value.Score,
                texts.TryGetValue(pair.Value.ChunkId, out var text) ? text : string.Empty))
            .ToList();

        return SemanticDocSearchResult.Available(hits);
    }

    /// <summary>
    /// Vectors of the current model in scope, live pages only. Deliberately does
    /// not load <c>Text</c>: snippets are read later for the selected chunks only.
    /// </summary>
    internal static IQueryable<VectorRow> BuildVectorQuery(
        IContext context,
        string model,
        IReadOnlyCollection<string> branchLanguageIds)
    {
        var live = LiveDocFiles.Query(context);
        return context.DocChunkEmbeddings
            .AsNoTracking()
            .Where(e => e.Model == model &&
                        branchLanguageIds.Contains(e.BranchLanguageId) &&
                        live.Any(file => file.Id == e.DocFileId))
            .Select(e => new VectorRow(e.Id, e.DocFileId, e.BranchLanguageId, e.Dimensions, e.Vector));
    }

    internal sealed record VectorRow(
        string Id,
        string DocFileId,
        string BranchLanguageId,
        int Dimensions,
        byte[] Vector);
}
