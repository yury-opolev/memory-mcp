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
    /// Returns false for anything that is not a single JSON object. <paramref name="idRaw"/> is the id's JSON text as
    /// sent (a string id keeps its quotes), so it can be written back unchanged; it is null when the id is absent,
    /// null, or not a number or string.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> line, out string? method, out string? idRaw, out bool isResponse)
    {
        if (TryReadMembers(line, out method, out idRaw, out isResponse))
        {
            return true;
        }

        method = null;
        idRaw = null;
        isResponse = false;
        return false;
    }

    private static bool TryReadMembers(ReadOnlySpan<byte> line, out string? method, out string? idRaw, out bool isResponse)
    {
        method = null;
        idRaw = null;
        isResponse = false;
        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
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
}
