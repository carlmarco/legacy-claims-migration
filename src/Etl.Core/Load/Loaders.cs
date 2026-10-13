using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Etl.Core.Mapping;
using Npgsql;

namespace Etl.Core.Load;

/// <summary>
/// Staging tables: UNLOGGED, no constraints, same column types as the target (CREATE ... AS TABLE target WITH NO DATA).
/// Rows land here first; survivorship, FK checks and the upsert run as SQL in the merge (DECISIONS D14, D19).
/// </summary>
public static class Staging
{
    public const string Schema = "etl_stage";
    public const string QuarantineKey = "_quarantine";
    public static readonly IReadOnlyList<string> QuarantineColumns = ["entity", "src_table", "src_rec_id", "reason", "detail", "raw"];

    public static string Table(MappingSpec m, string key) => key == QuarantineKey ? $"{m.Name}__quarantine" : $"{m.Name}__{key}";

    /// <summary>Engine bookkeeping DDL, embedded from src/etl/sql/engine.sql so both engines run the identical file.</summary>
    public static string EngineSql()
    {
        using var s = typeof(Staging).Assembly.GetManifestResourceStream("engine.sql")
                      ?? throw new InvalidOperationException("engine.sql resource missing");
        return new StreamReader(s).ReadToEnd();
    }

    public static void EnsureSchema(NpgsqlConnection con, MappingSpec m)
    {
        Pg.Exec(con, EngineSql());
        Pg.Exec(con, File.ReadAllText(m.TargetDdlPath));
        Pg.Exec(con, $"CREATE SCHEMA IF NOT EXISTS {Pg.Q(Schema)}");
    }

    /// <summary>Benchmark from an empty target: measures the insert path, not the no-op re-run.</summary>
    public static void DropTarget(NpgsqlConnection con, MappingSpec m)
    {
        Pg.Exec(con, $"DROP SCHEMA IF EXISTS {Pg.Q(m.TargetSchema)} CASCADE");
        if (Pg.Scalar<bool>(con, "SELECT to_regclass('etl.quarantine') IS NOT NULL"))
            Pg.Exec(con, "DELETE FROM etl.quarantine WHERE dataset = $1", null, m.Name);
    }

    public static void Reset(NpgsqlConnection con, MappingSpec m)
    {
        foreach (var entity in m.Entities.Keys)
        {
            var t = Pg.Q(Schema, Table(m, entity));
            Pg.Exec(con, $"DROP TABLE IF EXISTS {t}");
            Pg.Exec(con, $"CREATE UNLOGGED TABLE {t} AS TABLE {Pg.Q(m.TargetSchema, entity)} WITH NO DATA");
        }
        var q = Pg.Q(Schema, Table(m, QuarantineKey));
        Pg.Exec(con, $"DROP TABLE IF EXISTS {q}");
        Pg.Exec(con, $"CREATE UNLOGGED TABLE {q} (entity text, src_table text, src_rec_id bigint, reason text, detail text, raw jsonb)");
    }

    /// <summary>Typed columns for every staging destination (each entity, plus the quarantine table).</summary>
    public static Dictionary<string, (string Table, IReadOnlyList<PgColumn> Columns)> Destinations(NpgsqlConnection con, MappingSpec m)
    {
        var d = m.Entities.ToDictionary(e => e.Key, e => (Table(m, e.Key), PgColumn.For(con, Schema, Table(m, e.Key), e.Value.Output)));
        d[QuarantineKey] = (Table(m, QuarantineKey), PgColumn.For(con, Schema, Table(m, QuarantineKey), QuarantineColumns));
        return d;
    }

    /// <summary>A record as (destination key, staging row).</summary>
    public static (string Key, object?[] Values) Route(TransformRecord r) => r.IsRow
        ? (r.Entity, r.Values!)
        : (QuarantineKey, [r.Entity, r.Table, r.RecId, r.Reason, r.Detail, JsonSerializer.Serialize(r.Raw)]);
}

public sealed record LoadResult(Dictionary<string, long> Counts, Dictionary<string, double> Timing);

/// <summary>
/// Load strategies. They share the transform, staging, merge and validation; only "records -> staging" differs,
/// so benchmark differences are attributable to the load path alone (DECISIONS D20).
/// </summary>
public static class Loaders
{
    public const int BatchSize = 20_000;

    // ---- write primitives -------------------------------------------------------------------------

    /// <summary>Binary COPY: one statement streams the whole batch. Values must match the wire types exactly.</summary>
    public static void Copy(NpgsqlConnection con, string table, IReadOnlyList<PgColumn> cols, List<object?[]> rows)
    {
        var sql = $"COPY {Pg.Q(Staging.Schema, table)} ({string.Join(", ", cols.Select(c => Pg.Q(c.Name)))}) FROM STDIN (FORMAT BINARY)";
        using var w = con.BeginBinaryImport(sql);
        foreach (var row in rows)
        {
            w.StartRow();
            for (var i = 0; i < cols.Count; i++)
            {
                if (row[i] is null) w.WriteNull();
                else w.Write(cols[i].Coerce(row[i]!), cols[i].DbType);
            }
        }
        w.Complete();   // without Complete(), disposing the importer cancels the whole COPY
    }

    private static NpgsqlCommand InsertCommand(string table, IReadOnlyList<PgColumn> cols, NpgsqlConnection? con = null)
    {
        var cmd = new NpgsqlCommand(
            $"INSERT INTO {Pg.Q(Staging.Schema, table)} ({string.Join(", ", cols.Select(c => Pg.Q(c.Name)))}) " +
            $"VALUES ({string.Join(", ", cols.Select((_, i) => $"${i + 1}"))})", con);
        foreach (var c in cols) cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = c.DbType });
        return cmd;
    }

    private static void Bind(NpgsqlParameterCollection ps, IReadOnlyList<PgColumn> cols, object?[] row)
    {
        for (var i = 0; i < cols.Count; i++) ps[i].Value = row[i] is null ? DBNull.Value : cols[i].Coerce(row[i]!);
    }

    /// <summary>One round trip for the whole batch; Postgres still executes one INSERT per row.</summary>
    public static void InsertBatch(NpgsqlConnection con, string table, IReadOnlyList<PgColumn> cols, List<object?[]> rows)
    {
        using var batch = new NpgsqlBatch(con);
        var sql = InsertCommand(table, cols).CommandText;
        foreach (var row in rows)
        {
            var bc = new NpgsqlBatchCommand(sql);
            foreach (var c in cols) bc.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = c.DbType });
            Bind(bc.Parameters, cols, row);
            batch.BatchCommands.Add(bc);
        }
        batch.ExecuteNonQuery();
    }

    // ---- strategies -------------------------------------------------------------------------------

    /// <summary>Baseline: one prepared INSERT per row, autocommit. A round trip and a WAL flush per row.</summary>
    public static LoadResult Naive(NpgsqlDataSource ds, MappingSpec m, IEnumerable<TransformRecord> records)
    {
        using var con = ds.OpenConnection();
        var dest = Staging.Destinations(con, m);
        var commands = dest.ToDictionary(d => d.Key, d =>
        {
            var cmd = InsertCommand(d.Value.Table, d.Value.Columns, con);
            cmd.Prepare();   // parse/plan once per statement shape (psycopg auto-prepares the same way)
            return cmd;
        });
        var counts = new Dictionary<string, long>();
        foreach (var r in records)
        {
            var (key, values) = Staging.Route(r);
            Bind(commands[key].Parameters, dest[key].Columns, values);
            commands[key].ExecuteNonQuery();
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        foreach (var c in commands.Values) c.Dispose();
        return new LoadResult(counts, []);
    }

    /// <summary>Buffer per destination; flush each full buffer as one bulk operation. Memory is bounded by BatchSize.</summary>
    private static LoadResult Buffered(NpgsqlDataSource ds, MappingSpec m, IEnumerable<TransformRecord> records,
                                      Action<NpgsqlConnection, string, IReadOnlyList<PgColumn>, List<object?[]>> flush)
    {
        using var con = ds.OpenConnection();
        var dest = Staging.Destinations(con, m);
        var buffers = dest.Keys.ToDictionary(k => k, _ => new List<object?[]>(BatchSize));
        var counts = new Dictionary<string, long>();
        void Flush(string key)
        {
            var buf = buffers[key];
            if (buf.Count == 0) return;
            flush(con, dest[key].Table, dest[key].Columns, buf);
            counts[key] = counts.GetValueOrDefault(key) + buf.Count;
            buf.Clear();
        }
        foreach (var r in records)
        {
            var (key, values) = Staging.Route(r);
            buffers[key].Add(values);
            if (buffers[key].Count >= BatchSize) Flush(key);
        }
        foreach (var key in buffers.Keys) Flush(key);
        return new LoadResult(counts, []);
    }

    public static LoadResult ExecuteMany(NpgsqlDataSource ds, MappingSpec m, IEnumerable<TransformRecord> records) =>
        Buffered(ds, m, records, InsertBatch);

    public static LoadResult BatchedCopy(NpgsqlDataSource ds, MappingSpec m, IEnumerable<TransformRecord> records) =>
        Buffered(ds, m, records, Copy);

    private sealed record Batch(string Key, List<object?[]> Rows);

    /// <summary>
    /// Producer/consumer with backpressure. `workers` threads transform REC_ID-range chunks and push per-destination
    /// batches into a bounded channel; `writers` connections drain it with binary COPY. When the database falls behind,
    /// the channel fills and producers await (backpressure), so memory stays capped at capacity x batch size.
    /// Transforms are row-local and deterministic, so the partitioning can't change the output (D12, D14).
    /// </summary>
    public static LoadResult Parallel(NpgsqlDataSource ds, MappingSpec m, int workers, int writers, long? limit)
    {
        const int chunkBatch = 5_000;
        Dictionary<string, (string Table, IReadOnlyList<PgColumn> Columns)> dest;
        using (var con = ds.OpenConnection()) dest = Staging.Destinations(con, m);

        var chunks = Chunks(m, workers * 4, limit);
        var channel = Channel.CreateBounded<Batch>(new BoundedChannelOptions(workers * 2)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false,
        });
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        long transformTicks = 0, producerWaitTicks = 0, copyTicks = 0;

        var consumers = Enumerable.Range(0, writers).Select(_ => Task.Run(async () =>
        {
            await using var con = await ds.OpenConnectionAsync();
            await foreach (var b in channel.Reader.ReadAllAsync())
            {
                var t0 = Stopwatch.GetTimestamp();
                Copy(con, dest[b.Key].Table, dest[b.Key].Columns, b.Rows);
                Interlocked.Add(ref copyTicks, Stopwatch.GetTimestamp() - t0);
                counts.AddOrUpdate(b.Key, b.Rows.Count, (_, c) => c + b.Rows.Count);
            }
        })).ToArray();

        var producers = System.Threading.Tasks.Parallel.ForEachAsync(chunks,
            new ParallelOptions { MaxDegreeOfParallelism = workers }, async (chunk, ct) =>
        {
            var buffers = new Dictionary<string, List<object?[]>>();
            var transformStart = Stopwatch.GetTimestamp();
            long waited = 0;
            async ValueTask Send(string key)
            {
                var t0 = Stopwatch.GetTimestamp();
                await channel.Writer.WriteAsync(new Batch(key, buffers[key]), ct);
                waited += Stopwatch.GetTimestamp() - t0;
                buffers[key] = new List<object?[]>(chunkBatch);
            }
            foreach (var r in m.Transform([chunk.Table], (chunk.Lo, chunk.Hi)))
            {
                var (key, values) = Staging.Route(r);
                if (!buffers.TryGetValue(key, out var buf)) buffers[key] = buf = new List<object?[]>(chunkBatch);
                buf.Add(values);
                if (buf.Count >= chunkBatch) await Send(key);
            }
            foreach (var key in buffers.Keys.ToList())
                if (buffers[key].Count > 0) await Send(key);
            Interlocked.Add(ref producerWaitTicks, waited);
            Interlocked.Add(ref transformTicks, Stopwatch.GetTimestamp() - transformStart - waited);
        });

        producers.GetAwaiter().GetResult();
        channel.Writer.Complete();
        Task.WaitAll(consumers);
        double Sec(long ticks) => Math.Round((double)ticks / Stopwatch.Frequency, 2);
        return new LoadResult(new Dictionary<string, long>(counts), new Dictionary<string, double>
        {
            ["chunks"] = chunks.Count,
            ["worker_transform_cpu_s"] = Sec(transformTicks),
            ["producer_backpressure_wait_s"] = Sec(producerWaitTicks),
            ["writer_copy_s"] = Sec(copyTicks),
        });
    }

    private sealed record Chunk(string Table, long Lo, long Hi);

    /// <summary>Split each legacy table into REC_ID ranges; largest tables first so long chunks don't become the tail.</summary>
    private static List<Chunk> Chunks(MappingSpec m, int perTable, long? limit)
    {
        using var con = m.OpenSource();
        var chunks = new List<Chunk>();
        foreach (var table in m.ByTable.Keys)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT min({m.RecIdColumn}), max({m.RecIdColumn}) FROM {table}";
            using var r = cmd.ExecuteReader();
            r.Read();
            if (r.IsDBNull(0)) continue;
            long lo = r.GetInt64(0), hi = r.GetInt64(1);
            if (limit is { } l) hi = Math.Min(hi, lo + l - 1);
            var step = Math.Max(1, (hi - lo + perTable) / perTable);
            for (var a = lo; a <= hi; a += step) chunks.Add(new Chunk(table, a, Math.Min(a + step - 1, hi)));
        }
        return chunks.OrderByDescending(c => c.Hi - c.Lo).ToList();
    }
}
