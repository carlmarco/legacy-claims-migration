using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Etl.Core.Discovery;

/// <summary>Profile of one column, built in a single streaming pass.</summary>
public sealed class ColumnProfile(string name, string declaredType)
{
    public const int DistinctCap = 10_000;   // exact distinct counts up to this; beyond it, "> cap"

    public string Name { get; } = name;
    public string DeclaredType { get; } = declaredType;
    public long Rows { get; private set; }
    public long Nulls { get; private set; }           // SQL NULL
    public long Blanks { get; private set; }          // '' or whitespace-only text
    public Dictionary<string, long> StorageTypes { get; } = new();   // what SQLite actually stored, per value
    public Dictionary<string, long> Formats { get; } = new();        // shape of the text, e.g. "date:yyyymmdd"
    public int MinLength { get; private set; } = int.MaxValue;
    public int MaxLength { get; private set; }
    private readonly Dictionary<string, long> _values = new();
    public bool DistinctCapped { get; private set; }
    public long? ExactDistinct { get; set; }   // filled by SQL for capped, never-null columns
    public int Distinct => _values.Count;
    public IEnumerable<(string Value, long Count)> Top(int n) =>
        _values.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(n).Select(kv => (kv.Key, kv.Value));

    public void Observe(object? v)
    {
        Rows++;
        if (v is null) { Nulls++; Bump(StorageTypes, "null"); return; }
        Bump(StorageTypes, v switch { long => "integer", double => "real", string => "text", byte[] => "blob", _ => v.GetType().Name });
        var s = v switch
        {
            string t => t,
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
        };
        if (s.Trim().Length == 0) Blanks++;
        MinLength = Math.Min(MinLength, s.Length);
        MaxLength = Math.Max(MaxLength, s.Length);
        Bump(Formats, Classify(s));
        if (_values.TryGetValue(s, out var c)) _values[s] = c + 1;
        else if (_values.Count < DistinctCap) _values[s] = 1;
        else DistinctCapped = true;
    }

    private static void Bump(Dictionary<string, long> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;

    /// <summary>Classify a value's shape. These are the patterns that reveal mixed formats in a legacy column.</summary>
    public static string Classify(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return "blank";
        if (t != s) return "padded:" + Classify(t);
        foreach (var (name, re) in Patterns)
            if (re.IsMatch(t)) return name;
        return t.All(char.IsAsciiLetterOrDigit) ? (t.Any(char.IsAsciiLetter) ? "alnum" : "digits") : "other";
    }

    private static readonly (string, Regex)[] Patterns =
    [
        ("date:yyyymmdd", new Regex(@"^(19|20)\d{2}(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])$", RegexOptions.Compiled)),
        ("date:yyyy-mm-dd", new Regex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled)),
        ("date:m/d/yyyy", new Regex(@"^\d{1,2}/\d{1,2}/\d{4}$", RegexOptions.Compiled)),
        ("date:m/d/yy", new Regex(@"^\d{1,2}/\d{1,2}/\d{2}$", RegexOptions.Compiled)),
        ("timestamp", new Regex(@"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}", RegexOptions.Compiled)),
        ("sentinel:zeros", new Regex(@"^0{6,}$", RegexOptions.Compiled)),
        ("sentinel:n/a", new Regex(@"^(?i:n/?a|null|none|unknown)$", RegexOptions.Compiled)),
        ("money:$", new Regex(@"^-?\$[\d,]+(\.\d+)?$", RegexOptions.Compiled)),
        ("money:(neg)", new Regex(@"^\([\d,]+(\.\d+)?\)$", RegexOptions.Compiled)),
        ("number:thousands", new Regex(@"^-?\d{1,3}(,\d{3})+(\.\d+)?$", RegexOptions.Compiled)),
        ("decimal", new Regex(@"^-?\d+\.\d+$", RegexOptions.Compiled)),
        ("integer", new Regex(@"^-?[1-9]\d*$|^0$", RegexOptions.Compiled)),
        ("digits:leading-zero", new Regex(@"^0\d+$", RegexOptions.Compiled)),
        ("code:dotted", new Regex(@"^[A-Z]?\d+\.\d+$", RegexOptions.Compiled)),
        ("code:hyphenated", new Regex(@"^\d+(-\d+)+$", RegexOptions.Compiled)),
        ("flag:yn", new Regex(@"^[YNyn]$", RegexOptions.Compiled)),
    ];
}

public sealed record TableProfile(string Name, long Rows, IReadOnlyList<ColumnProfile> Columns,
                                  IReadOnlyList<string> CandidateKeys, IReadOnlyList<string> RepeatingGroups);

/// <summary>
/// Profiles a database you know nothing about: tables, declared vs stored types, null and blank rates,
/// distinct counts, value formats, candidate keys, and repeating column groups that should become child
/// tables. One streaming pass per table; memory is bounded by the per-column distinct cap.
/// </summary>
public static partial class SchemaProfiler
{
    public static List<TableProfile> Profile(string sqlitePath)
    {
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        con.Open();
        var tables = Query(con, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name")
            .Select(r => (string)r[0]!).ToList();
        var result = new List<TableProfile>();
        foreach (var table in tables)
        {
            var cols = Query(con, $"PRAGMA table_info(\"{table}\")").Select(r => new ColumnProfile((string)r[1]!, (string)r[2]!)).ToList();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = $"SELECT * FROM \"{table}\"";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    for (var i = 0; i < cols.Count; i++) cols[i].Observe(reader.IsDBNull(i) ? null : reader.GetValue(i));
            }
            var rows = cols.Count > 0 ? cols[0].Rows : 0;
            result.Add(new TableProfile(table, rows, cols, CandidateKeys(con, table, cols, rows), RepeatingGroups(cols)));
        }
        return result;
    }

    private static List<object?[]> Query(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>A column is a candidate key if it's never null and every value is distinct (exact check in SQL when capped).</summary>
    private static List<string> CandidateKeys(SqliteConnection con, string table, List<ColumnProfile> cols, long rows)
    {
        var keys = new List<string>();
        foreach (var c in cols.Where(c => c.Nulls == 0 && c.Blanks == 0 && rows > 0))
        {
            if (!c.DistinctCapped) { if (c.Distinct == rows) keys.Add(c.Name); continue; }
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT count(DISTINCT \"{c.Name}\") FROM \"{table}\"";
            c.ExactDistinct = (long)cmd.ExecuteScalar()!;
            if (c.ExactDistinct == rows) keys.Add(c.Name);
        }
        return keys;
    }

    [GeneratedRegex(@"^(?<pre>[A-Za-z_]*?)(?<n>\d+)(?<post>(_[A-Za-z_]+)?)$")]
    private static partial Regex NumberedName();

    /// <summary>Columns like DX1..DX10 or L1_PMT..L13_PMT: a repeating group that should become a child table.</summary>
    private static List<string> RepeatingGroups(List<ColumnProfile> cols)
    {
        var groups = new Dictionary<string, SortedSet<int>>();
        foreach (var c in cols)
        {
            var m = NumberedName().Match(c.Name);
            if (!m.Success) continue;
            var pattern = $"{m.Groups["pre"].Value}{{n}}{m.Groups["post"].Value}";
            if (!groups.TryGetValue(pattern, out var ns)) groups[pattern] = ns = [];
            ns.Add(int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture));
        }
        return groups.Where(g => g.Value.Count >= 3)
            .Select(g => $"{g.Key} (n = {g.Value.Min}..{g.Value.Max})").OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>Human-readable report: what a migration engineer needs before writing a mapping.</summary>
    public static string ToMarkdown(string source, IReadOnlyList<TableProfile> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Schema discovery: `{Path.GetFileName(source)}`\n");
        sb.AppendLine("| Table | Rows | Columns | Candidate keys | Repeating groups |\n|---|---:|---:|---|---|");
        foreach (var t in tables)
            sb.AppendLine($"| `{t.Name}` | {t.Rows:N0} | {t.Columns.Count} | {Join(t.CandidateKeys)} | {Join(t.RepeatingGroups)} |");
        foreach (var t in tables)
        {
            sb.AppendLine($"\n## `{t.Name}` ({t.Rows:N0} rows)\n");
            sb.AppendLine("| Column | Declared | Stored as | Null | Blank | Distinct | Len | Formats | Flags |");
            sb.AppendLine("|---|---|---|---:|---:|---:|---|---|---|");
            foreach (var c in t.Columns)
            {
                var flags = Flags(c);
                sb.AppendLine($"| `{c.Name}` | {c.DeclaredType} | {Dist(c.StorageTypes, c.Rows)} | {Pct(c.Nulls, c.Rows)} | {Pct(c.Blanks, c.Rows)} | "
                              + $"{(c.DistinctCapped ? $"> {ColumnProfile.DistinctCap:N0}" : c.Distinct.ToString("N0", CultureInfo.InvariantCulture))} | "
                              + $"{(c.MaxLength == 0 ? "" : $"{c.MinLength}–{c.MaxLength}")} | {Dist(c.Formats, c.Rows - c.Nulls, 4)} | {string.Join("; ", flags)} |");
            }
        }
        return sb.ToString();
    }

    /// <summary>Heuristic findings: the things that become mapping rules or defects to handle.</summary>
    public static List<string> Flags(ColumnProfile c)
    {
        var f = new List<string>();
        // Low-cardinality columns are code domains: listing them exposes inconsistent encodings (1/2/M/F/male).
        if (!c.DistinctCapped && c.Distinct is > 1 and <= 12)
            f.Add("code domain: " + string.Join(", ", c.Top(12).Select(t => $"`{t.Value.Trim()}`")));
        // Almost unique but not quite: where duplicate loads hide.
        var distinct = c.ExactDistinct ?? (c.DistinctCapped ? (long?)null : c.Distinct);
        var nonNull = c.Rows - c.Nulls;
        if (distinct is { } d && nonNull > 0 && d < nonNull && d >= 0.95 * nonNull)
            f.Add($"**near-unique: {nonNull - d:N0} repeated values (duplicate loads?)**");
        var stored = c.StorageTypes.Keys.Where(k => k != "null").ToList();
        if (stored.Count > 1) f.Add("**mixed storage types**");
        var dateFormats = c.Formats.Keys.Count(k => k.StartsWith("date:") || k.StartsWith("padded:date"));
        if (dateFormats > 1) f.Add("**mixed date formats**");
        if (c.Formats.Keys.Any(k => k.StartsWith("money:") || k == "number:thousands") && c.Formats.ContainsKey("decimal"))
            f.Add("**money as formatted text**");
        if (c.Formats.Keys.Any(k => k.StartsWith("sentinel:"))) f.Add("sentinel values");
        if (c.Formats.Keys.Any(k => k.StartsWith("padded:"))) f.Add("untrimmed whitespace");
        if (c.DeclaredType.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) && c.StorageTypes.ContainsKey("text"))
            f.Add("**INTEGER column holding text (codes?): leading zeros may be lost**");
        if (c.DeclaredType.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) && c.Formats.ContainsKey("integer")
            && c.MaxLength - c.MinLength >= 2 && c.MaxLength >= 4)
            f.Add("INTEGER values of varying digit length: if these are codes, leading zeros may have been lost");
        return f;
    }

    private static string Join(IReadOnlyList<string> xs) => xs.Count == 0 ? "—" : string.Join(", ", xs.Select(x => $"`{x}`"));
    private static string Pct(long n, long total) => total == 0 || n == 0 ? "" : $"{100.0 * n / total:0.#}%";
    private static string Dist(Dictionary<string, long> d, long total, int top = 3) =>
        string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(top).Select(kv => $"{kv.Key} {Pct(kv.Value, total)}"))
        + (d.Count > top ? $", +{d.Count - top} more" : "");
}
