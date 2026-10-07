using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.MCP;
using OpenDeepWiki.Services.Embeddings;
using OpenDeepWiki.Tests.Chat.Sessions;
using Xunit;

namespace OpenDeepWiki.Tests.MCP;

public class McpSemanticSearchTests
{
    private const string Query = "поиск документации";

    [Fact]
    public async Task SearchDocs_WithSemanticSearch_UsesVectorOrderAndCollapsesLanguages()
    {
        await using var context = TestDbContext.Create();
        await SeedKeywordFixtureAsync(context);

        // Keyword order first, to make sure the semantic order is really different.
        var keywordJson = await McpGlobalTools.SearchDocs(context, Query, language: "ru");
        using (var keyword = JsonDocument.Parse(keywordJson))
        {
            Assert.Equal(["a-repo", "b-repo", "c-repo"], RepoNames(keyword.RootElement.GetProperty("results")));
        }

        var semantic = new FakeSemanticSearch(new Dictionary<string, double>
        {
            ["c-repo-ru-doc"] = 0.9,
            ["b-repo-ru-doc"] = 0.8,
            ["a-repo-ru-doc"] = 0.6,
            ["a-repo-en-doc"] = 0.7
        });

        var json = await McpGlobalTools.SearchDocs(context, Query, semanticSearch: semantic);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var results = root.GetProperty("results");

        Assert.Equal(JsonValueKind.Null, root.GetProperty("language").ValueKind);
        Assert.Equal(["c-repo", "b-repo", "a-repo"], RepoNames(results));
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        Assert.All(results.EnumerateArray(), result =>
        {
            Assert.Equal("semantic", result.GetProperty("reason").GetString());
            Assert.Equal(0, result.GetProperty("matchedTokenCount").GetInt32());
            Assert.False(result.GetProperty("fullQueryMatched").GetBoolean());
        });
        Assert.Equal((int)Math.Round(0.9 * 1000), results[0].GetProperty("score").GetInt32());
        Assert.Equal(800, results[1].GetProperty("score").GetInt32());

        // a-repo exists in ru and en with the same path: one result, best score (en, 0.7).
        Assert.Equal(700, results[2].GetProperty("score").GetInt32());
        Assert.Equal("en", results[2].GetProperty("language").GetString());
        Assert.Equal("snippet of a-repo-en-doc", results[2].GetProperty("snippet").GetString());

        var routed = root.GetProperty("routedRepositories");
        Assert.Equal(["c-repo", "b-repo", "a-repo"], RepoNames(routed));
        Assert.All(routed.EnumerateArray(), item => Assert.Equal("semantic", item.GetProperty("reason").GetString()));

        // Every language of every ready repository was in scope.
        var scope = Assert.Single(semantic.Scopes);
        Assert.Equal(4, scope.Count);
    }

    [Fact]
    public async Task SearchDocs_WithSemanticSearchAndLanguage_LimitsScope()
    {
        await using var context = TestDbContext.Create();
        await SeedKeywordFixtureAsync(context);
        var semantic = new FakeSemanticSearch(new Dictionary<string, double>
        {
            ["a-repo-en-doc"] = 0.7,
            ["b-repo-ru-doc"] = 0.5
        });

        var json = await McpGlobalTools.SearchDocs(context, Query, "owner", "a-repo", "en", semanticSearch: semantic);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(["a-repo-en"], Assert.Single(semantic.Scopes).ToArray());
        Assert.Equal("en", document.RootElement.GetProperty("language").GetString());
        Assert.Equal(["a-repo"], RepoNames(document.RootElement.GetProperty("results")));
    }

    [Fact]
    public async Task SearchDocs_WhenSemanticUnavailable_ReturnsKeywordResultUnchanged()
    {
        await using var context = TestDbContext.Create();
        await SeedKeywordFixtureAsync(context);

        var withoutSemantic = await McpGlobalTools.SearchDocs(context, Query, language: "ru");
        var unavailable = await McpGlobalTools.SearchDocs(context, Query, language: "ru",
            semanticSearch: new FakeSemanticSearch(null));
        var nullService = await McpGlobalTools.SearchDocs(context, Query, language: "ru", semanticSearch: null);

        Assert.Equal(withoutSemantic, unavailable);
        Assert.Equal(withoutSemantic, nullService);
    }

    [Fact]
    public async Task SearchDocsAndReadDoc_WithoutLanguage_FindRussianDocuments()
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(context, "ru-repo", "owner", "ru-repo",
            ("ru", "Архитектура поиска", "search/architecture", "Индексация документации и поиск по смыслу."));

        var searchJson = await McpGlobalTools.SearchDocs(context, "индексация документации");
        using (var search = JsonDocument.Parse(searchJson))
        {
            var results = search.RootElement.GetProperty("results");
            Assert.True(results.GetArrayLength() > 0);
            Assert.Equal("search/architecture", results[0].GetProperty("path").GetString());
            Assert.Equal(JsonValueKind.Null, search.RootElement.GetProperty("language").ValueKind);
        }

        var readJson = await McpGlobalTools.ReadDoc(context, "owner", "ru-repo", "search/architecture");
        using var read = JsonDocument.Parse(readJson);
        Assert.Equal("ru", read.RootElement.GetProperty("language").GetString());
        Assert.Equal("Индексация документации и поиск по смыслу.", read.RootElement.GetProperty("content").GetString());

        var repositoriesJson = await McpGlobalTools.SearchRepositories(context, "индексация документации");
        using var repositories = JsonDocument.Parse(repositoriesJson);
        Assert.True(repositories.RootElement.GetProperty("repositories").GetArrayLength() > 0);
    }

    [Fact]
    public async Task GlobalTools_Json_KeepsCyrillicAndFieldNames()
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(context, "ru-repo", "owner", "ru-repo",
            ("ru", "Заголовок", "doc/path", "Привет, мир."));

        var errorJson = await McpGlobalTools.SearchDocs(context, "запрос", "Владелец", "репозиторий");
        Assert.Contains("Repository Владелец/репозиторий not found", errorJson);
        Assert.DoesNotContain("\\u04", errorJson);
        using (var error = JsonDocument.Parse(errorJson))
        {
            Assert.Equal(["error", "message"], PropertyNames(error.RootElement));
        }

        var readJson = await McpGlobalTools.ReadDoc(context, "owner", "ru-repo", "doc/path");
        Assert.Contains("Привет, мир.", readJson);
        Assert.Contains("Заголовок", readJson);
        Assert.DoesNotContain("\\u04", readJson);
        using var read = JsonDocument.Parse(readJson);
        Assert.Equal(
            ["branch", "content", "language", "owner", "path", "repo", "repository", "sourceFiles", "title"],
            PropertyNames(read.RootElement));
    }

    [Fact]
    public void RepositoryTools_JsonOptions_KeepCyrillicAndFieldNames()
    {
        var value = new
        {
            error = true,
            message = "Репозиторий не найден",
            matchLine = 0,
            Title = "Заголовок"
        };

        var relaxed = JsonSerializer.Serialize(value, McpRepositoryTools.JsonOptions);
        var original = JsonSerializer.Serialize(value);

        Assert.Contains("Репозиторий не найден", relaxed);
        Assert.Contains("Заголовок", relaxed);
        Assert.DoesNotContain("\\u04", relaxed);
        Assert.Contains("\\u04", original);
        using var relaxedDocument = JsonDocument.Parse(relaxed);
        using var originalDocument = JsonDocument.Parse(original);
        Assert.Equal(PropertyNames(originalDocument.RootElement), PropertyNames(relaxedDocument.RootElement));
        Assert.Null(McpRepositoryTools.JsonOptions.PropertyNamingPolicy);
    }

    [Fact]
    public async Task RepositorySearchLanguage_WithoutLanguage_PrefersDefaultThenAlphabetical()
    {
        await using var context = TestDbContext.Create();
        AddLanguage(context, "branch-1", "bl-en", "en", isDefault: false, withDocs: true);
        AddLanguage(context, "branch-1", "bl-de", "de", isDefault: true, withDocs: false);
        AddLanguage(context, "branch-1", "bl-ru", "ru", isDefault: true, withDocs: true);
        AddLanguage(context, "branch-2", "bl2-ru", "ru", isDefault: false, withDocs: true);
        AddLanguage(context, "branch-2", "bl2-en", "en", isDefault: false, withDocs: true);
        AddLanguage(context, "branch-2", "bl2-ar", "ar", isDefault: false, withDocs: false);
        AddLanguage(context, "branch-3", "bl3-zh", "zh", isDefault: true, withDocs: false, emptyCatalog: true);
        await context.SaveChangesAsync();

        Assert.Equal("ru", (await McpRepositoryTools.ResolveSearchLanguageAsync(context, "branch-1", null, default))?.LanguageCode);
        Assert.Equal("en", (await McpRepositoryTools.ResolveSearchLanguageAsync(context, "branch-2", null, default))?.LanguageCode);
        Assert.Equal("en", (await McpRepositoryTools.ResolveSearchLanguageAsync(context, "branch-2", "  ", default))?.LanguageCode);
        Assert.Null(await McpRepositoryTools.ResolveSearchLanguageAsync(context, "branch-3", null, default));

        Assert.Equal("bl-en", (await McpRepositoryTools.ResolveSearchLanguageAsync(context, "branch-1", "en", default))?.Id);
        Assert.Equal("bl-de", (await McpRepositoryTools.ResolveSearchLanguageAsync(context, "branch-1", "de", default))?.Id);
        Assert.Null(await McpRepositoryTools.ResolveSearchLanguageAsync(context, "branch-1", "fr", default));
    }

    [Fact]
    public async Task RepositorySemanticMatches_MapHitsToPages()
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(context, "repo", "owner", "repo",
            ("ru", "Первая", "first", "текст 1"));
        context.DocFiles.Add(new DocFile { Id = "second-doc", BranchLanguageId = "repo-ru", Content = "текст 2" });
        context.DocCatalogs.Add(new DocCatalog
        {
            Id = "second-catalog-b", BranchLanguageId = "repo-ru", Title = "Вторая (B)", Path = "second-b", DocFileId = "second-doc", Order = 2
        });
        context.DocCatalogs.Add(new DocCatalog
        {
            Id = "second-catalog-a", BranchLanguageId = "repo-ru", Title = "Вторая", Path = "second", DocFileId = "second-doc", Order = 1
        });
        await context.SaveChangesAsync();

        var semantic = new FakeSemanticSearch(new Dictionary<string, double>
        {
            ["second-doc"] = 0.83456,
            ["repo-ru-doc"] = 0.4
        });

        var matches = await McpRepositoryTools.SelectSemanticMatchesAsync(context, semantic, "repo-ru", "вопрос", 5, default);

        Assert.NotNull(matches);
        Assert.Equal(["second", "first"], matches!.Select(match => match.Path).ToArray());
        Assert.Equal("Вторая", matches[0].Title);
        Assert.Equal(0.83456, matches[0].Score!.Value, 5);
        Assert.Equal(0, matches[0].MatchLine);
        Assert.Equal("snippet of second-doc", matches[0].Snippet);
        Assert.Equal(["repo-ru"], Assert.Single(semantic.Scopes).ToArray());

        var unavailable = await McpRepositoryTools.SelectSemanticMatchesAsync(
            context, new FakeSemanticSearch(null), "repo-ru", "вопрос", 5, default);
        Assert.Null(unavailable);
    }

    [Fact]
    public void SearchDocsTool_SemanticParameter_IsInjectedNotExposed()
    {
        var services = new ServiceCollection();
        services.AddScoped<IContext>(_ => TestDbContext.Create());
        services.AddScoped<ISemanticDocSearch>(_ => new FakeSemanticSearch(null));
        using var provider = services.BuildServiceProvider();

        var tool = McpServerTool.Create(
            typeof(McpGlobalTools).GetMethod(nameof(McpGlobalTools.SearchDocs))!,
            options: new McpServerToolCreateOptions { Services = provider });

        var schema = tool.ProtocolTool.InputSchema.GetRawText();
        Assert.Contains("\"query\"", schema);
        Assert.Contains("\"language\"", schema);
        Assert.DoesNotContain("semanticSearch", schema, StringComparison.OrdinalIgnoreCase);

        // Control: without the DI registration the SDK would expose it as an argument.
        var bareServices = new ServiceCollection();
        bareServices.AddScoped<IContext>(_ => TestDbContext.Create());
        using var bareProvider = bareServices.BuildServiceProvider();
        var bareTool = McpServerTool.Create(
            typeof(McpGlobalTools).GetMethod(nameof(McpGlobalTools.SearchDocs))!,
            options: new McpServerToolCreateOptions { Services = bareProvider });
        Assert.Contains("semanticSearch", bareTool.ProtocolTool.InputSchema.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SeedKeywordFixtureAsync(TestDbContext context)
    {
        await SeedRepositoryAsync(context, "a-repo", "owner", "a-repo",
            ("ru", "Поиск документации", "docs/search", "поиск документации и ещё раз поиск документации"),
            ("en", "Documentation search", "docs/search", "documentation search in english"));
        await SeedRepositoryAsync(context, "b-repo", "owner", "b-repo",
            ("ru", "Обзор", "docs/overview", "здесь есть поиск по документации"));
        await SeedRepositoryAsync(context, "c-repo", "owner", "c-repo",
            ("ru", "Прочее", "docs/misc", "документации не хватает, поиск помогает"));
    }

    private static async Task SeedRepositoryAsync(
        TestDbContext context,
        string repositoryId,
        string owner,
        string repo,
        params (string Language, string Title, string Path, string Content)[] pages)
    {
        var branchId = $"{repositoryId}-branch";
        context.Repositories.Add(new Repository
        {
            Id = repositoryId,
            OwnerUserId = "user-1",
            OrgName = owner,
            RepoName = repo,
            GitUrl = $"https://example.com/{owner}/{repo}.git",
            Status = RepositoryStatus.Completed
        });
        context.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = branchId,
            RepositoryId = repositoryId,
            BranchName = "main"
        });

        var first = true;
        foreach (var page in pages)
        {
            var languageId = $"{repositoryId}-{page.Language}";
            var docFileId = $"{languageId}-doc";
            context.BranchLanguages.Add(new BranchLanguage
            {
                Id = languageId,
                RepositoryBranchId = branchId,
                LanguageCode = page.Language,
                IsDefault = first
            });
            context.DocFiles.Add(new DocFile
            {
                Id = docFileId,
                BranchLanguageId = languageId,
                Content = page.Content
            });
            context.DocCatalogs.Add(new DocCatalog
            {
                Id = $"{languageId}-catalog",
                BranchLanguageId = languageId,
                Title = page.Title,
                Path = page.Path,
                DocFileId = docFileId,
                Order = 1
            });
            first = false;
        }

        await context.SaveChangesAsync();
    }

    private static void AddLanguage(
        TestDbContext context,
        string branchId,
        string id,
        string code,
        bool isDefault,
        bool withDocs,
        bool emptyCatalog = false)
    {
        context.BranchLanguages.Add(new BranchLanguage
        {
            Id = id,
            RepositoryBranchId = branchId,
            LanguageCode = code,
            IsDefault = isDefault
        });

        if (withDocs)
        {
            context.DocCatalogs.Add(new DocCatalog
            {
                Id = $"{id}-catalog", BranchLanguageId = id, Title = "t", Path = "p", DocFileId = $"{id}-doc"
            });
        }

        if (emptyCatalog)
        {
            context.DocCatalogs.Add(new DocCatalog
            {
                Id = $"{id}-folder", BranchLanguageId = id, Title = "folder", Path = "folder", DocFileId = string.Empty
            });
        }
    }

    private static string[] RepoNames(JsonElement array)
    {
        return array.EnumerateArray().Select(item => item.GetProperty("repo").GetString()!).ToArray();
    }

    private static string[] PropertyNames(JsonElement element)
    {
        return element.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Returns configured pages of the requested scope by descending score;
    /// a null map means "semantic search unavailable".
    /// </summary>
    private sealed class FakeSemanticSearch(IReadOnlyDictionary<string, double>? scores) : ISemanticDocSearch
    {
        public List<IReadOnlyCollection<string>> Scopes { get; } = [];

        public Task<SemanticDocSearchResult> SearchAsync(
            string query,
            IReadOnlyCollection<string> branchLanguageIds,
            int topK,
            CancellationToken cancellationToken = default)
        {
            Scopes.Add(branchLanguageIds.OrderBy(id => id, StringComparer.Ordinal).ToList());
            if (scores == null)
            {
                return Task.FromResult(SemanticDocSearchResult.Unavailable);
            }

            var hits = scores
                .Select(pair => (DocFileId: pair.Key, BranchLanguageId: pair.Key[..^"-doc".Length], Score: pair.Value))
                .Select(item => item.DocFileId == "second-doc" ? item with { BranchLanguageId = "repo-ru" } : item)
                .Where(item => branchLanguageIds.Contains(item.BranchLanguageId))
                .OrderByDescending(item => item.Score)
                .Take(topK)
                .Select(item => new SemanticDocHit(item.DocFileId, item.BranchLanguageId, item.Score, $"snippet of {item.DocFileId}"))
                .ToList();
            return Task.FromResult(SemanticDocSearchResult.Available(hits));
        }
    }
}
