namespace OpenDeepWiki.Services.Embeddings;

/// <summary>
/// Settings of the semantic documentation search. An empty <see cref="Endpoint"/>
/// disables the feature: nothing is indexed and MCP search keeps using keywords.
/// </summary>
public class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    public const string DefaultModel = "intfloat/multilingual-e5-small";

    /// <summary>
    /// OpenAI-compatible API base, for example <c>http://embeddings:80/v1</c>.
    /// Empty means disabled. Env: <c>EMBEDDING_ENDPOINT</c>.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Model name sent in requests and stored with vectors. Env: <c>EMBEDDING_MODEL</c>.
    /// </summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>
    /// Optional bearer token. Env: <c>EMBEDDING_API_KEY</c>.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Prefix added to search queries. Env: <c>EMBEDDING_QUERY_PREFIX</c>.
    /// </summary>
    public string QueryPrefix { get; set; } = "query: ";

    /// <summary>
    /// Prefix added to document chunks. Env: <c>EMBEDDING_DOCUMENT_PREFIX</c>.
    /// </summary>
    public string DocumentPrefix { get; set; } = "passage: ";

    /// <summary>
    /// Pause between indexing passes. Env: <c>EMBEDDING_INDEX_INTERVAL_SECONDS</c>.
    /// </summary>
    public int IndexIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Chunks per embedding request. Env: <c>EMBEDDING_BATCH_SIZE</c>.
    /// </summary>
    public int BatchSize { get; set; } = 32;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Endpoint);

    internal int GetBatchSize() => BatchSize > 0 ? BatchSize : 32;

    internal TimeSpan GetIndexInterval() =>
        TimeSpan.FromSeconds(IndexIntervalSeconds > 0 ? IndexIntervalSeconds : 30);
}
