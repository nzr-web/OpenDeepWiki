using Microsoft.Extensions.Configuration;

namespace OpenDeepWiki.Services.Embeddings;

/// <summary>
/// Applies flat configuration names (<c>EMBEDDING_*</c>, usually environment variables)
/// on top of the <c>Embedding</c> section, the same way as
/// <see cref="Wiki.WikiGeneratorOptionsConfigurator"/> does for <c>WIKI_*</c>.
/// Order: flat name, then <c>Embedding:*</c> key, then the default.
/// </summary>
public static class EmbeddingOptionsConfigurator
{
    public static void Apply(EmbeddingOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        options.Endpoint = ResolveString(configuration, "EMBEDDING_ENDPOINT", nameof(EmbeddingOptions.Endpoint), options.Endpoint)?.Trim();
        options.Model = ResolveString(configuration, "EMBEDDING_MODEL", nameof(EmbeddingOptions.Model), options.Model)?.Trim()
                        ?? EmbeddingOptions.DefaultModel;
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            options.Model = EmbeddingOptions.DefaultModel;
        }

        options.ApiKey = ResolveString(configuration, "EMBEDDING_API_KEY", nameof(EmbeddingOptions.ApiKey), options.ApiKey)?.Trim();

        // Prefixes may be intentionally empty (models without prefixes), so an
        // explicitly present empty value wins over the default.
        options.QueryPrefix = ResolvePrefix(configuration, "EMBEDDING_QUERY_PREFIX", nameof(EmbeddingOptions.QueryPrefix), options.QueryPrefix);
        options.DocumentPrefix = ResolvePrefix(configuration, "EMBEDDING_DOCUMENT_PREFIX", nameof(EmbeddingOptions.DocumentPrefix), options.DocumentPrefix);

        options.IndexIntervalSeconds = ResolveInt(configuration, "EMBEDDING_INDEX_INTERVAL_SECONDS", nameof(EmbeddingOptions.IndexIntervalSeconds), options.IndexIntervalSeconds);
        options.BatchSize = ResolveInt(configuration, "EMBEDDING_BATCH_SIZE", nameof(EmbeddingOptions.BatchSize), options.BatchSize);
    }

    private static string? ResolveString(IConfiguration configuration, string flatKey, string property, string? fallback)
    {
        var flat = configuration[flatKey];
        if (!string.IsNullOrWhiteSpace(flat))
        {
            return flat;
        }

        var section = configuration[$"{EmbeddingOptions.SectionName}:{property}"];
        return !string.IsNullOrWhiteSpace(section) ? section : fallback;
    }

    private static string ResolvePrefix(IConfiguration configuration, string flatKey, string property, string fallback)
    {
        return configuration[flatKey]
               ?? configuration[$"{EmbeddingOptions.SectionName}:{property}"]
               ?? fallback;
    }

    private static int ResolveInt(IConfiguration configuration, string flatKey, string property, int fallback)
    {
        if (int.TryParse(configuration[flatKey], out var flat))
        {
            return flat;
        }

        return int.TryParse(configuration[$"{EmbeddingOptions.SectionName}:{property}"], out var section)
            ? section
            : fallback;
    }
}
