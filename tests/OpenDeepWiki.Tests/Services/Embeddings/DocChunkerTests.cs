using System.Text;
using OpenDeepWiki.Services.Embeddings;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Embeddings;

public class DocChunkerTests
{
    private const string Title = "Заголовок страницы";

    public static TheoryData<string, string> Inputs()
    {
        var data = new TheoryData<string, string>();

        var paragraph = new string('а', 499) + ".";
        data.Add("three paragraphs of 500", string.Join("\n\n", paragraph, paragraph.Replace('а', 'б'), paragraph.Replace('а', 'в')));

        data.Add("3000 chars without dots", new string('x', 3000));

        var sentences = new StringBuilder();
        var i = 0;
        while (sentences.Length < 3000)
        {
            sentences.Append($"Предложение номер {i++} рассказывает о поиске! ");
        }

        data.Add("3000 chars with sentences", sentences.ToString(0, 3000));

        var lines = new StringBuilder();
        while (lines.Length < 3000)
        {
            lines.Append("строка без точки с переносом\n");
        }

        data.Add("3000 chars with lines", lines.ToString());
        return data;
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Split_KeepsLimitTitleAndText(string name, string content)
    {
        var chunks = DocChunker.Split(content, Title);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk =>
        {
            Assert.True(chunk.Length <= DocChunker.MaxChunkLength + Title.Length + 1,
                $"{name}: chunk of {chunk.Length} chars");
            Assert.StartsWith(Title + "\n", chunk);
        });

        var joined = string.Concat(chunks.Select(chunk => chunk[(Title.Length + 1)..]));
        Assert.Equal(RemoveWhitespace(content), RemoveWhitespace(joined));
    }

    [Fact]
    public void Split_ThreeShortParagraphs_MergesWithinLimit()
    {
        var paragraph = new string('a', 500);
        var chunks = DocChunker.Split(string.Join("\n\n", paragraph, paragraph, paragraph), Title);

        // 500 + 2 + 500 fits, the third paragraph does not.
        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public void Split_LongTextWithoutDots_CutsHardAtLimit()
    {
        var chunks = DocChunker.SplitBodies(new string('x', 3000));

        Assert.Equal([1200, 1200, 600], chunks.Select(chunk => chunk.Length).ToArray());
    }

    [Fact]
    public void Split_LongTextWithSentences_CutsAtSentenceEnd()
    {
        var sentence = "Это одно предложение о семантическом поиске. ";
        var content = string.Concat(Enumerable.Repeat(sentence, 80));

        var chunks = DocChunker.SplitBodies(content);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.EndsWith(".", chunk));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n \t \r\n")]
    [InlineData(null)]
    public void Split_EmptyPage_ReturnsNoChunks(string? content)
    {
        Assert.Empty(DocChunker.Split(content, Title));
    }

    private static string RemoveWhitespace(string value)
    {
        return new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }
}
