using System.Text.RegularExpressions;

namespace Etl.Core.Mapping;

/// <summary>
/// Reads declared string lengths (varchar(n), char(n)) from the target DDL, so length validation has one source
/// of truth: the schema the data is going into. Handles `LIKE other INCLUDING ...` by copying the other table.
/// </summary>
public static partial class DdlLengths
{
    [GeneratedRegex(@"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:\w+\.)?(?<table>\w+)\s*\((?<body>.*?)\);", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"(?<col>\w+)\s+(?:varchar|character\s+varying|char|character)\s*\(\s*(?<n>\d+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex StringColumn();

    [GeneratedRegex(@"\bLIKE\s+(?:\w+\.)?(?<src>\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex Like();

    public static Dictionary<string, Dictionary<string, int>> Parse(string ddl)
    {
        var noComments = Regex.Replace(ddl, @"--[^\n]*", "");
        var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match t in CreateTable().Matches(noComments))
        {
            var cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var body = t.Groups["body"].Value;
            if (Like().Match(body) is { Success: true } like && result.TryGetValue(like.Groups["src"].Value, out var inherited))
                foreach (var (c, n) in inherited) cols[c] = n;
            foreach (Match c in StringColumn().Matches(body))
                cols[c.Groups["col"].Value] = int.Parse(c.Groups["n"].Value);
            result[t.Groups["table"].Value] = cols;
        }
        return result;
    }
}
