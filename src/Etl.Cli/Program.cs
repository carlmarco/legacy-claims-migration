using System.Diagnostics;
using System.Text.Json;
using Etl.Core.Mapping;

// etl transform --mapping <path> [--tables A,B] [--limit N] [--checksum]
// Minimal argument parsing on purpose: a handful of flags doesn't justify a CLI framework dependency.
var opts = ParseArgs(args);
if (args.Length == 0 || args[0] != "transform")
{
    Console.Error.WriteLine("usage: etl transform --mapping <path> [--tables A,B] [--limit N] [--checksum]");
    return 2;
}
return Transform(opts);

static int Transform(Dictionary<string, string?> o)
{
    var m = new MappingSpec(o["--mapping"] ?? throw new ArgumentException("--mapping is required"));
    var tables = o.TryGetValue("--tables", out var t) && t is not null ? t.Split(',') : null;
    long? limit = o.TryGetValue("--limit", out var l) && l is not null ? long.Parse(l) : null;
    var checksum = o.ContainsKey("--checksum") ? new RowChecksum() : null;
    var rows = new SortedDictionary<string, long>(StringComparer.Ordinal);
    var quarantined = new SortedDictionary<string, long>(StringComparer.Ordinal);
    var sw = Stopwatch.StartNew();
    foreach (var rec in m.Transform(tables, limit: limit))
    {
        if (rec.IsRow)
        {
            rows[rec.Entity] = rows.GetValueOrDefault(rec.Entity) + 1;
            checksum?.Add(rec.Entity, rec.Values!);
        }
        else quarantined[$"{rec.Entity}:{rec.Reason}"] = quarantined.GetValueOrDefault($"{rec.Entity}:{rec.Reason}") + 1;
    }
    var elapsed = sw.Elapsed.TotalSeconds;
    var report = new Dictionary<string, object?>
    {
        ["engine"] = "dotnet",
        ["excluded_soft_deleted"] = (tables ?? m.ByTable.Keys.ToArray()).ToDictionary(x => x, m.ExcludedCount),
        ["target_rows"] = rows,
        ["quarantined"] = quarantined,
        ["elapsed_s"] = Math.Round(elapsed, 1),
        ["target_rows_per_s"] = elapsed > 0 ? (long)(rows.Values.Sum() / elapsed) : null,
        ["checksum"] = checksum?.Result().ToDictionary(kv => kv.Key, kv => new { count = kv.Value.Count, sum = kv.Value.Sum }),
    };
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

static Dictionary<string, string?> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string?>();
    for (var i = 1; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--")) continue;
        var hasValue = i + 1 < a.Length && !a[i + 1].StartsWith("--");
        d[a[i]] = hasValue ? a[++i] : null;
    }
    return d;
}
