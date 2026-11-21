using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Core.Tests.Storage;

/// <summary>
/// Search correctness against references computed outside the engine:
/// filtered search must equal brute force over the filtered set, and the
/// threshold lookups used by Cortex must return the same candidates as the
/// pre-increment-0 KNN search (fetch limit*10 nearest chunks, then group).
/// </summary>
public sealed class ExactSearchTests : IDisposable
{
    private const int Dimensions = 16;
    private const int Clusters = 8;

    private readonly string tempDir;
    private readonly MemoryMcpOptions options;
    private readonly SqliteVecMemoryStore store;
    private readonly Dictionary<string, (List<float[]> Vectors, List<string> Tags)> seeded = [];

    public ExactSearchTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"memorymcp_exact_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.options = new MemoryMcpOptions
        {
            DataDirectory = this.tempDir,
            DatabaseFileName = "exact.db",
            Ollama = new OllamaOptions { Dimensions = Dimensions },
        };
        this.store = new SqliteVecMemoryStore(
            Options.Create(this.options), new NullContentEncryptor(), Substitute.For<ILogger<SqliteVecMemoryStore>>());
    }

    public void Dispose()
    {
        this.store.Dispose();
        try
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort: a pooled handle may still be closing
        }
    }

    [Fact]
    public async Task SearchAsync_SelectiveTagFilter_EqualsBruteForceOverFilteredSet()
    {
        var ct = TestContext.Current.CancellationToken;
        // 1% of 1,000 memories carry the tag: the old limit*10 post-filter found ~0.5 of them.
        await this.SeedAsync(memoryCount: 1000, tagsFor: i => i % 100 == 7 ? ["rare"] : ["common"], ct);
        var query = ClusteredVector(new Random(99), cluster: 3);

        var actual = await this.store.SearchAsync(query, limit: 5, minScore: null, tags: ["rare"], ct);

        var expected = this.BruteForce(query, limit: 5, minScore: null, tags: ["rare"]);
        Assert.Equal(5, expected.Count);
        AssertSameRanking(expected, actual);
    }

    [Fact]
    public async Task SearchAsync_TagFilter_IsCaseInsensitiveForNonAsciiTags()
    {
        var ct = TestContext.Current.CancellationToken;
        await this.SeedAsync(memoryCount: 50, tagsFor: i => i == 10 ? ["Проект"] : ["прочее"], ct);

        var results = await this.store.SearchAsync(ClusteredVector(new Random(1), cluster: 0), limit: 5, tags: ["проект"], cancellationToken: ct);

        Assert.Equal("mem-0010", Assert.Single(results).MemoryId);
    }

    // Parameter sets of the Cortex threshold callers: consolidation (5, 0.3), compaction (10, 0.7),
    // subagent context (3, 0.3) and the ingest duplicate guard (5, 0.9).
    [Theory]
    [InlineData(5, 0.3f)]
    [InlineData(10, 0.7f)]
    [InlineData(3, 0.3f)]
    [InlineData(5, 0.9f)]
    public async Task FindSimilarAsync_CandidateSets_MatchPreviousKnnSearch(int limit, float minScore)
    {
        var ct = TestContext.Current.CancellationToken;
        await this.SeedAsync(memoryCount: 300, tagsFor: _ => [], ct);
        var service = new MemoryService(
            Substitute.For<IChunkingService>(),
            Substitute.For<IEmbeddingService>(),
            this.store,
            Substitute.For<IOptionsMonitor<MemoryMcpOptions>>(),
            Substitute.For<ILogger<MemoryService>>());
        var rng = new Random(2024);
        var nonEmptyQueries = 0;

        for (int q = 0; q < 20; q++)
        {
            var query = ClusteredVector(rng, cluster: q % Clusters);

            var actual = await service.FindSimilarAsync(query, limit, minScore, ct);

            var expected = await this.LegacyKnnSearchAsync(query, limit, minScore, ct);
            AssertSameRanking(expected, actual);
            nonEmptyQueries += expected.Count > 0 ? 1 : 0;
        }

        Assert.True(nonEmptyQueries > 0, "fixture must produce candidates at this threshold");
    }

    private async Task SeedAsync(int memoryCount, Func<int, List<string>> tagsFor, CancellationToken ct)
    {
        await this.store.InitializeAsync(ct);
        var rng = new Random(42);
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < memoryCount; i++)
        {
            var memoryId = $"mem-{i:D4}";
            var tags = tagsFor(i);
            var chunkCount = 1 + (i % 3);
            var chunks = new List<ChunkRecord>();
            var vectors = new List<float[]>();
            for (int c = 0; c < chunkCount; c++)
            {
                chunks.Add(new ChunkRecord
                {
                    MemoryId = memoryId, ChunkIndex = c, StartOffset = c * 10, Length = 10,
                    Title = memoryId, Tags = tags, CreatedAt = now, UpdatedAt = now,
                });
                vectors.Add(ClusteredVector(rng, cluster: i % Clusters));
            }

            await this.store.StoreMemoryAsync(memoryId, $"content {i}", chunks, vectors, ct);
            this.seeded[memoryId] = (vectors, tags);
        }
    }

    /// <summary>A fixed per-cluster centroid plus noise, so in-cluster cosine spans roughly 0.6-0.95.</summary>
    private static float[] ClusteredVector(Random rng, int cluster)
    {
        var centroidRng = new Random(1000 + cluster);
        var vector = new float[Dimensions];
        for (int d = 0; d < Dimensions; d++)
        {
            var centroid = (centroidRng.NextDouble() * 2) - 1;
            var noise = ((rng.NextDouble() * 2) - 1) * 0.5;
            vector[d] = (float)(centroid + noise);
        }

        return vector;
    }

    private List<(string MemoryId, float Score)> BruteForce(float[] query, int limit, float? minScore, List<string>? tags)
    {
        return this.seeded
            .Where(m => tags is null || tags.Any(t => m.Value.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            .Select(m => (MemoryId: m.Key, Score: m.Value.Vectors.Max(v => Cosine(query, v))))
            .Where(r => minScore is null || r.Score >= minScore.Value)
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();
    }

    private static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            na += a[i] * (double)a[i];
            nb += b[i] * (double)b[i];
        }

        return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }

    /// <summary>The search algorithm shipped before increment 0, verbatim, as the reference.</summary>
    private async Task<List<(string MemoryId, float Score)>> LegacyKnnSearchAsync(float[] query, int limit, float minScore, CancellationToken ct)
    {
        using var connection = new SqliteConnection($"Data Source={this.options.DatabasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(ct);
        SqliteVecExtension.Load(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT v.ChunkKey, v.distance
            FROM chunks_vec v
            INNER JOIN chunks c ON c.MemoryId || ':' || c.ChunkIndex = v.ChunkKey
            WHERE v.Vector MATCH @queryVector
            AND k = @fetchLimit
            ORDER BY v.distance
            """;
        cmd.Parameters.AddWithValue("@queryVector", DatabaseBytes.Of(query));
        cmd.Parameters.AddWithValue("@fetchLimit", limit * 10);

        var best = new Dictionary<string, float>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var chunkKey = reader.GetString(0);
            var memoryId = chunkKey[..chunkKey.LastIndexOf(':')];
            var score = 1.0f - reader.GetFloat(1);
            if (score < minScore)
            {
                continue;
            }

            if (!best.TryGetValue(memoryId, out var existing) || score > existing)
            {
                best[memoryId] = score;
            }
        }

        return best
            .Select(kv => (MemoryId: kv.Key, Score: kv.Value))
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();
    }

    private static void AssertSameRanking(List<(string MemoryId, float Score)> expected, List<SearchResult> actual)
    {
        Assert.Equal(expected.Select(e => e.MemoryId), actual.Select(a => a.MemoryId));
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Score, actual[i].Score, 4);
        }
    }
}
