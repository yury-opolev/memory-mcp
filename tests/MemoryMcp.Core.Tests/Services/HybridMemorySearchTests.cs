using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace MemoryMcp.Core.Tests.Services;

public class HybridMemorySearchTests
{
    private static readonly float[] Vector = [1, 0, 0, 0];

    private readonly IEmbeddingService embedding = Substitute.For<IEmbeddingService>();
    private readonly IMemoryStore store = Substitute.For<IMemoryStore>();
    private readonly MemoryMcpOptions options = new();
    private readonly HybridMemorySearch search;

    public HybridMemorySearchTests()
    {
        var monitor = Substitute.For<IOptionsMonitor<MemoryMcpOptions>>();
        monitor.CurrentValue.Returns(this.options);
        this.store.KeywordIndexUnavailableReason.Returns((string?)null);
        this.embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Vector);
        this.store.GetMemoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new MemoryResult { MemoryId = call.Arg<string>(), Content = "content of " + call.Arg<string>() });
        this.search = new HybridMemorySearch(this.embedding, this.store, monitor);
    }

    private static SearchResult Dense(string id, float score) => new() { MemoryId = id, Content = "content of " + id, Score = score };

    private void DenseList(int limit, params SearchResult[] results) =>
        this.store.SearchDenseAsync(Vector, limit, Arg.Any<float?>(), Arg.Any<MemorySearchFilter>(), Arg.Any<CancellationToken>()).Returns(results.ToList());

    private void KeywordList(params string[] ids) =>
        this.store.KeywordSearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<MemorySearchFilter>(), Arg.Any<CancellationToken>())
            .Returns(ids.Select((id, i) => new KeywordHit(id, 10 - i)).ToList());

    private Task<HybridSearchResult> SearchAsync(string query, MemorySearchMode mode, MemorySearchFilter? filter = null, float? minScore = null, MemorySearchOptions? settings = null) =>
        this.search.SearchAsync(
            new HybridSearchRequest { Query = query, Mode = mode, Limit = 3, MinScore = minScore, Filter = filter ?? MemorySearchFilter.None, Settings = settings },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Dense_IsThePreviousSemanticSearch()
    {
        var filter = new MemorySearchFilter { Tags = ["project"] };
        this.DenseList(3, Dense("a", 0.9f), Dense("b", 0.8f));

        var result = await this.SearchAsync("query", MemorySearchMode.Dense, filter, minScore: 0.3f);

        Assert.Equal(["a", "b"], result.Hits.Select(h => h.Memory.MemoryId));
        Assert.All(result.Hits, h => Assert.Equal(MemoryMatchKind.Semantic, h.Match));
        Assert.Equal(MemorySearchMode.Dense, result.EffectiveMode);
        Assert.Null(result.Fallback);
        Assert.Equal(0.9f, result.TopSimilarity);
        await this.embedding.Received(1).EmbedAsync("query", Arg.Any<CancellationToken>());
        await this.store.Received(1).SearchDenseAsync(Vector, 3, 0.3f, filter, Arg.Any<CancellationToken>());
        await this.store.DidNotReceiveWithAnyArgs().KeywordSearchAsync(default!, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Dense_EmbedderFailure_Propagates()
    {
        this.embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("down"));

        await Assert.ThrowsAsync<HttpRequestException>(() => this.SearchAsync("query", MemorySearchMode.Dense));
    }

    [Fact]
    public async Task Keyword_RanksByBm25WithoutEmbedding()
    {
        this.KeywordList("k2", "k1");

        var result = await this.SearchAsync("invoice", MemorySearchMode.Keyword);

        Assert.Equal(["k2", "k1"], result.Hits.Select(h => h.Memory.MemoryId));
        Assert.All(result.Hits, h => Assert.Equal(MemoryMatchKind.Keyword, h.Match));
        Assert.All(result.Hits, h => Assert.Null(h.Similarity));
        Assert.Equal("content of k2", result.Hits[0].Memory.Content);
        await this.embedding.DidNotReceiveWithAnyArgs().EmbedAsync(default!, TestContext.Current.CancellationToken);
        await this.store.Received(1).KeywordSearchAsync("body : (\"invoice\")", 50, MemorySearchFilter.None, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Hybrid_FusesBothListsWithTheSameFilter()
    {
        var filter = new MemorySearchFilter { ExcludeIds = new HashSet<string> { "quarantined" } };
        this.KeywordList("a", "b");
        this.DenseList(50, Dense("b", 0.7f), Dense("c", 0.6f));

        var result = await this.SearchAsync("trip plans", MemorySearchMode.Hybrid, filter);

        Assert.Equal(["b", "a", "c"], result.Hits.Select(h => h.Memory.MemoryId));
        Assert.Equal([MemoryMatchKind.Both, MemoryMatchKind.Keyword, MemoryMatchKind.Semantic], result.Hits.Select(h => h.Match));
        Assert.Equal(0.7f, result.Hits[0].Similarity);
        Assert.Equal(MemorySearchMode.Hybrid, result.EffectiveMode);
        await this.store.Received(1).KeywordSearchAsync(Arg.Any<string>(), 50, filter, Arg.Any<CancellationToken>());
        await this.store.Received(1).SearchDenseAsync(Vector, 50, null, filter, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Hybrid_ExactIntent_GivesTheKeywordListPriority()
    {
        this.KeywordList("k1", "k2");
        this.DenseList(50, Dense("d1", 0.9f));

        var plain = await this.SearchAsync("invoice", MemorySearchMode.Hybrid);
        var exact = await this.SearchAsync("INV-2041", MemorySearchMode.Hybrid);

        Assert.Equal(["k1", "d1", "k2"], plain.Hits.Select(h => h.Memory.MemoryId));
        Assert.Equal(["k1", "k2", "d1"], exact.Hits.Select(h => h.Memory.MemoryId));
    }

    [Fact]
    public async Task Hybrid_EmbedderFails_FallsBackToKeywordWithReason()
    {
        this.embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("down"));
        this.KeywordList("k1");

        var result = await this.SearchAsync("invoice", MemorySearchMode.Hybrid);

        Assert.Equal(MemorySearchMode.Keyword, result.EffectiveMode);
        Assert.Equal("the query embedding failed", result.Fallback);
        Assert.Equal(["k1"], result.Hits.Select(h => h.Memory.MemoryId));
    }

    [Fact]
    public async Task HybridOrKeyword_KeywordIndexUnavailable_FallsBackToDense()
    {
        this.store.KeywordIndexUnavailableReason.Returns("old SQLite");
        this.DenseList(3, Dense("a", 0.9f));

        var result = await this.SearchAsync("invoice", MemorySearchMode.Keyword);

        Assert.Equal(MemorySearchMode.Dense, result.EffectiveMode);
        Assert.Equal("the keyword index is unavailable", result.Fallback);
        Assert.Equal(["a"], result.Hits.Select(h => h.Memory.MemoryId));
    }

    [Fact]
    public async Task Settings_Override_InstructionVariantAndCandidates()
    {
        var settings = new MemorySearchOptions { UseQueryInstruction = true, QueryInstruction = "Find notes", KeywordVariant = MemoryKeywordVariant.Stemmed, Candidates = 7 };
        this.embedding.EmbedQueryAsync("travels", "Find notes", Arg.Any<CancellationToken>()).Returns(Vector);
        this.DenseList(7);
        this.KeywordList();

        await this.SearchAsync("travels", MemorySearchMode.Hybrid, settings: settings);

        await this.embedding.Received(1).EmbedQueryAsync("travels", "Find notes", Arg.Any<CancellationToken>());
        await this.store.Received(1).KeywordSearchAsync(Arg.Is<string>(m => m.Contains("stem :", StringComparison.Ordinal)), 7, Arg.Any<MemorySearchFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Options_Validate_RejectsOutOfRangeValues()
    {
        Assert.Empty(new MemorySearchOptions().Validate());
        Assert.Equal(4, new MemorySearchOptions { RrfK = 0, KeywordWeight = 0, DenseWeight = 0, Candidates = 0, QueryTimeoutMs = 10 }.Validate().Count);
    }
}
