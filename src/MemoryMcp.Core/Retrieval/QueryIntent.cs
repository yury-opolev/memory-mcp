using System.Text.RegularExpressions;

namespace MemoryMcp.Core.Retrieval;

/// <summary>Detects queries that ask for exact wording or identifiers (BM25 priority in hybrid fusion).</summary>
public static partial class QueryIntent
{
    /// <summary>
    /// True for a quoted phrase or an identifier-like token: letters mixed with digits (<c>INV-2041</c>,
    /// <c>v1.2</c>), an inner underscore (<c>foo_bar</c>), camelCase (<c>getUser</c>) or <c>#123</c>.
    /// Plain hyphenated words are not identifiers.
    /// </summary>
    public static bool HasExactIntent(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        if (query.Count(c => c == '"') >= 2)
        {
            return true;
        }

        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('*', ',', '.', '!', '?', ';', ':', '(', ')'))
            .Any(t => IdentifierLike().IsMatch(t));
    }

    [GeneratedRegex(@"^(?=.*\p{L})(?=.*\d)[\p{L}\d._\-/#@]+$|^[\p{L}\d]+(_[\p{L}\d]+)+$|\p{Ll}\p{Lu}|^#\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierLike();
}
