using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using Etl.Core.Mapping;

// etl transform --mapping <path> [--tables A,B] [--limit N] [--checksum]
// Minimal argument parsing on purpose: a handful of flags doesn't justify a CLI framework dependency.
var opts = ParseArgs(args);
try
{
    return args.FirstOrDefault() switch
    {
        "transform" => Transform(opts),
        "discover" => Discover(opts),
        "migrate" => Migrate(opts),
        "sync" => Migrate(opts, incremental: true),
        _ => Usage(),
    };
}
catch (Exception e)
{
    // One clear line and a non-zero exit code for schedulers and scripts, instead of an unhandled-exception crash.
    Console.Error.WriteLine($"etl: {e.GetType().Name}: {e.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("""
        usage:
          etl transform --mapping <path> [--tables A,B] [--limit N] [--checksum]
          etl discover  --source <legacy.sqlite> [--out report.md]
          etl sync      --mapping <path> [--strategy batched] [--dry-run]   (incremental: only rows since the last committed sync)
          etl migrate   --mapping <path> --strategy naive|executemany|batched|parallel
                        [--workers N] [--writers K] [--limit N] [--fresh] [--no-merge] [--dry-run] [--defer-fks] [--out stats.json]
        """);
    return 2;
}

// Profile an unfamiliar database: types, formats, nulls, candidate keys, repeating groups.
static int Discover(Dictionary<string, string?> o)
{
    var source = o.GetValueOrDefault("--source") ?? throw new ArgumentException("--source is required");
    var sw = Stopwatch.StartNew();
    var tables = Etl.Core.Discovery.SchemaProfiler.Profile(source);
    var md = Etl.Core.Discovery.SchemaProfiler.ToMarkdown(source, tables);
    if (o.GetValueOrDefault("--out") is { } outPath) File.WriteAllText(outPath, md); else Console.WriteLine(md);
    Console.Error.WriteLine($"profiled {tables.Count} tables, {tables.Sum(t => t.Rows):N0} rows in {sw.Elapsed.TotalSeconds:0.0}s");
    return 0;
}

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

static int Migrate(Dictionary<string, string?> o, bool incremental = false)
{
    var options = new Etl.Core.RunOptions(
        MappingPath: o.GetValueOrDefault("--mapping") ?? throw new ArgumentException("--mapping is required"),
        Strategy: o.GetValueOrDefault("--strategy") ?? "batched",
        DryRun: o.ContainsKey("--dry-run"),
        Limit: o.GetValueOrDefault("--limit") is { } l ? long.Parse(l) : null,
        MergeStep: !o.ContainsKey("--no-merge"),
        Fresh: o.ContainsKey("--fresh"),
        Workers: o.GetValueOrDefault("--workers") is { } w ? int.Parse(w) : Environment.ProcessorCount / 2,
        Writers: o.GetValueOrDefault("--writers") is { } k ? int.Parse(k) : 2,
        DeferFks: o.ContainsKey("--defer-fks"),
        Incremental: incremental);
    // Structured logs: JSON lines on stderr (stdout stays clean for the stats JSON). Message templates keep
    // {Entity}, {Inserted}... as queryable fields instead of flattening them into text.
    using var loggerFactory = LoggerFactory.Create(b => b
        .AddConsole(c => c.LogToStandardErrorThreshold = LogLevel.Trace)
        .AddJsonConsole(j => j.IncludeScopes = true)
        .AddFilter("Microsoft", LogLevel.Warning));
    var log = loggerFactory.CreateLogger("etl");
    using var scope = log.BeginScope(new Dictionary<string, object> { ["dataset"] = options.MappingPath, ["strategy"] = options.Strategy });
    var stats = Etl.Core.Pipeline.Run(options, log);
    var json = JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true });
    if (o.GetValueOrDefault("--out") is { } outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, json);
    }
    Console.WriteLine(json);
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
