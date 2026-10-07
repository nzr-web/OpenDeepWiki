using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Embeddings;

/// <summary>
/// The single "live page" predicate used by indexing, vector cleanup and search:
/// a non-deleted <see cref="DocFile"/> with non-blank content that is referenced by
/// at least one non-deleted <see cref="DocCatalog"/>.
/// </summary>
internal static class LiveDocFiles
{
    public static IQueryable<DocFile> Query(IContext context)
    {
        return context.DocFiles
            .Where(file => !file.IsDeleted &&
                           file.Content.Trim(new[] { ' ', '\t', '\r', '\n' }) != string.Empty &&
                           context.DocCatalogs.Any(catalog => !catalog.IsDeleted && catalog.DocFileId == file.Id));
    }
}
