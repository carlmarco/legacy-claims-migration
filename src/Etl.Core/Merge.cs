using System.Diagnostics;
using Etl.Core.Load;
using Etl.Core.Mapping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Etl.Core;

/// <summary>
/// Set-based merge from staging into the target, generated from the mapping (DECISIONS D6, D14, D19, D22).
/// Per entity, parent before child, in ONE transaction:
///   1. survivorship  DISTINCT ON (key) ... ORDER BY key, order; field rules as window aggregates;
///                    follow_parent children keep only rows from the parent's surviving legacy row
///   2. orphans       parent key missing from the merged parent table -> etl.quarantine (ORPHAN_FK)
///   3. upsert        INSERT ... ON CONFLICT (key) DO UPDATE ... WHERE (cols) IS DISTINCT FROM (EXCLUDED...)
///   4. child sets    follow_parent children the surviving parent no longer has are deleted
/// A re-run over unchanged data writes zero rows. Ported SQL-for-SQL from src/etl/merge.py.
/// </summary>
public static class Merge
{
    private static string Ids(IEnumerable<string> cols, string? alias = null) =>
        string.Join(", ", cols.Select(c => alias is null ? Pg.Q(c) : $"{alias}.{Pg.Q(c)}"));

    private static long Count(NpgsqlConnection con, NpgsqlTransaction tx, string table) =>
        Pg.Scalar<long>(con, $"SELECT count(*) FROM {table}", tx);

    public static Dictionary<string, object?> Entity(NpgsqlConnection con, NpgsqlTransaction tx, MappingSpec m, string entity, long runId)
    {
        var e = m.Entities[entity];
        var target = Pg.Q(m.TargetSchema, entity);
        var stage = Pg.Q(Staging.Schema, Staging.Table(m, entity));
        var surv = Pg.Q($"surv__{entity}");
        var key = Ids(e.Key);
        var stats = new Dictionary<string, object?> { ["staged"] = Count(con, tx, stage) };
        var sw = Stopwatch.StartNew();
        double Lap() { var s = Math.Round(sw.Elapsed.TotalSeconds, 2); sw.Restart(); return s; }

        // 1. Survivorship. Ties on the order expression break on the higher legacy REC_ID, deterministically.
        var rules = e.Survivorship.TryGetValue("fields", out var f) && f is IReadOnlyDictionary<string, object?> fr ? fr : null;
        var order = e.Survivorship.TryGetValue("order", out var o) && o is string os ? os : "_version DESC";
        string Select(string? alias) => string.Join(", ", e.Output.Select(c =>
            rules is not null && rules.TryGetValue(c, out var fn)
                ? $"{(string)fn! switch { "max" => "max", "min" => "min", var x => throw new InvalidDataException($"field rule {x}") }}"
                  + $"({(alias is null ? "" : alias + ".")}{Pg.Q(c)}) OVER (PARTITION BY {Ids(e.Key, alias)}) AS {Pg.Q(c)}"
                : (alias is null ? "" : alias + ".") + Pg.Q(c)));
        Pg.Exec(con, $"DROP TABLE IF EXISTS {surv}", tx);
        var follow = e.Survivorship.TryGetValue("follow_parent", out var fp) ? fp as string : null;
        if (follow is not null)
        {
            // Child rows belong to a *version* of their parent: keep only children emitted by the parent's surviving
            // legacy row, so a slot the newest version dropped can't be revived from an older one (D22).
            var pe = m.Entities[follow];
            var pcols = e.References[follow];
            var porder = pe.Survivorship.TryGetValue("order", out var po) && po is string pos ? pos : "_version DESC";
            var win = $"SELECT DISTINCT ON ({Ids(pe.Key)}) {Ids(pe.Key)}, _src_table, _src_rec_id " +
                      $"FROM {Pg.Q(Staging.Schema, Staging.Table(m, follow))} ORDER BY {Ids(pe.Key)}, {porder}, _src_rec_id DESC";
            var on = string.Join(" AND ", pe.Key.Zip(pcols, (pk, ck) => $"w.{Pg.Q(pk)} = c.{Pg.Q(ck)}")
                                 .Concat(["w._src_table = c._src_table", "w._src_rec_id = c._src_rec_id"]));
            Pg.Exec(con, $"CREATE TEMP TABLE {surv} AS SELECT DISTINCT ON ({Ids(e.Key, "c")}) {Select("c")} " +
                         $"FROM {stage} c JOIN ({win}) w ON {on} ORDER BY {Ids(e.Key, "c")}, {order}, c._src_rec_id DESC", tx);
        }
        else
        {
            Pg.Exec(con, $"CREATE TEMP TABLE {surv} AS SELECT DISTINCT ON ({key}) {Select(null)} " +
                         $"FROM {stage} ORDER BY {key}, {order}, _src_rec_id DESC", tx);
        }
        var survivors = Count(con, tx, surv);
        stats["survivors"] = survivors;
        stats["duplicates_resolved"] = (long)stats["staged"]! - survivors;
        stats["t_survivorship"] = Lap();

        // 2. Orphans: parent key absent from the (already merged) parent target table.
        long orphans = 0;
        foreach (var (parent, childCols) in e.References)
        {
            var join = string.Join(" AND ", m.Entities[parent].Key.Zip(childCols, (pk, ck) => $"p.{Pg.Q(pk)} = s.{Pg.Q(ck)}"));
            var missing = $"NOT EXISTS (SELECT 1 FROM {Pg.Q(m.TargetSchema, parent)} p WHERE {join})";
            Pg.Exec(con, $"""
                INSERT INTO etl.quarantine (dataset, entity, src_table, src_rec_id, reason, detail, raw, run_id)
                SELECT $1, $2, s._src_table, s._src_rec_id, 'ORPHAN_FK', $3, to_jsonb(s), $4
                FROM {surv} s WHERE {missing}
                ON CONFLICT DO NOTHING
                """, tx, m.Name, entity, $"no {parent} for ({string.Join(", ", childCols)})", runId);
            var n = Pg.Exec(con, $"DELETE FROM {surv} s WHERE {missing}", tx);
            orphans += n;
            stats[$"orphans_{parent}"] = (long)n;
        }
        stats["orphans"] = orphans;
        stats["t_orphans"] = Lap();

        // 3. Upsert. Only rows whose data actually changed are written (lineage excluded from the comparison).
        var dataCols = e.Output.Where(c => !e.Key.Contains(c) && !MappingSpec.LineageColumns.Contains(c)).ToList();
        var setCols = e.Output.Where(c => !e.Key.Contains(c)).ToList();
        var distinct = dataCols.Count == 0 ? "false"
            : $"({Ids(dataCols, "t")}) IS DISTINCT FROM ({Ids(dataCols, "EXCLUDED")})";
        using (var cmd = new NpgsqlCommand($"""
            WITH w AS (
                INSERT INTO {target} AS t ({Ids(e.Output)}) SELECT {Ids(e.Output)} FROM {surv}
                ON CONFLICT ({key}) DO UPDATE SET {string.Join(", ", setCols.Select(c => $"{Pg.Q(c)} = EXCLUDED.{Pg.Q(c)}"))}
                WHERE {distinct}
                RETURNING (xmax = 0) AS inserted)
            SELECT count(*) FILTER (WHERE inserted), count(*) FILTER (WHERE NOT inserted) FROM w
            """, con, tx))
        using (var r = cmd.ExecuteReader())
        {
            r.Read();
            stats["inserted"] = r.GetInt64(0);
            stats["updated"] = r.GetInt64(1);
        }
        stats["unchanged"] = survivors - orphans - (long)stats["inserted"]! - (long)stats["updated"]!;
        stats["t_upsert"] = Lap();

        // 4. Replace the child set of every parent present in this run (follow_parent only; an FK alone never deletes).
        if (follow is not null)
        {
            var pcols = e.References[follow];
            var sameParent = string.Join(" AND ", m.Entities[follow].Key.Zip(pcols, (pk, ck) => $"p.{Pg.Q(pk)} = t.{Pg.Q(ck)}"));
            var sameKey = string.Join(" AND ", e.Key.Select(k => $"s.{Pg.Q(k)} = t.{Pg.Q(k)}"));
            stats["deleted"] = (long)Pg.Exec(con, $"""
                DELETE FROM {target} t
                WHERE EXISTS (SELECT 1 FROM {Pg.Q(Staging.Schema, Staging.Table(m, follow))} p WHERE {sameParent})
                  AND NOT EXISTS (SELECT 1 FROM {surv} s WHERE {sameKey})
                """, tx);
            stats["t_child_delete"] = Lap();
        }
        Pg.Exec(con, $"DROP TABLE {surv}", tx);
        return stats;
    }

    /// <summary>Drop the target's FK constraints, returning (table, name, definition) to restore them.</summary>
    public static List<(string Table, string Name, string Definition)> DropFks(NpgsqlConnection con, NpgsqlTransaction tx, MappingSpec m)
    {
        var fks = new List<(string, string, string)>();
        using (var cmd = new NpgsqlCommand("""
            SELECT c.conrelid::regclass::text, c.conname, pg_get_constraintdef(c.oid)
            FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE c.contype = 'f' AND n.nspname = $1 ORDER BY 1, 2
            """, con, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = m.TargetSchema });
            using var r = cmd.ExecuteReader();
            while (r.Read()) fks.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        foreach (var (table, name, _) in fks) Pg.Exec(con, $"ALTER TABLE {table} DROP CONSTRAINT {Pg.Q(name)}", tx);
        return fks;
    }

    /// <summary>Re-add FKs NOT VALID (instant), then VALIDATE: one set-based check per FK instead of a trigger
    /// lookup per inserted row. A violation fails the whole merge transaction (D26).</summary>
    public static void RestoreFks(NpgsqlConnection con, NpgsqlTransaction tx, List<(string Table, string Name, string Definition)> fks)
    {
        foreach (var (table, name, def) in fks) Pg.Exec(con, $"ALTER TABLE {table} ADD CONSTRAINT {Pg.Q(name)} {def} NOT VALID", tx);
        foreach (var (table, name, _) in fks) Pg.Exec(con, $"ALTER TABLE {table} VALIDATE CONSTRAINT {Pg.Q(name)}", tx);
    }

    /// <summary>Merge every entity in one transaction. Dry run = the same path ending in ROLLBACK (D19).</summary>
    public static Dictionary<string, object?> All(NpgsqlConnection con, MappingSpec m, long runId, bool deferFks, bool dryRun,
                                                  string? reviewDir = null, ILogger? log = null)
    {
        log ??= NullLogger.Instance;
        using var tx = con.BeginTransaction();
        var fks = deferFks ? DropFks(con, tx, m) : [];
        // Quarantine reflects the latest run: clear this dataset's rows, then re-add from this run.
        Pg.Exec(con, "DELETE FROM etl.quarantine WHERE dataset = $1", tx, m.Name);
        Pg.Exec(con, $"""
            INSERT INTO etl.quarantine (dataset, entity, src_table, src_rec_id, reason, detail, raw, run_id)
            SELECT $1, entity, src_table, src_rec_id, reason, min(detail), (array_agg(raw))[1], $2
            FROM {Pg.Q(Staging.Schema, Staging.Table(m, Staging.QuarantineKey))}
            GROUP BY entity, src_table, src_rec_id, reason
            ON CONFLICT DO NOTHING
            """, tx, m.Name, runId);
        var result = new Dictionary<string, object?>();
        foreach (var entity in m.LoadOrder())
        {
            var sw = Stopwatch.StartNew();
            var s = Entity(con, tx, m, entity, runId);
            s["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2);
            result[entity] = s;
            log.LogInformation("Merged {Entity}: {Staged} staged, {Inserted} inserted, {Updated} updated, {Orphans} orphans in {Seconds}s",
                               entity, s["staged"], s["inserted"], s["updated"], s["orphans"], s["seconds"]);
        }
        if (fks.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            RestoreFks(con, tx, fks);
            result["_fk_validate"] = new Dictionary<string, object?> { ["constraints"] = fks.Count, ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2) };
        }
        if (reviewDir is not null)
            result["_review_report"] = ReviewReport.Write(con, tx, m, runId, reviewDir, dryRun);
        if (dryRun) { tx.Rollback(); log.LogInformation("Dry run: merge rolled back"); }
        else tx.Commit();
        return result;
    }
}
