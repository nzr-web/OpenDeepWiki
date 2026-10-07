using System.Net;
using System.Text;
using System.Text.Json;
using OpenDeepWiki.Services.Embeddings;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Embeddings;

public class EmbeddingVectorTests
{
    [Fact]
    public void Bytes_RoundTrip_IsLossless()
    {
        float[] vector = [0f, -0f, 1.5f, -3.25e-7f, float.MaxValue, float.MinValue, float.Epsilon, 0.1f, (float)Math.PI];

        var bytes = EmbeddingVector.ToBytes(vector);
        var restored = EmbeddingVector.FromBytes(bytes);

        Assert.Equal(vector.Length * 4, bytes.Length);
        Assert.Equal(
            vector.Select(BitConverter.SingleToInt32Bits),
            restored.Select(BitConverter.SingleToInt32Bits));
    }

    [Fact]
    public void Bytes_AreLittleEndianFloat32()
    {
        var bytes = EmbeddingVector.ToBytes([1f]);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, bytes);
    }
}

public class EmbeddingClientTests
{
    private const string Endpoint = "http://embeddings.test/v1";

    [Fact]
    public async Task EmbedAsync_SendsModelAndInput()
    {
        var handler = new StubHandler(_ => Ok(Response((0, [3f, 4f]), (1, [0f, 2f]))));
        var client = CreateClient(handler);

        await client.EmbedAsync(["первый", "second"]);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{Endpoint}/embeddings", request.Uri);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
        var input = body.RootElement.GetProperty("input");
        Assert.Equal(2, input.GetArrayLength());
        Assert.Equal("первый", input[0].GetString());
        Assert.Equal("second", input[1].GetString());
    }

    [Fact]
    public async Task EmbedAsync_WithApiKey_SendsBearer()
    {
        var handler = new StubHandler(_ => Ok(Response((0, [1f]))));
        var client = CreateClient(handler, apiKey: "secret-key");

        await client.EmbedAsync(["text"]);

        Assert.Equal("Bearer secret-key", Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task EmbedAsync_WithoutApiKey_SendsNoAuthorization()
    {
        var handler = new StubHandler(_ => Ok(Response((0, [1f]))));
        var client = CreateClient(handler);

        await client.EmbedAsync(["text"]);

        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task EmbedAsync_NormalizesVectors()
    {
        var handler = new StubHandler(_ => Ok(Response((0, [3f, 4f]), (1, [0f, 0.5f, 0f]))));
        var client = CreateClient(handler);

        var vectors = await client.EmbedAsync(["a", "b"]);

        Assert.All(vectors, vector =>
            Assert.Equal(1.0, Math.Sqrt(vector.Sum(value => (double)value * value)), 5));
        Assert.Equal(0.6f, vectors[0][0], 5);
        Assert.Equal(0.8f, vectors[0][1], 5);
    }

    [Fact]
    public async Task EmbedAsync_ShuffledIndexes_ReturnsInputOrder()
    {
        var handler = new StubHandler(_ => Ok(Response((2, [0f, 0f, 1f]), (0, [1f, 0f, 0f]), (1, [0f, 1f, 0f]))));
        var client = CreateClient(handler);

        var vectors = await client.EmbedAsync(["a", "b", "c"]);

        Assert.Equal(1f, vectors[0][0]);
        Assert.Equal(1f, vectors[1][1]);
        Assert.Equal(1f, vectors[2][2]);
    }

    [Fact]
    public async Task EmbedAsync_Http500_ThrowsWithStatusAndAddress()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("model is loading")
        });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<EmbeddingServiceException>(() => client.EmbedAsync(["a"]));

        Assert.Contains("500", ex.Message);
        Assert.Contains($"{Endpoint}/embeddings", ex.Message);
    }

    [Fact]
    public async Task EmbedAsync_NetworkError_ThrowsWithAddress()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused"));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<EmbeddingServiceException>(() => client.EmbedAsync(["a"]));

        Assert.Contains($"{Endpoint}/embeddings", ex.Message);
        Assert.Contains("Connection refused", ex.Message);
    }

    [Fact]
    public async Task EmbedAsync_InvalidFormat_ThrowsWithStatusAndAddress()
    {
        var handler = new StubHandler(_ => Ok("""{"object":"list"}"""));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<EmbeddingServiceException>(() => client.EmbedAsync(["a"]));

        Assert.Contains("200", ex.Message);
        Assert.Contains($"{Endpoint}/embeddings", ex.Message);
    }

    [Fact]
    public async Task EmbedAsync_WrongCount_Throws()
    {
        var handler = new StubHandler(_ => Ok(Response((0, [1f]))));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<EmbeddingServiceException>(() => client.EmbedAsync(["a", "b"]));
    }

    [Fact]
    public async Task EmbedAsync_Disabled_DoesNotCreateHttpClient()
    {
        var factory = new StubFactory(new StubHandler(_ => Ok(Response((0, [1f])))));
        var client = new EmbeddingClient(factory, EmbeddingTestData.Options(endpoint: null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync(["a"]));
        Assert.Equal(0, factory.Created);
    }

    private static EmbeddingClient CreateClient(StubHandler handler, string? apiKey = null)
    {
        return new EmbeddingClient(new StubFactory(handler), EmbeddingTestData.Options(endpoint: Endpoint + "/", apiKey: apiKey));
    }

    private static string Response(params (int Index, float[] Embedding)[] items)
    {
        return JsonSerializer.Serialize(new
        {
            @object = "list",
            data = items.Select(item => new { @object = "embedding", index = item.Index, embedding = item.Embedding }),
            model = "test-model"
        });
    }

    private static HttpResponseMessage Ok(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string Body, string? Authorization);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                body,
                request.Headers.Authorization?.ToString()));
            return respond(request);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public int Created { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Created++;
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}
