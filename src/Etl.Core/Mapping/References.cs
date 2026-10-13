using System.IO.Compression;
using System.Text;

namespace Etl.Core.Mapping;

/// <summary>Loaders for reference code sets (valid-value lists) used by converters. Mirrors src/etl/reference.py.</summary>
public static class References
{
    /// <summary>
    /// spec: {loader, path, member?, encoding?} or {loader, sources: [{path, member?}, ...]} (sources are unioned,
    /// e.g. several versions of a code set). Paths are relative to <paramref name="baseDir"/>.
    /// </summary>
    public static IReadOnlySet<string> Load(IReadOnlyDictionary<string, object?> spec, string baseDir)
    {
        var loader = (string)spec["loader"]!;
        if (loader != "first_token") throw new InvalidDataException($"unknown reference loader '{loader}'");
        var sources = spec.TryGetValue("sources", out var s) && s is List<object?> list
            ? list.Cast<IReadOnlyDictionary<string, object?>>().ToList()
            : [spec];
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var src in sources)
        {
            var path = Path.GetFullPath(Path.Combine(baseDir, (string)src["path"]!));
            var member = src.TryGetValue("member", out var m) ? m as string : null;
            codes.UnionWith(FirstToken(path, member));
        }
        return codes;
    }

    /// <summary>The code is the first whitespace-separated token of each non-blank line (Latin-1 text).</summary>
    public static IEnumerable<string> FirstToken(string path, string? member)
    {
        string text;
        if (member is null)
        {
            text = File.ReadAllText(path, Encoding.Latin1);
        }
        else
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(member) ?? throw new FileNotFoundException($"{member} not in {path}");
            using var r = new StreamReader(entry.Open(), Encoding.Latin1);
            text = r.ReadToEnd();
        }
        foreach (var line in PythonSplitLines(text))
        {
            var token = FirstWhitespaceToken(line);
            if (token is not null) yield return token;
        }
    }

    // Python's str.splitlines() and str.split() treat more characters as line breaks / whitespace than .NET
    // does (e.g. \x1c-\x1e, \x85). Mirroring them keeps the reference sets identical across engines.
    private static bool IsPyLineBreak(char c) => c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or (char)0x2028 or (char)0x2029;
    private static bool IsPyWhitespace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    private static IEnumerable<string> PythonSplitLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsPyLineBreak(text[i])) continue;
            yield return text[start..i];
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        if (start < text.Length) yield return text[start..];
    }

    private static string? FirstWhitespaceToken(string line)
    {
        var i = 0;
        while (i < line.Length && IsPyWhitespace(line[i])) i++;
        if (i == line.Length) return null;
        var j = i;
        while (j < line.Length && !IsPyWhitespace(line[j])) j++;
        return line[i..j];
    }
}
