using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Wiki.EnvSecrets;

/// <summary>
/// Catalog items every generated wiki must have, added by code after the model wrote the catalog.
/// </summary>
internal static class MandatoryCatalogPages
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Adds the <see cref="EnvironmentPage.CatalogPath"/> item as the last top-level item
    /// when no item with that path exists at any level. Never throws (except on cancellation):
    /// failures are logged as warnings and leave the catalog as the model wrote it.
    /// </summary>
    public static async Task EnsureEnvironmentPageAsync(
        IContext context,
        string branchLanguageId,
        string? languageCode,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var storage = new CatalogStorage(context, branchLanguageId);
            var catalogJson = await storage.GetCatalogJsonAsync(cancellationToken);
            var root = JsonSerializer.Deserialize<CatalogRoot>(catalogJson, JsonOptions) ?? new CatalogRoot();

            var existing = FindByPath(root.Items, EnvironmentPage.CatalogPath);
            if (existing is not null)
            {
                if (existing.Children.Count > 0)
                {
                    logger.LogWarning(
                        "Catalog item '{Path}' has child items; its page will not be generated because documents are written only to leaf items. BranchLanguageId: {BranchLanguageId}",
                        existing.Path,
                        branchLanguageId);
                }

                return;
            }

            // SetCatalogAsync replaces every live item with the tree it is given. If the tree
            // does not hold every live item (orphans of a deleted parent), writing it back
            // would drop them, so leave the catalog alone.
            var liveCount = await context.DocCatalogs
                .CountAsync(c => c.BranchLanguageId == branchLanguageId && !c.IsDeleted, cancellationToken);
            var treeCount = CountItems(root.Items);
            if (treeCount != liveCount)
            {
                logger.LogWarning(
                    "Mandatory catalog item '{Path}' was not added: the catalog tree has {TreeCount} items but {LiveCount} are stored. BranchLanguageId: {BranchLanguageId}",
                    EnvironmentPage.CatalogPath,
                    treeCount,
                    liveCount,
                    branchLanguageId);
                return;
            }

            var order = root.Items.Count == 0 ? 0 : root.Items.Max(item => item.Order) + 1;
            root.Items.Add(new CatalogItem
            {
                Title = EnvironmentPage.GetTitle(languageCode),
                Path = EnvironmentPage.CatalogPath,
                Order = order
            });

            await storage.SetCatalogAsync(JsonSerializer.Serialize(root, JsonOptions), cancellationToken);
            logger.LogInformation(
                "Added mandatory catalog item '{Path}' with order {Order}. BranchLanguageId: {BranchLanguageId}",
                EnvironmentPage.CatalogPath,
                order,
                branchLanguageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DiscardPendingCatalogChanges(context, branchLanguageId);
            throw;
        }
        catch (Exception ex)
        {
            DiscardPendingCatalogChanges(context, branchLanguageId);
            logger.LogWarning(
                ex,
                "Failed to add mandatory catalog item '{Path}'; the catalog is left as generated. BranchLanguageId: {BranchLanguageId}",
                EnvironmentPage.CatalogPath,
                branchLanguageId);
        }
    }

    internal static CatalogItem? FindByPath(IEnumerable<CatalogItem> items, string path)
    {
        foreach (var item in items)
        {
            if (string.Equals((item.Path ?? string.Empty).Trim('/'), path, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }

            var child = FindByPath(item.Children, path);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static int CountItems(IEnumerable<CatalogItem> items)
    {
        return items.Sum(item => 1 + CountItems(item.Children));
    }

    /// <summary>
    /// A failed SetCatalogAsync can leave existing items marked deleted in the shared context;
    /// a later SaveChanges by anyone would then persist the deletion. Undo tracked catalog edits
    /// of this branch language only; pending edits of other branches are not ours to discard.
    /// </summary>
    private static void DiscardPendingCatalogChanges(IContext context, string branchLanguageId)
    {
        if (context is not DbContext dbContext)
        {
            return;
        }

        var entries = dbContext.ChangeTracker.Entries<DocCatalog>()
            .Where(entry => string.Equals(entry.Entity.BranchLanguageId, branchLanguageId, StringComparison.Ordinal))
            .ToList();
        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;
                case EntityState.Modified:
                case EntityState.Deleted:
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                    break;
            }
        }
    }
}
