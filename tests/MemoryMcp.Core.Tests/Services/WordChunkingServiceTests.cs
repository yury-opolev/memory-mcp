using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace MemoryMcp.Core.Tests.Services;

public class WordChunkingServiceTests
{
    private static WordChunkingService CreateService(int chunkSize = 10, int overlap = 2)
    {
        var options = Options.Create(new MemoryMcpOptions
        {
            ChunkSizeWords = chunkSize,
            ChunkOverlapWords = overlap,
        });
        return new WordChunkingService(options);
    }

    [Fact]
    public void Chunk_EmptyString_ReturnsEmpty()
    {
        var service = CreateService();
        var result = service.Chunk(string.Empty);
        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_NullString_ReturnsEmpty()
    {
        var service = CreateService();
        var result = service.Chunk(null!);
        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_WhitespaceOnly_ReturnsEmpty()
    {
        var service = CreateService();
        var result = service.Chunk("   \t\n  ");
        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_SingleWord_ReturnsSingleChunk()
    {
        var service = CreateService();
        var result = service.Chunk("hello");

        Assert.Single(result);
        Assert.Equal("hello", result[0].Text);
        Assert.Equal(0, result[0].ChunkIndex);
        Assert.Equal(0, result[0].StartOffset);
        Assert.Equal(5, result[0].Length);
    }

    [Fact]
    public void Chunk_FewWords_BelowChunkSize_ReturnsSingleChunk()
    {
        var service = CreateService(chunkSize: 10, overlap: 2);
        var text = "one two three four five";
        var result = service.Chunk(text);

        Assert.Single(result);
        Assert.Equal(text, result[0].Text);
        Assert.Equal(0, result[0].StartOffset);
        Assert.Equal(text.Length, result[0].Length);
    }

    [Fact]
    public void Chunk_ExactlyChunkSize_ReturnsSingleChunk()
    {
        var service = CreateService(chunkSize: 5, overlap: 1);
        var text = "one two three four five";
        var result = service.Chunk(text);

        Assert.Single(result);
        Assert.Equal(text, result[0].Text);
    }

    [Fact]
    public void Chunk_MultipleChunks_CorrectOverlap()
    {
        // ChunkSize=5, Overlap=2 => stride=3
        // Words: w0 w1 w2 w3 w4 w5 w6 w7
        // Chunk 0: w0 w1 w2 w3 w4
        // Chunk 1: w3 w4 w5 w6 w7
        var service = CreateService(chunkSize: 5, overlap: 2);
        var text = "zero one two three four five six seven";
        var result = service.Chunk(text);

        Assert.Equal(2, result.Count);

        // First chunk: words 0-4
        Assert.Equal(0, result[0].ChunkIndex);
        Assert.Equal("zero one two three four", result[0].Text);
        Assert.Equal(0, result[0].StartOffset);

        // Second chunk: words 3-7 (overlap of 2 words: "three four")
        Assert.Equal(1, result[1].ChunkIndex);
        Assert.Equal("three four five six seven", result[1].Text);
    }

    [Fact]
    public void Chunk_ThreeChunks_CorrectOffsets()
    {
        // ChunkSize=4, Overlap=1 => stride=3
        // Words: w0 w1 w2 w3 w4 w5 w6 w7 w8
        // Chunk 0: w0 w1 w2 w3
        // Chunk 1: w3 w4 w5 w6
        // Chunk 2: w6 w7 w8
        var service = CreateService(chunkSize: 4, overlap: 1);
        var text = "a b c d e f g h i";
        var result = service.Chunk(text);

        Assert.Equal(3, result.Count);

        Assert.Equal("a b c d", result[0].Text);
        Assert.Equal("d e f g", result[1].Text);
        Assert.Equal("g h i", result[2].Text);

        // Verify chunk indices are sequential
        for (int i = 0; i < result.Count; i++)
        {
            Assert.Equal(i, result[i].ChunkIndex);
        }
    }

    [Fact]
    public void Chunk_OffsetsAndLengthsAreCorrect()
    {
        var service = CreateService(chunkSize: 3, overlap: 1);
        var text = "hello world foo bar baz";
        var result = service.Chunk(text);

        // Each chunk's StartOffset and Length should correctly index into the original text
        foreach (var chunk in result)
        {
            var extracted = text.Substring(chunk.StartOffset, chunk.Length);
            Assert.Equal(chunk.Text, extracted);
        }
    }

    [Fact]
    public void Chunk_LeadingAndTrailingWhitespace_HandledCorrectly()
    {
        var service = CreateService(chunkSize: 10, overlap: 2);
        var text = "   hello world   ";
        var result = service.Chunk(text);

        Assert.Single(result);
        Assert.Equal("hello world", result[0].Text);
        Assert.Equal(3, result[0].StartOffset); // Starts after leading whitespace
    }

    [Fact]
    public void Chunk_MultipleWhitespaceBetweenWords_HandledCorrectly()
    {
        var service = CreateService(chunkSize: 10, overlap: 2);
        var text = "hello   world\t\tfoo\nbar";
        var result = service.Chunk(text);

        Assert.Single(result);
        // The chunk text should include the whitespace between words since it's a substring
        Assert.Equal(text.Trim(), result[0].Text);
    }

    [Fact]
    public void Constructor_ChunkSizeZero_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateService(chunkSize: 0, overlap: 0));
    }

    [Fact]
    public void Constructor_NegativeChunkSize_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateService(chunkSize: -1, overlap: 0));
    }

    [Fact]
    public void Constructor_NegativeOverlap_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateService(chunkSize: 10, overlap: -1));
    }

    [Fact]
    public void Constructor_OverlapEqualsChunkSize_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateService(chunkSize: 5, overlap: 5));
    }

    [Fact]
    public void Constructor_OverlapGreaterThanChunkSize_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateService(chunkSize: 5, overlap: 6));
    }

    [Fact]
    public void Chunk_NoOverlap_CorrectPartitioning()
    {
        var service = CreateService(chunkSize: 3, overlap: 0);
        var text = "a b c d e f g h i";
        var result = service.Chunk(text);

        Assert.Equal(3, result.Count);
        Assert.Equal("a b c", result[0].Text);
        Assert.Equal("d e f", result[1].Text);
        Assert.Equal("g h i", result[2].Text);
    }

    [Fact]
    public void Chunk_LargeText_ProducesMultipleChunks()
    {
        var service = CreateService(chunkSize: 512, overlap: 64);
        var words = Enumerable.Range(0, 2000).Select(i => $"word{i}");
        var text = string.Join(" ", words);
        var result = service.Chunk(text);

        // With 2000 words, chunk size 512, stride 448:
        // ceil((2000 - 512) / 448) + 1 = ceil(1488/448) + 1 = 4 + 1 = 5
        Assert.True(result.Count >= 4);
        Assert.True(result.Count <= 6);

        // Verify all chunk offsets/lengths are valid
        foreach (var chunk in result)
        {
            var extracted = text.Substring(chunk.StartOffset, chunk.Length);
            Assert.Equal(chunk.Text, extracted);
        }
    }
}
