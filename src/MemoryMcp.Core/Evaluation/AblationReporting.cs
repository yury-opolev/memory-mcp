using System.Globalization;
using System.Text;

namespace MemoryMcp.Core.Evaluation;

/// <summary>Per-query metrics of one configuration (ids and numbers only).</summary>
public sealed record AblationScore(
    string Config,
    string QueryId,
    string Category,
    string Stratum,
    double Ndcg10,
    double Recall10,
    double ReciprocalRank,
    double Judged10,
    int? WordsToFirstRelevant,
    double? SetF1,
    double Confidence,
    bool ExpectAbstain,
    double LatencyMs);

/// <summary>Means of one configuration.</summary>
public sealed record ConfigSummary(
    string Config,
    int Queries,
    double Ndcg10,
    double Recall10,
    double Mrr,
    double Judged10,
    double? MedianWordsToFirstRelevant,
    double? EnumerationF1,
    CalibrationScore Abstention,
    double P95LatencyMs);

/// <summary>Paired comparison of one configuration with the baseline on nDCG@10.</summary>
/// <param name="Config">The configuration compared.</param>
/// <param name="Baseline">The baseline it is compared with.</param>
/// <param name="Slice"><c>all</c>, <c>stratum:real</c>, <c>stratum:synthetic</c> or <c>category:…</c>.</param>
/// <param name="Queries">Queries in the slice.</param>
/// <param name="Mean">Mean paired difference.</param>
/// <param name="Low">Lower bootstrap bound.</param>
/// <param name="High">Upper bootstrap bound.</param>
/// <param name="PValue">Randomisation-test p-value; null for per-category slices (reported as CIs only).</param>
public sealed record PairedComparison(string Config, string Baseline, string Slice, int Queries, double Mean, double Low, double High, double? PValue);

/// <summary>Everything one ablation or holdout run produced.</summary>
public sealed record AblationReport(IReadOnlyList<AblationScore> Scores, IReadOnlyList<ConfigSummary> Summaries, IReadOnlyList<PairedComparison> Comparisons);

/// <summary>
/// Summarises per-query scores by configuration and compares every configuration with the baseline
/// using paired bootstrap CIs and a randomisation test (per-category slices as CIs only).
/// Shared by the history and the memory evaluations.
/// </summary>
public static class AblationReporting
{
    /// <summary>Summaries and paired comparisons of <paramref name="scores"/> against <paramref name="baseline"/>.</summary>
    public static AblationReport Build(IReadOnlyList<AblationScore> scores, string baseline)
    {
        ArgumentNullException.ThrowIfNull(scores);
        var summaries = scores.GroupBy(s => s.Config, StringComparer.Ordinal).Select(Summarise).ToList();
        return new AblationReport(scores, summaries, Compare(scores, baseline));
    }

    /// <summary>A Markdown report: summaries and comparisons (numbers only).</summary>
    public static string Markdown(AblationReport report, string title)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# {title}");
        text.AppendLine();
        text.AppendLine("| config | n | nDCG@10 | recall@10 | MRR | judged@10 | median words to first relevant | enumeration F1 | abstention Brier | ECE | p95 ms |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var s in report.Summaries)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {s.Config} | {s.Queries} | {s.Ndcg10:F3} | {s.Recall10:F3} | {s.Mrr:F3} | {s.Judged10:F2} | {s.MedianWordsToFirstRelevant?.ToString("F0", CultureInfo.InvariantCulture) ?? "–"} | {s.EnumerationF1?.ToString("F3", CultureInfo.InvariantCulture) ?? "–"} | {s.Abstention.Brier:F3} | {s.Abstention.ExpectedCalibrationError:F3} | {s.P95LatencyMs:F0} |");
        }

        text.AppendLine();
        text.AppendLine("Paired differences in nDCG@10 versus the baseline (bootstrap 95% CI; randomisation p, not for per-category slices):");
        text.AppendLine();
        text.AppendLine("| config | slice | n | Δ mean | 95% CI | p |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (var c in report.Comparisons)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {c.Config} | {c.Slice} | {c.Queries} | {c.Mean:+0.000;-0.000;0.000} | [{c.Low:+0.000;-0.000;0.000}, {c.High:+0.000;-0.000;0.000}] | {c.PValue?.ToString("F4", CultureInfo.InvariantCulture) ?? "CI only"} |");
        }

        return text.ToString();
    }

    private static ConfigSummary Summarise(IGrouping<string, AblationScore> scores)
    {
        var list = scores.ToList();
        var words = list.Where(s => s.WordsToFirstRelevant is not null).Select(s => (double)s.WordsToFirstRelevant!.Value).ToList();
        var enumeration = list.Where(s => s.SetF1 is not null).Select(s => s.SetF1!.Value).ToList();
        return new ConfigSummary(
            scores.Key,
            list.Count,
            list.Average(s => s.Ndcg10),
            list.Average(s => s.Recall10),
            list.Average(s => s.ReciprocalRank),
            list.Average(s => s.Judged10),
            words.Count == 0 ? null : EvalStatistics.Percentile(words, 50),
            enumeration.Count == 0 ? null : enumeration.Average(),
            RetrievalMetrics.Calibration(list.Select(s => (s.Confidence, !s.ExpectAbstain)).ToList()),
            EvalStatistics.Percentile(list.Select(s => s.LatencyMs).ToList(), 95));
    }

    private static List<PairedComparison> Compare(IReadOnlyList<AblationScore> scores, string baseline)
    {
        var comparisons = new List<PairedComparison>();
        var baseScores = scores.Where(s => s.Config == baseline).ToDictionary(s => s.QueryId, StringComparer.Ordinal);
        if (baseScores.Count == 0)
        {
            return comparisons;
        }

        foreach (var config in scores.Select(s => s.Config).Where(c => c != baseline).Distinct(StringComparer.Ordinal))
        {
            var mine = scores.Where(s => s.Config == config && baseScores.ContainsKey(s.QueryId)).ToList();
            var slices = new List<(string Slice, List<AblationScore> Rows, bool WithP)> { ("all", mine, true) };
            slices.AddRange(mine.GroupBy(s => s.Stratum, StringComparer.Ordinal).Select(g => ("stratum:" + g.Key, g.ToList(), true)));
            slices.AddRange(mine.GroupBy(s => s.Category, StringComparer.Ordinal).Select(g => ("category:" + g.Key, g.ToList(), false)));
            foreach (var (slice, rows, withP) in slices.Where(s => s.Rows.Count > 0))
            {
                var a = rows.Select(s => baseScores[s.QueryId].Ndcg10).ToList();
                var b = rows.Select(s => s.Ndcg10).ToList();
                var (mean, low, high) = EvalStatistics.PairedBootstrap(a, b);
                comparisons.Add(new PairedComparison(config, baseline, slice, rows.Count, mean, low, high,
                    withP ? EvalStatistics.PairedRandomisationTest(a, b) : null));
            }
        }

        return comparisons;
    }
}
