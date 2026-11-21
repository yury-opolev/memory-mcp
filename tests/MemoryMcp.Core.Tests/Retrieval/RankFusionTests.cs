using MemoryMcp.Core.Retrieval;
using Xunit;

namespace MemoryMcp.Core.Tests.Retrieval;

/// <summary>The generic fusion kernel works for any key type (history segments use long, memories use string ids).</summary>
public class RankFusionTests
{
    private static RankedItem<string>[] List(params string[] keys) =>
        keys.Select((key, i) => new RankedItem<string>(key, 10 - i)).ToArray();

    [Fact]
    public void Fuse_StringKeys_RrfScoresAndRanks()
    {
        var fused = RankFusion.Fuse(List("a", "b"), List("b", "c"), new FusionSettings { RrfK = 60 }, exactIntent: false, StringComparer.Ordinal);

        Assert.Equal(["b", "a", "c"], fused.Select(f => f.Key));
        Assert.Equal((1.0 / 62) + (1.0 / 61), fused[0].Score, 12);
        Assert.Equal(new FusedItem<string>("a", 1.0 / 61, 1, null), fused[1]);
        Assert.Equal(new FusedItem<string>("c", 1.0 / 62, null, 2), fused[2]);
    }

    [Fact]
    public void Fuse_EqualScores_PreferKeywordRankThenTheComparer()
    {
        var byKeywordRank = RankFusion.Fuse(List("y"), List("x"), new FusionSettings(), exactIntent: false, StringComparer.Ordinal);
        var byComparer = RankFusion.Fuse(
            [],
            [new RankedItem<string>("b", 0.5), new RankedItem<string>("a", 0.5)],
            new FusionSettings { Method = FusionMethod.Convex },
            exactIntent: false,
            StringComparer.Ordinal);

        Assert.Equal(["y", "x"], byKeywordRank.Select(f => f.Key));
        Assert.Equal(["a", "b"], byComparer.Select(f => f.Key));
    }

    [Fact]
    public void Fuse_ExactIntent_BoostsTheKeywordList()
    {
        var plain = RankFusion.Fuse(List("k9", "k1"), List("d2"), new FusionSettings(), exactIntent: false, StringComparer.Ordinal);
        var exact = RankFusion.Fuse(List("k9", "k1"), List("d2"), new FusionSettings(), exactIntent: true, StringComparer.Ordinal);

        Assert.Equal(["k9", "d2", "k1"], plain.Select(f => f.Key));
        Assert.Equal(["k9", "k1", "d2"], exact.Select(f => f.Key));
    }
}
