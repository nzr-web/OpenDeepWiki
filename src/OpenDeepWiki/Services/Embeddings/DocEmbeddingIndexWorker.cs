using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories;

namespace OpenDeepWiki.Services.Embeddings;

/// <summary>
/// Background indexer of wiki pages for the semantic search. Polls the database,
/// (re)embeds pages that have no vectors, vectors of another model or a newer
/// source stamp, and drops vectors of pages that are no longer live.
/// Does nothing while <c>EMBEDDING_ENDPOINT</c> is empty.
/// </summary>
public sealed class DocEmbeddingIndexWorker : BackgroundService
{
    internal const int MaxPagesPerPass = 20;
    internal const int EmptyPageMarkerChunkIndex = -1;
    internal static readonly TimeSpan FailurePause = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<DocEmbeddingIndexWorker> _logger;
    private readonly TimeProvider _timeProvider;

    private DateTimeOffset? _pausedUntil;
    private bool _recoveryPending;

    public DocEmbeddingIndexWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<EmbeddingOptions> options,
        ILogger<DocEmbeddingIndexWorker> logger)
        : this(scopeFactory, options, logger, TimeProvider.System)
    {
    }

    internal DocEmbeddingIndexWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<EmbeddingOptions> options,
        ILogger<DocEmbeddingIndexWorker> logger,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsEnabled)
        {
            return;
        }

        _logger.LogInformation(
            "Doc embedding index worker started. Model: {Model}, interval: {Interval}s, batch size: {BatchSize}",
            _options.Model,
            _options.GetIndexInterval().TotalSeconds,
            _options.GetBatchSize());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Doc embedding indexing pass failed");
            }

            try
            {
                await Task.Delay(_options.GetIndexInterval(), _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One indexing pass. Exposed for tests.
    /// </summary>
    internal async Task RunPassAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsEnabled)
        {
            return;
        }

        if (_pausedUntil is { } pausedUntil && _timeProvider.GetUtcNow() < pausedUntil)
        {
            return;
        }

        _pausedUntil = null;

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();
        var client = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();

        try
        {
            var candidates = await FindCandidatesAsync(context, _options.Model, cancellationToken);
            foreach (var docFileId in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await IndexPageAsync(context, client, docFileId, cancellationToken);
            }

            await RemoveStaleVectorsAsync(context, cancellationToken);
        }
        catch (EmbeddingClientFailedException ex)
        {
            _pausedUntil = _timeProvider.GetUtcNow() + FailurePause;
            _recoveryPending = true;
            _logger.LogWarning(
                "Embedding service is unavailable, indexing paused for {Minutes} minutes: {Error}",
                FailurePause.TotalMinutes,
                ex.InnerException?.Message ?? ex.Message);
        }
    }

    internal static Task<List<string>> FindCandidatesAsync(
        IContext context,
        string model,
        CancellationToken cancellationToken)
    {
        var embeddings = context.DocChunkEmbeddings;
        return LiveDocFiles.Query(context)
            .Where(file =>
                !embeddings.Any(e => e.DocFileId == file.Id) ||
                embeddings.Any(e => e.DocFileId == file.Id &&
                                    (e.Model != model || (file.UpdatedAt ?? file.CreatedAt) > e.SourceStamp)))
            .OrderBy(file => file.CreatedAt)
            .ThenBy(file => file.Id)
            .Select(file => file.Id)
            .Take(MaxPagesPerPass)
            .ToListAsync(cancellationToken);
    }

    private async Task IndexPageAsync(
        IContext context,
        IEmbeddingClient client,
        string docFileId,
        CancellationToken cancellationToken)
    {
        // 1. Content, title and source stamp, read before chunking.
        var page = await context.DocFiles
            .AsNoTracking()
            .Where(file => file.Id == docFileId)
            .Select(file => new
            {
                file.Id,
                file.BranchLanguageId,
                file.Content,
                SourceStamp = file.UpdatedAt ?? file.CreatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (page == null)
        {
            return;
        }

        var title = await context.DocCatalogs
            .AsNoTracking()
            .Where(catalog => !catalog.IsDeleted && catalog.DocFileId == docFileId)
            .OrderBy(catalog => catalog.Order)
            .ThenBy(catalog => catalog.Id)
            .Select(catalog => catalog.Title)
            .FirstOrDefaultAsync(cancellationToken);

        // 2. Chunks.
        var chunks = DocChunker.Split(page.Content, title);
        // 3. Vectors, outside of any transaction. A page without chunks (content of
        // whitespace the SQL predicate does not trim, e.g. NBSP) gets no vectors.
        var vectors = new List<float[]>(chunks.Count);
        var batchSize = _options.GetBatchSize();
        for (var offset = 0; offset < chunks.Count; offset += batchSize)
        {
            var batch = chunks
                .Skip(offset)
                .Take(batchSize)
                .Select(chunk => _options.DocumentPrefix + chunk)
                .ToList();
            var batchVectors = await EmbedAsync(client, batch, cancellationToken);
            if (batchVectors.Count != batch.Count)
            {
                throw new EmbeddingClientFailedException(new EmbeddingServiceException(
                    $"Embedding client returned {batchVectors.Count} vectors for {batch.Count} texts"));
            }

            vectors.AddRange(batchVectors);
        }

        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(page.Content))).ToLowerInvariant();
        var now = DateTime.UtcNow;
        var rows = chunks
            .Select((chunk, index) => new DocChunkEmbedding
            {
                Id = Guid.NewGuid().ToString(),
                DocFileId = page.Id,
                BranchLanguageId = page.BranchLanguageId,
                ChunkIndex = index,
                Text = chunk,
                ContentHash = contentHash,
                SourceStamp = page.SourceStamp,
                Model = _options.Model,
                Dimensions = vectors[index].Length,
                Vector = EmbeddingVector.ToBytes(vectors[index]),
                CreatedAt = now
            })
            .ToList();

        if (rows.Count == 0)
        {
            // Marker row: the page counts as indexed with the current model and stops
            // being a candidate until it changes; search skips it (Dimensions 0).
            rows.Add(new DocChunkEmbedding
            {
                Id = Guid.NewGuid().ToString(),
                DocFileId = page.Id,
                BranchLanguageId = page.BranchLanguageId,
                ChunkIndex = EmptyPageMarkerChunkIndex,
                Text = string.Empty,
                ContentHash = contentHash,
                SourceStamp = page.SourceStamp,
                Model = _options.Model,
                Dimensions = 0,
                Vector = [],
                CreatedAt = now
            });
        }

        // 4-5. Replace the page vectors atomically.
        var supportsRelational = EfContextCapabilities.SupportsExecuteUpdate(context);
        IDbContextTransaction? transaction = null;
        if (supportsRelational && context is DbContext dbContext)
        {
            transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            if (supportsRelational)
            {
                await context.DocChunkEmbeddings
                    .Where(e => e.DocFileId == page.Id)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            else
            {
                var existing = await context.DocChunkEmbeddings
                    .Where(e => e.DocFileId == page.Id)
                    .ToListAsync(cancellationToken);
                context.DocChunkEmbeddings.RemoveRange(existing);
            }

            context.DocChunkEmbeddings.AddRange(rows);
            await context.SaveChangesAsync(cancellationToken);

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }

        if (context is DbContext trackingContext)
        {
            trackingContext.ChangeTracker.Clear();
        }
    }

    internal static async Task RemoveStaleVectorsAsync(IContext context, CancellationToken cancellationToken)
    {
        var live = LiveDocFiles.Query(context);
        var stale = context.DocChunkEmbeddings.Where(e => !live.Any(file => file.Id == e.DocFileId));

        if (EfContextCapabilities.SupportsExecuteUpdate(context))
        {
            await stale.ExecuteDeleteAsync(cancellationToken);
            return;
        }

        var rows = await stale.ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        context.DocChunkEmbeddings.RemoveRange(rows);
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<float[]>> EmbedAsync(
        IEmbeddingClient client,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<float[]> vectors;
        try
        {
            vectors = await client.EmbedAsync(texts, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EmbeddingClientFailedException(ex);
        }

        if (_recoveryPending)
        {
            _recoveryPending = false;
            _logger.LogInformation("Embedding service is available again, indexing resumed");
        }

        return vectors;
    }

    private sealed class EmbeddingClientFailedException(Exception inner)
        : Exception("Embedding client failed", inner);
}
