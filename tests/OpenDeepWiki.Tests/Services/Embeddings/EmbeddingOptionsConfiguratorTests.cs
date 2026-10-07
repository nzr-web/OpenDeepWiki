using Microsoft.Extensions.Configuration;
using OpenDeepWiki.Services.Embeddings;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Embeddings;

public class EmbeddingOptionsConfiguratorTests
{
    [Fact]
    public void Apply_WithoutSettings_KeepsDefaultsAndIsDisabled()
    {
        var options = new EmbeddingOptions();

        EmbeddingOptionsConfigurator.Apply(options, Build(new Dictionary<string, string?>()));

        Assert.False(options.IsEnabled);
        Assert.Equal("intfloat/multilingual-e5-small", options.Model);
        Assert.Null(options.ApiKey);
        Assert.Equal("query: ", options.QueryPrefix);
        Assert.Equal("passage: ", options.DocumentPrefix);
        Assert.Equal(30, options.IndexIntervalSeconds);
        Assert.Equal(32, options.BatchSize);
    }

    [Fact]
    public void Apply_ReadsFlatNames()
    {
        var options = new EmbeddingOptions();

        EmbeddingOptionsConfigurator.Apply(options, Build(new Dictionary<string, string?>
        {
            ["EMBEDDING_ENDPOINT"] = "http://embeddings:80/v1",
            ["EMBEDDING_MODEL"] = "Qwen/Qwen3-Embedding-0.6B",
            ["EMBEDDING_API_KEY"] = "key",
            ["EMBEDDING_QUERY_PREFIX"] = "Instruct: find docs\nQuery: ",
            ["EMBEDDING_DOCUMENT_PREFIX"] = "",
            ["EMBEDDING_INDEX_INTERVAL_SECONDS"] = "5",
            ["EMBEDDING_BATCH_SIZE"] = "8"
        }));

        Assert.True(options.IsEnabled);
        Assert.Equal("http://embeddings:80/v1", options.Endpoint);
        Assert.Equal("Qwen/Qwen3-Embedding-0.6B", options.Model);
        Assert.Equal("key", options.ApiKey);
        Assert.Equal("Instruct: find docs\nQuery: ", options.QueryPrefix);
        Assert.Equal(string.Empty, options.DocumentPrefix);
        Assert.Equal(5, options.IndexIntervalSeconds);
        Assert.Equal(8, options.BatchSize);
    }

    [Fact]
    public void Apply_ReadsSection_AndFlatNamesWin()
    {
        var options = new EmbeddingOptions();

        EmbeddingOptionsConfigurator.Apply(options, Build(new Dictionary<string, string?>
        {
            ["Embedding:Endpoint"] = "http://section/v1",
            ["Embedding:Model"] = "section-model",
            ["Embedding:BatchSize"] = "16",
            ["EMBEDDING_MODEL"] = "flat-model"
        }));

        Assert.Equal("http://section/v1", options.Endpoint);
        Assert.Equal("flat-model", options.Model);
        Assert.Equal(16, options.BatchSize);
    }

    private static IConfiguration Build(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
