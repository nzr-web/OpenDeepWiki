using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Wiki;
using OpenDeepWiki.Services.Wiki.EnvSecrets;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Wiki.EnvSecrets;

public class MandatoryCatalogPagesTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private class TestDbContext : MasterDbContext
    {
        public TestDbContext(DbContextOptions<TestDbContext> options)
            : base(options)
        {
        }

        public bool FailSaves { get; set; }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            if (FailSaves)
            {
                throw new DbUpdateException("simulated save failure");
            }

            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }

    private readonly TestDbContext _context;
    private readonly string _branchLanguageId = Guid.NewGuid().ToString();
    private readonly CapturingLogger _logger = new();

    public MandatoryCatalogPagesTests()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TestDbContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private static CatalogItem Item(string path, int order, params CatalogItem[] children) => new()
    {
        Title = "Title " + path,
        Path = path,
        Order = order,
        Children = children.ToList()
    };

    private async Task SeedAsync(params CatalogItem[] items)
    {
        var root = new CatalogRoot { Items = items.ToList() };
        await new CatalogStorage(_context, _branchLanguageId).SetCatalogAsync(JsonSerializer.Serialize(root, JsonOptions));
    }

    private Task EnsureAsync(string languageCode = "en")
    {
        return MandatoryCatalogPages.EnsureEnvironmentPageAsync(_context, _branchLanguageId, languageCode, _logger, CancellationToken.None);
    }

    private Task<List<DocCatalog>> LiveAsync()
    {
        return _context.DocCatalogs
            .Where(c => c.BranchLanguageId == _branchLanguageId && !c.IsDeleted)
            .OrderBy(c => c.Path)
            .ToListAsync();
    }

    private async Task<List<(string Path, string Title, int Order, string? ParentId)>> SnapshotAsync()
    {
        return (await LiveAsync()).Select(c => (c.Path, c.Title, c.Order, c.ParentId)).ToList();
    }

    [Fact]
    public async Task Missing_AddsLastTopLevelItemWithNextOrder()
    {
        await SeedAsync(Item("1-overview", 1), Item("2-architecture", 5, Item("2-1-core", 1)), Item("3-api", 3));

        await EnsureAsync();

        var live = await LiveAsync();
        var page = Assert.Single(live, c => c.Path == EnvironmentPage.CatalogPath);
        Assert.Null(page.ParentId);
        Assert.Equal(6, page.Order);
        Assert.Equal("Environment variables and secrets", page.Title);
        Assert.DoesNotContain(live, c => c.ParentId == page.Id);
        Assert.Equal(5, live.Count);

        var json = await new CatalogStorage(_context, _branchLanguageId).GetCatalogJsonAsync();
        var root = JsonSerializer.Deserialize<CatalogRoot>(json, JsonOptions)!;
        Assert.Equal(EnvironmentPage.CatalogPath, root.Items[^1].Path);
    }

    [Theory]
    [InlineData("ru", "Переменные окружения и секреты")]
    [InlineData("ZH", "环境变量与密钥")]
    [InlineData("zh-tw", "環境變數與密鑰")]
    [InlineData("pt-br", "Environment variables and secrets")]
    public async Task Title_DependsOnLanguage(string languageCode, string expected)
    {
        await SeedAsync(Item("1-overview", 1));

        await EnsureAsync(languageCode);

        var page = Assert.Single(await LiveAsync(), c => c.Path == EnvironmentPage.CatalogPath);
        Assert.Equal(expected, page.Title);
    }

    [Theory]
    [InlineData("environment-and-secrets")]
    [InlineData("/Environment-And-Secrets/")]
    public async Task AlreadyPresent_AtAnyLevel_NothingChanges(string existingPath)
    {
        await SeedAsync(Item("1-overview", 1), Item("2-ops", 2, Item(existingPath, 1)));
        var before = await SnapshotAsync();

        await EnsureAsync();

        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task CalledTwice_DoesNotDuplicate()
    {
        await SeedAsync(Item("1-overview", 1), Item("2-api", 2));

        await EnsureAsync();
        await EnsureAsync();

        Assert.Single(await LiveAsync(), c => c.Path == EnvironmentPage.CatalogPath);
        Assert.Single(await _context.DocCatalogs.Where(c => c.Path == EnvironmentPage.CatalogPath).ToListAsync());
    }

    [Fact]
    public async Task Insert_KeepsIdsOfExistingItemsAndDocFileOfLeaf()
    {
        await SeedAsync(Item("1-overview", 1), Item("2-architecture", 2, Item("2-1-core", 1), Item("2-2-data", 2)));
        var leaf = await _context.DocCatalogs.SingleAsync(c => c.Path == "2-1-core");
        var docFile = new DocFile { Id = Guid.NewGuid().ToString(), BranchLanguageId = _branchLanguageId, Content = "# Core" };
        _context.DocFiles.Add(docFile);
        leaf.DocFileId = docFile.Id;
        await _context.SaveChangesAsync();
        var idsBefore = (await LiveAsync()).ToDictionary(c => c.Path, c => c.Id);

        await EnsureAsync();

        var after = await LiveAsync();
        foreach (var (path, id) in idsBefore)
        {
            Assert.Equal(id, after.Single(c => c.Path == path).Id);
        }

        Assert.Equal(docFile.Id, after.Single(c => c.Path == "2-1-core").DocFileId);
        Assert.Contains(after, c => c.Path == EnvironmentPage.CatalogPath);
    }

    [Fact]
    public async Task SoftDeletedRecordWithSamePath_IsRevivedWithoutDuplicate()
    {
        // First full generation produced the page; the second one rewrote the catalog without it.
        await SeedAsync(Item("1-overview", 1), Item(EnvironmentPage.CatalogPath, 2));
        await SeedAsync(Item("1-overview", 1), Item("2-api", 2));
        Assert.DoesNotContain(await LiveAsync(), c => c.Path == EnvironmentPage.CatalogPath);

        await EnsureAsync();

        var all = await _context.DocCatalogs.Where(c => c.Path == EnvironmentPage.CatalogPath).ToListAsync();
        var record = Assert.Single(all);
        Assert.False(record.IsDeleted);
        Assert.Null(record.ParentId);
        Assert.Equal(3, record.Order);
    }

    [Fact]
    public async Task LiveItemUnderDeletedParent_IsNotDropped_PageNotAdded_AndWarns()
    {
        await SeedAsync(Item("1-overview", 1), Item("2-group", 2, Item("2-1-leaf", 1)));
        var parent = await _context.DocCatalogs.SingleAsync(c => c.Path == "2-group");
        parent.IsDeleted = true;
        await _context.SaveChangesAsync();
        var before = await SnapshotAsync();
        Assert.Contains(before, c => c.Path == "2-1-leaf");

        await EnsureAsync();

        Assert.Equal(before, await SnapshotAsync());
        Assert.DoesNotContain(await LiveAsync(), c => c.Path == EnvironmentPage.CatalogPath);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("was not added"));
    }

    [Fact]
    public async Task SaveFailure_RollsBackOwnBranchOnly_KeepsPendingEditOfOtherBranch()
    {
        await SeedAsync(Item("1-overview", 1), Item("2-api", 2));
        var otherBranchId = Guid.NewGuid().ToString();
        _context.DocCatalogs.Add(new DocCatalog
        {
            Id = Guid.NewGuid().ToString(),
            BranchLanguageId = otherBranchId,
            Title = "Other",
            Path = "other-overview",
            Order = 1
        });
        await _context.SaveChangesAsync();

        var other = await _context.DocCatalogs.SingleAsync(c => c.BranchLanguageId == otherBranchId);
        other.Title = "Other (pending edit)";
        _context.FailSaves = true;

        await EnsureAsync();

        // Own branch: SetCatalogAsync had marked items deleted before the save failed; that is undone.
        var own = _context.ChangeTracker.Entries<DocCatalog>()
            .Where(e => e.Entity.BranchLanguageId == _branchLanguageId)
            .ToList();
        Assert.NotEmpty(own);
        Assert.All(own, e =>
        {
            Assert.Equal(EntityState.Unchanged, e.State);
            Assert.False(e.Entity.IsDeleted);
        });
        Assert.DoesNotContain(own, e => e.Entity.Path == EnvironmentPage.CatalogPath);

        // Other branch: the pending edit survives.
        var otherEntry = _context.Entry(other);
        Assert.Equal(EntityState.Modified, otherEntry.State);
        Assert.Equal("Other (pending edit)", other.Title);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("Failed to add"));

        _context.FailSaves = false;
        await _context.SaveChangesAsync();
        Assert.Equal(2, await _context.DocCatalogs.CountAsync(c => c.BranchLanguageId == _branchLanguageId && !c.IsDeleted));
        Assert.Equal("Other (pending edit)", (await _context.DocCatalogs.AsNoTracking().SingleAsync(c => c.Id == other.Id)).Title);
    }

    [Fact]
    public async Task PresentWithChildren_NothingChanges_AndWarns()
    {
        await SeedAsync(Item("1-overview", 1), Item(EnvironmentPage.CatalogPath, 2, Item("env-child", 1)));
        var before = await SnapshotAsync();

        await EnsureAsync();

        Assert.Equal(before, await SnapshotAsync());
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("child items"));
    }
}
