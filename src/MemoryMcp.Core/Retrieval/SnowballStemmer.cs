using Snowball;

namespace MemoryMcp.Core.Retrieval;

/// <summary>
/// Snowball stemming for stemmed keyword columns. Applied to already folded tokens on both the
/// index and the query side, so both always agree: Cyrillic tokens get the Russian stemmer; Latin
/// tokens get the Danish and the English stem (both kept when they differ, because a token's
/// language is unknown); other tokens are kept as they are.
/// </summary>
public static class SnowballStemmer
{
    /// <summary>Stemmer version; bump on any behaviour change (part of every stemmed index's fingerprint).</summary>
    public const int Version = 1;

    /// <summary>The stemmed shadow body of folded text: stems of every token, space-separated.</summary>
    public static string StemBody(string folded)
    {
        var stemmers = new Stemmers();
        return string.Join(' ', KeywordTokenizer.Tokenize(folded).SelectMany(stemmers.Stems));
    }

    /// <summary>The distinct folded stems of one folded token.</summary>
    public static IReadOnlyList<string> Stems(string token) => new Stemmers().Stems(token);

    // Snowball stemmers keep per-instance state, so each call uses its own instances.
    private sealed class Stemmers
    {
        private readonly RussianStemmer russian = new();
        private readonly DanishStemmer danish = new();
        private readonly EnglishStemmer english = new();

        public IReadOnlyList<string> Stems(string token)
        {
            if (token.Any(c => c is >= '\u0400' and <= '\u04FF'))
            {
                return [TextFolder.Fold(this.russian.Stem(token))];
            }

            if (token.Any(c => c is >= 'a' and <= 'z'))
            {
                var danishStem = TextFolder.Fold(this.danish.Stem(token));
                var englishStem = TextFolder.Fold(this.english.Stem(token));
                return string.Equals(danishStem, englishStem, StringComparison.Ordinal) ? [danishStem] : [danishStem, englishStem];
            }

            return [token];
        }
    }
}
