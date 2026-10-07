using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OpenDeepWiki.Services.Embeddings;

/// <summary>
/// Turns texts into unit-length embedding vectors.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>
    /// Returns one normalized vector per input text, in input order.
    /// Throws <see cref="EmbeddingServiceException"/> on any failure.
    /// Callers must not call it when <see cref="EmbeddingOptions.IsEnabled"/> is false.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}

public sealed class EmbeddingServiceException : Exception
{
    public EmbeddingServiceException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Client of an OpenAI-compatible <c>POST {endpoint}/embeddings</c> API
/// (text-embeddings-inference, LM Studio, Ollama, OpenAI).
/// </summary>
public sealed class EmbeddingClient(
    IHttpClientFactory httpClientFactory,
    IOptions<EmbeddingOptions> options) : IEmbeddingClient
{
    public const string HttpClientName = "embeddings";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions RequestJsonOptions = new();

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var settings = options.Value;
        if (!settings.IsEnabled)
        {
            throw new InvalidOperationException("Embedding service is disabled (EMBEDDING_ENDPOINT is empty).");
        }

        if (texts.Count == 0)
        {
            return [];
        }

        var url = settings.Endpoint!.TrimEnd('/') + "/embeddings";
        var body = JsonSerializer.Serialize(new { model = settings.Model, input = texts }, RequestJsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        var httpClient = httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            throw new EmbeddingServiceException($"Embedding request to {url} failed: {ex.Message}", ex);
        }

        using (response)
        {
            string responseText;
            try
            {
                responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                throw new EmbeddingServiceException(
                    $"Embedding request to {url} failed while reading the response (HTTP {(int)response.StatusCode}): {ex.Message}", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new EmbeddingServiceException(
                    $"Embedding request to {url} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(responseText, 300)}");
            }

            return ParseResponse(responseText, texts.Count, url, (int)response.StatusCode);
        }
    }

    private static IReadOnlyList<float[]> ParseResponse(string responseText, int expectedCount, string url, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw Invalid("missing 'data' array");
            }

            var result = new float[expectedCount][];
            var position = 0;
            foreach (var item in data.EnumerateArray())
            {
                var index = item.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number
                    ? indexElement.GetInt32()
                    : position;
                position++;

                if (index < 0 || index >= expectedCount || result[index] != null)
                {
                    throw Invalid($"unexpected index {index}");
                }

                if (!item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array)
                {
                    throw Invalid($"item {index} has no 'embedding' array");
                }

                var vector = new float[embedding.GetArrayLength()];
                var i = 0;
                foreach (var value in embedding.EnumerateArray())
                {
                    vector[i++] = value.GetSingle();
                }

                if (vector.Length == 0)
                {
                    throw Invalid($"item {index} has an empty embedding");
                }

                result[index] = EmbeddingVector.Normalize(vector);
            }

            if (position != expectedCount)
            {
                throw Invalid($"expected {expectedCount} embeddings, got {position}");
            }

            return result;
        }
        catch (EmbeddingServiceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw Invalid(ex.Message, ex);
        }

        EmbeddingServiceException Invalid(string reason, Exception? inner = null) =>
            new($"Embedding response from {url} (HTTP {statusCode}) has an invalid format: {reason}", inner);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
