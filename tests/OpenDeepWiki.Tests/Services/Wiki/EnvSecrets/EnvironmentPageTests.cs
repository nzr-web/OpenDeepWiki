using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Wiki;
using OpenDeepWiki.Services.Wiki.EnvSecrets;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Wiki.EnvSecrets;

public class EnvironmentPageTests
{
    private static EnvironmentScanResult ScanResult(params EnvironmentVariableReference[] variables) => new(variables, failed: false);

    private static EnvironmentVariableReference Variable(string name, string? envName = null, bool secret = false) => new()
    {
        Name = name,
        EnvName = envName,
        Sources = ["C#"],
        Locations = ["src/Program.cs:12"],
        IsLikelySecret = secret,
        SecretReason = secret ? "name token KEY" : null
    };

    // ---- path and budgets ----

    [Theory]
    [InlineData("environment-and-secrets", true)]
    [InlineData("/Environment-And-Secrets/", true)]
    [InlineData("overview", false)]
    [InlineData("ops/environment-and-secrets", false)]
    [InlineData(null, false)]
    public void IsEnvironmentPage(string? path, bool expected)
    {
        Assert.Equal(expected, EnvironmentPage.IsEnvironmentPage(path));
    }

    [Theory]
    [InlineData(4, 20)]
    [InlineData(10, 26)]
    public void GetDocumentBudgets_EnvironmentPage_Uses12SourcesAndAtLeast20Calls(int appendOperations, int expectedCalls)
    {
        var options = new WikiGeneratorOptions
        {
            MaxDocumentSourceToolCalls = 6,
            MaxDocumentToolCalls = 14,
            MaxDocumentAppendOperations = appendOperations
        };

        var budgets = EnvironmentPage.GetDocumentBudgets("environment-and-secrets", options);

        Assert.Equal(12, budgets.SourceToolCalls);
        Assert.True(budgets.MaxToolCalls >= 20);
        Assert.Equal(expectedCalls, budgets.MaxToolCalls);
        Assert.Equal(appendOperations, budgets.AppendOperations);
    }

    [Fact]
    public void GetDocumentBudgets_OtherPages_UseOptions()
    {
        var options = new WikiGeneratorOptions
        {
            MaxDocumentSourceToolCalls = 7,
            MaxDocumentToolCalls = 15,
            MaxDocumentAppendOperations = 3
        };

        var budgets = EnvironmentPage.GetDocumentBudgets("overview", options);

        Assert.Equal(new DocumentBudgets(7, 15, 3), budgets);
    }

    // ---- message ----

    [Fact]
    public void BuildEnvironmentPageMessage_ContainsTableContextAndLinks_WithoutGenericRequirements()
    {
        var options = new WikiGeneratorOptions();
        var workspace = new RepositoryWorkspace
        {
            Organization = "acme",
            RepositoryName = "shop",
            BranchName = "main",
            GitUrl = "https://github.com/acme/shop.git"
        };
        var scan = ScanResult(Variable("OPENAI_API_KEY", secret: true), Variable("LOG_LEVEL"));
        var table = EnvironmentReferenceScanner.Render(scan);

        var message = EnvironmentPage.BuildEnvironmentPageMessage(
            workspace,
            "ru",
            "https://github.com/acme/shop/blob/main",
            EnvironmentPage.CatalogPath,
            "Переменные окружения и секреты",
            table,
            EnvironmentPage.GetDocumentBudgets(EnvironmentPage.CatalogPath, options));

        Assert.Contains(table, message);
        Assert.Contains("| `OPENAI_API_KEY` |", message);
        Assert.Contains("## Runtime Context", message);
        Assert.Contains("- Repository: acme/shop", message);
        Assert.Contains("- Branch: main", message);
        Assert.Contains("- Target Language: ru", message);
        Assert.Contains("- Catalog Title: Переменные окружения и секреты", message);
        Assert.Contains("File Reference Base URL: https://github.com/acme/shop/blob/main", message);
        Assert.Contains("[Example.cs](https://github.com/acme/shop/blob/main/src/Example.cs#L10-L20)", message);
        Assert.Contains("WriteDoc", message);
        Assert.Contains("AppendDoc", message);
        Assert.Contains("12 calls", message);
        Assert.Contains("***", message);
        Assert.DoesNotContain("at least one", message);
        Assert.DoesNotContain($"at most {options.MaxDocumentSourceToolCalls} total calls", message);
    }

    // ---- completeness pass ----

    [Fact]
    public void EnsureAllVariablesListed_AppendsMissingNames()
    {
        var scan = ScanResult(Variable("LOG_LEVEL"), Variable("OPENAI_API_KEY", secret: true), Variable("DB_TYPE"));
        const string content = "# Переменные\n\n| `LOG_LEVEL` | уровень логов |\n";

        var result = EnvironmentPage.EnsureAllVariablesListed(content, scan, "ru");

        Assert.StartsWith(content.TrimEnd(), result);
        var section = result[result.IndexOf("## Не описано при генерации", StringComparison.Ordinal)..];
        Assert.Contains("| `OPENAI_API_KEY` | да | `src/Program.cs:12` | назначение не установлено |", section);
        Assert.Contains("| `DB_TYPE` | нет |", section);
        Assert.DoesNotContain("LOG_LEVEL", section);
    }

    [Theory]
    [InlineData("en", "## Not described during generation")]
    [InlineData("zh-CN", "## 生成时未描述")]
    public void EnsureAllVariablesListed_SectionTitleFollowsLanguage(string languageCode, string heading)
    {
        var result = EnvironmentPage.EnsureAllVariablesListed("# Page\n", ScanResult(Variable("PORT")), languageCode);

        Assert.Contains(heading, result);
    }

    [Fact]
    public void EnsureAllVariablesListed_AllPresent_ReturnsSameText()
    {
        var scan = ScanResult(Variable("LOG_LEVEL"), Variable("AI:Endpoint", "AI__Endpoint"));
        const string content = "# Page\n\nUse `LOG_LEVEL` and `AI:Endpoint`.\n";

        Assert.Equal(content, EnvironmentPage.EnsureAllVariablesListed(content, scan, "en"));
    }

    [Fact]
    public void EnsureAllVariablesListed_DotNetKeyMentionedAsEnvForm_Counts()
    {
        var scan = ScanResult(Variable("AI:Endpoint", "AI__Endpoint"));
        const string content = "# Page\n\nSet AI__Endpoint in compose.\n";

        Assert.Equal(content, EnvironmentPage.EnsureAllVariablesListed(content, scan, "en"));
    }

    [Fact]
    public void EnsureAllVariablesListed_NameInsideAnotherWord_DoesNotCount()
    {
        var scan = ScanResult(Variable("PORT"));
        const string content = "# Page\n\nThe REPORT_DIR and PORTAL settings.\n";

        var result = EnvironmentPage.EnsureAllVariablesListed(content, scan, "en");

        Assert.Contains("| `PORT` | no |", result);
    }

    [Theory]
    [InlineData("# Page\n\nSee the REPORT section.\n")]
    [InlineData("# Page\n\nREPORT\n")]
    [InlineData("# Page\n\nSet MY_PORT and XPORT.\n")]
    public void EnsureAllVariablesListed_NameAsTailOfAnotherWord_DoesNotCount(string content)
    {
        var scan = ScanResult(Variable("PORT"));

        var result = EnvironmentPage.EnsureAllVariablesListed(content, scan, "en");

        Assert.Contains("| `PORT` | no |", result);
    }

    [Fact]
    public void EnsureAllVariablesListed_FailedOrEmptyScan_ReturnsSameText()
    {
        const string content = "# Page\n";

        Assert.Equal(content, EnvironmentPage.EnsureAllVariablesListed(content, EnvironmentScanResult.FailedResult(), "en"));
        Assert.Equal(content, EnvironmentPage.EnsureAllVariablesListed(content, ScanResult(), "en"));
    }

    // ---- completeness pass against storage ----

    private class TestDbContext : MasterDbContext
    {
        public TestDbContext(DbContextOptions<TestDbContext> options)
            : base(options)
        {
        }
    }

    private sealed class InMemoryContextFactory(string databaseName) : IContextFactory
    {
        public TestDbContext Create() => new(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(databaseName).Options);

        public IContext CreateContext() => Create();
    }

    [Fact]
    public async Task ApplyCompletenessPassAsync_SavesAppendedSectionAndTimestamp()
    {
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        const string branchLanguageId = "bl-1";
        using (var seed = factory.Create())
        {
            seed.DocFiles.Add(new DocFile { Id = "doc-1", BranchLanguageId = branchLanguageId, Content = "# Env\n\n`LOG_LEVEL`\n" });
            seed.DocCatalogs.Add(new DocCatalog
            {
                Id = "cat-1",
                BranchLanguageId = branchLanguageId,
                Path = EnvironmentPage.CatalogPath,
                Title = "Env",
                DocFileId = "doc-1"
            });
            await seed.SaveChangesAsync();
        }

        var scan = ScanResult(Variable("LOG_LEVEL"), Variable("DB_TYPE"));
        await EnvironmentPage.ApplyCompletenessPassAsync(
            factory, branchLanguageId, EnvironmentPage.CatalogPath, scan, "en", NullLogger.Instance, CancellationToken.None);

        using var check = factory.Create();
        var doc = await check.DocFiles.SingleAsync(d => d.Id == "doc-1");
        Assert.Contains("## Not described during generation", doc.Content);
        Assert.Contains("`DB_TYPE`", doc.Content);
        Assert.NotNull(doc.UpdatedAt);
    }
}
