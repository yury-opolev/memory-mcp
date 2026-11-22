using MemoryMcp.Core.Evaluation;
using Xunit;

namespace MemoryMcp.Core.Tests.Evaluation;

/// <summary>Metrics and pre-registration are engine-neutral: memory ids are strings.</summary>
public class EvaluationKernelTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "prereg-" + Guid.NewGuid().ToString("N"));

    public EvaluationKernelTests() => Directory.CreateDirectory(this.directory);

    public void Dispose() => Directory.Delete(this.directory, recursive: true);

    [Fact]
    public void RetrievalMetrics_StringIds_ScoreLikeNumericIds()
    {
        IReadOnlyList<string> ranked = ["m9", "m2", "m1"];
        var grades = new Dictionary<string, int>(StringComparer.Ordinal) { ["m1"] = 2, ["m2"] = 1 };
        var relevant = new HashSet<string>(StringComparer.Ordinal) { "m1", "m2" };

        Assert.Equal(0.5, RetrievalMetrics.ReciprocalRank(ranked, relevant));
        Assert.Equal(1, RetrievalMetrics.RecallAt(ranked, relevant, 3));
        Assert.Equal(15, RetrievalMetrics.WordsToFirstRelevant([("m9", 10), ("m2", 5), ("m1", 7)], relevant));
        Assert.True(RetrievalMetrics.NdcgAt(ranked, grades, 10) is > 0 and < 1);
    }

    [Fact]
    public void Preregistration_RegisterOnce_VerifyDetectsChanges()
    {
        var dataset = Path.Combine(this.directory, "dataset.jsonl");
        File.WriteAllText(dataset, "a");
        var registration = new Preregistration(Path.Combine(this.directory, "preregistration.json"), new Dictionary<string, string> { ["dataset.jsonl"] = dataset });

        Assert.Throws<InvalidOperationException>(registration.Verify);
        registration.Register();
        registration.Verify();
        Assert.Throws<InvalidOperationException>(registration.Register);

        File.WriteAllText(dataset, "b");

        var error = Assert.Throws<InvalidOperationException>(registration.Verify);
        Assert.Contains("dataset.jsonl", error.Message, StringComparison.Ordinal);
    }
}
