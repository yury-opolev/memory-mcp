using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using Microsoft.Extensions.Options;

namespace MemoryMcp.Core.Services;

/// <summary>
/// Splits text into chunks by word boundaries with configurable size and overlap.
/// Uses whitespace splitting (~1.3 tokens per word on average).
/// </summary>
public class WordChunkingService : IChunkingService
{
    private readonly int _chunkSizeWords;
    private readonly int _chunkOverlapWords;

    public WordChunkingService(IOptions<MemoryMcpOptions> options)
    {
        _chunkSizeWords = options.Value.ChunkSizeWords;
        _chunkOverlapWords = options.Value.ChunkOverlapWords;

        if (_chunkSizeWords <= 0)
            throw new ArgumentException("ChunkSizeWords must be positive.", nameof(options));
        if (_chunkOverlapWords < 0)
            throw new ArgumentException("ChunkOverlapWords must be non-negative.", nameof(options));
        if (_chunkOverlapWords >= _chunkSizeWords)
            throw new ArgumentException("ChunkOverlapWords must be less than ChunkSizeWords.", nameof(options));
    }

    public List<ChunkInfo> Chunk(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        // Find word boundaries: each entry is (startCharIndex, endCharIndex) of a word
        var wordSpans = FindWordSpans(text);

        if (wordSpans.Count == 0)
            return [];

        var chunks = new List<ChunkInfo>();
        int stride = _chunkSizeWords - _chunkOverlapWords;
        int chunkIndex = 0;

        for (int wordStart = 0; wordStart < wordSpans.Count; wordStart += stride)
        {
            int wordEnd = Math.Min(wordStart + _chunkSizeWords, wordSpans.Count);

            int charStart = wordSpans[wordStart].Start;
            int charEnd = wordSpans[wordEnd - 1].End;

            chunks.Add(new ChunkInfo
            {
                ChunkIndex = chunkIndex++,
                Text = text[charStart..charEnd],
                StartOffset = charStart,
                Length = charEnd - charStart,
            });

            // If we've consumed all words, stop
            if (wordEnd >= wordSpans.Count)
                break;
        }

        return chunks;
    }

    /// <summary>
    /// Finds the start and end character indices of each whitespace-delimited word in the text.
    /// </summary>
    private static List<(int Start, int End)> FindWordSpans(string text)
    {
        var spans = new List<(int Start, int End)>();
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            // Skip whitespace
            while (i < len && char.IsWhiteSpace(text[i]))
                i++;

            if (i >= len)
                break;

            int start = i;

            // Consume non-whitespace
            while (i < len && !char.IsWhiteSpace(text[i]))
                i++;

            spans.Add((start, i));
        }

        return spans;
    }
}
