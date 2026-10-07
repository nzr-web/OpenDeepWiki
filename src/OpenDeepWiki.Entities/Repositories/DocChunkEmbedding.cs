using System.ComponentModel.DataAnnotations;

namespace OpenDeepWiki.Entities;

/// <summary>
/// Embedding vector of one chunk of a generated wiki page (<see cref="DocFile"/>).
/// Used by the semantic documentation search.
/// </summary>
public class DocChunkEmbedding : AggregateRoot<string>
{
    /// <summary>
    /// Source page id.
    /// </summary>
    [Required]
    [StringLength(36)]
    public string DocFileId { get; set; } = string.Empty;

    /// <summary>
    /// Branch language of the source page.
    /// </summary>
    [Required]
    [StringLength(36)]
    public string BranchLanguageId { get; set; } = string.Empty;

    /// <summary>
    /// Position of the chunk inside the page, starting at 0.
    /// </summary>
    public int ChunkIndex { get; set; }

    /// <summary>
    /// Chunk text without the model prefix; used as the search snippet.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Hex SHA-256 of the page content at indexing time (diagnostics only).
    /// </summary>
    [StringLength(64)]
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Page source stamp (<c>UpdatedAt ?? CreatedAt</c>) read before chunking.
    /// </summary>
    public DateTime SourceStamp { get; set; }

    /// <summary>
    /// Embedding model name.
    /// </summary>
    [Required]
    [StringLength(256)]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Vector length.
    /// </summary>
    public int Dimensions { get; set; }

    /// <summary>
    /// Vector as float32 little-endian bytes.
    /// </summary>
    public byte[] Vector { get; set; } = [];
}
