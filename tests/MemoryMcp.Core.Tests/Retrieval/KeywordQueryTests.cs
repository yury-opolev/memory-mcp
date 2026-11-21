using MemoryMcp.Core.Retrieval;
using Xunit;

namespace MemoryMcp.Core.Tests.Retrieval;

public class KeywordQueryTests
{
    [Fact]
    public void Build_PlainWords_FoldedQuotedAndOred()
    {
        Assert.Equal("body : (\"kobenhavn\" OR \"train\")", KeywordQuery.Build("København train", stemmed: false));
    }

    [Fact]
    public void Build_PhraseAndPrefix_StayPhraseAndPrefix()
    {
        Assert.Equal("body : (\"night train\" OR \"inv\"* OR \"town\")", KeywordQuery.Build("\"Night Train\" inv* town", stemmed: false));
    }

    [Fact]
    public void Build_Stemmed_AddsStemColumnForPlainWordsOnly()
    {
        var expression = KeywordQuery.Build("\"trains\" travelling", stemmed: true);

        Assert.Equal("body : (\"trains\" OR \"travelling\") OR stem : (\"travelling\" OR \"travel\")", expression);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("\"\" * - ( ) : ^")]
    public void Build_NoSearchableToken_ReturnsNull(string? query)
    {
        Assert.Null(KeywordQuery.Build(query, stemmed: true));
    }

    [Fact]
    public void Build_Fts5Syntax_IsNeutralised()
    {
        var expression = KeywordQuery.Build("body:secret OR NEAR(a b) -x", stemmed: false);

        Assert.Equal("body : (\"body\" OR \"secret\" OR \"or\" OR \"near\" OR \"a\" OR \"b\" OR \"x\")", expression);
    }
}
