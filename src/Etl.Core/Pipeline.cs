using System.Diagnostics;
using System.Text.Json;
using Etl.Core.Load;
using Etl.Core.Mapping;
using Npgsql;
using NpgsqlTypes;

namespace Etl.Core;

public sealed record RunOptions(string MappingPath, string Strategy, bool DryRun = false, long? Limit = null,
                                bool MergeStep = true, bool Fresh = false, int Workers = 1, int Writers = 2,
                                bool DeferFks = false, string? Dsn = null);

/// <summary>
/// One migration run: extract -> transform -> load (strategy) into staging -> merge into the target. Each phase is
/// timed separately so the benchmark shows where the bottleneck actually is. Every run is recorded in etl.migration_run.
/// </summary>
public static class Pipeline
{
    /// <summary>Wraps an enumerable and accumulates the time spent producing items (the transform's share).</summary>
    private sealed class Timed<T>(IEnumerable<T> inner) : IEnumerable<T>
    {
        public long Ticks;
        public IEnumerator<T> GetEnumerator()
        {
            using var e = inner.GetEnumerator();
            while (true)
            {
                var t0 = Stopwatch.GetTimestamp();
                var more = e.MoveNext();
                Ticks += Stopwatch.GetTimestamp() - t0;
                if (!more) yield break;
                yield return e.Current;
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public static Dictionary<string, object?> Run(RunOptions o, Func<NpgsqlConnection, MappingSpec, long, Dictionary<string, object?>>? merge = null)
    {
        var m = new MappingSpec(o.MappingPath);
        using var ds = Pg.DataSource(o.Dsn);
        long runId;
        using (var con = ds.OpenConnection())
        {
            if (o.Fresh) Staging.DropTarget(con, m);
            Staging.EnsureSchema(con, m);
            Staging.Reset(con, m);
            runId = Pg.Scalar<long>(con, "INSERT INTO etl.migration_run (dataset, strategy, dry_run) VALUES ($1, $2, $3) RETURNING run_id",
                                    null, m.Name, "dotnet:" + o.Strategy, o.DryRun);
        }
        var stats = new Dictionary<string, object?>
        {
            ["engine"] = "dotnet", ["run_id"] = runId, ["dataset"] = m.Name, ["strategy"] = o.Strategy, ["dry_run"] = o.DryRun,
            ["limit"] = o.Limit, ["workers"] = o.Workers, ["writers"] = o.Writers, ["defer_fks"] = o.DeferFks,
        };
        try
        {
            var total = Stopwatch.StartNew();
            LoadResult load;
            if (o.Strategy == "parallel")
            {
                load = Loaders.Parallel(ds, m, o.Workers, o.Writers, o.Limit);
                foreach (var (k, v) in load.Timing) stats[k] = v;
            }
            else
            {
                var records = new Timed<TransformRecord>(m.Transform(limit: o.Limit));
                load = o.Strategy switch
                {
                    "naive" => Loaders.Naive(ds, m, records),
                    "executemany" => Loaders.ExecuteMany(ds, m, records),
                    "batched" => Loaders.BatchedCopy(ds, m, records),
                    _ => throw new ArgumentException($"unknown strategy '{o.Strategy}'"),
                };
                var transformS = (double)records.Ticks / Stopwatch.Frequency;
                stats["transform_s"] = Math.Round(transformS, 2);
                stats["load_s"] = Math.Round(total.Elapsed.TotalSeconds - transformS, 2);
            }
            var extractLoad = total.Elapsed.TotalSeconds;
            var staged = load.Counts.Where(kv => kv.Key != Staging.QuarantineKey).Sum(kv => kv.Value);
            stats["staged_rows"] = staged;
            stats["staged_by_entity"] = new SortedDictionary<string, long>(load.Counts, StringComparer.Ordinal);
            stats["extract_load_s"] = Math.Round(extractLoad, 2);
            stats["staged_rows_per_s"] = extractLoad > 0 ? (long)(staged / extractLoad) : null;

            if (o.MergeStep && merge is not null)
            {
                var t = Stopwatch.StartNew();
                using var con = ds.OpenConnection();
                stats["merge"] = merge(con, m, runId);
                stats["merge_s"] = Math.Round(t.Elapsed.TotalSeconds, 2);
            }
            stats["total_s"] = Math.Round(total.Elapsed.TotalSeconds, 2);
            stats["peak_working_set_mb"] = Math.Round(Process.GetCurrentProcess().PeakWorkingSet64 / 1e6, 1);
            Finish(ds, runId, "succeeded", stats);
        }
        catch (Exception e)
        {
            stats["error"] = e.Message;
            Finish(ds, runId, "failed", stats);
            throw;
        }
        return stats;
    }

    private static void Finish(NpgsqlDataSource ds, long runId, string status, Dictionary<string, object?> stats)
    {
        using var con = ds.OpenConnection();
        using var cmd = new NpgsqlCommand("UPDATE etl.migration_run SET finished_at = now(), status = $1, stats = $2 WHERE run_id = $3", con);
        cmd.Parameters.Add(new NpgsqlParameter { Value = status });
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(stats), NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = runId });
        cmd.ExecuteNonQuery();
    }
}
