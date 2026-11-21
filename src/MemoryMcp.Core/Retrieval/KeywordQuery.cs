using System.Text.RegularExpressions;

namespace MemoryMcp.Core.Retrieval;

/// <summary>
/// Builds a safe FTS5 <c>MATCH</c> expression for relevance search over a keyword index with a folded
/// <c>body</c> column and an optional Snowball <c>stem</c> column. Every term is a folded token (letters
/// and digits only) wrapped in double quotes, so user text can never inject FTS5 syntax. Quoted phrases
/// stay phrases, <c>word*</c> stays a prefix query, and all other words are OR-ed (BM25 ranks them).
/// </summary>
public static partial class KeywordQuery
{
    /// <summary>
    /// The MATCH expression for <paramref name="query"/>, or null when it has no searchable token.
    /// With <paramref name="stemmed"/>, plain words also match the stem column through their Snowball stems.
    /// </summary>
    public static string? Build(string? query, bool stemmed)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var exact = new List<string>();
        var stems = new List<string>();
        foreach (Match phrase in Phrase().Matches(query))
        {
            var tokens = KeywordTokenizer.Tokenize(TextFolder.Fold(phrase.Groups[1].Value));
            if (tokens.Count > 0)
            {
                exact.Add("\"" + string.Join(' ', tokens) + "\"");
            }
        }

        foreach (var word in Phrase().Replace(query, " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = KeywordTokenizer.Tokenize(TextFolder.Fold(word));
            if (word.EndsWith('*') && tokens.Count == 1)
            {
                exact.Add("\"" + tokens[0] + "\"*");
                continue;
            }

            foreach (var token in tokens)
            {
                exact.Add("\"" + token + "\"");
                if (stemmed)
                {
                    stems.AddRange(SnowballStemmer.Stems(token).Select(s => "\"" + s + "\""));
                }
            }
        }

        if (exact.Count == 0)
        {
            return null;
        }

        var body = "body : (" + string.Join(" OR ", exact.Distinct(StringComparer.Ordinal)) + ")";
        return stems.Count == 0 ? body : body + " OR stem : (" + string.Join(" OR ", stems.Distinct(StringComparer.Ordinal)) + ")";
    }

    [GeneratedRegex("\"([^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex Phrase();
}
