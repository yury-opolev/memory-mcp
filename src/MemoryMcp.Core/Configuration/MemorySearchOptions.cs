using MemoryMcp.Core.Retrieval;

namespace MemoryMcp.Core.Configuration;

/// <summary>How a memory search ranks memories.</summary>
public enum MemorySearchMode
{
    /// <summary>Semantic (cosine) similarity only: the behaviour before keyword search existed.</summary>
    Dense,

    /// <summary>BM25 over the keyword index only (exact words, names, numbers, identifiers).</summary>
    Keyword,

    /// <summary>Keyword and semantic lists fused (RRF by default).</summary>
    Hybrid,
}

/// <summary>Which column of the keyword index plain query words match.</summary>
public enum MemoryKeywordVariant
{
    /// <summary>Folded words only.</summary>
    Plain,

    /// <summary>Folded words plus their Snowball stems (ru/da/en).</summary>
    Stemmed,
}

/// <summary>
/// Memory search settings, bound from <c>MemoryMcp:Search</c>. All apply at query time (the keyword
/// index always holds both the plain and the stemmed column), so every value can be ablated without
/// re-indexing. <see cref="DefaultMode"/> is <see cref="MemorySearchMode.Hybrid"/>; it falls back to the
/// dense or keyword list, saying so, when the keyword index or the embedder is unavailable.
/// </summary>
public class MemorySearchOptions
{
    /// <summary>The mode used when the caller does not choose one (<c>memory_search mode=auto</c>).</summary>
    public MemorySearchMode DefaultMode { get; set; } = MemorySearchMode.Hybrid;

    /// <summary>Keyword matching variant.</summary>
    public MemoryKeywordVariant KeywordVariant { get; set; } = MemoryKeywordVariant.Plain;

    /// <summary>Fusion method for hybrid search.</summary>
    public FusionMethod Fusion { get; set; } = FusionMethod.Rrf;

    /// <summary>RRF constant <c>k</c>.</summary>
    public int RrfK { get; set; } = 60;

    /// <summary>Weight of the keyword list (weighted RRF and convex).</summary>
    public double KeywordWeight { get; set; } = 1.0;

    /// <summary>Weight of the dense list (weighted RRF and convex).</summary>
    public double DenseWeight { get; set; } = 1.0;

    /// <summary>Keyword-list weight multiplier for quoted phrases and identifier-like tokens.</summary>
    public double ExactIntentKeywordBoost { get; set; } = 2.0;

    /// <summary>Candidates fetched from each list before fusion.</summary>
    public int Candidates { get; set; } = 50;

    /// <summary>
    /// Embeds queries with <see cref="QueryInstruction"/> (Qwen3 format). Off by default: memory
    /// queries have always been embedded like documents.
    /// </summary>
    public bool UseQueryInstruction { get; set; }

    /// <summary>Qwen3 query instruction (task description) used when <see cref="UseQueryInstruction"/> is on.</summary>
    public string QueryInstruction { get; set; } = "Given a question, retrieve the stored notes that answer it";

    /// <summary>Budget for embedding the query in hybrid mode before falling back to keyword-only.</summary>
    public int QueryTimeoutMs { get; set; } = 3000;

    /// <summary>The fusion parameters of the shared engine.</summary>
    public FusionSettings ToFusionSettings() => new()
    {
        Method = this.Fusion,
        RrfK = this.RrfK,
        KeywordWeight = this.KeywordWeight,
        DenseWeight = this.DenseWeight,
        ExactIntentKeywordBoost = this.ExactIntentKeywordBoost,
    };

    /// <summary>The query instruction to send, or null when instructions are off.</summary>
    public string? EffectiveQueryInstruction() =>
        this.UseQueryInstruction && !string.IsNullOrWhiteSpace(this.QueryInstruction) ? this.QueryInstruction : null;

    /// <summary>Returns validation errors (empty when valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (this.RrfK < 1)
        {
            errors.Add("MemoryMcp:Search:RrfK must be at least 1.");
        }

        if (this.KeywordWeight < 0 || this.DenseWeight < 0 || this.KeywordWeight + this.DenseWeight <= 0 || this.ExactIntentKeywordBoost < 1)
        {
            errors.Add("MemoryMcp:Search weights must be non-negative, not both zero, and ExactIntentKeywordBoost at least 1.");
        }

        if (this.Candidates is < 1 or > 1000)
        {
            errors.Add("MemoryMcp:Search:Candidates must be between 1 and 1000.");
        }

        if (this.QueryTimeoutMs < 100)
        {
            errors.Add("MemoryMcp:Search:QueryTimeoutMs must be at least 100.");
        }

        return errors;
    }
}
