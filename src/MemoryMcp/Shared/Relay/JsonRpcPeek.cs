using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MemoryMcp.Shared.Relay;

/// <summary>
/// Reads just enough of one JSON-RPC line to route it: the top-level <c>method</c>, <c>id</c> and whether it carries a
/// <c>result</c> or <c>error</c>. Nested members (such as a <c>method</c> inside <c>params</c>) are skipped.
/// </summary>
public static class JsonRpcPeek
{
    /// <summary>
    /// How deeply a line may nest. The reader tracks nesting in a bit stack, so its memory is bounded by the line's
    /// length; the default of 64 would leave a request with deeply nested arguments untracked.
    /// </summary>
    private const int MaxNestingDepth = int.MaxValue;

    private const string StringKeyPrefix = "s:";
    private const string NumberKeyPrefix = "n:";

    /// <summary>
    /// Returns false for anything that is not a single JSON object. <paramref name="idRaw"/> is the id's JSON text as
    /// sent (a string id keeps its quotes), so it can be written back unchanged; it is null when the id is absent,
    /// null, or not a number or string.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> line, out string? method, out string? idRaw, out bool isResponse)
    {
        return TryRead(line, out method, out idRaw, out _, out isResponse);
    }

    /// <summary>
    /// As <see cref="TryRead(ReadOnlySpan{byte}, out string?, out string?, out bool)"/>, and also gives
    /// <paramref name="idKey"/>: the id in a canonical form, equal for every JSON encoding of the same id (an escaped
    /// string and its plain form, <c>1.0</c> and <c>1</c>), so a request and its response can be matched even when
    /// the other side writes the id differently. A string id and a number id never share a key.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> line, out string? method, out string? idRaw, out string? idKey, out bool isResponse)
    {
        if (TryReadMembers(line, out method, out idRaw, out idKey, out isResponse))
        {
            return true;
        }

        method = null;
        idRaw = null;
        idKey = null;
        isResponse = false;
        return false;
    }

    /// <summary>The <c>idKey</c> <see cref="TryRead(ReadOnlySpan{byte}, out string?, out string?, out string?, out bool)"/> gives a string id.</summary>
    public static string StringIdKey(string id)
    {
        return StringKeyPrefix + id;
    }

    private static bool TryReadMembers(ReadOnlySpan<byte> line, out string? method, out string? idRaw, out string? idKey, out bool isResponse)
    {
        method = null;
        idRaw = null;
        idKey = null;
        isResponse = false;
        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = MaxNestingDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isMethod = reader.ValueTextEquals("method"u8);
                var isId = reader.ValueTextEquals("id"u8);
                var isResult = reader.ValueTextEquals("result"u8) || reader.ValueTextEquals("error"u8);
                if (!reader.Read())
                {
                    return false;
                }

                if (isMethod && reader.TokenType == JsonTokenType.String)
                {
                    method = reader.GetString();
                }
                else if (isId && reader.TokenType is JsonTokenType.String or JsonTokenType.Number)
                {
                    var start = (int)reader.TokenStartIndex;
                    idRaw = Encoding.UTF8.GetString(line[start..(int)reader.BytesConsumed]);
                    idKey = reader.TokenType == JsonTokenType.String ? StringIdKey(reader.GetString()!) : NumberIdKey(ref reader, idRaw);
                }

                if (isResult)
                {
                    isResponse = true;
                }

                reader.Skip();
            }

            if (reader.TokenType != JsonTokenType.EndObject)
            {
                return false;
            }

            // A single object only: anything after it (a second object) makes the line unreadable.
            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A number id in fixed-point form without trailing zeros, so <c>1</c>, <c>1.0</c> and <c>1e0</c> are one id.</summary>
    private static string NumberIdKey(ref Utf8JsonReader reader, string idRaw)
    {
        if (reader.TryGetDecimal(out var exact))
        {
            return NumberKeyPrefix + exact.ToString("0.############################", CultureInfo.InvariantCulture);
        }

        if (reader.TryGetDouble(out var approximate))
        {
            return NumberKeyPrefix + approximate.ToString("R", CultureInfo.InvariantCulture);
        }

        return NumberKeyPrefix + idRaw;
    }
}
