using System.Text;
using System.Text.RegularExpressions;

namespace OpenDeepWiki.Services.Embeddings;

/// <summary>
/// Splits a wiki page into chunks for embedding.
/// </summary>
internal static class DocChunker
{
    public const int MaxChunkLength = 1200;

    private static readonly Regex ParagraphSeparator = new(@"\r?\n[ \t]*\r?\n", RegexOptions.Compiled);

    private static readonly string[] SentenceEnds = [". ", "! ", "? "];

    /// <summary>
    /// Splits <paramref name="content"/> into chunks. Paragraphs (separated by blank
    /// lines) are merged while the chunk stays within <see cref="MaxChunkLength"/>;
    /// longer paragraphs are cut at the last sentence or line end within the limit,
    /// or hard at the limit. Every chunk starts with a line holding <paramref name="title"/>.
    /// </summary>
    public static IReadOnlyList<string> Split(string? content, string? title)
    {
        var bodies = SplitBodies(content);
        var header = (title ?? string.Empty).Trim();
        return bodies
            .Select(body => header.Length == 0 ? body : header + "\n" + body)
            .ToList();
    }

    /// <summary>
    /// Chunk bodies without the title line.
    /// </summary>
    public static IReadOnlyList<string> SplitBodies(string? content)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return chunks;
        }

        var paragraphs = ParagraphSeparator
            .Split(content)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .SelectMany(SplitLongParagraph);

        var current = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            if (current.Length > 0 && current.Length + 2 + paragraph.Length > MaxChunkLength)
            {
                Flush(current, chunks);
            }

            if (current.Length > 0)
            {
                current.Append("\n\n");
            }

            current.Append(paragraph);
        }

        Flush(current, chunks);
        return chunks;
    }

    private static IEnumerable<string> SplitLongParagraph(string paragraph)
    {
        var rest = paragraph;
        while (rest.Length > MaxChunkLength)
        {
            var cut = FindCut(rest);
            var piece = rest[..cut].Trim();
            if (piece.Length > 0)
            {
                yield return piece;
            }

            rest = rest[cut..].TrimStart();
        }

        if (rest.Length > 0)
        {
            yield return rest;
        }
    }

    /// <summary>
    /// Position (exclusive) where the first piece ends; always in 1..MaxChunkLength.
    /// </summary>
    private static int FindCut(string text)
    {
        var window = text[..MaxChunkLength];

        var best = -1;
        foreach (var end in SentenceEnds)
        {
            var index = window.LastIndexOf(end, StringComparison.Ordinal);
            if (index >= 0)
            {
                // Keep the punctuation mark with the sentence.
                best = Math.Max(best, index + 1);
            }
        }

        // A sentence end exactly at the window border (". " straddling the limit).
        if (best < 0 && MaxChunkLength < text.Length &&
            text[MaxChunkLength] == ' ' && text[MaxChunkLength - 1] is '.' or '!' or '?')
        {
            best = MaxChunkLength;
        }

        if (best <= 0)
        {
            var newline = window.LastIndexOf('\n');
            if (newline > 0)
            {
                best = newline;
            }
        }

        return best > 0 ? best : MaxChunkLength;
    }

    private static void Flush(StringBuilder current, List<string> chunks)
    {
        var chunk = current.ToString().Trim();
        if (chunk.Length > 0)
        {
            chunks.Add(chunk);
        }

        current.Clear();
    }
}
