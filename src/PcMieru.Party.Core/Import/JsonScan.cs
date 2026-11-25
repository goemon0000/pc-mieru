using System.Text.Json;

namespace PcMieru.Party.Import;

internal enum ScanResult { Ok, Broken, TooDeep, DuplicateKey }

/// <summary>
/// One streaming pass over the raw bytes before anything is deserialized.
/// It answers three questions that a normal deserializer answers badly or not at all:
/// is it JSON, is it nested too deep, and does any object repeat a key (System.Text.Json keeps the last one and says nothing).
/// A repeated key anywhere is refused: which of the two values the old app "meant" cannot be known.
/// </summary>
internal static class JsonScan
{
    public static ScanResult Run(ReadOnlySpan<byte> utf8, int maxDepth, out string detail)
    {
        detail = "";
        // The reader may go one level deeper than the limit so that this loop, not the reader, reports "too deep".
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = maxDepth + 1 });
        // One entry per open container: the key set of an object, or null for an array.
        var open = new Stack<HashSet<string>?>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        if (open.Count >= maxDepth)
                        {
                            detail = $"deeper than {maxDepth}";
                            return ScanResult.TooDeep;
                        }
                        open.Push(reader.TokenType == JsonTokenType.StartObject ? new HashSet<string>(StringComparer.Ordinal) : null);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        open.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        // GetString unescapes, so "Chapter" and "Chapter" count as the same key.
                        string name = reader.GetString()!;
                        if (!open.Peek()!.Add(name))
                        {
                            detail = "repeated key: " + (name.Length <= 64 ? name : name[..64] + "...");
                            return ScanResult.DuplicateKey;
                        }
                        break;
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            // JsonReaderException is a JsonException; an unreadable escape in a key surfaces as InvalidOperationException.
            detail = e.GetType().Name;
            return ScanResult.Broken;
        }
        return ScanResult.Ok;
    }
}
