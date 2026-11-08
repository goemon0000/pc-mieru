using System.Text;
using System.Text.Json.Nodes;

namespace PcMieru.Cli.Mcp;

internal static class JsonRpc
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int NotInitialized = -32002;

    public static JsonObject Result(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    public static JsonObject Error(JsonNode? id, int code, string message) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };

    /// <summary>A request id is a string or a number; anything else (null, object, bool) is not a valid id.</summary>
    public static bool IsValidId(JsonNode? id) =>
        id is JsonValue v && (v.TryGetValue<string>(out _) || v.TryGetValue<double>(out _));
}

internal readonly record struct InputLine(string? Text, bool TooLong);

/// <summary>Reads '\n'-terminated lines of at most <c>maxChars</c> characters; an over-long line is skipped to its end and reported.</summary>
internal sealed class BoundedLineReader(TextReader reader, int maxChars)
{
    private readonly char[] _buffer = new char[4096];
    private int _pos;
    private int _len;
    private bool _atStart = true;

    public async Task<InputLine> ReadAsync(CancellationToken ct)
    {
        var line = new StringBuilder();
        var tooLong = false;
        while (true)
        {
            if (_pos >= _len)
            {
                _len = await reader.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
                _pos = 0;
                if (_atStart && _len > 0)
                {
                    // A client that pipes through Windows PowerShell in a UTF-8 console puts a BOM in front of the first message.
                    // RFC 8259 lets a parser ignore it; without this the first request is a parse error and the rest say "send initialize first".
                    _atStart = false;
                    if (_buffer[0] == '\uFEFF')
                    {
                        _pos = 1;
                    }
                }

                if (_len == 0)
                {
                    return line.Length == 0 && !tooLong ? new InputLine(null, false) : new InputLine(tooLong ? string.Empty : line.ToString(), tooLong);
                }
            }

            var span = _buffer.AsSpan(_pos, _len - _pos);
            var newline = span.IndexOf('\n');
            var take = newline < 0 ? span.Length : newline;
            if (!tooLong)
            {
                if (line.Length + take > maxChars)
                {
                    tooLong = true;
                    line.Clear();
                }
                else
                {
                    line.Append(span[..take]);
                }
            }

            _pos += take + (newline < 0 ? 0 : 1);
            if (newline >= 0)
            {
                return new InputLine(tooLong ? string.Empty : line.ToString().TrimEnd('\r'), tooLong);
            }
        }
    }
}
