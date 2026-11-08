using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PcMieru.Core.Reporting;

/// <summary>
/// The real "what is this?" catalog. A name is never enough: a process is matched on its image name AND where its file
/// is AND who signed it (SPEC section 6).
///
/// What comes back:
///   null                     - the name is not in the catalog, or the entry needs a file location and the caller has none
///                              (matching on the name alone would be a guess).
///   PathMatches = false      - the file is somewhere the real one never is. The description then says
///                              "場所が普通ではありません" (and where the real one lives) instead of describing the program.
///   SignerMatches = true     - the signature was verified (embedded or catalog) and names the expected publisher.
///   SignerMatches = false    - the signature is missing, invalid, from someone else, or was not collected. Only a
///                              signature that is PRESENT and wrong adds a warning sentence; "not collected" adds none.
/// Entries that have no file (System, Registry, vmmem) have nothing to check, so both flags are true for them when the
/// caller has no path.
/// </summary>
public sealed partial class KnownProcessCatalog : IKnownProcessCatalog
{
    private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);

    public KnownProcessCatalog()
        : this(KnownProcessData.Json, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>For tests: the catalog text and the environment it expands %Var% from.</summary>
    public KnownProcessCatalog(string json, Func<string, string?> environment)
    {
        Strings.Register(ReportingStrings.All);
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("The known-process catalog is not a JSON object.");
        var groups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (root["signerGroups"] is JsonObject groupNode)
        {
            foreach (var (name, list) in groupNode)
            {
                groups[name] = ReadStrings(list);
            }
        }

        foreach (var node in root["entries"]?.AsArray() ?? throw new FormatException("The known-process catalog has no entries."))
        {
            if (node is not JsonObject item)
            {
                throw new FormatException("A catalog entry is not an object.");
            }

            var id = Required(item, "id");
            var signers = item["signers"] switch
            {
                JsonArray array => ReadStrings(array),
                JsonValue value when value.TryGetValue<string>(out var group) => groups.TryGetValue(group, out var found)
                    ? found
                    : throw new FormatException($"Catalog entry '{id}' names the unknown signer group '{group}'."),
                _ => [],
            };
            var declaredPaths = ReadStrings(item["paths"]);
            var patterns = declaredPaths.SelectMany(WithWow64).Select(p => ToRegex(p, environment)).OfType<Regex>().ToArray();
            var entry = new Entry(id, Required(item, "ja"), Required(item, "en"), declaredPaths, patterns, signers.Select(Normalize).ToArray(), signers);

            foreach (var name in ReadStrings(item["names"]))
            {
                if (!_byName.TryAdd(name, entry))
                {
                    throw new FormatException($"The catalog lists '{name}' twice.");
                }
            }
        }
    }

    public int Count => _byName.Values.Distinct().Count();

    /// <summary>The ids of every entry, for tests and for the docs.</summary>
    public IReadOnlyList<string> Ids => _byName.Values.Select(e => e.Id).Distinct().Order(StringComparer.Ordinal).ToArray();

    public KnownProcess? Match(string imageName, string? path, SignatureInfo? signature)
    {
        if (string.IsNullOrWhiteSpace(imageName))
        {
            return null;
        }

        // Some callers pass a full path as the name; the catalog is keyed by the file name.
        var name = imageName.Trim();
        var slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (!_byName.TryGetValue(name, out var entry))
        {
            return null;
        }

        var hasPath = !string.IsNullOrWhiteSpace(path);
        var fileless = entry.DeclaredPaths.Length == 0;
        if (!hasPath && !fileless)
        {
            return null;
        }

        var pathOk = fileless ? !hasPath : entry.Patterns.Any(p => p.IsMatch(NormalizePath(path!)));
        var signerOk = SignerVerified(entry, signature, out var signaturePresent);
        if (!pathOk)
        {
            var key = fileless ? "report.known.pathMismatchNoFile" : "report.known.pathMismatch";
            return new KnownProcess(entry.Id, Say(key, name, entry, Language.Ja), Say(key, name, entry, Language.En), PathMatches: false, SignerMatches: signerOk);
        }

        if (!signerOk && signaturePresent && entry.Signers.Length > 0)
        {
            const string key = "report.known.signerMismatch";
            return new KnownProcess(entry.Id, Say(key, name, entry, Language.Ja), Say(key, name, entry, Language.En), PathMatches: true, SignerMatches: false);
        }

        return new KnownProcess(entry.Id, entry.Ja, entry.En, PathMatches: true, SignerMatches: signerOk || entry.Signers.Length == 0);
    }

    private static string Say(string key, string name, Entry entry, Language language) =>
        Strings.Format(
            key,
            new Dictionary<string, string>
            {
                ["name"] = name,
                ["desc"] = (language == Language.Ja ? entry.Ja : entry.En).TrimEnd('\u3002', '.'),
                ["where"] = entry.DeclaredPaths.FirstOrDefault() ?? string.Empty,
                ["signer"] = string.Join(" / ", entry.SignerLabels),
            },
            language);

    /// <summary>
    /// True when the signature was verified and names an expected publisher. <paramref name="present"/> tells whether
    /// there was any signature information to judge (an Unsigned or Invalid result counts as present: it was measured).
    /// </summary>
    private static bool SignerVerified(Entry entry, SignatureInfo? signature, out bool present)
    {
        present = signature is not null;
        if (signature is null || entry.Signers.Length == 0)
        {
            return false;
        }

        if (signature.State is not (SignatureState.SignedEmbedded or SignatureState.SignedCatalog))
        {
            return false;
        }

        foreach (var candidate in SignerNames(signature.Signer))
        {
            if (entry.Signers.Contains(candidate, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The certificate subject may be a bare name or a full "CN=..., O=..." string; try the whole text, the CN and the O.</summary>
    private static IEnumerable<string> SignerNames(string? signer)
    {
        if (string.IsNullOrWhiteSpace(signer))
        {
            yield break;
        }

        yield return Normalize(signer);
        foreach (Match m in SubjectPartRegex().Matches(signer))
        {
            yield return Normalize(m.Groups[2].Value.Trim('"'));
        }
    }

    private static string Normalize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Trim().ToUpperInvariant())
        {
            if (c is ',' or '.' or '"')
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    private static string NormalizePath(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        return p;
    }

    /// <summary>A path under System32 may legitimately be the 32-bit copy under SysWOW64 (32-bit apps run those).</summary>
    private static IEnumerable<string> WithWow64(string pattern)
    {
        yield return pattern;
        const string system32 = @"%SystemRoot%\System32\";
        if (pattern.StartsWith(system32, StringComparison.OrdinalIgnoreCase))
        {
            yield return @"%SystemRoot%\SysWOW64\" + pattern[system32.Length..];
        }
    }

    /// <summary>%Var% from the environment, * for one folder or file name part, ** for anything. An unset variable drops the pattern.</summary>
    private static Regex? ToRegex(string pattern, Func<string, string?> environment)
    {
        var expanded = new StringBuilder();
        var i = 0;
        while (i < pattern.Length)
        {
            if (pattern[i] == '%')
            {
                var end = pattern.IndexOf('%', i + 1);
                if (end < 0)
                {
                    return null;
                }

                var value = environment(pattern[(i + 1)..end]);
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                expanded.Append(Regex.Escape(NormalizePath(value).TrimEnd('\\')));
                i = end + 1;
            }
            else if (pattern[i] == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    expanded.Append(".*");
                    i += 2;
                }
                else
                {
                    expanded.Append(@"[^\\]*");
                    i++;
                }
            }
            else
            {
                expanded.Append(Regex.Escape(pattern[i].ToString()));
                i++;
            }
        }

        return new Regex("^" + expanded + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string[] ReadStrings(JsonNode? node) =>
        node is JsonArray array
            ? array.Select(n => n?.GetValue<string>() ?? throw new FormatException("A catalog list holds a non-text value.")).ToArray()
            : [];

    private static string Required(JsonObject item, string key) =>
        item[key]?.GetValue<string>() is { Length: > 0 } text ? text : throw new FormatException($"A catalog entry has no '{key}'.");

    [GeneratedRegex(@"(^|,\s*)(?:CN|O)=(""[^""]*""|[^,]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubjectPartRegex();

    private sealed record Entry(string Id, string Ja, string En, string[] DeclaredPaths, Regex[] Patterns, string[] Signers, string[] SignerLabels);
}
